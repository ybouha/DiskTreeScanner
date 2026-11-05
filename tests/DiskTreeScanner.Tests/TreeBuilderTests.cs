using DiskTreeScanner.Core.Analysis;
using DiskTreeScanner.Core.Model;
using Xunit;

namespace DiskTreeScanner.Tests;

public class TreeBuilderTests
{
    private const int N = TreeBuilder.NotANode;

    [Fact]
    public void Layout_IsPreorder_WithContiguousSubtrees()
    {
        // slot: 0 root, 1 orphan spare, 2 A (root), 3 B (root), 4 a1 (A), 5 a2 (A), 6 b1 (B), 7 not a node
        int[] parents = [N, N, 0, 0, 2, 2, 3, N];
        TreeLayout layout = TreeBuilder.Build(parents, 8, rootSlot: 0, orphanSlot: 1, ReadOnlySpan<int>.Empty, orderInParents: false);
        Assert.Equal(6, layout.NodeCount);
        Assert.Equal([0, 2, 4, 5, 3, 6], layout.Order[..6]);
        Assert.Equal([-1, 0, 1, 1, 0, 4], layout.Parent);
        Assert.Equal([6, 4, 3, 4, 6, 6], layout.SubtreeEnd);
        Assert.Equal(-1, layout.OrphansPosition);
    }

    [Fact]
    public void ExcludedSubtree_IsDropped_NotOrphaned()
    {
        int[] parents = [N, N, 0, 2, 3, 0];
        TreeLayout layout = TreeBuilder.Build(parents, 6, 0, 1, [2], orderInParents: true);
        Assert.Equal(2, layout.NodeCount); // root + slot 5
        Assert.Equal(3, layout.ExcludedNodes);
        Assert.Equal(-1, layout.OrphansPosition);
    }

    [Fact]
    public void DeepChain_DoesNotOverflowTheStack()
    {
        const int depth = 200_000;
        var parents = new int[depth + 2];
        parents[0] = N;
        parents[1] = N;
        for (int s = 2; s < parents.Length; s++)
            parents[s] = s == 2 ? 0 : s - 1;
        TreeLayout layout = TreeBuilder.Build(parents, parents.Length, 0, 1, ReadOnlySpan<int>.Empty, orderInParents: true);
        Assert.Equal(depth + 1, layout.NodeCount);
        Assert.Equal(depth + 1, layout.SubtreeEnd[0]);
    }

    [Fact]
    public void MissingParentsAndLoops_EndUnderOrphans_AsTheLastChildOfTheRoot()
    {
        // 2: child of root; 3 -> missing parent; 4 <-> 5 loop; 6 under 5; 7 self loop; 8 -> slot that is not a node
        int[] parents = [N, N, 0, TreeBuilder.MissingParent, 5, 4, 5, 7, 9, N];
        TreeLayout layout = TreeBuilder.Build(parents, 10, 0, 1, ReadOnlySpan<int>.Empty, orderInParents: false);
        Assert.Equal(9, layout.NodeCount); // root, 2, orphans, 3, 4, 5, 6, 7, 8
        int orphans = layout.OrphansPosition;
        Assert.Equal(2, orphans);
        Assert.Equal(1, layout.Order[orphans]);
        Assert.Equal(4, layout.OrphanRoots); // 3, the loop cut, 7, 8
        Assert.Equal(6, layout.OrphanNodes);
        Assert.Equal(9, layout.SubtreeEnd[0]);
        Assert.Equal(9, layout.SubtreeEnd[orphans]);
    }

