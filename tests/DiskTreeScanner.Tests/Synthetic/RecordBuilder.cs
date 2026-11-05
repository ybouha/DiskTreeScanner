using System.Buffers.Binary;
using System.Text;
using DiskTreeScanner.Ntfs.Records;

namespace DiskTreeScanner.Tests.Synthetic;

/// <summary>Builds valid (or deliberately broken) 1 KiB NTFS FILE records for tests.</summary>
internal sealed class RecordBuilder
{
    public const int Size = 1024;
    public const long DefaultTime = 0x01DC_4E00_0000_0000; // 2026
    private readonly List<byte[]> _attributes = new();
    private ushort _instance;
    private int _names;

    public long RecordNumber { get; init; }
    public ushort Sequence { get; init; } = 1;
    public bool InUse { get; init; } = true;
    public bool Directory { get; init; }
    public long BaseRecord { get; init; }
    public ushort BaseSequence { get; init; }

    public RecordBuilder StandardInformation(uint attributes = 0x20, long modified = DefaultTime)
    {
        var v = new byte[0x48];
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0x00), modified);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0x08), modified);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0x10), modified);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0x18), modified);
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(0x20), attributes);
        return Resident(AttributeType.StandardInformation, v);
    }

    public RecordBuilder FileName(long parent, ushort parentSequence, string name, byte nameSpace = FileNameNamespace.Win32)
    {
        var v = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)parent | ((ulong)parentSequence << 48));
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(0x38), Directory ? 0x1000_0000u : 0x20u);
        v[0x40] = (byte)name.Length;
        v[0x41] = nameSpace;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 0x42);
        _names++;
        return Resident(AttributeType.FileName, v, indexed: true);
    }

    public RecordBuilder ResidentData(int length, string? streamName = null) => Resident(AttributeType.Data, new byte[length], streamName);

    /// <summary>Non-resident $DATA; flags 0x0001 = compressed, 0x8000 = sparse (adds the TotalAllocated field).</summary>
    public RecordBuilder NonResidentData(long dataSize, long allocatedSize, string? streamName = null, ushort flags = 0, long totalAllocated = -1,
        long lowestVcn = 0, IReadOnlyList<DataRun>? runs = null)
    {
        runs ??= [new DataRun(lowestVcn, 1000, Math.Max(1, allocatedSize / 4096))];
        long clusters = runs.Sum(r => r.Length);
        bool full = (flags & 0x80FF) != 0;
        return NonResident(AttributeType.Data, streamName, flags, lowestVcn, lowestVcn + clusters - 1, allocatedSize, dataSize, dataSize,
            full ? (totalAllocated >= 0 ? totalAllocated : allocatedSize) : -1, Runlist.Encode(runs));
    }

    public RecordBuilder AttributeList(params (uint Type, long LowestVcn, long Segment, ushort Sequence)[] entries)
    {
        var bytes = new List<byte>();
        foreach (var e in entries)
        {
            var entry = new byte[0x20];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, e.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), 0x20);
            entry[6] = 0;
            entry[7] = 0x1A;
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), e.LowestVcn);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0x10), (ulong)e.Segment | ((ulong)e.Sequence << 48));
            bytes.AddRange(entry);
        }
        return Resident(AttributeType.AttributeList, bytes.ToArray());
    }

    /// <summary>Appends raw attribute bytes as is (for malformed-input tests).</summary>
    public RecordBuilder RawAttribute(byte[] attribute)
    {
        _attributes.Add(attribute);
        return this;
    }

    private RecordBuilder Resident(uint type, byte[] value, string? name = null, bool indexed = false)
    {
        int nameBytes = (name?.Length ?? 0) * 2;
        int valueOffset = Align8(0x18 + nameBytes);
        int length = Align8(valueOffset + value.Length);
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 0;
        a[9] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 0x18);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(14), _instance++);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x14), (ushort)valueOffset);
        a[0x16] = indexed ? (byte)1 : (byte)0;
        if (name is not null)
            Encoding.Unicode.GetBytes(name).CopyTo(a, 0x18);
        value.CopyTo(a, valueOffset);
        _attributes.Add(a);
        return this;
    }

    private RecordBuilder NonResident(uint type, string? name, ushort flags, long lowestVcn, long highestVcn, long allocated, long dataSize, long initialized, long totalAllocated, byte[] mappingPairs)
    {
        bool full = totalAllocated >= 0;
        int header = full ? 0x48 : 0x40;
        int nameBytes = (name?.Length ?? 0) * 2;
        int pairsOffset = Align8(header + nameBytes);
        int length = Align8(pairsOffset + mappingPairs.Length);
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 1;
        a[9] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), (ushort)header);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(14), _instance++);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x10), lowestVcn);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x18), highestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x20), (ushort)pairsOffset);
        a[0x22] = (flags & 0x00FF) != 0 ? (byte)4 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x28), allocated);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x30), dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x38), initialized);
        if (full)
            BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x40), totalAllocated);
        if (name is not null)
            Encoding.Unicode.GetBytes(name).CopyTo(a, header);
        mappingPairs.CopyTo(a, pairsOffset);
        _attributes.Add(a);
        return this;
    }

    /// <summary>Serializes the record and protects it with an update sequence array.</summary>
    public byte[] Build(ushort usn = 0x0BAD, bool tornWrite = false)
    {
        var r = new byte[Size];
        const int usaOffset = 0x30;
        int usaCount = Size / MftRecord.SequenceStride + 1;
        int first = Align8(usaOffset + usaCount * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(r, MftRecord.FileSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x04), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x06), (ushort)usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x10), Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x12), (ushort)Math.Max(1, _names));
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), (ushort)first);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x16), (ushort)((InUse ? 1 : 0) | (Directory ? 2 : 0)));
        int offset = first;
        foreach (byte[] a in _attributes)
        {
            a.CopyTo(r, offset);
            offset += a.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(offset), AttributeType.End);
        offset += 8;
        if (offset > Size)
            throw new InvalidOperationException("Synthetic record does not fit in 1 KiB.");
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x18), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x1C), Size);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0x20), (ulong)BaseRecord | ((ulong)BaseSequence << 48));
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x28), _instance);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x2C), (uint)RecordNumber);

        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(usaOffset), usn);
        for (int i = 1; i < usaCount; i++)
        {
            int tail = i * MftRecord.SequenceStride - 2;
            r[usaOffset + 2 * i] = r[tail];
            r[usaOffset + 2 * i + 1] = r[tail + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(tail), usn);
        }
        if (tornWrite)
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(Size - 2), (ushort)(usn + 1));
        return r;
    }

    private static int Align8(int v) => (v + 7) & ~7;
}
