using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Platform;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Interop;
using DiskTreeScanner.Ntfs.Mft;
using DiskTreeScanner.Ntfs.Records;

namespace DiskTreeScanner.Ntfs.Usn;

/// <summary>Outcome of an incremental refresh.</summary>
public sealed class UsnRefreshResult
{
    public required ScanResult Result { get; init; }

    /// <summary>MFT records changed since the last scan or refresh.</summary>
    public required int Changes { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>True when names, folders or links changed (the tree was re-laid out).</summary>
    public bool Structural { get; init; }

    internal int[]? NewPositionOfOld { get; init; }

    /// <summary>Position, in the refreshed model, of a node of the previous model (-1 if it is gone).</summary>
    public int Map(int oldNode) => NewPositionOfOld is null ? oldNode : (uint)oldNode < (uint)NewPositionOfOld.Length ? NewPositionOfOld[oldNode] : -1;
}

/// <summary>
/// Incremental refresh after an MFT scan: reads the USN change journal from the recorded position,
/// re-reads each changed record (FSCTL_GET_NTFS_FILE_RECORD, same parser as the scan) and patches the
/// model. Data-only changes re-aggregate only the ancestors of the changed files; creations,
/// deletions, renames and hard-link changes re-lay out the tree. The model shown by the UI is never
/// modified: a new one is returned. Returns null when a full rescan is needed (journal recreated or
/// wrapped, too many changes).
/// </summary>
public static unsafe class UsnRefresher
{
    public const int MaxChanges = 100_000;

    private const uint ReasonCreate = 0x0000_0100;
    private const uint ReasonDelete = 0x0000_0200;
    private const uint ReasonRenameOld = 0x0000_1000;
    private const uint ReasonRenameNew = 0x0000_2000;
    private const uint ReasonHardLink = 0x0001_0000;
    private const uint Structural = ReasonCreate | ReasonDelete | ReasonRenameOld | ReasonRenameNew | ReasonHardLink;
    private const uint KeptFlags = (uint)(NodeFlags.HardLinkSecondary | NodeFlags.NtfsSpecial | NodeFlags.Synthetic | NodeFlags.Orphan);

    private sealed class RecordState
    {
        public bool InUse;
        public bool IsDirectory;
        public long Size;
        public long Allocated;
        public uint Flags;
        public long Modified;
        public readonly List<(int ParentRecord, ushort ParentSequence, string Name)> Links = new();
    }

    public static UsnRefreshResult? TryRefresh(ScanResult previous, ActivityLog log, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        if (previous.Usn is not { } checkpoint || previous.Model.RecordNumber is null || previous.Target.VolumeDevicePath is not { } device)
            return null;
        using NtfsVolume volume = NtfsVolume.Open(device);
        var range = UsnJournal.QueryRange(volume.Handle);
        if (range is null || range.Value.JournalId != checkpoint.JournalId)
        {
            log.Info("USN journal recreated or inactive since the scan: full rescan.");
            return null;
        }
        if (checkpoint.NextUsn < range.Value.FirstUsn)
        {
            log.Info("USN journal wrapped since the scan (changes lost): full rescan.");
            return null;
        }

        var changed = new Dictionary<int, uint>();
        long next = ReadChanges(volume, checkpoint, range.Value.NextUsn, changed, cancellationToken);
        if (next < 0)
        {
            log.Info("USN journal entries were deleted while reading: full rescan.");
            return null;
        }
        if (changed.Count > MaxChanges)
        {
            log.Info($"{changed.Count:N0} changed records since the scan: a full rescan is faster.");
            return null;
        }

        int recordSize = volume.Data.BytesPerFileRecordSegment;
        var states = new Dictionary<int, RecordState?>();
        foreach (int record in changed.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFetch(volume, record, recordSize, previous.Options.IncludeAlternateStreams, out RecordState? state))
            {
                log.Info($"MFT record {record} uses a non-resident attribute list: full rescan.");
                return null;
            }
            states[record] = state;
        }

