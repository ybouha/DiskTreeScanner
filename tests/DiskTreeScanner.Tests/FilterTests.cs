using DiskTreeScanner.Core.Analysis;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Tests.Synthetic;
using Xunit;

namespace DiskTreeScanner.Tests;

/// <summary>Column filters: file criteria (name, size, date) and the totals they leave on the folders.</summary>
public class FilterTests
{
    private const long Old = RecordBuilder.DefaultTime - 1_000_000_000_000_000; // about 3 years earlier

    /// <summary>
    /// X:\ docs\{a.txt 1 000, big.iso 5 000 000, old.log 300 000 (old)}
    ///     vm\{disk.vhdx 50 000 000, sub\{tiny.vhdx 100}}
    ///     readme.md 50
    /// </summary>
    private static TreeModel Sample()
    {
        var v = new SyntheticVolume();
        v.Directory(20, 5, "docs");
        v.File(21, 20, "a.txt", 1_000);
        v.File(22, 20, "big.iso", 5_000_000);
        v.File(23, 20, "old.log", 300_000, modified: Old);
        v.Directory(24, 5, "vm");
        v.File(25, 24, "disk.vhdx", 50_000_000);
        v.Directory(26, 24, "sub");
        v.File(27, 26, "tiny.vhdx", 100);
        v.File(28, 5, "readme.md", 50);
        return v.Scan();
    }

    [Fact]
    public void SizeCriterion_KeepsTheLargeFiles_AndTheirFolders()
    {
        TreeModel m = Sample();
        var totals = NameSearch.Filter(m, 0, new ItemFilter { MinSize = 200_000 }, CancellationToken.None);

        Assert.Equal(3, totals[0].Files);
        Assert.Equal(5_000_000 + 300_000 + 50_000_000, totals[0].Size);
        Assert.Equal(2, totals[0].Folders); // docs and vm; sub has no large file
        Assert.Equal(2, totals[m.Node("docs")].Files);
        Assert.Equal(0, totals[m.Node(@"vm")].Folders);
        Assert.False(totals.ContainsKey(m.Node(@"vm\sub")));
        Assert.False(totals.ContainsKey(m.Node("readme.md")));
        Assert.False(totals.ContainsKey(m.Node(@"docs\a.txt")));
    }

    [Fact]
    public void NameAndSize_MustBothMatch()
    {
        TreeModel m = Sample();
        NamePattern pattern = NamePattern.Parse("*.vhdx", m.UpCase)!;
        var totals = NameSearch.Filter(m, 0, new ItemFilter { Name = pattern, MinSize = 1_000 }, CancellationToken.None);

        Assert.Equal(1, totals[0].Files);
        Assert.Equal(50_000_000, totals[0].Size);
        Assert.True(totals.ContainsKey(m.Node(@"vm\disk.vhdx")));
        Assert.False(totals.ContainsKey(m.Node(@"vm\sub")));
    }

    [Fact]
    public void MatchingFolder_StandsForItsContent_CheckedAgainstTheValues()
    {
        TreeModel m = Sample();
        NamePattern pattern = NamePattern.Parse("vm", m.UpCase)!;
        var totals = NameSearch.Filter(m, 0, new ItemFilter { Name = pattern, MaxSize = 1_000 }, CancellationToken.None);

        Assert.Equal(1, totals[0].Files); // tiny.vhdx: inside vm, small enough; disk.vhdx is too large
        Assert.Equal(100, totals[0].Size);
        Assert.Equal(2, totals[0].Folders); // vm and vm\sub
        Assert.False(totals.ContainsKey(m.Node(@"vm\disk.vhdx")));
    }

    [Fact]
    public void MatchingFolder_WithoutValues_CountsWhole()
    {
        TreeModel m = Sample();
        var totals = NameSearch.Filter(m, 0, NamePattern.Parse("vm", m.UpCase)!, CancellationToken.None);

        Assert.Equal(2, totals[0].Files);
        Assert.Equal(50_000_100, totals[0].Size);
        Assert.Equal(2, totals[0].Folders);
        Assert.True(totals.ContainsKey(m.Node(@"vm\sub\tiny.vhdx")));
    }

    [Fact]
    public void DateCriterion_DropsOldFiles()
    {
        TreeModel m = Sample();
        var totals = NameSearch.Filter(m, 0, new ItemFilter { ModifiedFrom = Old + 1 }, CancellationToken.None);

        Assert.False(totals.ContainsKey(m.Node(@"docs\old.log")));
        Assert.Equal(5, totals[0].Files);
    }

    [Fact]
    public void LargestFiles_HonorTheValueCriteria()
    {
        TreeModel m = Sample();
        int[] top = LargestFiles.Find(m, 0, 2, new ItemFilter { MaxSize = 1_000_000 }, out int kept);

        Assert.Equal(4, kept); // a.txt, old.log, tiny.vhdx, readme.md
        Assert.Equal([300_000L, 1_000L], top.Select(i => m.Size[i]));
    }

    [Fact]
    public void Search_WithValues_ListsFilesOnly()
    {
        TreeModel m = Sample();
        NamePattern pattern = NamePattern.Parse("*", m.UpCase)!;
        SearchResult found = NameSearch.Find(m, 0, pattern, new ItemFilter { MinSize = 5_000_000 }, 10, CancellationToken.None);

        Assert.Equal(2, found.TotalMatches);
        Assert.All(found.Matches, i => Assert.False(m.IsDirectory(i)));
    }
}
