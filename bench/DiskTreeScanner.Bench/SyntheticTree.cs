using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.Bench;

/// <summary>
/// "Slot → parent" links shaped like a system drive, in the order an MFT yields them.
/// </summary>
/// <remarks>
/// Folders attach to a uniformly chosen folder (depth grows like ln N: about 12 on average). Files go
/// to one of the most recent folders (an install or an unpacked archive) or, preferentially, to
/// folders that already have many children, which gives a few huge folders like WinSxS. Records are
/// in creation order, except 5 % of them that swap places (reused records: a child before its
/// parent), and 64 nodes have a missing parent (live volume: they end up under &lt;orphans&gt;).
/// </remarks>
internal sealed class SyntheticTree
{
    private SyntheticTree(int[] parents, bool[] isDirectory, int seed)
    {
        Parents = parents;
        IsDirectory = isDirectory;
        Seed = seed;
    }

    /// <summary>Parent slot of every slot; slot 0 is the root, the last slot is a spare for &lt;orphans&gt;.</summary>
    public int[] Parents { get; }

    public bool[] IsDirectory { get; }

    public int Seed { get; }

    public int OrphanSlot => Parents.Length - 1;

    public static SyntheticTree Generate(int nodes, int seed)
    {
        var rng = new Random(seed);
        var parents = new int[nodes];
        var isDirectory = new bool[nodes];
        var directories = new List<int>(nodes / 7) { 0 };
        var popular = new List<int>(nodes + nodes / 7) { 0 }; // a folder appears once, plus once per child
        isDirectory[0] = true;
        for (int s = 1; s < nodes; s++)
        {
            int parent;
            if (rng.NextDouble() < 0.12)
            {
                parent = directories[rng.Next(directories.Count)];
                isDirectory[s] = true;
                directories.Add(s);
                popular.Add(s);
            }
            else
            {
                parent = rng.NextDouble() < 0.6
                    ? directories[directories.Count - 1 - rng.Next(Math.Min(16, directories.Count))]
                    : popular[rng.Next(popular.Count)];
            }
            parents[s] = parent;
            popular.Add(parent);
        }

        // Reused records: swap 5 % of the slots (the root stays at slot 0).
        var slotOf = new int[nodes];
        for (int s = 0; s < nodes; s++)
            slotOf[s] = s;
        for (int k = 0; k < nodes / 20; k++)
        {
            int a = 1 + rng.Next(nodes - 1), b = 1 + rng.Next(nodes - 1);
            (slotOf[a], slotOf[b]) = (slotOf[b], slotOf[a]);
        }
        var shuffledParents = new int[nodes + 1];
        var shuffledDirectory = new bool[nodes + 1];
        for (int s = 0; s < nodes; s++)
        {
            shuffledParents[slotOf[s]] = s == 0 ? TreeBuilder.MissingParent : slotOf[parents[s]];
            shuffledDirectory[slotOf[s]] = isDirectory[s];
        }
        for (int k = 0; k < 64; k++)
            shuffledParents[1 + rng.Next(nodes - 1)] = TreeBuilder.MissingParent;
        shuffledParents[nodes] = TreeBuilder.NotANode;
        return new SyntheticTree(shuffledParents, shuffledDirectory, seed);
    }

    /// <summary>The tree as a model in DFS order, with file sizes set and folders not yet aggregated.</summary>
    public TreeModel ToModel()
    {
        int[] work = (int[])Parents.Clone();
        TreeLayout layout = TreeBuilder.Build(work, work.Length, 0, OrphanSlot, ReadOnlySpan<int>.Empty, orderInParents: false);
        int n = layout.NodeCount;
        var rng = new Random(Seed);
        var size = new long[n];
        var allocated = new long[n];
        var flags = new uint[n];
        var modified = new long[n];
        for (int pos = 0; pos < n; pos++)
        {
            int slot = layout.Order[pos];
            if (slot == OrphanSlot)
            {
                flags[pos] = (uint)(NodeFlags.Directory | NodeFlags.Synthetic);
                continue;
            }
            modified[pos] = 0x01DC_4E00_0000_0000 - rng.NextInt64(1L << 52);
            if (IsDirectory[slot])
            {
                flags[pos] = (uint)NodeFlags.Directory;
                continue;
            }
            size[pos] = (long)Math.Exp(rng.NextDouble() * Math.Log(1L << 30)) - 1;
            allocated[pos] = (size[pos] + 4095) / 4096 * 4096;
            flags[pos] = (uint)NodeFlags.Archive;
        }
        return new TreeModel(n, layout.Parent, layout.SubtreeEnd, size, allocated, flags, modified, new int[n], new ushort[n],
            new NameArena(), null, @"X:\", UpCaseTable.Invariant);
    }
}