        TreeModel old = previous.Model;
        var needed = new HashSet<int>(changed.Keys);
        foreach (RecordState? s in states.Values)
        {
            if (s is not null)
            {
                foreach (var link in s.Links)
                    needed.Add(link.ParentRecord);
            }
        }
        Dictionary<int, List<int>> nodesOf = FindNodes(old, needed);
        bool structural = changed.Values.Any(r => (r & Structural) != 0)
            || states.Any(kv => kv.Value is not null && !nodesOf.ContainsKey(kv.Key));

        TreeModel model;
        int[]? map = null;
        if (!structural)
            model = PatchValues(old, states, nodesOf, previous.Options.HardLinks);
        else
            model = Relayout(old, states, changed, nodesOf, previous.Options.HardLinks, out map);

        (long total, long free) = Volumes.GetSpace(previous.Target.VolumeRoot!);
        var timings = new PhaseTimings();
        timings.Add("usn refresh", clock.Elapsed, $"{changed.Count:N0} changed records, {(structural ? "tree re-laid out" : "ancestors re-aggregated")}");
        var result = new ScanResult
        {
            Model = model,
            ViewRoot = map is null ? previous.ViewRoot : Math.Max(0, map[previous.ViewRoot]),
            Engine = previous.Engine,
            Target = previous.Target,
            Options = previous.Options,
            VolumeTotalBytes = total,
            VolumeFreeBytes = free,
            Media = previous.Media,
            Statistics = previous.Statistics,
            Timings = timings,
            Usn = new UsnCheckpoint(checkpoint.JournalId, next),
            Duration = clock.Elapsed,
            CompletedAt = DateTime.Now,
        };
        log.Info($"USN refresh: {changed.Count:N0} changed records in {clock.ElapsedMilliseconds} ms ({(structural ? "re-layout" : "values only")}).");
        return new UsnRefreshResult { Result = result, Changes = changed.Count, Duration = clock.Elapsed, Structural = structural, NewPositionOfOld = map };
    }

