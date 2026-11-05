using BenchmarkDotNet.Attributes;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Ntfs.Mft;
using DiskTreeScanner.Ntfs.Records;

namespace DiskTreeScanner.Bench;

/// <summary>
/// FILE record parsing as done by the scan: fixups, attribute walk, names into the arena, values
/// into the raw table. Times are per record (1 KiB); 1 µs per record is 1 GB/s.
/// </summary>
public class ParsingBenchmarks
{
    private const int Records = 65_536; // 64 MiB
    private const int RecordsPerBlock = 1024; // 1 MiB: enough blocks to balance every parser thread
    private const int Blocks = Records / RecordsPerBlock;

    private SyntheticMft _mft = null!;
    private MftRawTable _raw = null!;
    private byte[] _fragmentedRunlist = null!;
    private readonly List<DataRun> _runs = new(64);

    [GlobalSetup]
    public void Setup()
    {
        _mft = new SyntheticMft(Records, seed: 1);
        _raw = new MftRawTable(Records);
        MftWorker check = OneThread();
        if (check.BadRecords != 0 || check.InUse != Records)
            throw new InvalidOperationException($"Synthetic MFT: {check.InUse} records parsed, {check.BadRecords} bad.");
        SyntheticMft.Reprotect(_mft.Image);

        // A fragmented $MFT: 64 runs, LCN deltas of both signs, a few sparse runs.
        var rng = new Random(3);
        var runs = new List<DataRun>();
        long vcn = 0;
        for (int i = 0; i < 64; i++)
        {
            long length = 1 + rng.Next(1 << 20);
            runs.Add(new DataRun(vcn, i % 16 == 15 ? -1 : rng.NextInt64(1L << 36), length));
            vcn += length;
        }
        _fragmentedRunlist = Runlist.Encode(runs);
    }

    [GlobalCleanup]
    public void Cleanup() => _raw.Dispose();

    [Benchmark(OperationsPerInvoke = Records, Description = "Reprotect (cost included below)")]
    public void Reprotect() => SyntheticMft.Reprotect(_mft.Image);

    [Benchmark(OperationsPerInvoke = Records, Description = "Parse records, 1 thread")]
    public long ParseOneThread()
    {
        SyntheticMft.Reprotect(_mft.Image);
        return OneThread().InUse;
    }

    [Benchmark(OperationsPerInvoke = Records, Description = "Parse records, cores - 1 threads")]
    public long ParseParallel()
    {
        int threads = Math.Max(1, Environment.ProcessorCount - 1);
        var names = new NameArena();
        var workers = new MftWorker[threads];
        int next = -1;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var worker = workers[t] = new MftWorker(names);
            for (int block; (block = Interlocked.Increment(ref next)) < Blocks;)
            {
                Span<byte> data = _mft.Image.AsSpan(block * RecordsPerBlock * SyntheticMft.RecordSize, RecordsPerBlock * SyntheticMft.RecordSize);
                SyntheticMft.Reprotect(data);
                MftPipeline.ParseRecords(block * RecordsPerBlock, RecordsPerBlock, data, worker, _mft.Layout, _raw, includeStreams: false);
            }
        });
        return workers.Sum(w => w.InUse);
    }

    [Benchmark(Description = "Decode a 64-run runlist")]
    public int DecodeRunlist()
    {
        _runs.Clear();
        Runlist.TryDecode(_fragmentedRunlist, 0, _runs);
        return _runs.Count;
    }

    private MftWorker OneThread()
    {
        var worker = new MftWorker(new NameArena());
        for (int block = 0; block < Blocks; block++)
        {
            Span<byte> data = _mft.Image.AsSpan(block * RecordsPerBlock * SyntheticMft.RecordSize, RecordsPerBlock * SyntheticMft.RecordSize);
            MftPipeline.ParseRecords(block * RecordsPerBlock, RecordsPerBlock, data, worker, _mft.Layout, _raw, includeStreams: false);
        }
        return worker;
    }
}
