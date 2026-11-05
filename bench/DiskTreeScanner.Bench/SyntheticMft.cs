using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using DiskTreeScanner.Ntfs.Mft;
using DiskTreeScanner.Ntfs.Records;
using DiskTreeScanner.Tests.Synthetic;

namespace DiskTreeScanner.Bench;

/// <summary>
/// An MFT image of synthetic FILE records shaped like a system drive: 12 % directories, names of 4
/// to 40 characters with an 8.3 alias for most long ones, small resident files, fragmented
/// non-resident files, a few hard links, alternate streams, compressed and sparse files.
/// </summary>
internal sealed class SyntheticMft
{
    public const int RecordSize = RecordBuilder.Size;
    private const long RootRecord = 5;
    private const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789_-";
    private static readonly string[] Extensions =
        [".dll", ".exe", ".sys", ".mui", ".cat", ".manifest", ".xml", ".json", ".txt", ".png", ".js", ".log", ".dat", ".config", ".pdb", ".ttf", ".etl", ".cab"];

    /// <summary>Resident $INDEX_ROOT named $I30, as in every directory record (the parser skips it).</summary>
    private static readonly byte[] IndexRoot = BuildIndexRoot();

    public SyntheticMft(int recordCount, int seed)
    {
        var rng = new Random(seed);
        Image = new byte[(long)recordCount * RecordSize];
        var directories = new List<long> { RootRecord };
        for (long r = 0; r < recordCount; r++)
        {
            byte[] record = r == RootRecord
                ? new RecordBuilder { RecordNumber = r, Directory = true }.StandardInformation(0x16).FileName(r, 1, ".", FileNameNamespace.Win32AndDos).RawAttribute(IndexRoot).Build()
                : NextRecord(r, rng, directories);
            record.CopyTo(Image, r * RecordSize);
        }

        int words = (recordCount + 63) >> 6;
        var bitmap = new ulong[words];
        for (int r = 0; r < recordCount; r++)
            bitmap[r >> 6] |= 1UL << r;
        var rank = new int[words + 1];
        for (int w = 0; w < words; w++)
            rank[w + 1] = rank[w] + BitOperations.PopCount(bitmap[w]);
        Layout = new MftLayout
        {
            RecordSize = RecordSize,
            ClusterSize = 4096,
            SectorSize = 512,
            RecordCount = recordCount,
            Extents = Array.Empty<DataRun>(),
            Bitmap = bitmap,
            WordRank = rank,
        };
    }

    /// <summary>The records, protected by their update sequence arrays as on disk.</summary>
    public byte[] Image { get; }

    /// <summary>Every record is in use.</summary>
    public MftLayout Layout { get; }

    /// <summary>
    /// Writes the update sequence number back at the end of every 512-byte stride. Parsing applies the
    /// fixups in place and changes nothing else, so this makes the records parseable again.
    /// </summary>
    public static void Reprotect(Span<byte> records)
    {
        for (int offset = 0; offset + RecordSize <= records.Length; offset += RecordSize)
        {
            Span<byte> record = records.Slice(offset, RecordSize);
            int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[MftRecord.OffsetUsaOffset..]);
            ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
            for (int tail = MftRecord.SequenceStride - 2; tail < RecordSize; tail += MftRecord.SequenceStride)
                BinaryPrimitives.WriteUInt16LittleEndian(record[tail..], usn);
        }
    }

    private static byte[] NextRecord(long record, Random rng, List<long> directories)
    {
        bool directory = rng.NextDouble() < 0.12;
        long parent = directories[rng.Next(directories.Count)];
        string name = Name(rng, directory);
        var b = new RecordBuilder { RecordNumber = record, Directory = directory };
        b.StandardInformation(directory ? 0x10u : 0x20u, RecordBuilder.DefaultTime - rng.NextInt64(1L << 52));
        if (IsShortName(name))
        {
            b.FileName(parent, 1, name, FileNameNamespace.Win32AndDos);
        }
        else
        {
            b.FileName(parent, 1, name, FileNameNamespace.Win32);
            if (rng.NextDouble() < 0.6)
                b.FileName(parent, 1, ShortAlias(name), FileNameNamespace.Dos);
        }
        if (directory)
        {
            b.RawAttribute(IndexRoot);
            directories.Add(record);
        }
        else
        {
            if (rng.NextDouble() < 0.03)
                b.FileName(directories[rng.Next(directories.Count)], 1, name, FileNameNamespace.Win32); // hard link
            AddData(b, rng);
            if (rng.NextDouble() < 0.005)
                b.ResidentData(26, "Zone.Identifier");
        }
        return b.Build(usn: (ushort)(1 + rng.Next(0xFFFE)));
    }

    private static void AddData(RecordBuilder b, Random rng)
    {
        if (rng.NextDouble() < 0.35)
        {
            b.ResidentData(rng.Next(201));
            return;
        }
        // Log-uniform size from 1 KiB to 256 MiB.
        long size = (long)Math.Exp(Math.Log(1024) + rng.NextDouble() * (Math.Log(256L << 20) - Math.Log(1024)));
        long clusters = (size + 4095) / 4096;
        long allocated = clusters * 4096;
        double f = rng.NextDouble();
        int fragments = (int)Math.Min(clusters, f < 0.70 ? 1 : f < 0.95 ? 2 + rng.Next(2) : 4 + rng.Next(5));
        var runs = new List<DataRun>(fragments);
        long vcn = 0;
        for (int k = 0; k < fragments; k++)
        {
            long length = k == fragments - 1 ? clusters - vcn : clusters / fragments;
            runs.Add(new DataRun(vcn, 1000 + rng.NextInt64(60_000_000), length));
            vcn += length;
        }
        double kind = rng.NextDouble();
        if (kind < 0.01)
            b.NonResidentData(size, allocated, flags: 0x0001, totalAllocated: allocated / 2, runs: runs); // compressed
        else if (kind < 0.015)
            b.NonResidentData(size, allocated, flags: 0x8000, totalAllocated: allocated / 4, runs: runs); // sparse
        else
            b.NonResidentData(size, allocated, runs: runs);
    }

    private static string Name(Random rng, bool directory)
    {
        int length = 4 + rng.Next(37);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = Letters[rng.Next(Letters.Length)];
        if (rng.NextDouble() < 0.003)
            chars[rng.Next(length)] = '文'; // outside Latin-1: stored as UTF-16 in the name arena
        string stem = new(chars);
        return directory ? stem : stem + Extensions[rng.Next(Extensions.Length)];
    }

    private static bool IsShortName(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name.Length <= 8 : dot <= 8 && name.Length - dot - 1 <= 3;
    }

    private static string ShortAlias(string name)
    {
        int dot = name.LastIndexOf('.');
        string stem = (dot < 0 ? name : name[..dot]).ToUpperInvariant();
        string extension = dot < 0 ? "" : name[dot..Math.Min(name.Length, dot + 4)].ToUpperInvariant();
        return stem[..Math.Min(6, stem.Length)] + "~1" + extension;
    }

    private static byte[] BuildIndexRoot()
    {
        const int valueOffset = 0x20; // 0x18 header + 4 UTF-16 name chars
        const int valueLength = 0x30; // index root header, node header, end entry
        var a = new byte[valueOffset + valueLength];
        BinaryPrimitives.WriteUInt32LittleEndian(a, AttributeType.IndexRoot);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)a.Length);
        a[9] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x10), valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x14), valueOffset);
        Encoding.Unicode.GetBytes("$I30").CopyTo(a, 0x18);
        return a;
    }
}
