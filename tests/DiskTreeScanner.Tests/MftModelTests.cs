using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Records;
using DiskTreeScanner.Tests.Synthetic;
using Xunit;

namespace DiskTreeScanner.Tests;

/// <summary>Synthetic MFTs through the real parser, merge, DFS build and aggregation.</summary>
public class MftModelTests
{
    [Fact]
    public void Tree_IsNumberedInPreorder_AndAggregated()
    {
        var volume = new SyntheticVolume()
            .Directory(30, 5, "A")
            .File(31, 30, "a1.bin", 10_000)
            .File(32, 30, "a2.txt", 100)
            .Directory(33, 30, "Sub")
            .File(34, 33, "deep.bin", 1_000_000)
            .Directory(35, 5, "B")
            .File(36, 5, "root.txt", 5000);
        TreeModel m = volume.Scan();

        Assert.Equal(7, m.ItemCount(0));
        Assert.Equal(4, m.FileCount(0));
        Assert.Equal(3, m.FolderCount(0));
        Assert.Equal(10_000 + 100 + 1_000_000 + 5000, m.Size[0]);
        Assert.Equal(12_288 + 0 + 1_003_520 + 8192, m.Allocated[0]);

        int a = m.Node("A");
        Assert.Equal(4, m.ItemCount(a));
        Assert.Equal(1, m.FolderCount(a));
        Assert.Equal(1_010_100, m.Size[a]);
        // Every subtree is a contiguous range.
        for (int i = 0; i < m.Count; i++)
        {
            for (int c = i + 1; c < m.SubtreeEnd[i]; c = m.SubtreeEnd[c])
                Assert.Equal(i, m.Parent[c]);
        }
        Assert.Equal(@"X:\A\Sub\deep.bin", m.GetPath(m.Node(@"A\Sub\deep.bin")));
        Assert.Equal(-1, m.Orphans());
    }

    [Fact]
    public void HardLink_IsCountedOnceOnTheFirstLink_ByDefault()
    {
        TreeModel m = HardLinkVolume().Scan();
        int first = m.Node(@"A\shared.bin");
        int second = m.Node(@"B\other name.bin");
        Assert.Equal(1_000_000, m.Size[first]);
        Assert.Equal(0, m.Size[second]);
        Assert.Equal(0, m.Allocated[second]);
        Assert.True(m.HasFlag(second, NodeFlags.HardLinkSecondary));
        Assert.Equal(1_000_000, m.Size[0]);
        Assert.Equal(2, m.FileCount(0)); // both names are entries
    }

    [Fact]
    public void HardLink_CanBeCountedOnEveryLink()
    {
        TreeModel m = HardLinkVolume().Scan(new ScanOptions { HardLinks = HardLinkMode.EveryLink });
        Assert.Equal(2_000_000, m.Size[0]);
        Assert.Equal(2 * 1_003_520, m.Allocated[0]);
    }

    [Fact]
    public void HardLink_MixedMode_SizeOnEveryLink_AllocationOnce()
    {
        TreeModel m = HardLinkVolume().Scan(new ScanOptions { HardLinks = HardLinkMode.SizeEveryLinkAllocatedOnce });
        Assert.Equal(2_000_000, m.Size[0]);
        Assert.Equal(1_003_520, m.Allocated[0]);
    }

    private static SyntheticVolume HardLinkVolume()
    {
        var volume = new SyntheticVolume().Directory(30, 5, "A").Directory(31, 5, "B");
        volume.Add(new RecordBuilder { RecordNumber = 40 }
            .StandardInformation()
            .FileName(30, 1, "shared.bin")
            .FileName(31, 1, "other name.bin", FileNameNamespace.Posix)
            .NonResidentData(1_000_000, 1_003_520));
        return volume;
    }

