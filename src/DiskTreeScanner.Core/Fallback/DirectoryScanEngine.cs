using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DiskTreeScanner.Core.Formatting;
using DiskTreeScanner.Core.Interop;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Platform;
using DiskTreeScanner.Core.Scanning;

namespace DiskTreeScanner.Core.Fallback;

/// <summary>
/// Engine 2: lists directories with NtQueryDirectoryFile (FileIdBothDirectoryInformation, 128 KiB
/// buffer), falling back to FindFirstFileExW (FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH). Works on
/// any file system: NTFS without admin rights, ReFS, exFAT/FAT, network shares, WSL (\\wsl$).
/// Reparse points (junctions, symbolic links, mount points) are never followed, so nothing is
/// counted twice. It fills the same model as the MFT engine.
/// </summary>
public sealed unsafe class DirectoryScanEngine : IScanEngine
{
    private const int QueryBufferBytes = 128 * 1024;
    private const uint CloudPlaceholder = (uint)(NodeFlags.RecallOnDataAccess | NodeFlags.RecallOnOpen);

    /// <summary>Virtual or mounted folders of a WSL distribution root that are not part of it.</summary>
    private static readonly HashSet<string> WslRootExclusions = new(StringComparer.Ordinal) { "proc", "sys", "dev", "run" };

    public string Name => "Directory";

    public EngineProbe Probe(ScanTargetInfo target, ScanOptions options)
    {
        string reason = target.Kind switch
        {
            DriveKind.Network => "network share",
            DriveKind.Wsl => $"WSL distribution {target.WslDistribution}",
            _ when options.ForceDirectoryScan => "directory scan forced by the options",
            _ when !target.IsNtfs => $"{(target.FileSystem.Length > 0 ? target.FileSystem : "non-NTFS")} volume",
            _ when !target.IsElevated => "not elevated",
            _ => "fallback",
        };
        return new EngineProbe(true, reason);
    }

