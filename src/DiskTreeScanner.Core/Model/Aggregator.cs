using System.Runtime.CompilerServices;

namespace DiskTreeScanner.Core.Model;

/// <summary>
/// Bottom-up aggregation of size, allocated size and most recent change. File and folder counts
/// need no aggregation: they come from the DFS ranges (see <see cref="TreeModel.FolderCount"/>).
/// </summary>
/// <remarks>
/// In DFS preorder a node's descendants all sit after it, so visiting positions in decreasing order
/// completes every child before its parent (a postorder walk without recursion). Independent
/// subtrees are disjoint ranges: they are aggregated in parallel, starting from the first-level
/// subtrees and splitting the oversized ones further, then the few nodes above them are folded
/// sequentially.
/// Precondition: directories hold their own values only (0 size).
/// </remarks>
public static class Aggregator
{
    public static void Aggregate(TreeModel model)
    {
        int n = model.Count;
        if (n <= 1)
            return;

        int[] subtreeEnd = model.SubtreeEnd;
        int limit = Math.Max(16_384, n / (Environment.ProcessorCount * 4));
        var frontier = new List<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            int u = pending.Pop();
            foreach (int c in model.Children(u))
            {
                int rangeSize = subtreeEnd[c] - c;
                if (rangeSize > limit)
                    pending.Push(c);
                else if (rangeSize > 1)
                    frontier.Add(c);
            }
        }
        frontier.Sort();

        Parallel.For(0, frontier.Count, k =>
        {
            int f = frontier[k];
            FoldRange(model, f + 1, subtreeEnd[f]);
        });

        // Nodes outside the frontier ranges, in decreasing order.
        int i = n - 1;
        for (int k = frontier.Count - 1; k >= 0; k--)
        {
            int f = frontier[k];
            int end = subtreeEnd[f];
            for (; i >= end; i--)
                Fold(model, i);
            Fold(model, f);
            i = f - 1;
        }
        for (; i > 0; i--)
            Fold(model, i);
    }

    /// <summary>Resets directory totals so the tree can be aggregated again.</summary>
    public static void ResetDirectories(TreeModel model)
    {
        for (int i = 0; i < model.Count; i++)
        {
            if (model.IsDirectory(i))
            {
                model.Size[i] = 0;
                model.Allocated[i] = 0;
            }
        }
    }

    private static void FoldRange(TreeModel model, int from, int to)
    {
        for (int i = to - 1; i >= from; i--)
            Fold(model, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fold(TreeModel model, int i)
    {
        int p = model.Parent[i];
        model.Size[p] += model.Size[i];
        model.Allocated[p] += model.Allocated[i];
        if (model.Modified[i] > model.Modified[p])
            model.Modified[p] = model.Modified[i];
    }
}