    [Fact]
    public void DosName_IsIgnoredWhenALongNameExists()
    {
        var volume = new SyntheticVolume();
        volume.Add(new RecordBuilder { RecordNumber = 40 }
            .StandardInformation()
            .FileName(5, 5, "PROGRA~1", FileNameNamespace.Dos)
            .FileName(5, 5, "Program Files", FileNameNamespace.Win32)
            .ResidentData(10));
        TreeModel m = volume.Scan();
        Assert.Equal(["Program Files"], m.ChildNames(0));
    }

    [Fact]
    public void DosOnlyName_IsUsedWhenItIsTheOnlyName()
    {
        var volume = new SyntheticVolume();
        volume.Add(new RecordBuilder { RecordNumber = 40 }.StandardInformation().FileName(5, 5, "README.TXT", FileNameNamespace.Dos).ResidentData(10));
        Assert.Equal(["README.TXT"], volume.Scan().ChildNames(0));
    }

    [Fact]
    public void ExtensionRecord_DataIsMergedIntoTheBaseRecord()
    {
        var volume = new SyntheticVolume();
        volume.Add(new RecordBuilder { RecordNumber = 50, Sequence = 4 }
            .StandardInformation()
            .AttributeList((AttributeType.StandardInformation, 0, 50, 4), (AttributeType.FileName, 0, 50, 4), (AttributeType.Data, 0, 51, 2))
            .FileName(5, 5, "fragmented.vhdx"));
        volume.Add(new RecordBuilder { RecordNumber = 51, Sequence = 2, BaseRecord = 50, BaseSequence = 4 }
            .NonResidentData(5_000_000_000, 5_000_003_584));
        TreeModel m = volume.Scan();
        Assert.Equal(1, m.ItemCount(0)); // the extension record is not a node
        int file = m.Node("fragmented.vhdx");
        Assert.Equal(5_000_000_000, m.Size[file]);
        Assert.Equal(5_000_003_584, m.Allocated[file]);
        Assert.Equal(1, volume.Statistics.ExtensionRecords);
    }

    [Fact]
    public void ExtensionRecord_NameIsMergedIntoTheBaseRecord()
    {
        var volume = new SyntheticVolume();
        volume.Add(new RecordBuilder { RecordNumber = 52, Sequence = 3 }.StandardInformation().AttributeList((AttributeType.FileName, 0, 53, 1)).ResidentData(42));
        volume.Add(new RecordBuilder { RecordNumber = 53, BaseRecord = 52, BaseSequence = 3 }.FileName(5, 5, "named elsewhere.txt"));
        TreeModel m = volume.Scan();
        Assert.Equal(42, m.Size[m.Node("named elsewhere.txt")]);
    }

    [Fact]
    public void StaleExtensionRecord_FromAPreviousUseOfTheBaseRecord_IsIgnored()
    {
        var volume = new SyntheticVolume().File(50, 5, "file.txt", 100, sequence: 6);
        volume.Add(new RecordBuilder { RecordNumber = 51, BaseRecord = 50, BaseSequence = 5 }.NonResidentData(9_999_999, 10_002_432));
        TreeModel m = volume.Scan();
        Assert.Equal(100, m.Size[0]);
        Assert.Equal(1, volume.Statistics.StaleExtensions);
    }

    [Fact]
    public void MissingParent_IsAttachedToOrphans()
    {
        TreeModel m = new SyntheticVolume().File(40, 999, "lost.txt", 100, parentSequence: 1).Scan();
        int orphans = m.Orphans();
        Assert.True(orphans > 0);
        Assert.Equal(TreeModel.OrphansName, m.GetName(orphans));
        Assert.Equal(["lost.txt"], m.ChildNames(orphans));
        Assert.True(m.HasFlag(m.Node(@"<orphans>\lost.txt"), NodeFlags.Orphan));
        Assert.Equal(100, m.Size[0]); // orphans still count in the volume total
    }