    public ScanResult Scan(ScanTargetInfo target, ScanOptions options, ScanProgress progress, ActivityLog log, CancellationToken cancellationToken)
    {
        var timings = new PhaseTimings();
        var stats = new ScanStatistics();
        long t = Stopwatch.GetTimestamp();

        int threads = options.DirectoryParallelism > 0
            ? options.DirectoryParallelism
            : target.Kind switch
            {
                DriveKind.Network => 8,
                DriveKind.Wsl => 4, // the 9P server gains nothing from more
                _ => Math.Clamp(Environment.ProcessorCount, 4, 32),
            };
        if (target.IsElevated && Elevation.TryEnablePrivilege("SeBackupPrivilege"))
            log.Info("SeBackupPrivilege enabled: folders protected by their ACL can be listed.");

        var context = new Context(target, options, progress, new ChunkedNodeTable(), new NameArena(), threads);
        ChunkedNodeTable table = context.Table;
        int rootSlot = table.Reserve(2);
        int orphanSlot = rootSlot + 1;
        NameArena.Writer rootWriter = context.Names.CreateWriter();
        table.Parent(rootSlot) = -1;
        table.Flags(rootSlot) = (uint)NodeFlags.Directory | RootAttributes(target.Path, out long rootModified);
        table.Modified(rootSlot) = rootModified;
        table.NameOffset(rootSlot) = rootWriter.Append(target.Path, out table.NameLength(rootSlot));
        table.Parent(orphanSlot) = TreeBuilder.NotANode;
        table.Flags(orphanSlot) = (uint)(NodeFlags.Directory | NodeFlags.Synthetic);
        table.NameOffset(orphanSlot) = rootWriter.Append(TreeModel.OrphansName, out table.NameLength(orphanSlot));

        progress.Phase = "Listing folders";
        var scheduler = new WorkStealingScheduler(threads);
        context.Scheduler = scheduler;
        var workers = new Worker[threads];
        try
        {
            scheduler.Run(rootSlot, (w, slot) => (workers[w] ??= new Worker(context)).List(w, slot), cancellationToken);
        }
        finally
        {
            foreach (Worker? worker in workers)
                worker?.Dispose();
        }
        int count = table.Count;
        stats.DirectoriesListed = context.Directories;
        stats.AccessDenied = context.Denied;
        t = timings.Mark("listing", t, $"{count - 2:N0} entries in {context.Directories:N0} folders on {threads} threads, {context.Denied:N0} not accessible; {ProcessMemory.Describe()}");
        if (context.Denied > 0)
            log.Warn($"{context.Denied:N0} folder(s) could not be listed (access denied or removed during the scan).");
        if (context.WslSkipped.Count > 0)
            log.Info($"WSL: skipped {string.Join(", ", context.WslSkipped)} (virtual or Windows-mounted folders).");

        if (target.IsLocal && target.IsNtfs)
            ResolveHardLinks(table, count, options.HardLinks, stats);
        table.ReleaseFileIds();

        progress.Phase = "Building tree";
        var parents = new int[count];
        for (int s = 0; s < count; s++)
            parents[s] = table.Parent(s);
        parents[rootSlot] = TreeBuilder.MissingParent;
        TreeLayout layout = TreeBuilder.Build(parents, count, rootSlot, orphanSlot, ReadOnlySpan<int>.Empty, orderInParents: true);
        TreeModel model = Gather(table, layout, context.Names, target.Path);
        stats.OrphanRoots = layout.OrphanRoots;
        stats.OrphanNodes = layout.OrphanNodes;
        stats.NameArenaChunks = context.Names.ChunkCount;
        stats.NameArenaBytes = context.Names.AllocatedBytes;
        t = timings.Mark("tree", t, $"{model.Count:N0} nodes, DFS preorder; {ProcessMemory.Describe()}");

        progress.Phase = "Aggregating";
        Aggregator.Aggregate(model);
        timings.Mark("aggregation", t, ProcessMemory.Describe());

        (long total, long free) = Volumes.GetSpace(target.Path.EndsWith('\\') ? target.Path : target.Path + @"\");
        var result = new ScanResult
        {
            Model = model,
            Engine = Name,
            Target = target,
            Options = options,
            VolumeTotalBytes = total,
            VolumeFreeBytes = free,
            Media = target.VolumeDevicePath is { } device ? Volumes.GetMediaKind(device) : MediaKind.Unknown,
            Statistics = stats,
            Timings = timings,
        };
        log.Info($"Directory scan: {model.ItemCount(0):N0} items in {context.Directories:N0} folders, {Format.Bytes(model.Size[0])}.");
        return result;
    }

    private static uint RootAttributes(string path, out long modified)
    {
        modified = 0;
        try
        {
            var info = new DirectoryInfo(path);
            modified = info.LastWriteTimeUtc.ToFileTimeUtc();
            return (uint)info.Attributes & (uint)NodeFlags.AttributeMask & ~(uint)NodeFlags.ReparsePoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Directory listings do not give the link count, so hard links are found by file id
    /// (unique per NTFS volume): every id seen more than once is a hard-linked file. The first
    /// link in scan order keeps the size, as in the MFT engine.
    /// </summary>
    private static void ResolveHardLinks(ChunkedNodeTable table, int count, HardLinkMode mode, ScanStatistics stats)
    {
        int files = 0;
        for (int s = 0; s < count; s++)
        {
            if ((table.Flags(s) & (uint)NodeFlags.Directory) == 0 && table.FileId(s) != 0)
                files++;
        }
        var ids = new long[files];
        var slots = new int[files];
        int k = 0;
        for (int s = 0; s < count; s++)
        {
            if ((table.Flags(s) & (uint)NodeFlags.Directory) == 0 && table.FileId(s) != 0)
            {
                ids[k] = table.FileId(s);
                slots[k++] = s;
            }
        }
        Array.Sort(ids, slots);
        for (int i = 0; i < files;)
        {
            int j = i + 1;
            while (j < files && ids[j] == ids[i])
                j++;
            if (j - i > 1)
            {
                int first = slots[i];
                for (int m = i + 1; m < j; m++)
                    first = Math.Min(first, slots[m]);
                for (int m = i; m < j; m++)
                {
                    int s = slots[m];
                    if (s == first)
                        continue;
                    table.Flags(s) |= (uint)NodeFlags.HardLinkSecondary;
                    stats.HardLinkExtraNames++;
                    stats.HardLinkDuplicateSize += table.Size(s);
                    stats.HardLinkDuplicateAllocated += table.Allocated(s);
                    if (mode == HardLinkMode.FirstLinkOnly)
                        table.Size(s) = 0;
                    if (mode != HardLinkMode.EveryLink)
                        table.Allocated(s) = 0;
                }
            }
            i = j;
        }
    }

    /// <summary>Flattens the chunked table into DFS order, one column at a time, freeing each column once copied.</summary>
    private static TreeModel Gather(ChunkedNodeTable table, TreeLayout layout, NameArena names, string rootPath)
    {
        int n = layout.NodeCount;
        int[] order = layout.Order;
        long[] size = table.GatherAndRelease<long>(ChunkedNodeTable.Column.Size, order, n);
        long[] allocated = table.GatherAndRelease<long>(ChunkedNodeTable.Column.Allocated, order, n);
        long[] modified = table.GatherAndRelease<long>(ChunkedNodeTable.Column.Modified, order, n);
        uint[] flags = table.GatherAndRelease<uint>(ChunkedNodeTable.Column.Flags, order, n);
        int[] nameOffset = table.GatherAndRelease<int>(ChunkedNodeTable.Column.NameOffset, order, n);
        ushort[] nameLength = table.GatherAndRelease<ushort>(ChunkedNodeTable.Column.NameLength, order, n);
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var model = new TreeModel(n, layout.Parent, layout.SubtreeEnd, size, allocated, flags, modified, nameOffset, nameLength,
            names, null, rootPath, UpCaseTable.Invariant);
        // Folders carry no own size (as in the MFT engine): only their content is summed.
        Aggregator.ResetDirectories(model);
        if (layout.OrphansPosition >= 0)
        {
            foreach (int c in model.Children(layout.OrphansPosition))
                flags[c] |= (uint)NodeFlags.Orphan;
        }
        return model;
    }

    /// <summary>Shared state of one scan.</summary>
    private sealed class Context
    {
        public Context(ScanTargetInfo target, ScanOptions options, ScanProgress progress, ChunkedNodeTable table, NameArena names, int threads)
        {
            Target = target;
            Options = options;
            Progress = progress;
            Table = table;
            Names = names;
            Threads = threads;
            Prefix = ToNtPath(target.Path);
            ClusterSize = target.VolumeRoot is { } root && target.IsLocal ? Volumes.GetClusterSize(root) : 4096;
            IsNtfs = target.IsLocal && target.IsNtfs;
            IsWslRoot = target.Kind == DriveKind.Wsl && target.IsVolumeRoot;
        }

        public bool IsNtfs { get; }

        public ScanTargetInfo Target { get; }
        public ScanOptions Options { get; }
        public ScanProgress Progress { get; }
        public ChunkedNodeTable Table { get; }
        public NameArena Names { get; }
        public int Threads { get; }
        public string Prefix { get; }
        public int ClusterSize { get; }
        public bool IsWslRoot { get; }
        public WorkStealingScheduler Scheduler { get; set; } = null!;
        public volatile int WslMountSlot = -1;
        public long Directories;
        public long Denied;
        public List<string> WslSkipped { get; } = new();

        private static string ToNtPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + path[2..];
            return @"\\?\" + path;
        }
    }

    private readonly record struct Entry(int NameOffset, ushort NameLength, uint Attributes, long Size, long Allocated, long Modified, long FileId, uint ReparseTag)
    {
        /// <summary>
        /// Folders to list. Name surrogates (junctions, symbolic links, mount points) point elsewhere and
        /// are not followed, so nothing is counted twice; other reparse folders (cloud files, AppX/WCI
        /// container layers) are real folders with real content and are listed.
        /// </summary>
        public bool IsListableDirectory =>
            (Attributes & (uint)NodeFlags.Directory) != 0
            && ((Attributes & (uint)NodeFlags.ReparsePoint) == 0 || (ReparseTag & NameSurrogateBit) == 0);
    }

    private const uint NameSurrogateBit = 0x2000_0000;

    /// <summary>Per-thread buffers: the NtQueryDirectoryFile buffer, a path buffer and a name writer.</summary>
    private sealed class Worker : IDisposable
    {
        private readonly Context _context;
        private readonly NameArena.Writer _names;
        private readonly byte* _buffer;
        private readonly char[] _path = new char[33_000];
        private readonly List<int> _chain = new(64);
        private readonly List<Entry> _entries = new(256);
        private bool _useFindFirst;

        public Worker(Context context)
        {
            _context = context;
            _names = context.Names.CreateWriter();
            _buffer = (byte*)NativeMemory.AlignedAlloc(QueryBufferBytes, 8);
        }

        public void Dispose() => NativeMemory.AlignedFree(_buffer);

        public void List(int worker, int slot)
        {
            ChunkedNodeTable table = _context.Table;
            int length = BuildPath(slot);
            _entries.Clear();
            bool listed = _useFindFirst ? ListWithFindFirst(length) : ListWithNtQuery(length);
            Interlocked.Increment(ref _context.Directories);
            _context.Progress.AddDirectories(1);
            if (!listed)
            {
                table.Flags(slot) |= (uint)NodeFlags.AccessDenied;
                Interlocked.Increment(ref _context.Denied);
                return;
            }
            if (_entries.Count == 0)
                return;

            int first = table.Reserve(_entries.Count);
            bool wslRoot = _context.IsWslRoot && slot == 0;
            for (int k = 0; k < _entries.Count; k++)
            {
                Entry e = _entries[k];
                int s = first + k;
                table.Parent(s) = slot;
                table.NameOffset(s) = e.NameOffset;
                table.NameLength(s) = e.NameLength;
                table.Modified(s) = e.Modified;
                table.FileId(s) = e.FileId;
                bool directory = (e.Attributes & (uint)NodeFlags.Directory) != 0;
                table.Flags(s) = e.Attributes & (uint)NodeFlags.AttributeMask;
                if (!directory)
                {
                    table.Size(s) = e.Size;
                    // Cloud placeholders hold no local data. On NTFS a real allocation is a whole number
                    // of clusters; anything else is a small file stored inside its MFT record (the listing
                    // reports its size rounded to 8 bytes): no cluster used, as the MFT engine counts it.
                    bool noClusters = (e.Attributes & CloudPlaceholder) != 0 || (_context.IsNtfs && e.Allocated % _context.ClusterSize != 0);
                    table.Allocated(s) = noClusters ? 0 : e.Allocated;
                }
                if (wslRoot && directory && _context.Names.GetString(e.NameOffset, e.NameLength) == "mnt")
                    _context.WslMountSlot = s;
            }
            _context.Progress.AddItems(_entries.Count);
            // Publish after the entries are written: workers read them to build child paths.
            for (int k = 0; k < _entries.Count; k++)
            {
                if (_entries[k].IsListableDirectory)
                    _context.Scheduler.Push(worker, first + k);
            }
        }

        private int BuildPath(int slot)
        {
            ChunkedNodeTable table = _context.Table;
            _chain.Clear();
            for (int x = slot; x > 0; x = table.Parent(x))
                _chain.Add(x);
            string prefix = _context.Prefix;
            prefix.CopyTo(_path);
            int length = prefix.Length;
            for (int i = _chain.Count - 1; i >= 0; i--)
            {
                int x = _chain[i];
                if (_path[length - 1] != '\\')
                    _path[length++] = '\\';
                length += _context.Names.GetChars(table.NameOffset(x), table.NameLength(x), _path.AsSpan(length));
            }
            return length;
        }

        private bool ListWithNtQuery(int pathLength)
        {
            _path[pathLength] = '\0';
            SafeFileHandle handle;
            fixed (char* p = _path)
            {
                handle = Win32.CreateFile(p, Win32.FILE_LIST_DIRECTORY | Win32.SYNCHRONIZE,
                    Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, 0, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, 0);
            }
            using (handle)
            {
                if (handle.IsInvalid)
                    return false;
                bool restart = true;
                while (true)
                {
                    Win32.IO_STATUS_BLOCK status;
                    int result = Win32.NtQueryDirectoryFile(handle, 0, 0, 0, &status, _buffer, QueryBufferBytes,
                        Win32.FileIdBothDirectoryInformation, 0, null, restart ? (byte)1 : (byte)0);
                    if (result == Win32.STATUS_NO_MORE_FILES)
                        return true;
                    if (result is Win32.STATUS_INVALID_INFO_CLASS or Win32.STATUS_INVALID_PARAMETER or Win32.STATUS_NOT_SUPPORTED)
                    {
                        _useFindFirst = true; // file system without this class (some redirectors)
                        _entries.Clear();
                        return ListWithFindFirst(pathLength);
                    }
                    if (result < 0)
                        return !restart; // error on the first call: not listable
                    restart = false;
                    ParseEntries();
                }
            }
        }

        private void ParseEntries()
        {
            byte* p = _buffer;
            byte* end = _buffer + QueryBufferBytes;
            while (p + 104 <= end)
            {
                uint next = *(uint*)p;
                uint nameBytes = *(uint*)(p + 60);
                if (p + 104 + nameBytes > end)
                    break;
                var name = new ReadOnlySpan<char>(p + 104, (int)(nameBytes / 2));
                if (!IsDotEntry(name))
                {
                    uint attributes = *(uint*)(p + 56);
                    // EaSize holds the reparse tag when the entry is a reparse point.
                    uint reparseTag = (attributes & (uint)NodeFlags.ReparsePoint) != 0 ? *(uint*)(p + 64) : 0;
                    AddEntry(name, attributes, *(long*)(p + 40), *(long*)(p + 48), *(long*)(p + 24), *(long*)(p + 96), reparseTag);
                }
                if (next == 0)
                    break;
                p += next;
            }
        }

        private bool ListWithFindFirst(int pathLength)
        {
            _path[pathLength] = '\\';
            _path[pathLength + 1] = '*';
            _path[pathLength + 2] = '\0';
            if (_path[pathLength - 1] == '\\')
            {
                _path[pathLength] = '*';
                _path[pathLength + 1] = '\0';
            }
            Win32.WIN32_FIND_DATAW data;
            nint find;
            fixed (char* p = _path)
                find = Win32.FindFirstFileEx(p, Win32.FindExInfoBasic, &data, Win32.FindExSearchNameMatch, 0, Win32.FIND_FIRST_EX_LARGE_FETCH);
            if (find == -1)
                return Marshal.GetLastWin32Error() is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_NO_MORE_FILES;
            try
            {
                do
                {
                    var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data.cFileName);
                    if (IsDotEntry(name))
                        continue;
                    long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    long allocated = (size + _context.ClusterSize - 1) / _context.ClusterSize * _context.ClusterSize;
                    uint reparseTag = (data.dwFileAttributes & (uint)NodeFlags.ReparsePoint) != 0 ? data.dwReserved0 : 0;
                    AddEntry(name, data.dwFileAttributes, size, allocated, data.ftLastWriteTime.Value, 0, reparseTag);
                }
                while (Win32.FindNextFile(find, &data));
            }
            finally
            {
                Win32.FindClose(find);
            }
            return true;
        }

        private void AddEntry(ReadOnlySpan<char> name, uint attributes, long size, long allocated, long modified, long fileId, uint reparseTag)
        {
            if ((attributes & (uint)NodeFlags.Directory) != 0 && _context.IsWslRoot)
            {
                if (_chain.Count == 0 && WslRootExclusions.Contains(name.ToString()))
                {
                    lock (_context.WslSkipped)
                        _context.WslSkipped.Add("/" + name.ToString());
                    return;
                }
                if (_chain.Count == 1 && _chain[0] == _context.WslMountSlot && IsWindowsMount(name))
                {
                    lock (_context.WslSkipped)
                        _context.WslSkipped.Add("/mnt/" + name.ToString());
                    return;
                }
            }
            int offset = _names.Append(name, out ushort length);
            _entries.Add(new Entry(offset, length, attributes, size, allocated, modified, fileId, reparseTag));
        }

        private static bool IsWindowsMount(ReadOnlySpan<char> name) =>
            (name.Length == 1 && char.IsAsciiLetterLower(name[0])) || name.SequenceEqual("wsl") || name.SequenceEqual("wslg");

        private static bool IsDotEntry(ReadOnlySpan<char> name) => name is "." or "..";
    }
}
