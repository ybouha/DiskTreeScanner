using System.Numerics;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Ntfs.Records;

namespace DiskTreeScanner.Ntfs.Mft;

/// <summary>Physical piece of a read block: <see cref="Length"/> bytes at <see cref="DiskOffset"/> into the buffer at <see cref="BufferOffset"/>.</summary>
internal readonly record struct ReadPiece(long DiskOffset, int BufferOffset, int Length, bool Zero);

/// <summary>A run of consecutive MFT records read in one go (4–16 MiB).</summary>
internal sealed class ReadBlock
{
    public required long FirstRecord { get; init; }

    public required int RecordCount { get; init; }

    public required int ByteLength { get; init; }

    public required ReadPiece[] Pieces { get; init; }
}

/// <summary>
/// Where the MFT lives (its extents; it is never assumed contiguous) and which records are in use
/// (the $MFT::$BITMAP). The bitmap also gives every in-use record a compact slot number
/// (<see cref="Rank"/>), so per-record arrays are sized to the records actually in use.
/// </summary>
internal sealed unsafe class MftLayout
{
    public required int RecordSize { get; init; }

    public required int ClusterSize { get; init; }

    public required int SectorSize { get; init; }

    /// <summary>Records covered by MftValidDataLength.</summary>
    public required long RecordCount { get; init; }

    /// <summary>MFT extents in VCN order.</summary>
    public required DataRun[] Extents { get; init; }

    /// <summary>One bit per record, set when the record is in use.</summary>
    public required ulong[] Bitmap { get; init; }

    /// <summary>WordRank[w] = in-use records before bitmap word w.</summary>
    public required int[] WordRank { get; init; }

    public int InUseCount => WordRank[^1];

    public long MftBytes => RecordCount * RecordSize;

    public bool IsInUse(long record) => (ulong)record < (ulong)RecordCount && (Bitmap[record >> 6] & (1UL << (int)record)) != 0;

    /// <summary>Number of in-use records before <paramref name="record"/>: the record's compact slot.</summary>
    public int Rank(long record)
    {
        int word = (int)(record >> 6);
        return WordRank[word] + BitOperations.PopCount(Bitmap[word] & ((1UL << (int)(record & 63)) - 1));
    }

