using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.Core.Analysis;

/// <summary>Top N files of a subtree: one linear pass over its DFS range with a bounded min-heap.</summary>
public static class LargestFiles
{
    public static int[] Find(TreeModel model, int node, int count, bool byAllocated = false, CancellationToken cancellationToken = default) =>
        Find(model, node, count, null, out _, byAllocated, cancellationToken);

    /// <summary>Same, keeping only the files within the value criteria of <paramref name="values"/>; <paramref name="kept"/> counts them.</summary>
    public static int[] Find(TreeModel model, int node, int count, ItemFilter? values, out int kept, bool byAllocated = false, CancellationToken cancellationToken = default)
    {
        kept = 0;
        if (count <= 0)
            return Array.Empty<int>();
        bool check = values is { HasValueCriteria: true };
        long[] key = byAllocated ? model.Allocated : model.Size;
        uint[] flags = model.Flags;
        var heap = new PriorityQueue<int, long>(count + 1);
        int end = model.SubtreeEnd[node];
        for (int i = node + 1; i < end; i++)
        {
            if ((flags[i] & (uint)NodeFlags.Directory) != 0 || (check && !values!.MatchesValues(model, i)))
                continue;
            kept++;
            long value = key[i];
            if (heap.Count < count)
            {
                heap.Enqueue(i, value);
            }
            else if (heap.TryPeek(out _, out long smallest) && value > smallest)
            {
                heap.EnqueueDequeue(i, value);
            }
            if ((i & 0xFFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
        var result = new int[heap.Count];
        for (int k = result.Length - 1; k >= 0; k--)
            result[k] = heap.Dequeue();
        return result;
    }

    /// <summary>Number of files in a subtree (excluding folders).</summary>
    public static int CountFiles(TreeModel model, int node) => model.IsDirectory(node) ? model.FileCount(node) : 1;
}