    /// <summary>Collects record → reasons from <paramref name="from"/> to the journal end; returns the next USN, or -1 when entries were lost.</summary>
    private static long ReadChanges(NtfsVolume volume, UsnCheckpoint from, long end, Dictionary<int, uint> changed, CancellationToken cancellationToken)
    {
        const int BufferSize = 64 * 1024;
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        try
        {
            var input = new NtfsNative.READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = from.NextUsn,
                ReasonMask = 0xFFFF_FFFF,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalID = from.JournalId,
            };
            while (input.StartUsn < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!NtfsNative.DeviceIoControl(volume.Handle, NtfsNative.FSCTL_READ_USN_JOURNAL, &input, (uint)sizeof(NtfsNative.READ_USN_JOURNAL_DATA_V0), buffer, BufferSize, out uint returned, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == NtfsNative.ERROR_JOURNAL_ENTRY_DELETED)
                        return -1;
                    throw new System.ComponentModel.Win32Exception(error, "FSCTL_READ_USN_JOURNAL failed");
                }
                long nextUsn = *(long*)buffer;
                int offset = 8;
                while (offset + 60 <= returned)
                {
                    byte* r = buffer + offset;
                    int length = *(int*)r;
                    if (length <= 0)
                        break;
                    if (*(ushort*)(r + 4) == 2)
                    {
                        int record = (int)(*(long*)(r + 8) & MftRecord.RecordNumberMask);
                        uint reason = *(uint*)(r + 40);
                        changed[record] = changed.TryGetValue(record, out uint known) ? known | reason : reason;
                    }
                    offset += length;
                }
                if (returned <= 8 || nextUsn <= input.StartUsn)
                    break;
                input.StartUsn = nextUsn;
            }
            return input.StartUsn;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Current state of a record (null when it is no longer in use); false when it cannot be handled incrementally.</summary>
    private static bool TryFetch(NtfsVolume volume, int record, int recordSize, bool includeStreams, out RecordState? state)
    {
        state = null;
        byte[]? bytes = FetchRaw(volume, record, recordSize);
        if (bytes is null)
            return true;
        var info = default(RecordInfo);
        if (Parse(bytes, ref info) != RecordStatus.Ok || !info.InUse || info.IsExtension)
            return true;

        var s = new RecordState { InUse = true, IsDirectory = info.IsDirectory, Modified = info.Modified };
        AddLinks(bytes, in info, s);
        long dataSize = info.HasData ? info.DataSize : 0, dataAllocated = info.HasData ? info.DataAllocated : 0;
        long wof = info.WofAllocated;
        int streams = info.StreamCount;
        long streamSize = info.StreamSize, streamAllocated = info.StreamAllocated;
        uint attributes = info.Attributes;

        if (info.HasAttributeList)
        {
            // Attributes moved to extension records: read them too (only resident attribute lists).
            List<AttributeListEntry>? entries = ReadAttributeList(bytes);
            if (entries is null)
                return false;
            foreach (long segment in entries.Select(e => e.SegmentRecord).Where(r => r != record).Distinct())
            {
                byte[]? extension = FetchRaw(volume, (int)segment, recordSize);
                var ext = default(RecordInfo);
                if (extension is null || Parse(extension, ref ext) != RecordStatus.Ok || ext.BaseRecord != record)
                    continue;
                AddLinks(extension, in ext, s);
                if (ext.HasData)
                {
                    dataSize += ext.DataSize;
                    dataAllocated += ext.DataAllocated;
                }
                wof += ext.WofAllocated;
                streams += ext.StreamCount;
                streamSize += ext.StreamSize;
                streamAllocated += ext.StreamAllocated;
            }
        }
        var merged = new RecordInfo
        {
            InUse = true,
            IsDirectory = info.IsDirectory,
            Attributes = attributes,
            HasData = dataSize > 0 || info.HasData,
            DataSize = dataSize,
            DataAllocated = dataAllocated,
            HasWof = wof > 0 || info.HasWof,
            WofAllocated = wof,
            StreamCount = streams,
            StreamSize = streamSize,
            StreamAllocated = streamAllocated,
        };
        long parent = s.Links.Count > 0 ? s.Links[0].ParentRecord : -1;
        MftPipeline.ComputeValues(in merged, record, parent, includeStreams, out s.Size, out s.Allocated, out s.Flags);
        state = s;
        return true;
    }

    /// <summary>
    /// Records may come back with or without the update sequence applied. Applying it to an already
    /// fixed-up record is harmless (its stride tails already hold the saved bytes), and a mismatch
    /// means it was applied: parse as is.
    /// </summary>
    private static RecordStatus Parse(byte[] bytes, ref RecordInfo info)
    {
        RecordStatus status = RecordParser.Parse(bytes, ref info);
        return status == RecordStatus.BadFixup ? RecordParser.Parse(bytes, ref info, alreadyFixedUp: true) : status;
    }

    private static void AddLinks(byte[] bytes, in RecordInfo info, RecordState state)
    {
        string? dos = null;
        int dosParent = 0;
        ushort dosSequence = 0;
        for (int i = 0; i < info.NameCount; i++)
        {
            var name = new FileNameView(bytes, info.NameOffsets[i]);
            if (name.ParentRecord > int.MaxValue)
                continue;
            if (name.Namespace == FileNameNamespace.Dos)
            {
                dos ??= name.Name.ToString();
                dosParent = (int)name.ParentRecord;
                dosSequence = name.ParentSequence;
                continue;
            }
            state.Links.Add(((int)name.ParentRecord, name.ParentSequence, name.Name.ToString()));
        }
        if (state.Links.Count == 0 && dos is not null)
            state.Links.Add((dosParent, dosSequence, dos));
    }

