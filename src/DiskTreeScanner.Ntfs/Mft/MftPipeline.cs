using System.Diagnostics;
using System.Threading.Channels;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Records;

namespace DiskTreeScanner.Ntfs.Mft;

/// <summary>Data found in an extension record, merged into its base record after parsing.</summary>
internal readonly record struct ExtensionData(
    long BaseRecord,
    ushort BaseSequence,
    bool HasData,
    long DataSize,
    long DataAllocated,
    bool HasWof,
    long WofAllocated,
    int Streams,
    long StreamSize,
    long StreamAllocated);

/// <summary>A $FILE_NAME that is not the primary name of its record (hard link, or name held by an extension record).</summary>
internal readonly record struct LinkEntry(
    long OwnerRecord,
    ushort OwnerSequence,
    int ParentRecord,
    ushort ParentSequence,
    int NameOffset,
    ushort NameLength,
    long Order);

/// <summary>Named streams of one file.</summary>
internal readonly record struct StreamFile(long Record, int Streams, long Size, long Allocated);

/// <summary>State owned by one parser thread (no sharing, no locks).</summary>
internal sealed class MftWorker
{
    public MftWorker(NameArena names) => Names = names.CreateWriter();

    public NameArena.Writer Names { get; }

    public List<ExtensionData> Extensions { get; } = new();

    public List<LinkEntry> Links { get; } = new();

    /// <summary>Slots whose primary name is a DOS-only name (replaced if an extension record has a long name).</summary>
    public List<int> DosFallbacks { get; } = new();

    /// <summary>Base records with named streams counted as ADS (rare); re-examined once the $Extend subtree is known.</summary>
    public List<StreamFile> StreamFiles { get; } = new();

    public long InUse;
    public long ExtensionRecords;
    public long BadRecords;
    public long Streams;
    public long StreamSize;
    public long StreamAllocated;
    public long WofFiles;
    public long ParseTicks;
}

internal sealed class PipelineResult
{
    public required MftWorker[] Workers { get; init; }
    public long BytesRead { get; init; }
    public long BytesSkipped { get; init; }
    public int Blocks { get; init; }
    public int QueueDepth { get; init; }
    public int BufferCount { get; init; }
    public TimeSpan ReadTime { get; init; }
    public TimeSpan StallTime { get; init; }
    public TimeSpan WallTime { get; init; }
}

/// <summary>
/// Producer / consumers. The calling thread is the only reader: it keeps several overlapped reads in
/// flight (queue depth) into a small pool of aligned buffers and hands each completed block to a
/// bounded channel; N parser tasks consume blocks and return buffers to the pool. Parsing is several
/// times faster than the disk, so the reader practically never waits (stall time is measured).
/// </summary>
internal static class MftPipeline
{
    public const int BadClusRecord = 8;

    public static PipelineResult Run(
        NtfsVolume volume,
        MftLayout layout,
        MftRawTable raw,
        NameArena names,
        ScanOptions options,
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        int blockBytes = Math.Clamp(options.MftBlockMegabytes, 4, 16) << 20;
        int parsers = options.MftParserThreads > 0 ? options.MftParserThreads : Math.Max(1, Environment.ProcessorCount - 1);
        int queueDepth = Math.Clamp(options.MftQueueDepth, 1, 32);
        // One parser thread alone almost keeps up with the disk, so one spare buffer is enough.
        int bufferCount = options.MftBufferCount > 0 ? Math.Max(options.MftBufferCount, 2) : queueDepth + 1;

        List<ReadBlock> blocks = layout.PlanReads(blockBytes, minimumGapBytes: 256 * 1024, out long bytesToRead);
        progress.BytesTotal = bytesToRead;

        var free = Channel.CreateBounded<AlignedBuffer>(bufferCount);
        var filled = Channel.CreateBounded<(ReadBlock Block, AlignedBuffer Buffer)>(new BoundedChannelOptions(bufferCount)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var buffers = new AlignedBuffer[bufferCount];
        var workers = new MftWorker[parsers];
        var tasks = new Task[parsers];
        var inflight = new Queue<InFlight>();
        var spareReads = new Stack<AsyncRead>();
        var allReads = new List<AsyncRead>();
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool includeStreams = options.IncludeAlternateStreams;
        long start = Stopwatch.GetTimestamp();
        long readerStart = 0;
        long readerEnd = 0;
        long stallTicks = 0;

        try
        {
            for (int i = 0; i < bufferCount; i++)
            {
                buffers[i] = new AlignedBuffer(blockBytes);
                free.Writer.TryWrite(buffers[i]);
            }
            for (int w = 0; w < parsers; w++)
            {
                var worker = workers[w] = new MftWorker(names);
                tasks[w] = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var (block, buffer) in filled.Reader.ReadAllAsync(failure.Token).ConfigureAwait(false))
                        {
                            long t0 = Stopwatch.GetTimestamp();
                            ParseBlock(block, buffer, worker, layout, raw, includeStreams);
                            worker.ParseTicks += Stopwatch.GetTimestamp() - t0;
                            free.Writer.TryWrite(buffer);
                            progress.AddItems(block.RecordCount);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failure.Cancel(); // unblock the reader, which may be waiting for a buffer
                        throw;
                    }
                });
            }

