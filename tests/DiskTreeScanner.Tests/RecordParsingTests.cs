using System.Buffers.Binary;
using DiskTreeScanner.Ntfs.Records;
using DiskTreeScanner.Tests.Synthetic;
using Xunit;

namespace DiskTreeScanner.Tests;

public class FixupTests
{
    [Fact]
    public void ValidRecord_RestoresTheBytesSavedInTheUpdateSequenceArray()
    {
        // A 900-byte resident value spans the end of the first 512-byte stride.
        byte[] record = new RecordBuilder { RecordNumber = 40 }.StandardInformation().ResidentData(700).Build(usn: 0x4242);
        Assert.Equal(0x4242, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));

        Assert.Equal(FixupStatus.Ok, MftRecord.ApplyFixups(record));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510))); // original data byte restored
    }

    [Fact]
    public void TornWrite_IsRejected()
    {
        byte[] record = new RecordBuilder().StandardInformation().Build(tornWrite: true);
        Assert.Equal(FixupStatus.Invalid, MftRecord.ApplyFixups(record));
        var info = default(RecordInfo);
        Assert.Equal(RecordStatus.BadFixup, RecordParser.Parse(record, ref info));
    }

    [Fact]
    public void ZeroRecord_IsEmpty_AndBadSignature_IsNotAFileRecord()
    {
        Assert.Equal(FixupStatus.Empty, MftRecord.ApplyFixups(new byte[1024]));
        byte[] baad = new RecordBuilder().StandardInformation().Build();
        "BAAD"u8.CopyTo(baad);
        Assert.Equal(FixupStatus.NotFileRecord, MftRecord.ApplyFixups(baad));
    }

    [Fact]
    public void UpdateSequenceCountNotMatchingTheRecordSize_IsRejected()
    {
        byte[] record = new RecordBuilder().StandardInformation().Build();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 9); // 4 KiB worth of strides in a 1 KiB record
        Assert.Equal(FixupStatus.Invalid, MftRecord.ApplyFixups(record));
    }
}

public class RecordParserTests
{
    private static RecordInfo Parse(byte[] record)
    {
        var info = default(RecordInfo);
        Assert.Equal(RecordStatus.Ok, RecordParser.Parse(record, ref info));
        return info;
    }

    [Fact]
    public void StandardInformation_GivesAttributesAndModificationTime()
    {
        RecordInfo info = Parse(new RecordBuilder().StandardInformation(attributes: 0x6, modified: 133_000_000_000_000_000).Build());
        Assert.True(info.HasStandardInformation);
        Assert.Equal(0x6u, info.Attributes);
        Assert.Equal(133_000_000_000_000_000, info.Modified);
    }

    [Fact]
    public void FileNames_KeepEveryNamespaceAndParent()
    {
        byte[] record = new RecordBuilder()
            .StandardInformation()
            .FileName(40, 3, "Long file name.txt", FileNameNamespace.Win32)
            .FileName(40, 3, "LONGFI~1.TXT", FileNameNamespace.Dos)
            .Build();
        RecordInfo info = Parse(record);
        Assert.Equal(2, info.NameCount);
        var win32 = new FileNameView(record, info.NameOffsets[0]);
        var dos = new FileNameView(record, info.NameOffsets[1]);
        Assert.Equal("Long file name.txt", win32.Name.ToString());
        Assert.Equal(FileNameNamespace.Win32, win32.Namespace);
        Assert.Equal(40, win32.ParentRecord);
        Assert.Equal(3, win32.ParentSequence);
        Assert.Equal(FileNameNamespace.Dos, dos.Namespace);
    }

    [Fact]
    public void ResidentData_SizeIsTheValueLength_AndUsesNoCluster()
    {
        RecordInfo info = Parse(new RecordBuilder().StandardInformation().ResidentData(123).Build());
        Assert.True(info.HasData);
        Assert.True(info.DataResident);
        Assert.Equal(123, info.DataSize);
        Assert.Equal(0, info.DataAllocated);
    }