    private static List<AttributeListEntry>? ReadAttributeList(byte[] record)
    {
        var reader = new AttributeReader(record);
        while (reader.MoveNext(out AttributeView attribute))
        {
            if (attribute.Type != AttributeType.AttributeList)
                continue;
            if (!attribute.IsResident)
                return null;
            var entries = new List<AttributeListEntry>();
            return AttributeList.TryParse(attribute.Value, entries) ? entries : null;
        }
        return new List<AttributeListEntry>();
    }

    /// <summary>The record as the file system holds it (already fixed up), or null when it is not in use.</summary>
    private static byte[]? FetchRaw(NtfsVolume volume, int record, int recordSize)
    {
        int outputSize = 12 + recordSize;
        byte* output = stackalloc byte[outputSize];
        long input = record;
        if (!NtfsNative.DeviceIoControl(volume.Handle, NtfsNative.FSCTL_GET_NTFS_FILE_RECORD, &input, sizeof(long), output, (uint)outputSize, out uint returned, 0) || returned < 12)
            return null;
        // The file system returns the closest in-use record at or below the one asked for.
        if ((*(long*)output & MftRecord.RecordNumberMask) != record)
            return null;
        int length = Math.Min(*(int*)(output + 8), recordSize);
        return new ReadOnlySpan<byte>(output + 12, length).ToArray();
    }

    /// <summary>Nodes of the given records (primary first), from one pass over the record numbers.</summary>
    private static Dictionary<int, List<int>> FindNodes(TreeModel model, HashSet<int> records)
    {
        var nodes = new Dictionary<int, List<int>>();
        int[] recordNumber = model.RecordNumber!;
        for (int i = 0; i < model.Count; i++)
        {
            int r = recordNumber[i];
            if (r < 0 || !records.Contains(r) || model.HasFlag(i, NodeFlags.Synthetic))
                continue;
            if (!nodes.TryGetValue(r, out List<int>? list))
                nodes[r] = list = new List<int>(1);
            if (model.HasFlag(i, NodeFlags.HardLinkSecondary))
                list.Add(i);
            else
                list.Insert(0, i);
        }
        return nodes;
    }

    /// <summary>Only sizes, attributes or times changed: new value arrays, deltas pushed up to the root.</summary>
    private static TreeModel PatchValues(TreeModel old, Dictionary<int, RecordState?> states, Dictionary<int, List<int>> nodesOf, HardLinkMode mode)
    {
        long[] size = (long[])old.Size.Clone();
        long[] allocated = (long[])old.Allocated.Clone();
        long[] modified = (long[])old.Modified.Clone();
        uint[] flags = (uint[])old.Flags.Clone();
        foreach (var (record, state) in states)
        {
            if (state is null || !nodesOf.TryGetValue(record, out List<int>? nodes))
                continue;
            for (int k = 0; k < nodes.Count; k++)
            {
                int node = nodes[k];
                bool secondary = (flags[node] & (uint)NodeFlags.HardLinkSecondary) != 0;
                flags[node] = (flags[node] & KeptFlags) | state.Flags;
                if (!state.IsDirectory)
                {
                    long newSize = secondary && mode == HardLinkMode.FirstLinkOnly ? 0 : state.Size;
                    long newAllocated = secondary && mode != HardLinkMode.EveryLink ? 0 : state.Allocated;
                    long dSize = newSize - size[node];
                    long dAllocated = newAllocated - allocated[node];
                    for (int x = node; x >= 0; x = old.Parent[x])
                    {
                        size[x] += dSize;
                        allocated[x] += dAllocated;
                    }
                }
                for (int x = node; x >= 0; x = old.Parent[x])
                {
                    if (state.Modified > modified[x])
                        modified[x] = state.Modified;
                }
            }
        }
        return new TreeModel(old.Count, old.Parent, old.SubtreeEnd, size, allocated, flags, modified, old.NameOffset, old.NameLength,
            old.Names, old.RecordNumber, old.RootPath, old.UpCase);
    }

