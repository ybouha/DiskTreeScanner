using System.Diagnostics;
using System.Numerics;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Platform;
using DiskTreeScanner.Core.Scanning;
using static DiskTreeScanner.Ntfs.Mft.MftScanEngine;

namespace DiskTreeScanner.Ntfs.Mft;

/// <summary>
/// Turns parsed MFT records into the tree model: merges extension records and hard links,
/// validates parent references, builds the DFS layout, renumbers in place and aggregates.
/// Independent of any volume, so it is unit-tested on synthetic records.
/// </summary>
internal static class MftModelBuilder
{
    public static TreeModel Build(MftLayout layout, MftRawTable raw, MftWorker[] workers, NameArena names, ScanOptions options,
        string rootPath, UpCaseTable upCase, ScanStatistics stats, ActivityLog log, out string breakdown)
    {
        if (!layout.IsInUse(RootRecord))
            throw new InvalidDataException("The root directory record (5) is not in use.");
        int rootSlot = layout.Rank(RootRecord);
        if ((raw.Flags[rootSlot] & MftRawTable.RawValid) == 0 || (raw.Flags[rootSlot] & (uint)NodeFlags.Directory) == 0)
            throw new InvalidDataException("The root directory record (5) is unreadable.");

        long s0 = Stopwatch.GetTimestamp();
        Dictionary<long, StreamFile> streamFiles = CollectStreamFiles(workers);
        List<long> extraOwners = Merge(raw, layout, workers, options, stats, streamFiles);
        int orphanSlot = raw.AddSlot();
        raw.Flags[orphanSlot] = (uint)(NodeFlags.Directory | NodeFlags.Synthetic);
        NameArena.Writer writer = names.CreateWriter();
        raw.NameOffset[orphanSlot] = writer.Append(TreeModel.OrphansName, out raw.NameLength[orphanSlot]);
        raw.NameOffset[rootSlot] = writer.Append(rootPath, out raw.NameLength[rootSlot]);
        long s1 = Stopwatch.GetTimestamp();

        int unnamed = ResolveParents(raw, layout);
        raw.ReleaseSequences();
        if (unnamed > 0)
            log.Warn($"{unnamed:N0} in-use record(s) without any file name were ignored.");
        var excluded = new List<int>();
        if (!options.IncludeNtfsSpecialFiles)
        {
            for (int r = 0; r < FirstUserRecord; r++)
            {
                if (r != RootRecord && layout.IsInUse(r))
                    excluded.Add(layout.Rank(r));
            }
        }
        // Parse-time lists are garbage now (large-object heap, only freed by a full collection):
        // reclaim and decommit them before the tree build reaches the memory peak.
        long g0 = Stopwatch.GetTimestamp();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        long gc = Stopwatch.GetTimestamp() - g0;
        long s2 = Stopwatch.GetTimestamp();
        TreeLayout tree = TreeBuilder.Build(raw.ParentRecord, raw.Count, rootSlot, orphanSlot, excluded.ToArray(), orderInParents: true);
        long s3 = Stopwatch.GetTimestamp();
        TreeModel model = Materialize(raw, layout, tree, extraOwners, orphanSlot, names, rootPath, upCase);
        long s4 = Stopwatch.GetTimestamp();
        stats.OrphanRoots = tree.OrphanRoots;
        stats.OrphanNodes = tree.OrphanNodes;
        stats.ExcludedNodes = tree.ExcludedNodes;
        stats.NameArenaChunks = names.ChunkCount;
        stats.NameArenaBytes = names.AllocatedBytes;
        if (tree.OrphanRoots > 0)
            log.Warn($"{tree.OrphanRoots:N0} item(s) with a missing parent, stale reference or loop were attached to {TreeModel.OrphansName} ({tree.OrphanNodes:N0} nodes).");
        ReclassifyExtendStreams(model, streamFiles, options, stats);
        breakdown = $"{model.Count:N0} nodes, {tree.OrphanRoots:N0} orphan root(s); merge {Ms(s1 - s0)}, parents {Ms(s2 - s1 - gc)}, collect {Ms(gc)}, build ({tree.Breakdown}), in-place DFS renumbering {Ms(s4 - s3)}; {ProcessMemory.Describe()}";
        return model;
    }

    internal static string Ms(long ticks) => $"{ticks * 1000.0 / Stopwatch.Frequency:F0} ms";

    internal static Dictionary<long, StreamFile> CollectStreamFiles(MftWorker[] workers)
    {
        var files = new Dictionary<long, StreamFile>();
        foreach (MftWorker worker in workers)
        {
            foreach (StreamFile file in worker.StreamFiles)
                files[file.Record] = file;
            worker.StreamFiles.Clear();
        }
        return files;
    }

