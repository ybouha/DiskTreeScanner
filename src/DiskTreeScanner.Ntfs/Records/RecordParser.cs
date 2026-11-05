using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static DiskTreeScanner.Ntfs.Records.MftRecord;

namespace DiskTreeScanner.Ntfs.Records;

public enum RecordStatus : byte
{
    Ok,
    /// <summary>Never-used record (all zeros).</summary>
    Empty,
    /// <summary>No FILE signature.</summary>
    NotFile,
    /// <summary>Update sequence mismatch.</summary>
    BadFixup,
    /// <summary>Attribute layout inconsistent.</summary>
    Malformed,
}

/// <summary>$FILE_NAME namespaces.</summary>
public static class FileNameNamespace
{
    public const byte Posix = 0;
    public const byte Win32 = 1;
    public const byte Dos = 2;
    public const byte Win32AndDos = 3;
}

[InlineArray(RecordInfo.MaxNames)]
public struct NameOffsetBuffer
{
    private int _element0;
}

/// <summary>What one FILE record contributes (fields are only valid when <see cref="Status"/> is Ok and the record is in use).</summary>
public struct RecordInfo
{
    /// <summary>A 1 KiB record cannot hold more $FILE_NAME attributes than this.</summary>
    public const int MaxNames = 16;

    public RecordStatus Status;
    public bool InUse;
    public bool IsDirectory;
    public ushort Sequence;
    /// <summary>Record number written in the header (NTFS 3.1+), used to detect misplaced reads.</summary>
    public long HeaderRecordNumber;
    /// <summary>Base record number; non-zero for an extension record.</summary>
    public long BaseRecord;
    public ushort BaseSequence;

    public bool HasStandardInformation;
    public uint Attributes;
    public long Modified;

    /// <summary>Unnamed $DATA (first extent seen in this record).</summary>
    public bool HasData;
    public long DataSize;
    public long DataAllocated;
    public bool DataResident;

    /// <summary>"WofCompressedData" stream of a WOF-compressed file: the file's real allocation.</summary>
    public bool HasWof;
    public long WofAllocated;

    /// <summary>Other named $DATA streams (ADS).</summary>
    public int StreamCount;
    public long StreamSize;
    public long StreamAllocated;

    public bool HasAttributeList;

    public int NameCount;
    /// <summary>Offset, in the record, of the value of each $FILE_NAME attribute.</summary>
    public NameOffsetBuffer NameOffsets;

    public readonly bool IsExtension => BaseRecord != 0;
}

/// <summary>Parsed $FILE_NAME value.</summary>
public readonly ref struct FileNameView
{
    public FileNameView(ReadOnlySpan<byte> record, int valueOffset)
    {
        ulong parent = U64(record, valueOffset);
        ParentRecord = (long)(parent & RecordNumberMask);
        ParentSequence = (ushort)(parent >> 48);
        int length = record[valueOffset + 0x40];
        Namespace = record[valueOffset + 0x41];
        Name = MemoryMarshal.Cast<byte, char>(record.Slice(valueOffset + 0x42, length * 2));
    }

    public long ParentRecord { get; }

    public ushort ParentSequence { get; }

    public byte Namespace { get; }

    public ReadOnlySpan<char> Name { get; }
}

/// <summary>Zero-allocation parser of FILE records.</summary>
public static class RecordParser
{
    /// <summary>Applies fixups to <paramref name="record"/> in place, then extracts its attributes.</summary>
    /// <param name="alreadyFixedUp">True for records returned by the file system (FSCTL_GET_NTFS_FILE_RECORD), already fixed up.</param>
    public static RecordStatus Parse(Span<byte> record, ref RecordInfo info, bool alreadyFixedUp = false)
    {
        info = default;
        FixupStatus fixup = alreadyFixedUp
            ? (record.Length < 0x30 ? FixupStatus.Invalid : U32(record, 0) == FileSignature ? FixupStatus.Ok : FixupStatus.NotFileRecord)
            : ApplyFixups(record);
        switch (fixup)
        {
            case FixupStatus.Ok:
                break;
            case FixupStatus.Empty:
                return info.Status = RecordStatus.Empty;
            case FixupStatus.NotFileRecord:
                return info.Status = RecordStatus.NotFile;
            default:
                return info.Status = RecordStatus.BadFixup;
        }

        ushort flags = U16(record, OffsetFlags);
        info.InUse = (flags & FlagInUse) != 0;
        info.IsDirectory = (flags & FlagDirectory) != 0;
        info.Sequence = U16(record, OffsetSequence);
        ulong baseReference = U64(record, OffsetBaseRecord);
        info.BaseRecord = (long)(baseReference & RecordNumberMask);
        info.BaseSequence = (ushort)(baseReference >> 48);
        info.HeaderRecordNumber = U16(record, OffsetUsaOffset) >= 0x30 ? U32(record, OffsetRecordNumber) : -1;
        if (!info.InUse)
            return info.Status = RecordStatus.Ok;

        var reader = new AttributeReader(record);
        while (reader.MoveNext(out AttributeView attribute))
        {
            switch (attribute.Type)
            {
                case AttributeType.StandardInformation:
                    if (attribute.IsResident && attribute.ValueLength >= 0x24)
                    {
                        ReadOnlySpan<byte> value = attribute.Value;
                        info.Modified = I64(value, 0x08);
                        info.Attributes = U32(value, 0x20);
                        info.HasStandardInformation = true;
                    }
                    break;
                case AttributeType.AttributeList:
                    info.HasAttributeList = true;
                    break;
                case AttributeType.FileName:
                    if (attribute.IsResident && attribute.ValueLength >= 0x42)
                    {
                        ReadOnlySpan<byte> value = attribute.Value;
                        int length = value[0x40];
                        if (length > 0 && 0x42 + 2 * length <= value.Length && info.NameCount < RecordInfo.MaxNames)
                            info.NameOffsets[info.NameCount++] = attribute.Offset + attribute.ValueOffset;
                    }
                    break;
                case AttributeType.Data:
                    ParseData(attribute, ref info);
                    break;
            }
        }
        return info.Status = reader.Malformed ? RecordStatus.Malformed : RecordStatus.Ok;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParseData(in AttributeView attribute, ref RecordInfo info)
    {
        long size;
        long allocated;
        if (attribute.IsResident)
        {
            size = attribute.ValueLength;
            allocated = 0; // stored inside the MFT record: no cluster of its own
        }
        else
        {
            if (attribute.LowestVcn != 0)
                return; // continuation extent: sizes live in the first extent
            size = attribute.DataSize;
            allocated = attribute.DiskAllocated;
        }

        if (attribute.NameLength == 0)
        {
            info.HasData = true;
            info.DataSize = size;
            info.DataAllocated = allocated;
            info.DataResident = attribute.IsResident;
        }
        else if (attribute.Name.SequenceEqual("WofCompressedData"))
        {
            info.HasWof = true;
            info.WofAllocated += allocated;
        }
        else
        {
            info.StreamCount++;
            info.StreamSize += size;
            info.StreamAllocated += allocated;
        }
    }
}