    /// <summary>Structural changes: rebuild the DFS layout from the current nodes plus the changed records.</summary>
    private static TreeModel Relayout(TreeModel old, Dictionary<int, RecordState?> states, Dictionary<int, uint> reasons, Dictionary<int, List<int>> nodesOf, HardLinkMode mode, out int[] newPositionOfOld)
    {
        // NTFS reuses freed records at once: a record deleted in this batch (or whose type changed)
        // now holds a different file. Its old nodes go; the current occupant is a new item.
        bool Replaced(int record, RecordState? state) =>
            nodesOf.TryGetValue(record, out List<int>? nodes) && nodes.Count > 0
            && (state is null || (reasons[record] & ReasonDelete) != 0 || old.IsDirectory(nodes[0]) != state.IsDirectory);

        int n = old.Count;
        var parents = new List<int>(n + states.Count * 2 + 1);
        for (int i = 0; i < n; i++)
            parents.Add(old.HasFlag(i, NodeFlags.Synthetic) ? TreeBuilder.NotANode : i == 0 ? TreeBuilder.MissingParent : old.Parent[i]);

        // Appended nodes.
        var added = new List<(long Size, long Allocated, uint Flags, long Modified, int NameOffset, ushort NameLength, int Record)>();
        var renamed = new Dictionary<int, (int NameOffset, ushort NameLength, uint Flags, long Modified)>();
        NameArena.Writer writer = old.Names.CreateWriter();
        var nodeOfDirectory = new Dictionary<int, int>();
        foreach (var (record, list) in nodesOf)
        {
            if (list.Count > 0 && old.IsDirectory(list[0]))
                nodeOfDirectory[record] = list[0];
        }

        // Removed: every node of a changed record that is gone, and the old nodes of changed files
        // (their names are re-created below, which covers renames, moves and hard-link changes).
        var replaced = new HashSet<int>();
        foreach (var (record, state) in states)
        {
            if (!nodesOf.TryGetValue(record, out List<int>? list))
                continue;
            bool gone = Replaced(record, state);
            if (gone)
                replaced.Add(record);
            if (gone || !state!.IsDirectory)
            {
                foreach (int node in list)
                    parents[node] = TreeBuilder.NotANode;
                if (gone)
                    nodeOfDirectory.Remove(record);
            }
        }

        // Folders first (in dependency order: a new folder may live in another new folder), then files.
        var pending = states.Where(kv => kv.Value is { IsDirectory: true, Links.Count: > 0 }).ToList();
        for (int pass = 0; pending.Count > 0 && pass < 64; pass++)
        {
            var next = new List<KeyValuePair<int, RecordState?>>();
            foreach (var (record, state) in pending)
            {
                var link = state!.Links[0];
                if (!nodeOfDirectory.TryGetValue(link.ParentRecord, out int parent) && pass < 63)
                {
                    next.Add(new(record, state));
                    continue;
                }
                int parentSlot = nodeOfDirectory.TryGetValue(link.ParentRecord, out parent) ? parent : TreeBuilder.MissingParent;
                nodesOf.TryGetValue(record, out List<int>? list);
                bool exists = !replaced.Contains(record) && list is { Count: > 0 };
                if (parentSlot == TreeBuilder.MissingParent && !exists)
                    continue; // parent outside the model (e.g. excluded NTFS metadata): not shown
                int nameOffset = writer.Append(link.Name, out ushort nameLength);
                if (exists)
                {
                    int node = list![0];
                    parents[node] = parentSlot;
                    renamed[node] = (nameOffset, nameLength, (old.Flags[node] & KeptFlags) | state.Flags, Math.Max(state.Modified, old.Modified[node]));
                }
                else
                {
                    int slot = parents.Count;
                    parents.Add(parentSlot);
                    added.Add((0, 0, state.Flags, state.Modified, nameOffset, nameLength, record));
                    nodeOfDirectory[record] = slot;
                }
            }
            pending = next;
        }
        foreach (var (record, state) in states)
        {
            if (state is null || state.IsDirectory)
                continue;
            for (int k = 0; k < state.Links.Count; k++)
            {
                var link = state.Links[k];
                bool secondary = k > 0;
                if (!nodeOfDirectory.TryGetValue(link.ParentRecord, out int parentSlot))
                    continue; // parent outside the model (e.g. excluded NTFS metadata): not shown
                int nameOffset = writer.Append(link.Name, out ushort nameLength);
                long size = secondary && mode == HardLinkMode.FirstLinkOnly ? 0 : state.Size;
                long allocated = secondary && mode != HardLinkMode.EveryLink ? 0 : state.Allocated;
                uint flags = state.Flags | (secondary ? (uint)NodeFlags.HardLinkSecondary : 0);
                parents.Add(parentSlot);
                added.Add((size, allocated, flags, state.Modified, nameOffset, nameLength, record));
            }
        }
        int orphanSlot = parents.Count;
        parents.Add(TreeBuilder.NotANode);
        added.Add((0, 0, (uint)(NodeFlags.Directory | NodeFlags.Synthetic), 0, writer.Append(TreeModel.OrphansName, out ushort orphansLength), orphansLength, -1));

        int[] parentArray = parents.ToArray();
        int slots = parentArray.Length;
        TreeLayout layout = TreeBuilder.Build(parentArray, slots, 0, orphanSlot, ReadOnlySpan<int>.Empty, orderInParents: true);
        int count = layout.NodeCount;
        var newSize = new long[count];
        var newAllocated = new long[count];
        var newModified = new long[count];
        var newFlags = new uint[count];
        var newNameOffset = new int[count];
        var newNameLength = new ushort[count];
        var newRecord = new int[count];
        newPositionOfOld = new int[n];
        Array.Fill(newPositionOfOld, -1);
        int[] oldRecord = old.RecordNumber!;
        for (int pos = 0; pos < count; pos++)
        {
            int s = layout.Order[pos];
            if (s < n)
            {
                newPositionOfOld[s] = pos;
                bool directory = old.IsDirectory(s);
                newSize[pos] = directory ? 0 : old.Size[s];
                newAllocated[pos] = directory ? 0 : old.Allocated[s];
                newModified[pos] = old.Modified[s];
                newFlags[pos] = old.Flags[s] & ~(uint)NodeFlags.Orphan;
                newNameOffset[pos] = old.NameOffset[s];
                newNameLength[pos] = old.NameLength[s];
                newRecord[pos] = oldRecord[s];
                if (renamed.TryGetValue(s, out var r))
                {
                    newNameOffset[pos] = r.NameOffset;
                    newNameLength[pos] = r.NameLength;
                    newFlags[pos] = r.Flags;
                    newModified[pos] = r.Modified;
                }
            }
            else
            {
                var a = added[s - n];
                newSize[pos] = a.Size;
                newAllocated[pos] = a.Allocated;
                newModified[pos] = a.Modified;
                newFlags[pos] = a.Flags;
                newNameOffset[pos] = a.NameOffset;
                newNameLength[pos] = a.NameLength;
                newRecord[pos] = a.Record;
            }
        }
        var model = new TreeModel(count, layout.Parent, layout.SubtreeEnd, newSize, newAllocated, newFlags, newModified, newNameOffset, newNameLength,
            old.Names, newRecord, old.RootPath, old.UpCase);
        if (layout.OrphansPosition >= 0)
        {
            foreach (int c in model.Children(layout.OrphansPosition))
                newFlags[c] |= (uint)NodeFlags.Orphan;
        }
        Aggregator.Aggregate(model);
        return model;
    }
}
