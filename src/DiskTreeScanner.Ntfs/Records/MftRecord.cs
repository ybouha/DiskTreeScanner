using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace DiskTreeScanner.Ntfs.Records;

public enum FixupStatus : byte
{
    Ok,
    /// <summary>All-zero record (never used).</summary>
    Empty,
    /// <summary>No FILE signature (e.g. "BAAD" or garbage).</summary>
    NotFileRecord,
    /// <summary>Update sequence array inconsistent: torn or corrupt write.</summary>
    Invalid,
}

/// <summary>FILE record header layout and multi-sector protection (fixups).</summary>
public static class MftRecord
{
    public const uint FileSignature = 0x454C_4946; // "FILE"
    /// <summary>NTFS protects every 512-byte stride of a record, whatever the device sector size.</summary>
    public const int SequenceStride = 512;

    public const ushort FlagInUse = 0x0001;
    public const ushort FlagDirectory = 0x0002;

    public const int OffsetUsaOffset = 0x04;
    public const int OffsetUsaCount = 0x06;
    public const int OffsetSequence = 0x10;
    public const int OffsetFirstAttribute = 0x14;
    public const int OffsetFlags = 0x16;
    public const int OffsetBytesInUse = 0x18;
    public const int OffsetBytesAllocated = 0x1C;
    public const int OffsetBaseRecord = 0x20;
    public const int OffsetRecordNumber = 0x2C;

    public const long RecordNumberMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>Validates the header and applies the update sequence array in place.</summary>
    /// <remarks>
    /// The last two bytes of each 512-byte stride hold the update sequence number; the real bytes
    /// are saved in the array. A stride whose tail does not match was not fully written.
    /// </remarks>
    public static FixupStatus ApplyFixups(Span<byte> record)
    {
        if (record.Length < 0x30 || record.Length % SequenceStride != 0)
            return FixupStatus.Invalid;
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (signature != FileSignature)
            return signature == 0 ? FixupStatus.Empty : FixupStatus.NotFileRecord;

        int usaOffset = U16(record, OffsetUsaOffset);
        int usaCount = U16(record, OffsetUsaCount);
        int strides = record.Length / SequenceStride;
        if (usaCount != strides + 1 || (usaOffset & 1) != 0 || usaOffset < 0x28 || usaOffset + usaCount * 2 > SequenceStride)
            return FixupStatus.Invalid;

        ushort usn = U16(record, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            if (U16(record, i * SequenceStride - 2) != usn)
                return FixupStatus.Invalid;
        }
        for (int i = 1; i < usaCount; i++)
        {
            int tail = i * SequenceStride - 2;
            record[tail] = record[usaOffset + 2 * i];
            record[tail + 1] = record[usaOffset + 2 * i + 1];
        }
        return FixupStatus.Ok;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort U16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(s[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong U64(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(s[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long I64(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadInt64LittleEndian(s[offset..]);
}