    [Fact]
    public void NonResidentData_UsesRealAndAllocatedSize()
    {
        RecordInfo info = Parse(new RecordBuilder().StandardInformation().NonResidentData(10_000, 12_288).Build());
        Assert.False(info.DataResident);
        Assert.Equal(10_000, info.DataSize);
        Assert.Equal(12_288, info.DataAllocated);
    }

    [Theory]
    [InlineData((ushort)0x8000)] // sparse
    [InlineData((ushort)0x0001)] // compressed
    public void SparseOrCompressedData_UsesTotalAllocated(ushort flags)
    {
        RecordInfo info = Parse(new RecordBuilder().StandardInformation()
            .NonResidentData(dataSize: 1_000_000, allocatedSize: 1_048_576, flags: flags, totalAllocated: 65_536).Build());
        Assert.Equal(1_000_000, info.DataSize);
        Assert.Equal(65_536, info.DataAllocated);
    }

    [Fact]
    public void NamedStreams_AreCountedApart_AndWofDataIsRecognised()
    {
        RecordInfo info = Parse(new RecordBuilder().StandardInformation()
            .NonResidentData(500_000, 0, flags: 0x8000, totalAllocated: 0)
            .ResidentData(26, "Zone.Identifier")
            .NonResidentData(90_000, 90_112, "WofCompressedData")
            .Build());
        Assert.Equal(500_000, info.DataSize);
        Assert.Equal(0, info.DataAllocated);
        Assert.Equal(1, info.StreamCount);
        Assert.Equal(26, info.StreamSize);
        Assert.True(info.HasWof);
        Assert.Equal(90_112, info.WofAllocated);
    }

    [Fact]
    public void ContinuationExtent_OfAFragmentedAttribute_DoesNotCarrySizes()
    {
        RecordInfo info = Parse(new RecordBuilder().StandardInformation()
            .NonResidentData(0, 0, lowestVcn: 5000, runs: [new DataRun(5000, 90_000, 16)])
            .Build());
        Assert.False(info.HasData);
    }

    [Fact]
    public void ExtensionRecord_ExposesItsBaseReference()
    {
        RecordInfo info = Parse(new RecordBuilder { BaseRecord = 77, BaseSequence = 9 }.NonResidentData(4096, 4096).Build());
        Assert.True(info.IsExtension);
        Assert.Equal(77, info.BaseRecord);
        Assert.Equal(9, info.BaseSequence);
        Assert.True(info.HasData);
    }

    [Fact]
    public void AttributeWithImpossibleLength_IsMalformed()
    {
        var bogus = new byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(bogus, AttributeType.Data);
        BinaryPrimitives.WriteUInt32LittleEndian(bogus.AsSpan(4), 0x4000); // runs past the record
        byte[] record = new RecordBuilder().StandardInformation().RawAttribute(bogus).Build();
        var info = default(RecordInfo);
        Assert.Equal(RecordStatus.Malformed, RecordParser.Parse(record, ref info));
    }

    [Fact]
    public void FreeRecord_IsReportedNotInUse()
    {
        RecordInfo info = Parse(new RecordBuilder { InUse = false }.StandardInformation().Build());
        Assert.False(info.InUse);
    }
}

public class RunlistTests
{
    [Fact]
    public void Decode_PositiveAndNegativeDeltas()
    {
        // 0x21: 1-byte length, 2-byte offset. Second run moves back (negative delta).
        byte[] pairs = [0x21, 0x10, 0x00, 0x10, 0x11, 0x08, 0xF0, 0x00];
        var runs = new List<DataRun>();
        Assert.True(Runlist.TryDecode(pairs, 0, runs));
        Assert.Equal([new DataRun(0, 0x1000, 0x10), new DataRun(0x10, 0x1000 - 0x10, 0x08)], runs);
    }