    [Fact]
    public void ReusedParentRecord_WithANewSequenceNumber_DoesNotAdoptOldChildren()
    {
        // Record 30 now holds folder "New" (sequence 8); "old.txt" still points to sequence 7.
        TreeModel m = new SyntheticVolume()
            .Directory(30, 5, "New", sequence: 8)
            .File(40, 30, "old.txt", 100, parentSequence: 7)
            .File(41, 30, "current.txt", 100, parentSequence: 8)
            .Scan();
        Assert.Equal(["current.txt"], m.ChildNames(m.Node("New")));
        Assert.Equal(["old.txt"], m.ChildNames(m.Orphans()));
    }

    [Fact]
    public void ParentLoop_IsCut_WithoutHangingOrCrashing()
    {
        var volume = new SyntheticVolume();
        volume.Add(new RecordBuilder { RecordNumber = 60, Directory = true }.StandardInformation(0x10).FileName(61, 1, "ping"));
        volume.Add(new RecordBuilder { RecordNumber = 61, Directory = true }.StandardInformation(0x10).FileName(60, 1, "pong"));
        volume.Add(new RecordBuilder { RecordNumber = 62, Directory = true }.StandardInformation(0x10).FileName(62, 1, "self"));
        volume.File(63, 61, "inside.txt", 300);
        TreeModel m = volume.Scan();
        int orphans = m.Orphans();
        Assert.True(orphans > 0);
        Assert.Equal(4, m.ItemCount(orphans)); // pong (the loop is cut there), ping, inside.txt, self: nothing lost
        Assert.Equal(300, m.Size[orphans]);
    }

    [Fact]
    public void ParentThatIsAFile_IsRejected()
    {
        TreeModel m = new SyntheticVolume().File(40, 5, "a file", 10).File(41, 40, "child of a file", 10).Scan();
        Assert.False(m.HasChildren(m.Node("a file")));
        Assert.Equal(["child of a file"], m.ChildNames(m.Orphans()));
    }

    [Fact]
    public void BadRecord_IsCountedAndSkipped()
    {
        var volume = new SyntheticVolume().File(40, 5, "good.txt", 10);
        volume.Add(41, new RecordBuilder { RecordNumber = 41 }.StandardInformation().FileName(5, 5, "torn.txt").Build(tornWrite: true));
        TreeModel m = volume.Scan();
        Assert.Equal(["good.txt"], m.ChildNames(0));
        Assert.Equal(1, volume.Statistics.BadRecords);
    }

    [Fact]
    public void NtfsSpecialFiles_AreIncludedByDefault_AndCanBeExcluded()
    {
        SyntheticVolume Build()
        {
            var v = new SyntheticVolume();
            v.Add(new RecordBuilder { RecordNumber = 0 }.StandardInformation(0x6).FileName(5, 5, "$MFT").NonResidentData(1_048_576, 1_048_576));
            v.Add(new RecordBuilder { RecordNumber = 11, Directory = true }.StandardInformation(0x16).FileName(5, 5, "$Extend"));
            v.File(25, 11, "$Deleted file", 8192);
            v.File(40, 5, "hiberfil.sys", 4_000_000);
            return v;
        }
        TreeModel included = Build().Scan();
        Assert.Equal(1_048_576 + 8192 + 4_000_000, included.Size[0]);
        Assert.True(included.HasFlag(included.Node("$MFT"), NodeFlags.NtfsSpecial));
        Assert.True(included.HasFlag(included.Node(@"$Extend\$Deleted file"), NodeFlags.NtfsSpecial));
        Assert.False(included.HasFlag(included.Node("hiberfil.sys"), NodeFlags.NtfsSpecial));

        TreeModel excluded = Build().Scan(new ScanOptions { IncludeNtfsSpecialFiles = false });
        Assert.Equal(["hiberfil.sys"], excluded.ChildNames(0)); // hiberfil.sys stays visible
        Assert.Equal(4_000_000, excluded.Size[0]);
        Assert.Equal(-1, excluded.Orphans()); // $Extend's children are excluded, not orphaned
    }