    /// <summary>Reads record 0, the MFT extents and the MFT bitmap.</summary>
    public static MftLayout Load(NtfsVolume volume)
    {
        NtfsVolumeData d = volume.Data;
        int recordSize = d.BytesPerFileRecordSegment;
        int clusterSize = d.BytesPerCluster;
        long recordCount = d.MftValidDataLength / recordSize;

        byte[] record0 = ReadDisk(volume, d.MftStartLcn * clusterSize, recordSize);
        if (MftRecord.ApplyFixups(record0) != FixupStatus.Ok)
            throw new InvalidDataException("$MFT record 0 is not a valid FILE record.");

        var dataRuns = new List<DataRun>();
        var bitmapRuns = new List<DataRun>();
        byte[]? bitmapResident = null;
        long bitmapSize = 0;
        List<AttributeListEntry>? attributeList = null;

        CollectMftAttributes(record0, volume, clusterSize, dataRuns, bitmapRuns, ref bitmapResident, ref bitmapSize, ref attributeList, readAttributeList: true);

        // A fragmented MFT spills its own runlist into extension records listed in $ATTRIBUTE_LIST.
        if (attributeList is not null)
        {
            var pending = attributeList
                .Where(e => (e.Type == AttributeType.Data || e.Type == AttributeType.Bitmap) && e.Name.Length == 0 && e.SegmentRecord != 0)
                .OrderBy(e => e.LowestVcn)
                .ToList();
            var loaded = new HashSet<long>();
            foreach (AttributeListEntry entry in pending)
            {
                if (!loaded.Add(entry.SegmentRecord))
                    continue;
                dataRuns.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
                byte[] extension = ReadRecordThroughRuns(volume, dataRuns, entry.SegmentRecord, recordSize, clusterSize);
                if (MftRecord.ApplyFixups(extension) != FixupStatus.Ok)
                    throw new InvalidDataException($"$MFT extension record {entry.SegmentRecord} is not a valid FILE record.");
                List<AttributeListEntry>? ignored = null;
                CollectMftAttributes(extension, volume, clusterSize, dataRuns, bitmapRuns, ref bitmapResident, ref bitmapSize, ref ignored, readAttributeList: false);
            }
        }

        dataRuns.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
        long expectedVcn = 0;
        foreach (DataRun run in dataRuns)
        {
            if (run.Vcn != expectedVcn)
                throw new InvalidDataException($"$MFT runlist has a gap at VCN {expectedVcn}.");
            expectedVcn += run.Length;
        }
        if (expectedVcn * clusterSize < d.MftValidDataLength)
            throw new InvalidDataException("$MFT extents do not cover MftValidDataLength.");

        byte[] bitmapBytes;
        if (bitmapResident is not null)
        {
            bitmapBytes = bitmapResident;
        }
        else if (bitmapRuns.Count > 0)
        {
            bitmapRuns.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
            bitmapBytes = ReadRuns(volume, bitmapRuns, bitmapSize, clusterSize);
        }
        else
        {
            throw new InvalidDataException("$MFT has no $BITMAP attribute.");
        }

        int words = (int)((recordCount + 63) >> 6);
        var bitmap = new ulong[words];
        int copyBytes = (int)Math.Min(bitmapBytes.Length, (long)words * 8);
        Buffer.BlockCopy(bitmapBytes, 0, bitmap, 0, copyBytes);
        if ((recordCount & 63) != 0)
            bitmap[^1] &= (1UL << (int)(recordCount & 63)) - 1;
        var wordRank = new int[words + 1];
        for (int w = 0; w < words; w++)
            wordRank[w + 1] = wordRank[w] + BitOperations.PopCount(bitmap[w]);

        return new MftLayout
        {
            RecordSize = recordSize,
            ClusterSize = clusterSize,
            SectorSize = d.BytesPerSector,
            RecordCount = recordCount,
            Extents = dataRuns.ToArray(),
            Bitmap = bitmap,
            WordRank = wordRank,
        };
    }

    private static void CollectMftAttributes(
        byte[] record,
        NtfsVolume volume,
        int clusterSize,
        List<DataRun> dataRuns,
        List<DataRun> bitmapRuns,
        ref byte[]? bitmapResident,
        ref long bitmapSize,
        ref List<AttributeListEntry>? attributeList,
        bool readAttributeList)
    {
        var reader = new AttributeReader(record);
        while (reader.MoveNext(out AttributeView attribute))
        {
            if (attribute.NameLength != 0)
                continue;
            switch (attribute.Type)
            {
                case AttributeType.AttributeList when readAttributeList:
                {
                    byte[] value;
                    if (attribute.IsResident)
                    {
                        value = attribute.Value.ToArray();
                    }
                    else
                    {
                        var runs = new List<DataRun>();
                        if (!Runlist.TryDecode(attribute.MappingPairs, attribute.LowestVcn, runs))
                            throw new InvalidDataException("$MFT attribute list runlist is malformed.");
                        value = ReadRuns(volume, runs, attribute.DataSize, clusterSize);
                    }
                    attributeList = new List<AttributeListEntry>();
                    if (!AttributeList.TryParse(value, attributeList))
                        throw new InvalidDataException("$MFT attribute list is malformed.");
                    break;
                }
                case AttributeType.Data:
                    if (attribute.IsResident)
                        throw new InvalidDataException("$MFT $DATA cannot be resident.");
                    if (!Runlist.TryDecode(attribute.MappingPairs, attribute.LowestVcn, dataRuns))
                        throw new InvalidDataException("$MFT $DATA runlist is malformed.");
                    break;
                case AttributeType.Bitmap:
                    if (attribute.IsResident)
                    {
                        bitmapResident = attribute.Value.ToArray();
                    }
                    else
                    {
                        if (attribute.LowestVcn == 0)
                            bitmapSize = attribute.DataSize;
                        if (!Runlist.TryDecode(attribute.MappingPairs, attribute.LowestVcn, bitmapRuns))
                            throw new InvalidDataException("$MFT $BITMAP runlist is malformed.");
                    }
                    break;
            }
        }
        if (reader.Malformed)
            throw new InvalidDataException("$MFT record has a malformed attribute.");
    }