    [Fact]
    public void Aggregation_MatchesANaiveRecursiveSum_OnALargeRandomTree()
    {
        var random = new Random(7);
        const int count = 300_000;
        var parents = new int[count + 2];
        parents[0] = N;
        parents[1] = N;
        var isDirectory = new bool[count + 2];
        isDirectory[0] = true;
        for (int s = 2; s < parents.Length; s++)
        {
            // parent among earlier directories
            int p;
            do
            {
                p = random.Next(0, s);
            }
            while (!isDirectory[p] || p == 1);
            parents[s] = p;
            isDirectory[s] = random.Next(4) == 0;
        }
        var expectedSize = new long[parents.Length];
        var ownSize = new long[parents.Length];
        for (int s = 2; s < parents.Length; s++)
            ownSize[s] = isDirectory[s] ? 0 : random.Next(0, 1_000_000);
        for (int s = parents.Length - 1; s >= 2; s--)
        {
            expectedSize[s] += ownSize[s];
            expectedSize[parents[s]] += expectedSize[s];
        }

        int[] copy = (int[])parents.Clone();
        TreeLayout layout = TreeBuilder.Build(copy, copy.Length, 0, 1, ReadOnlySpan<int>.Empty, orderInParents: false);
        int n = layout.NodeCount;
        var size = new long[n];
        var flags = new uint[n];
        for (int i = 0; i < n; i++)
        {
            int s = layout.Order[i];
            size[i] = ownSize[s];
            flags[i] = isDirectory[s] ? (uint)NodeFlags.Directory : 0;
        }
        var model = new TreeModel(n, layout.Parent, layout.SubtreeEnd, size, new long[n], flags, new long[n], new int[n], new ushort[n],
            new NameArena(), null, @"X:\", UpCaseTable.Invariant);
        Aggregator.Aggregate(model);
        int expectedFolders = isDirectory.Skip(2).Count(d => d);
        Assert.Equal(expectedSize[0], model.Size[0]);
        Assert.Equal(expectedFolders, model.FolderCount(0));
        for (int i = 0; i < n; i += 997)
            Assert.Equal(expectedSize[layout.Order[i]], model.Size[i]);
    }
}

public class AnalysisTests
{
    private static TreeModel Flat(params long[] sizes)
    {
        int n = sizes.Length + 1;
        var parent = new int[n];
        var end = new int[n];
        var size = new long[n];
        parent[0] = -1;
        end[0] = n;
        for (int i = 1; i < n; i++)
        {
            parent[i] = 0;
            end[i] = i + 1;
            size[i] = sizes[i - 1];
        }
        var flags = new uint[n];
        flags[0] = (uint)NodeFlags.Directory;
        var model = new TreeModel(n, parent, end, size, (long[])size.Clone(), flags, new long[n], new int[n], new ushort[n], new NameArena(), null, @"X:\", UpCaseTable.Invariant);
        Aggregator.Aggregate(model);
        return model;
    }

    [Fact]
    public void LargestFiles_ReturnsTheTopN_InDescendingOrder()
    {
        TreeModel m = Flat(5, 900, 3, 77, 900, 12);
        int[] top = LargestFiles.Find(m, 0, 3);
        Assert.Equal([900L, 900L, 77L], top.Select(i => m.Size[i]));
    }

    [Fact]
    public void ChildOrder_SortsBySizeDescending_WithStableTies()
    {
        TreeModel m = Flat(5, 900, 3, 900);
        int[] sorted = ChildOrder.Sorted(m, 0, SortColumn.Size, descending: true);
        Assert.Equal([2, 4, 1, 3], sorted);
    }
}

public class NameArenaTests
{
    [Fact]
    public void Latin1AndWideNames_RoundTrip()
    {
        var arena = new NameArena();
        NameArena.Writer writer = arena.CreateWriter();
        string[] names = ["plain.txt", "Téléchargements", "日本語のファイル.txt", "emoji 😀.png", ""];
        var refs = names.Select(n => (Offset: writer.Append(n, out ushort length), Length: length)).ToArray();
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], arena.GetString(refs[i].Offset, refs[i].Length));
        Assert.False(NameArena.IsWide(refs[1].Length)); // Latin-1: one byte per char
        Assert.True(NameArena.IsWide(refs[2].Length));
    }

    [Fact]
    public void ManyNames_SpanSeveralChunks()
    {
        var arena = new NameArena();
        NameArena.Writer writer = arena.CreateWriter();
        var refs = new List<(int, ushort, string)>();
        for (int i = 0; i < 100_000; i++)
        {
            string name = $"file number {i} with a reasonably long name.bin";
            refs.Add((writer.Append(name, out ushort length), length, name));
        }
        Assert.True(arena.ChunkCount > 1);
        foreach (var (offset, length, name) in refs.Where((_, i) => i % 991 == 0))
            Assert.Equal(name, arena.GetString(offset, length));
    }
}