    /// <summary>
    /// Files anywhere in the $Extend tree are NTFS metadata ($RmMetadata\$Repair:$Config,
    /// $TxfLog\$Tops:$T…): their named streams are their content, so their clusters count as
    /// allocation (never their logical size, often sparse), and they are not reported as ADS.
    /// </summary>
    internal static void ReclassifyExtendStreams(TreeModel model, Dictionary<long, StreamFile> streamFiles, ScanOptions options, ScanStatistics stats)
    {
        if (streamFiles.Count == 0 || model.RecordNumber is not { } records)
            return;
        foreach (int c in model.Children(0))
        {
            if (records[c] != ExtendRecord || !model.IsDirectory(c))
                continue;
            for (int i = c + 1; i < model.SubtreeEnd[c]; i++)
            {
                if (!model.HasFlag(i, NodeFlags.HasAlternateStreams) || model.HasFlag(i, NodeFlags.HardLinkSecondary)
                    || !streamFiles.TryGetValue(records[i], out StreamFile file))
                    continue;
                if (options.IncludeAlternateStreams)
                    model.Size[i] -= file.Size;
                else
                    model.Allocated[i] += file.Allocated;
                model.Flags[i] &= ~(uint)NodeFlags.HasAlternateStreams;
                stats.AlternateStreams -= file.Streams;
                stats.AlternateStreamSize -= file.Size;
                stats.AlternateStreamAllocated -= file.Allocated;
            }
        }
    }

    /// <summary>Folds extension records into their base records and turns extra names into hard-link nodes.</summary>
    internal static List<long> Merge(MftRawTable raw, MftLayout layout, MftWorker[] workers, ScanOptions options, ScanStatistics stats, Dictionary<long, StreamFile> streamFiles)
    {
        bool includeStreams = options.IncludeAlternateStreams;

        bool TryBase(long record, ushort expectedSequence, out int slot)
        {
            slot = -1;
            if (!layout.IsInUse(record))
                return false;
            slot = layout.Rank(record);
            return (raw.Flags[slot] & MftRawTable.RawValid) != 0 && (expectedSequence == 0 || raw.Sequence[slot] == expectedSequence);
        }

        foreach (MftWorker worker in workers)
        {
            stats.ExtensionRecords += worker.ExtensionRecords;
            stats.BadRecords += worker.BadRecords;
            stats.MftRecordsInUse += worker.InUse;
            stats.AlternateStreams += worker.Streams;
            stats.AlternateStreamSize += worker.StreamSize;
            stats.AlternateStreamAllocated += worker.StreamAllocated;
            stats.WofCompressedFiles += worker.WofFiles;
            foreach (ExtensionData ext in worker.Extensions)
            {
                if (!TryBase(ext.BaseRecord, ext.BaseSequence, out int b))
                {
                    stats.StaleExtensions++;
                    continue;
                }
                if (ext.HasData)
                {
                    raw.Size[b] += ext.DataSize;
                    raw.Allocated[b] += ext.DataAllocated;
                }
                if (ext.HasWof)
                {
                    raw.Allocated[b] += ext.WofAllocated;
                    if ((raw.Flags[b] & (uint)NodeFlags.WofCompressed) == 0)
                        stats.WofCompressedFiles++;
                    raw.Flags[b] |= (uint)NodeFlags.WofCompressed;
                }
                if (ext.Streams > 0)
                {
                    if (MftPipeline.IsMetadata(ext.BaseRecord, raw.ParentRecord[b]))
                    {
                        if (ext.BaseRecord != MftPipeline.BadClusRecord)
                            raw.Allocated[b] += ext.StreamAllocated;
                        continue;
                    }
                    raw.Flags[b] |= (uint)NodeFlags.HasAlternateStreams;
                    stats.AlternateStreams += ext.Streams;
                    stats.AlternateStreamSize += ext.StreamSize;
                    stats.AlternateStreamAllocated += ext.StreamAllocated;
                    streamFiles.TryGetValue(ext.BaseRecord, out StreamFile known);
                    streamFiles[ext.BaseRecord] = new StreamFile(ext.BaseRecord, known.Streams + ext.Streams, known.Size + ext.StreamSize, known.Allocated + ext.StreamAllocated);
                    if (includeStreams)
                    {
                        raw.Size[b] += ext.StreamSize;
                        raw.Allocated[b] += ext.StreamAllocated;
                    }
                }
            }
        }

        var dosFallbacks = new HashSet<int>(workers.SelectMany(w => w.DosFallbacks));
        var links = new LinkEntry[workers.Sum(w => w.Links.Count)];
        int filled = 0;
        foreach (MftWorker worker in workers)
        {
            worker.Links.CopyTo(links, filled);
            filled += worker.Links.Count;
            worker.Links.Clear();
            worker.Links.TrimExcess();
            worker.Extensions.Clear();
            worker.Extensions.TrimExcess();
        }
        // Deterministic whatever the thread schedule: links in record order, then attribute order.
        var keys = new long[links.Length];
        for (int i = 0; i < links.Length; i++)
            keys[i] = links[i].Order;
        Array.Sort(keys, links);
        raw.EnsureCapacity(raw.Count + links.Length + 1); // one exact resize at most

        var extraOwners = new List<long>(links.Length);
        foreach (LinkEntry link in links)
        {
            if (!TryBase(link.OwnerRecord, link.OwnerSequence, out int owner))
            {
                stats.StaleExtensions++;
                continue;
            }
            if (raw.ParentRecord[owner] == MftRawTable.AwaitingName || dosFallbacks.Remove(owner))
            {
                raw.ParentRecord[owner] = link.ParentRecord;
                raw.ParentSequence[owner] = link.ParentSequence;
                raw.NameOffset[owner] = link.NameOffset;
                raw.NameLength[owner] = link.NameLength;
                continue;
            }
            int slot = raw.AddSlot();
            raw.ParentRecord[slot] = link.ParentRecord;
            raw.ParentSequence[slot] = link.ParentSequence;
            raw.NameOffset[slot] = link.NameOffset;
            raw.NameLength[slot] = link.NameLength;
            raw.Flags[slot] = raw.Flags[owner] | (uint)NodeFlags.HardLinkSecondary;
            raw.Modified[slot] = raw.Modified[owner];
            raw.Size[slot] = options.HardLinks == HardLinkMode.FirstLinkOnly ? 0 : raw.Size[owner];
            raw.Allocated[slot] = options.HardLinks == HardLinkMode.EveryLink ? raw.Allocated[owner] : 0;
            extraOwners.Add(link.OwnerRecord);
            stats.HardLinkExtraNames++;
            stats.HardLinkDuplicateSize += raw.Size[owner];
            stats.HardLinkDuplicateAllocated += raw.Allocated[owner];
        }
        return extraOwners;
    }