    /// <summary>Reads one MFT record by number through the given extents (fixups not applied).</summary>
    private static byte[] ReadRecordThroughRuns(NtfsVolume volume, List<DataRun> runs, long record, int recordSize, int clusterSize)
    {
        long virtualOffset = record * recordSize;
        long vcn = virtualOffset / clusterSize;
        foreach (DataRun run in runs)
        {
            if (vcn >= run.Vcn && vcn < run.Vcn + run.Length && !run.IsSparse)
            {
                long disk = (run.Lcn + (vcn - run.Vcn)) * clusterSize + virtualOffset % clusterSize;
                return ReadDisk(volume, disk, recordSize);
            }
        }
        throw new InvalidDataException($"MFT record {record} is not covered by the known $MFT extents.");
    }

    /// <summary>Reads one MFT record by number (fixups not applied).</summary>
    public byte[] ReadRecord(NtfsVolume volume, long record)
    {
        var runs = new List<DataRun>(Extents);
        return ReadRecordThroughRuns(volume, runs, record, RecordSize, ClusterSize);
    }

    /// <summary>Reads the content of a non-resident attribute.</summary>
    public static byte[] ReadRuns(NtfsVolume volume, List<DataRun> runs, long dataSize, int clusterSize)
    {
        if (dataSize < 0 || dataSize > int.MaxValue)
            throw new InvalidDataException($"Unexpected attribute size {dataSize}.");
        var result = new byte[dataSize];
        long position = 0;
        foreach (DataRun run in runs)
        {
            if (position >= dataSize)
                break;
            long runBytes = run.Length * clusterSize;
            long take = Math.Min(runBytes, dataSize - position);
            if (!run.IsSparse)
            {
                for (long done = 0; done < take;)
                {
                    int chunk = (int)Math.Min(take - done, 1 << 20);
                    byte[] bytes = ReadDisk(volume, run.Lcn * clusterSize + done, chunk);
                    Buffer.BlockCopy(bytes, 0, result, (int)(position + done), chunk);
                    done += chunk;
                }
            }
            position += take;
        }
        return result;
    }

    /// <summary>Reads any byte range of the volume, widening it to sector boundaries internally.</summary>
    public static byte[] ReadDisk(NtfsVolume volume, long offset, int length)
    {
        int sector = Math.Max(volume.Data.BytesPerSector, AlignedBuffer.Alignment);
        long alignedStart = offset & ~((long)sector - 1);
        long alignedEnd = (offset + length + sector - 1) & ~((long)sector - 1);
        using var buffer = new AlignedBuffer((int)(alignedEnd - alignedStart));
        volume.Read(alignedStart, buffer.Pointer, buffer.Length);
        return buffer.Span.Slice((int)(offset - alignedStart), length).ToArray();
    }

