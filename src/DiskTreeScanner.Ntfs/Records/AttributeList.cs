using System.Runtime.InteropServices;
using static DiskTreeScanner.Ntfs.Records.MftRecord;

namespace DiskTreeScanner.Ntfs.Records;

/// <summary>Entry of an $ATTRIBUTE_LIST: which FILE record holds which part of which attribute.</summary>
public readonly record struct AttributeListEntry(uint Type, long LowestVcn, long SegmentRecord, ushort SegmentSequence, ushort Instance, string Name);

/// <summary>Parser of $ATTRIBUTE_LIST values (only needed for the $MFT itself: other files are merged through base references).</summary>
public static class AttributeList
{
    public static bool TryParse(ReadOnlySpan<byte> data, List<AttributeListEntry> entries)
    {
        int i = 0;
        while (i + 0x1A <= data.Length)
        {
            uint type = U32(data, i);
            if (type == 0 || type == AttributeType.End)
                break;
            int length = U16(data, i + 4);
            if (length < 0x1A || i + length > data.Length)
                return false;
            int nameLength = data[i + 6];
            int nameOffset = data[i + 7];
            if (nameLength > 0 && nameOffset + nameLength * 2 > length)
                return false;
            long lowestVcn = I64(data, i + 0x08);
            ulong segment = U64(data, i + 0x10);
            ushort instance = U16(data, i + 0x18);
            string name = nameLength > 0 ? new string(MemoryMarshal.Cast<byte, char>(data.Slice(i + nameOffset, nameLength * 2))) : string.Empty;
            entries.Add(new AttributeListEntry(type, lowestVcn, (long)(segment & RecordNumberMask), (ushort)(segment >> 48), instance, name));
            i += length;
        }
        return true;
    }
}