    [Fact]
    public void AlternateStreams_AreCountedApart_UnlessIncluded()
    {
        SyntheticVolume Build()
        {
            var v = new SyntheticVolume();
            v.Add(new RecordBuilder { RecordNumber = 40 }.StandardInformation().FileName(5, 5, "doc.pdf")
                .NonResidentData(100_000, 102_400).NonResidentData(20_000, 20_480, "thumbnail"));
            return v;
        }
        SyntheticVolume defaults = Build();
        TreeModel m = defaults.Scan();
        Assert.Equal(100_000, m.Size[0]);
        Assert.Equal(102_400, m.Allocated[0]);
        Assert.True(m.HasFlag(m.Node("doc.pdf"), NodeFlags.HasAlternateStreams));
        Assert.Equal(1, defaults.Statistics.AlternateStreams);
        Assert.Equal(20_000, defaults.Statistics.AlternateStreamSize);

        TreeModel withStreams = Build().Scan(new ScanOptions { IncludeAlternateStreams = true });
        Assert.Equal(120_000, withStreams.Size[0]);
        Assert.Equal(122_880, withStreams.Allocated[0]);
    }

    [Fact]
    public void MetadataStreams_CountAsAllocation_ButBadClusMapDoesNot()
    {
        var volume = new SyntheticVolume();
        volume.Add(new RecordBuilder { RecordNumber = 8 }.StandardInformation(0x6).FileName(5, 5, "$BadClus")
            .ResidentData(0).NonResidentData(500_000_000_000, 500_000_000_000, "$Bad"));
        volume.Add(new RecordBuilder { RecordNumber = 9 }.StandardInformation(0x6).FileName(5, 5, "$Secure")
            .NonResidentData(16_000_000, 16_068_608, "$SDS"));
        TreeModel m = volume.Scan();
        Assert.Equal(0, m.Allocated[m.Node("$BadClus")]);
        Assert.Equal(16_068_608, m.Allocated[m.Node("$Secure")]);
        Assert.Equal(0, m.Size[0]);
        Assert.Equal(0, volume.Statistics.AlternateStreams);
    }

    [Fact]
    public void WofCompressedFile_IsAllocatedByItsWofStream()
    {
        var volume = new SyntheticVolume();
        volume.Add(new RecordBuilder { RecordNumber = 40 }.StandardInformation(0x420).FileName(5, 5, "app.dll")
            .NonResidentData(1_000_000, 1_003_520, flags: 0x8000, totalAllocated: 0)
            .NonResidentData(300_000, 303_104, "WofCompressedData"));
        TreeModel m = volume.Scan();
        int file = m.Node("app.dll");
        Assert.Equal(1_000_000, m.Size[file]);
        Assert.Equal(303_104, m.Allocated[file]);
        Assert.True(m.HasFlag(file, NodeFlags.WofCompressed));
    }

    [Fact]
    public void LastChange_OfAFolder_IsTheMostRecentChangeBelowIt()
    {
        TreeModel m = new SyntheticVolume()
            .Directory(30, 5, "A")
            .File(31, 30, "old", 10, modified: 100)
            .File(32, 30, "recent", 10, modified: RecordBuilder.DefaultTime + 1000)
            .Scan();
        Assert.Equal(RecordBuilder.DefaultTime + 1000, m.Modified[m.Node("A")]);
        Assert.Equal(RecordBuilder.DefaultTime + 1000, m.Modified[0]);
    }

    [Fact]
    public void CaseInsensitiveLookup_UsesTheCaseTable()
    {
        TreeModel m = new SyntheticVolume().Directory(30, 5, "Users").File(31, 30, "Élise.txt", 5).Scan();
        Assert.Equal(m.Node(@"Users\Élise.txt"), m.Node(@"USERS\ÉLISE.TXT"));
    }
}