    [Fact]
    public void SparseRun_HasNoLcn_AndDoesNotMoveTheNextDelta()
    {
        byte[] pairs = Runlist.Encode([new DataRun(0, 5000, 4), new DataRun(4, -1, 100), new DataRun(104, 5100, 8)]);
        var runs = new List<DataRun>();
        Assert.True(Runlist.TryDecode(pairs, 0, runs));
        Assert.True(runs[1].IsSparse);
        Assert.Equal(5100, runs[2].Lcn); // delta relative to 5000, not to the sparse run
        Assert.Equal(104, runs[2].Vcn);
    }

    [Fact]
    public void FragmentedMft_ManyRunsRoundTrip()
    {
        var random = new Random(42);
        var expected = new List<DataRun>();
        long vcn = 0, lcn = 1_000_000;
        for (int i = 0; i < 500; i++)
        {
            long length = random.Next(1, 70_000);
            lcn = Math.Max(1, lcn + random.Next(-900_000, 900_000));
            expected.Add(new DataRun(vcn, lcn, length));
            vcn += length;
        }
        var runs = new List<DataRun>();
        Assert.True(Runlist.TryDecode(Runlist.Encode(expected), 0, runs));
        Assert.Equal(expected, runs);
    }

    [Fact]
    public void StartingVcnOfAContinuationExtent_IsHonoured()
    {
        var runs = new List<DataRun>();
        Assert.True(Runlist.TryDecode(Runlist.Encode([new DataRun(0, 700, 10)]), startVcn: 4096, runs));
        Assert.Equal(4096, runs[0].Vcn);
    }

    [Theory]
    [InlineData(new byte[] { 0x11, 0x05 })]             // truncated: offset byte missing
    [InlineData(new byte[] { 0x11, 0x05, 0x10 })]       // no terminator
    [InlineData(new byte[] { 0x10, 0x00 })]             // length field missing
    [InlineData(new byte[] { 0x91, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // 9-byte offset field
    [InlineData(new byte[] { 0x11, 0x00, 0x10, 0x00 })] // zero-length run
    [InlineData(new byte[] { 0x11, 0x01, 0x80, 0x00 })] // LCN goes negative
    public void MalformedMappingPairs_AreRejected(byte[] pairs)
    {
        Assert.False(Runlist.TryDecode(pairs, 0, new List<DataRun>()));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF }, -1L)]
    [InlineData(new byte[] { 0x7F }, 127L)]
    [InlineData(new byte[] { 0x00, 0x80 }, -32768L)]
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80 }, long.MinValue + 1)]
    public void SignedFields_AreSignExtended(byte[] bytes, long expected)
    {
        Assert.Equal(expected, Runlist.ReadSigned(bytes));
    }
}

public class AttributeListTests
{
    [Fact]
    public void Entries_GiveTypeVcnAndSegment()
    {
        byte[] record = new RecordBuilder().StandardInformation()
            .AttributeList((AttributeType.StandardInformation, 0, 50, 2), (AttributeType.Data, 0, 51, 3), (AttributeType.Data, 4000, 52, 3))
            .Build();
        Assert.Equal(FixupStatus.Ok, MftRecord.ApplyFixups(record));
        var reader = new AttributeReader(record);
        byte[]? value = null;
        while (reader.MoveNext(out AttributeView a))
        {
            if (a.Type == AttributeType.AttributeList)
                value = a.Value.ToArray();
        }
        var entries = new List<AttributeListEntry>();
        Assert.True(AttributeList.TryParse(value!, entries));
        Assert.Equal(3, entries.Count);
        Assert.Equal(AttributeType.Data, entries[2].Type);
        Assert.Equal(4000, entries[2].LowestVcn);
        Assert.Equal(52, entries[2].SegmentRecord);
        Assert.Equal(3, entries[2].SegmentSequence);
    }
}
