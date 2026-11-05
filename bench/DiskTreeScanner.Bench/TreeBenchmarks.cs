using BenchmarkDotNet.Attributes;
using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.Bench;

/// <summary>Tree build and bottom-up aggregation of 2 million nodes (a system drive).</summary>
public class TreeBenchmarks
{
    private const int Nodes = 2_000_000;

    private SyntheticTree _tree = null!;
    private int[] _parents = null!;
    private TreeModel _model = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tree = SyntheticTree.Generate(Nodes, seed: 7);
        _parents = new int[_tree.Parents.Length];
        _model = _tree.ToModel();
    }

    /// <summary>The build rewrites the links it is given, so each run starts from a fresh copy (8 MB memcpy).</summary>
    [Benchmark(Description = "Build tree (CSR, DFS preorder, ranges)")]
    public TreeLayout BuildTree()
    {
        _tree.Parents.CopyTo(_parents, 0);
        return TreeBuilder.Build(_parents, _parents.Length, 0, _tree.OrphanSlot, ReadOnlySpan<int>.Empty, orderInParents: true);
    }

    [Benchmark(Description = "Reset folder totals (cost included below)")]
    public void ResetDirectories() => Aggregator.ResetDirectories(_model);

    [Benchmark(Description = "Reset + aggregate (parallel)")]
    public long Aggregate()
    {
        Aggregator.ResetDirectories(_model);
        Aggregator.Aggregate(_model);
        return _model.Size[0];
    }
}