    /// <summary>Reads the volume's $UpCase table (record 10), or null when it cannot be read.</summary>
    public UpCaseTable? ReadUpCase(NtfsVolume volume)
    {
        try
        {
            byte[] record = ReadRecord(volume, 10);
            if (MftRecord.ApplyFixups(record) != FixupStatus.Ok)
                return null;
            var reader = new AttributeReader(record);
            while (reader.MoveNext(out AttributeView attribute))
            {
                if (attribute.Type != AttributeType.Data || attribute.NameLength != 0 || attribute.IsResident || attribute.LowestVcn != 0)
                    continue;
                var runs = new List<DataRun>();
                if (!Runlist.TryDecode(attribute.MappingPairs, 0, runs))
                    return null;
                return UpCaseTable.FromNtfs(ReadRuns(volume, runs, attribute.DataSize, ClusterSize));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
        }
        return null;
    }

    /// <summary>
    /// Splits the MFT into read blocks of at most <paramref name="blockBytes"/>, skipping free
    /// regions of at least <paramref name="minimumGapBytes"/>. Boundaries are 4 KiB-aligned so every
    /// physical read stays sector-aligned; a block may span several extents.
    /// </summary>
    public List<ReadBlock> PlanReads(int blockBytes, int minimumGapBytes, out long bytesToRead)
    {
        int alignBytes = Math.Max(SectorSize, AlignedBuffer.Alignment);
        int alignRecords = Math.Max(1, alignBytes / RecordSize);
        alignBytes = alignRecords * RecordSize;
        long pages = (RecordCount + alignRecords - 1) / alignRecords;
        long pagesPerBlock = Math.Max(1, blockBytes / alignBytes);
        long minimumGapPages = Math.Max(1, minimumGapBytes / alignBytes);

        var blocks = new List<ReadBlock>();
        bytesToRead = 0;
        long page = 0;
        while (page < pages)
        {
            while (page < pages && !PageUsed(page, alignRecords))
                page++;
            if (page >= pages)
                break;
            long first = page;
            long lastUsed = page;
            long gap = 0;
            for (page++; page < pages && page - first < pagesPerBlock; page++)
            {
                if (PageUsed(page, alignRecords))
                {
                    lastUsed = page;
                    gap = 0;
                }
                else if (++gap >= minimumGapPages)
                {
                    break;
                }
            }
            long firstRecord = first * alignRecords;
            long endRecord = Math.Min((lastUsed + 1) * alignRecords, RecordCount);
            int byteLength = (int)((lastUsed + 1 - first) * alignBytes);
            blocks.Add(new ReadBlock
            {
                FirstRecord = firstRecord,
                RecordCount = (int)(endRecord - firstRecord),
                ByteLength = byteLength,
                Pieces = MapPieces(firstRecord * RecordSize, byteLength),
            });
            bytesToRead += byteLength;
            page = lastUsed + 1;
        }
        return blocks;
    }

    private bool PageUsed(long page, int alignRecords)
    {
        long start = page * alignRecords;
        long end = Math.Min(start + alignRecords, RecordCount);
        for (long r = start; r < end; r++)
        {
            if ((Bitmap[r >> 6] & (1UL << (int)r)) != 0)
                return true;
        }
        return false;
    }

    private ReadPiece[] MapPieces(long virtualStart, int length)
    {
        var pieces = new List<ReadPiece>(2);
        long virtualEnd = virtualStart + length;
        long position = virtualStart;
        foreach (DataRun run in Extents)
        {
            long runStart = run.Vcn * ClusterSize;
            long runEnd = (run.Vcn + run.Length) * ClusterSize;
            if (runEnd <= position)
                continue;
            if (runStart >= virtualEnd)
                break;
            long from = Math.Max(position, runStart);
            long to = Math.Min(virtualEnd, runEnd);
            if (from > position)
                pieces.Add(new ReadPiece(0, (int)(position - virtualStart), (int)(from - position), Zero: true));
            pieces.Add(run.IsSparse
                ? new ReadPiece(0, (int)(from - virtualStart), (int)(to - from), Zero: true)
                : new ReadPiece(run.Lcn * ClusterSize + (from - runStart), (int)(from - virtualStart), (int)(to - from), Zero: false));
            position = to;
            if (position >= virtualEnd)
                break;
        }
        if (position < virtualEnd)
            pieces.Add(new ReadPiece(0, (int)(position - virtualStart), (int)(virtualEnd - position), Zero: true));
        return pieces.ToArray();
    }
}