    /// <summary>Replaces, in place, each slot's parent record number by its parent slot (validated).</summary>
    internal static int ResolveParents(MftRawTable raw, MftLayout layout)
    {
        int[] parents = raw.ParentRecord;
        Span<ushort> sequence = raw.Sequence;
        Span<ushort> parentSequence = raw.ParentSequence;
        uint[] flags = raw.Flags;
        int unnamed = 0;
        for (int s = 0; s < raw.Count; s++)
        {
            int record = parents[s];
            if (record < 0 || (flags[s] & MftRawTable.RawValid) == 0)
            {
                if (record == MftRawTable.AwaitingName)
                    unnamed++;
                parents[s] = TreeBuilder.NotANode;
                continue;
            }
            if (!layout.IsInUse(record))
            {
                parents[s] = TreeBuilder.MissingParent;
                continue;
            }
            int p = layout.Rank(record);
            ushort expected = parentSequence[s];
            bool valid = (flags[p] & MftRawTable.RawValid) != 0
                && (flags[p] & (uint)NodeFlags.Directory) != 0
                && (expected == 0 || sequence[p] == expected); // a reused record has a new sequence number
            parents[s] = valid ? p : TreeBuilder.MissingParent;
        }
        return unnamed;
    }

    /// <summary>Permutes the raw arrays in place into DFS order; they become the model.</summary>
    internal static unsafe TreeModel Materialize(MftRawTable raw, MftLayout layout, TreeLayout tree, List<long> extraOwners, int orphanSlot, NameArena names, string rootPath, UpCaseTable upCase)
    {
        int count = raw.Count;
        int n = tree.NodeCount;
        int[] order = tree.Order; // aliases raw.ParentRecord

        // Complete the permutation with the slots that are not nodes.
        var numbered = new ulong[(count + 63) >> 6];
        for (int i = 0; i < n; i++)
            numbered[order[i] >> 6] |= 1UL << order[i];
        int k = n;
        for (int s = 0; s < count; s++)
        {
            if ((numbered[s >> 6] & (1UL << s)) == 0)
                order[k++] = s;
        }

        // Gather each array through one 32-bit native scratch (parallel, sequential writes), copy back.
        // 64-bit arrays are permuted as two independent 32-bit halves: the low halves are gathered
        // and written back while the high halves stay untouched, then the high halves. So the
        // scratch is half the size of a long[] and the peak does not grow.
        using (var scratch = new NativeArray<uint>(count, zeroed: false))
        {
            Gather64(order, count, raw.Size, scratch.Pointer);
            Gather64(order, count, raw.Allocated, scratch.Pointer);
            Gather64(order, count, raw.Modified, scratch.Pointer);
            Gather(order, count, raw.Flags, scratch.Pointer);
            Gather(order, count, raw.NameOffset, scratch.Pointer);
            Gather(order, count, raw.NameLength, scratch.Pointer);
        }

        // The order array becomes the record number of each node.
        using (var recordOfSlot = new NativeArray<int>(count, zeroed: false))
        {
            int* map = recordOfSlot.Pointer;
            int slot = 0;
            ulong[] bitmap = layout.Bitmap;
            for (int w = 0; w < bitmap.Length; w++)
            {
                ulong bits = bitmap[w];
                while (bits != 0)
                {
                    map[slot++] = w * 64 + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                }
            }
            for (int j = 0; j < extraOwners.Count; j++)
                map[raw.RecordSlots + j] = (int)extraOwners[j];
            map[orphanSlot] = -1;
            for (int i = 0; i < n; i++)
                order[i] = map[order[i]];
        }
        int[] recordNumber = order;

        uint[] flags = raw.Flags;
        for (int i = 0; i < n; i++)
            flags[i] &= ~MftRawTable.RawValid;
        var model = new TreeModel(n, tree.Parent, tree.SubtreeEnd, raw.Size, raw.Allocated, flags, raw.Modified,
            raw.NameOffset, raw.NameLength, names, recordNumber, rootPath, upCase);

        if (tree.OrphansPosition >= 0)
        {
            foreach (int c in model.Children(tree.OrphansPosition))
                flags[c] |= (uint)NodeFlags.Orphan;
        }
        for (int i = 0; i < n; i++)
        {
            int r = recordNumber[i];
            if (r >= 0 && r < FirstUserRecord && r != RootRecord)
            {
                flags[i] |= (uint)(NodeFlags.NtfsSpecial | NodeFlags.Hidden | NodeFlags.System);
                if (r == ExtendRecord && model.IsDirectory(i))
                {
                    for (int d = i + 1; d < model.SubtreeEnd[i]; d++)
                        flags[d] |= (uint)NodeFlags.NtfsSpecial;
                }
            }
        }
        return model;
    }