            readerStart = Stopwatch.GetTimestamp();
            Microsoft.Win32.SafeHandles.SafeFileHandle handle = volume.AsyncHandle;
            foreach (ReadBlock block in blocks)
            {
                failure.Token.ThrowIfCancellationRequested();
                AlignedBuffer? buffer;
                while (!free.Reader.TryRead(out buffer))
                {
                    if (inflight.Count > 0)
                    {
                        Complete(inflight.Dequeue(), handle, spareReads, filled, progress, failure.Token);
                        continue;
                    }
                    long waitStart = Stopwatch.GetTimestamp();
                    buffer = free.Reader.ReadAsync(failure.Token).AsTask().GetAwaiter().GetResult();
                    stallTicks += Stopwatch.GetTimestamp() - waitStart;
                    break;
                }
                inflight.Enqueue(Issue(block, buffer!, handle, spareReads, allReads));
                while (inflight.Count >= queueDepth)
                    Complete(inflight.Dequeue(), handle, spareReads, filled, progress, failure.Token);
            }
            while (inflight.Count > 0)
                Complete(inflight.Dequeue(), handle, spareReads, filled, progress, failure.Token);
            readerEnd = Stopwatch.GetTimestamp();
            filled.Writer.Complete();
            WaitWorkers(tasks, cancellationToken);
        }
        catch (Exception readerError)
        {
            failure.Cancel();
            filled.Writer.TryComplete();
            DrainPending(volume, allReads);
            Exception? workerError = null;
            try
            {
                Task.WaitAll(tasks.Where(t => t is not null).ToArray());
            }
            catch (AggregateException ex)
            {
                workerError = ex.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
            }
            // A parser failure cancels the reader: report the parser error, not a cancellation.
            if (workerError is not null && readerError is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new InvalidOperationException($"MFT parser failed: {workerError.Message}", workerError);
            throw;
        }
        finally
        {
            foreach (AsyncRead read in allReads)
                read.Dispose();
            foreach (AlignedBuffer? buffer in buffers)
                buffer?.Dispose();
        }

        double tick = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        return new PipelineResult
        {
            Workers = workers,
            BytesRead = bytesToRead,
            BytesSkipped = Math.Max(0, layout.MftBytes - bytesToRead),
            Blocks = blocks.Count,
            QueueDepth = queueDepth,
            BufferCount = bufferCount,
            ReadTime = TimeSpan.FromTicks((long)((readerEnd - readerStart - stallTicks) * tick)),
            StallTime = TimeSpan.FromTicks((long)(stallTicks * tick)),
            WallTime = Stopwatch.GetElapsedTime(start),
        };
    }

    private sealed record InFlight(ReadBlock Block, AlignedBuffer Buffer, AsyncRead[] Reads);

    private static unsafe InFlight Issue(ReadBlock block, AlignedBuffer buffer, Microsoft.Win32.SafeHandles.SafeFileHandle handle, Stack<AsyncRead> spare, List<AsyncRead> all)
    {
        int count = 0;
        foreach (ReadPiece piece in block.Pieces)
        {
            if (!piece.Zero)
                count++;
        }
        var reads = new AsyncRead[count];
        int k = 0;
        foreach (ReadPiece piece in block.Pieces)
        {
            if (piece.Zero)
            {
                new Span<byte>(buffer.Pointer + piece.BufferOffset, piece.Length).Clear();
                continue;
            }
            AsyncRead read;
            if (spare.Count > 0)
            {
                read = spare.Pop();
            }
            else
            {
                read = new AsyncRead();
                all.Add(read);
            }
            reads[k++] = read;
            read.Start(handle, piece.DiskOffset, buffer.Pointer + piece.BufferOffset, piece.Length);
        }
        return new InFlight(block, buffer, reads);
    }

    private static void Complete(
        InFlight item,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        Stack<AsyncRead> spare,
        Channel<(ReadBlock Block, AlignedBuffer Buffer)> filled,
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        foreach (AsyncRead read in item.Reads)
        {
            read.Wait(handle);
            spare.Push(read);
        }
        progress.AddBytes(item.Block.ByteLength);
        if (!filled.Writer.TryWrite((item.Block, item.Buffer)))
            filled.Writer.WriteAsync((item.Block, item.Buffer), cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Cancels and waits for pending reads so no buffer is freed while the kernel writes into it.</summary>
    private static unsafe void DrainPending(NtfsVolume volume, List<AsyncRead> reads)
    {
        if (!reads.Exists(r => r.Pending))
            return;
        Microsoft.Win32.SafeHandles.SafeFileHandle handle = volume.AsyncHandle;
        Interop.NtfsNative.CancelIoEx(handle, null);
        foreach (AsyncRead read in reads)
            read.WaitQuietly(handle);
    }

    private static void WaitWorkers(Task[] tasks, CancellationToken cancellationToken)
    {
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex)
        {
            Exception? error = ex.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
            if (error is not null)
                throw new InvalidOperationException($"MFT parser failed: {error.Message}", error);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static unsafe void ParseBlock(ReadBlock block, AlignedBuffer buffer, MftWorker worker, MftLayout layout, MftRawTable raw, bool includeStreams) =>
        ParseRecords(block.FirstRecord, block.RecordCount, new Span<byte>(buffer.Pointer, buffer.Length), worker, layout, raw, includeStreams);

    /// <summary>Parses consecutive records (in-use per the bitmap) into their slots. Also used by the tests.</summary>
    internal static void ParseRecords(long first, int recordCount, Span<byte> data, MftWorker worker, MftLayout layout, MftRawTable raw, bool includeStreams)
    {
        int recordSize = layout.RecordSize;
        int slot = layout.Rank(first);
        ulong[] bitmap = layout.Bitmap;
        Span<ushort> sequence = raw.Sequence;
        Span<ushort> parentSequence = raw.ParentSequence;
        var info = default(RecordInfo);
        for (int k = 0; k < recordCount; k++)
        {
            long record = first + k;
            if ((bitmap[record >> 6] & (1UL << (int)record)) == 0)
                continue;
            ParseRecord(record, slot++, data.Slice(k * recordSize, recordSize), ref info, worker, raw, sequence, parentSequence, includeStreams);
        }
    }

    private static void ParseRecord(
        long record,
        int slot,
        Span<byte> bytes,
        ref RecordInfo info,
        MftWorker worker,
        MftRawTable raw,
        Span<ushort> sequence,
        Span<ushort> parentSequence,
        bool includeStreams)
    {
        RecordStatus status = RecordParser.Parse(bytes, ref info);
        sequence[slot] = info.Sequence;
        if (status != RecordStatus.Ok || !info.InUse || (info.HeaderRecordNumber >= 0 && info.HeaderRecordNumber != (uint)record))
        {
            // Bitmap said "in use": an unreadable, freed-meanwhile or misplaced record is ignored.
            if (status != RecordStatus.Ok || info.InUse)
                worker.BadRecords++;
            raw.ParentRecord[slot] = MftRawTable.NotANode;
            raw.Flags[slot] = 0;
            return;
        }
        worker.InUse++;

        if (info.IsExtension)
        {
            worker.ExtensionRecords++;
            raw.ParentRecord[slot] = MftRawTable.NotANode;
            raw.Flags[slot] = 0;
            if (info.HasData || info.HasWof || info.StreamCount > 0)
            {
                worker.Extensions.Add(new ExtensionData(info.BaseRecord, info.BaseSequence, info.HasData, info.DataSize, info.DataAllocated,
                    info.HasWof, info.WofAllocated, info.StreamCount, info.StreamSize, info.StreamAllocated));
            }
            for (int i = 0; i < info.NameCount; i++)
            {
                var name = new FileNameView(bytes, info.NameOffsets[i]);
                if (name.Namespace == FileNameNamespace.Dos || name.ParentRecord > int.MaxValue)
                    continue;
                int offset = worker.Names.Append(name.Name, out ushort length);
                worker.Links.Add(new LinkEntry(info.BaseRecord, info.BaseSequence, (int)name.ParentRecord, name.ParentSequence, offset, length, (record << 5) | (long)i));
            }
            return;
        }

        // Primary name: the first long (non-DOS) name; other long names are hard links.
        int primary = -1;
        int dos = -1;
        for (int i = 0; i < info.NameCount; i++)
        {
            var name = new FileNameView(bytes, info.NameOffsets[i]);
            if (name.ParentRecord > int.MaxValue)
                continue;
            if (name.Namespace == FileNameNamespace.Dos)
            {
                if (dos < 0)
                    dos = i;
                continue;
            }
            if (primary < 0)
            {
                primary = i;
                continue;
            }
            int offset = worker.Names.Append(name.Name, out ushort length);
            worker.Links.Add(new LinkEntry(record, info.Sequence, (int)name.ParentRecord, name.ParentSequence, offset, length, (record << 5) | (long)i));
        }
        if (primary < 0 && dos >= 0)
        {
            primary = dos; // only a DOS name here: used unless an extension record holds a long one
            worker.DosFallbacks.Add(slot);
        }
        long parentRecord = -1;
        if (primary >= 0)
        {
            var name = new FileNameView(bytes, info.NameOffsets[primary]);
            parentRecord = name.ParentRecord;
            raw.ParentRecord[slot] = (int)name.ParentRecord;
            parentSequence[slot] = name.ParentSequence;
            raw.NameOffset[slot] = worker.Names.Append(name.Name, out raw.NameLength[slot]);
        }
        else
        {
            // Reserved metadata records (12–23) legitimately have no name.
            raw.ParentRecord[slot] = record < 24 ? MftRawTable.NotANode : MftRawTable.AwaitingName;
        }

        bool userStreams = ComputeValues(in info, record, parentRecord, includeStreams, out long size, out long allocated, out uint flags);
        if (info.HasWof)
            worker.WofFiles++;
        if (userStreams)
        {
            worker.Streams += info.StreamCount;
            worker.StreamSize += info.StreamSize;
            worker.StreamAllocated += info.StreamAllocated;
            worker.StreamFiles.Add(new StreamFile(record, info.StreamCount, info.StreamSize, info.StreamAllocated));
        }
        raw.Size[slot] = size;
        raw.Allocated[slot] = allocated;
        raw.Flags[slot] = flags | MftRawTable.RawValid;
        raw.Modified[slot] = info.Modified;
    }

    /// <summary>
    /// Size, allocation and flags of a base record, as counted by DiskTreeScanner (shared by the scan and
    /// the USN refresh). Returns true when the record has user named streams (ADS).
    /// </summary>
    internal static bool ComputeValues(in RecordInfo info, long record, long parentRecord, bool includeStreams, out long size, out long allocated, out uint flags)
    {
        flags = (info.Attributes & (uint)NodeFlags.AttributeMask) | (info.IsDirectory ? (uint)NodeFlags.Directory : 0);
        size = info.HasData ? info.DataSize : 0;
        allocated = (info.HasData ? info.DataAllocated : 0) + info.WofAllocated;
        if (info.HasWof)
            flags |= (uint)NodeFlags.WofCompressed;
        if (info.StreamCount == 0)
            return false;
        if (IsMetadata(record, parentRecord))
        {
            // $Secure:$SDS, $UsnJrnl:$J...: the streams are the file's content, their clusters count.
            // $BadClus:$Bad maps the whole volume and is not flagged sparse: ignored.
            if (record != BadClusRecord)
                allocated += info.StreamAllocated;
            return false;
        }
        flags |= (uint)NodeFlags.HasAlternateStreams;
        if (includeStreams)
        {
            size += info.StreamSize;
            allocated += info.StreamAllocated;
        }
        return true;
    }

    /// <summary>NTFS metadata: system records 0–15 and the files directly in $Extend.</summary>
    public static bool IsMetadata(long record, long parentRecord) => record < MftScanEngine.FirstUserRecord || parentRecord == MftScanEngine.ExtendRecord;
}
