using System.Runtime.InteropServices;
using static DiskTreeScanner.Ntfs.Records.MftRecord;

namespace DiskTreeScanner.Ntfs.Records;

public static class AttributeType
{
    public const uint StandardInformation = 0x10;
    public const uint AttributeList = 0x20;
    public const uint FileName = 0x30;
    public const uint Data = 0x80;
    public const uint IndexRoot = 0x90;
    public const uint IndexAllocation = 0xA0;
    public const uint Bitmap = 0xB0;
    public const uint ReparsePoint = 0xC0;
    public const uint End = 0xFFFF_FFFF;
}

/// <summary>Walks the attributes of a fixed-up FILE record, validating every bound before use.</summary>
public ref struct AttributeReader
{
    private readonly ReadOnlySpan<byte> _record;
    private readonly int _end;
    private int _offset;

    public AttributeReader(ReadOnlySpan<byte> record)
    {
        _record = record;
        int first = U16(record, OffsetFirstAttribute);
        int used = (int)U32(record, OffsetBytesInUse);
        if (used > record.Length || first < 0x2A || first + 8 > used || (first & 7) != 0)
        {
            Malformed = true;
            _end = 0;
            _offset = 0;
        }
        else
        {
            _end = used;
            _offset = first;
        }
    }

    /// <summary>Set when the walk stopped on an inconsistent attribute.</summary>
    public bool Malformed { get; private set; }

    public bool MoveNext(out AttributeView attribute)
    {
        attribute = default;
        if (Malformed)
            return false;
        if (_offset + 4 > _end)
            return Fail();
        uint type = U32(_record, _offset);
        if (type == AttributeType.End)
            return false;
        if (_offset + 0x18 > _end)
            return Fail();
        int length = (int)U32(_record, _offset + 4);
        if (length < 0x18 || (length & 7) != 0 || length > _end - _offset)
            return Fail();

        ReadOnlySpan<byte> span = _record.Slice(_offset, length);
        int nameLength = span[9];
        int nameOffset = U16(span, 10);
        if (nameLength > 0 && nameOffset + nameLength * 2 > length)
            return Fail();
        if (span[8] != 0)
        {
            if (length < 0x40)
                return Fail();
            int mappingPairs = U16(span, 0x20);
            if (mappingPairs < 0x40 || mappingPairs > length)
                return Fail();
        }
        else
        {
            long valueLength = U32(span, 0x10);
            int valueOffset = U16(span, 0x14);
            if (valueOffset < 0x18 || valueOffset + valueLength > length)
                return Fail();
        }
        attribute = new AttributeView(span, _offset);
        _offset += length;
        return true;
    }

    private bool Fail()
    {
        Malformed = true;
        return false;
    }
}

/// <summary>One attribute record (resident or non-resident) inside a FILE record.</summary>
public readonly ref struct AttributeView
{
    public AttributeView(ReadOnlySpan<byte> span, int offset)
    {
        Span = span;
        Offset = offset;
    }

    public ReadOnlySpan<byte> Span { get; }

    /// <summary>Offset of the attribute inside its FILE record.</summary>
    public int Offset { get; }

    public uint Type => U32(Span, 0);

    public bool IsResident => Span[8] == 0;

    public int NameLength => Span[9];

    public ReadOnlySpan<char> Name => MemoryMarshal.Cast<byte, char>(Span.Slice(U16(Span, 10), NameLength * 2));

    public ushort Flags => U16(Span, 12);

    /// <summary>ATTRIBUTE_FLAG_COMPRESSION_MASK (0x00FF) or ATTRIBUTE_FLAG_SPARSE (0x8000).</summary>
    public bool IsCompressedOrSparse => (Flags & 0x80FF) != 0;

    // Resident attributes.
    public int ValueLength => (int)U32(Span, 0x10);

    public int ValueOffset => U16(Span, 0x14);

    public ReadOnlySpan<byte> Value => Span.Slice(ValueOffset, ValueLength);

    // Non-resident attributes. Sizes are only meaningful in the first extent (LowestVcn == 0).
    public long LowestVcn => I64(Span, 0x10);

    public long HighestVcn => I64(Span, 0x18);

    public ReadOnlySpan<byte> MappingPairs => Span[U16(Span, 0x20)..];

    public long AllocatedSize => I64(Span, 0x28);

    public long DataSize => I64(Span, 0x30);

    public long InitializedSize => I64(Span, 0x38);

    /// <summary>The TotalAllocated field exists only in the 0x48-byte header of compressed or sparse attributes.</summary>
    public bool HasTotalAllocated => IsCompressedOrSparse && U16(Span, 0x20) >= 0x48 && (NameLength == 0 || U16(Span, 10) >= 0x48);

    public long TotalAllocated => I64(Span, 0x40);

    /// <summary>Clusters actually used: TotalAllocated when compressed or sparse, AllocatedSize otherwise.</summary>
    public long DiskAllocated => HasTotalAllocated ? TotalAllocated : AllocatedSize;
}