    private const int GatherChunk = 1 << 16;

    /// <summary><c>array[i] ← array[order[i]]</c> for an array of at most 4-byte elements.</summary>
    internal static unsafe void Gather<T>(int[] order, int count, T[] array, uint* scratch) where T : unmanaged
    {
        nint buffer = (nint)scratch;
        Parallel.For(0, (count + GatherChunk - 1) / GatherChunk, c =>
        {
            T* target = (T*)buffer;
            int end = Math.Min(count, (c + 1) * GatherChunk);
            for (int i = c * GatherChunk; i < end; i++)
                target[i] = array[order[i]];
        });
        new ReadOnlySpan<T>((void*)buffer, count).CopyTo(array);
    }

    /// <summary><c>array[i] ← array[order[i]]</c> for a long[], one 32-bit half at a time.</summary>
    internal static unsafe void Gather64(int[] order, int count, long[] array, uint* scratch)
    {
        fixed (long* pinned = array)
        {
            nint halves = (nint)pinned;
            nint buffer = (nint)scratch;
            for (int half = 0; half < 2; half++)
            {
                int h = half;
                Parallel.For(0, (count + GatherChunk - 1) / GatherChunk, c =>
                {
                    uint* source = (uint*)halves;
                    uint* target = (uint*)buffer;
                    int end = Math.Min(count, (c + 1) * GatherChunk);
                    for (int i = c * GatherChunk; i < end; i++)
                        target[i] = source[2 * (long)order[i] + h];
                });
                uint* write = (uint*)halves;
                uint* read = (uint*)buffer;
                for (int i = 0; i < count; i++)
                    write[2 * (long)i + half] = read[i];
            }
        }
    }

    internal static void CountSpecial(TreeModel model, ScanStatistics stats)
    {
        for (int i = 0; i < model.Count; i++)
        {
            if (model.HasFlag(i, NodeFlags.NtfsSpecial) && !model.IsDirectory(i))
            {
                stats.SpecialFilesSize += model.Size[i];
                stats.SpecialFilesAllocated += model.Allocated[i];
            }
        }
    }
}
