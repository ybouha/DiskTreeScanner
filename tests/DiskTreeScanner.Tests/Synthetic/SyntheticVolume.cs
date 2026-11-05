using System.Numerics;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Mft;
using DiskTreeScanner.Ntfs.Records;

namespace DiskTreeScanner.Tests.Synthetic;

/// <summary>
/// An in-memory MFT: records go through the real parser and model builder (no disk, no admin).
/// Record 5 is the root directory, as on NTFS.
/// </summary>
internal sealed class SyntheticVolume
{
    public const ushort RootSequence = 5;
    private readonly SortedDictionary<long, byte[]> _records = new();

    public SyntheticVolume(bool withRoot = true)
    {
        if (withRoot)
        {
            Add(new RecordBuilder { RecordNumber = 5, Sequence = RootSequence, Directory = true }
                .StandardInformation(0x16)
                .FileName(5, RootSequence, "."));
        }
    }

    public ScanStatistics Statistics { get; private set; } = new();

    public ActivityLog Log { get; } = new();

    public SyntheticVolume Add(RecordBuilder record) => Add(record.RecordNumber, record.Build());

    public SyntheticVolume Add(long recordNumber, byte[] bytes)
    {
        _records[recordNumber] = bytes;
        return this;
    }

    /// <summary>A directory record under <paramref name="parent"/>.</summary>
    public SyntheticVolume Directory(long record, long parent, string name, ushort sequence = 1, ushort parentSequence = 0)
    {
        return Add(new RecordBuilder { RecordNumber = record, Sequence = sequence, Directory = true }
            .StandardInformation(0x10)
            .FileName(parent, parentSequence == 0 ? SequenceOf(parent) : parentSequence, name));
    }

    /// <summary>A file with non-resident data of <paramref name="size"/> bytes (allocation rounded to 4 KiB).</summary>
    public SyntheticVolume File(long record, long parent, string name, long size, ushort sequence = 1, ushort parentSequence = 0, long modified = RecordBuilder.DefaultTime)
    {
        var builder = new RecordBuilder { RecordNumber = record, Sequence = sequence }
            .StandardInformation(0x20, modified)
            .FileName(parent, parentSequence == 0 ? SequenceOf(parent) : parentSequence, name);
        if (size <= 600)
            builder.ResidentData((int)size);
        else
            builder.NonResidentData(size, (size + 4095) / 4096 * 4096);
        return Add(builder);
    }

    private ushort SequenceOf(long record)
    {
        if (record == 5)
            return RootSequence;
        return _records.TryGetValue(record, out byte[]? bytes) ? BitConverter.ToUInt16(bytes, 0x10) : (ushort)1;
    }

    public TreeModel Scan(ScanOptions? options = null)
    {
        options ??= new ScanOptions();
        long count = _records.Count == 0 ? 0 : _records.Keys.Max() + 1;
        int words = (int)((count + 63) >> 6);
        var bitmap = new ulong[Math.Max(1, words)];
        foreach (long r in _records.Keys)
            bitmap[r >> 6] |= 1UL << (int)r;
        var rank = new int[bitmap.Length + 1];
        for (int w = 0; w < bitmap.Length; w++)
            rank[w + 1] = rank[w] + BitOperations.PopCount(bitmap[w]);
        var layout = new MftLayout
        {
            RecordSize = RecordBuilder.Size,
            ClusterSize = 4096,
            SectorSize = 512,
            RecordCount = count,
            Extents = Array.Empty<DataRun>(),
            Bitmap = bitmap,
            WordRank = rank,
        };
        var data = new byte[count * RecordBuilder.Size];
        foreach (var (r, bytes) in _records)
            bytes.CopyTo(data, r * RecordBuilder.Size);

        var names = new NameArena();
        using var raw = new MftRawTable(layout.InUseCount);
        // Two workers, records interleaved, to exercise the deterministic merge.
        var workers = new[] { new MftWorker(names), new MftWorker(names) };
        for (long r = 0; r < count; r++)
        {
            if (layout.IsInUse(r))
                MftPipeline.ParseRecords(r, 1, data.AsSpan((int)(r * RecordBuilder.Size), RecordBuilder.Size), workers[r % 2], layout, raw, options.IncludeAlternateStreams);
        }
        Statistics = new ScanStatistics();
        TreeModel model = MftModelBuilder.Build(layout, raw, workers, names, options, @"X:\", UpCaseTable.Invariant, Statistics, Log, out _);
        Aggregator.Aggregate(model);
        return model;
    }
}

internal static class ModelAssertions
{
    /// <summary>Node at a path relative to the root ("A\B\file"), failing the test when absent.</summary>
    public static int Node(this TreeModel model, string relative)
    {
        int node = model.FindRelative(relative);
        Xunit.Assert.True(node >= 0, $"'{relative}' not found in the model");
        return node;
    }

    public static int Orphans(this TreeModel model)
    {
        foreach (int c in model.Children(0))
        {
            if (model.HasFlag(c, NodeFlags.Synthetic))
                return c;
        }
        return -1;
    }

    public static List<string> ChildNames(this TreeModel model, int node)
    {
        var names = new List<string>();
        foreach (int c in model.Children(node))
            names.Add(model.GetName(c));
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
