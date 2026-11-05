using System.Diagnostics;

namespace DiskTreeScanner.Core.Model;

/// <summary>DFS preorder numbering of raw slots, produced by <see cref="TreeBuilder.Build"/>.</summary>
public sealed class TreeLayout
{
    public required int NodeCount { get; init; }

    /// <summary>Raw slot of each position (<c>Order[pos] = slot</c>). Length may exceed <see cref="NodeCount"/>.</summary>
    public required int[] Order { get; init; }

    /// <summary>Parent position of each position, -1 for the root.</summary>
    public required int[] Parent { get; init; }

    /// <summary>End (exclusive) of each position's subtree range.</summary>
    public required int[] SubtreeEnd { get; init; }

    /// <summary>Position of the synthetic &lt;orphans&gt; folder, or -1 when every node was reachable.</summary>
    public int OrphansPosition { get; init; } = -1;

    /// <summary>Nodes attached directly under &lt;orphans&gt; (missing parent, stale reference or loop).</summary>
    public int OrphanRoots { get; init; }

    /// <summary>Nodes in the orphan subtrees, &lt;orphans&gt; itself excluded.</summary>
    public int OrphanNodes { get; init; }

    /// <summary>Nodes dropped with an excluded subtree (e.g. NTFS special files when not included).</summary>
    public int ExcludedNodes { get; init; }

    /// <summary>Time spent in each step (classification, CSR, DFS, ranges).</summary>
    public string Breakdown { get; init; } = string.Empty;
}

/// <summary>
/// Turns "slot → parent slot" links into a tree numbered in DFS preorder.
/// </summary>
/// <remarks>
/// <para>1. Classification: every node walks up its parent chain until it meets a node already
/// classified, the root, an excluded root, a missing parent or itself (a loop). Each node is walked
/// once. Unreachable chains get their top re-parented to the synthetic &lt;orphans&gt; folder (the
/// loop is cut where it closes); excluded subtrees are dropped. Afterwards the links form a proper
/// tree, so nothing below can recurse forever.</para>
/// <para>2. CSR: children are grouped by counting per parent, prefix sum, fill.</para>
/// <para>3. DFS: an iterative walk with an explicit stack assigns preorder positions.</para>
/// <para>4. Ranges: subtree sizes are summed in reverse preorder, giving <c>SubtreeEnd</c>.</para>
/// </remarks>
public static unsafe class TreeBuilder
{
    /// <summary>Parent code of a slot that carries no node (free record, extension record, padding).</summary>
    public const int NotANode = -1;

    /// <summary>Parent code of a node whose parent cannot be resolved; the node becomes an orphan.</summary>
    public const int MissingParent = -2;

    private const byte Unknown = 0;
    private const byte Attached = 1;
    private const byte Excluded = 2;
    private const byte Orphaned = 3;

    /// <param name="parents">Parent slot of every slot in <c>[0, slotCount)</c>, or <see cref="NotANode"/> /
    /// <see cref="MissingParent"/>. Rewritten by the build (orphans re-parented, excluded nodes removed).</param>
    /// <param name="slotCount">Number of slots used in <paramref name="parents"/>.</param>
    /// <param name="rootSlot">Slot of the root.</param>
    /// <param name="orphanSlot">Spare slot (parent <see cref="NotANode"/>) used for the &lt;orphans&gt; folder.</param>
    /// <param name="excludedRoots">Slots whose whole subtree is left out.</param>
    /// <param name="orderInParents">Write <see cref="TreeLayout.Order"/> into <paramref name="parents"/>
    /// once it is no longer needed (saves one slot-sized array at the peak).</param>
    public static TreeLayout Build(int[] parents, int slotCount, int rootSlot, int orphanSlot, ReadOnlySpan<int> excludedRoots, bool orderInParents)
    {
        if ((uint)slotCount > (uint)parents.Length)
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        if ((uint)rootSlot >= (uint)slotCount)
            throw new ArgumentOutOfRangeException(nameof(rootSlot));
        if ((uint)orphanSlot >= (uint)slotCount || orphanSlot == rootSlot || parents[orphanSlot] != NotANode)
            throw new ArgumentException("The orphan slot must be a spare slot.", nameof(orphanSlot));

        long t0 = Stopwatch.GetTimestamp();
        int orphanRoots = 0;
        int excludedNodes = 0;
        using (var stateBuffer = new NativeArray<byte>(slotCount, zeroed: true))
        using (var onPathBits = new NativeArray<ulong>((slotCount + 63) >> 6, zeroed: true))
        {
            byte* state = stateBuffer.Pointer;
            ulong* onPath = onPathBits.Pointer;
            state[rootSlot] = Attached;
            foreach (int ex in excludedRoots)
            {
                if ((uint)ex < (uint)slotCount && ex != rootSlot && parents[ex] != NotANode)
                {
                    state[ex] = Excluded;
                    parents[ex] = NotANode;
                    excludedNodes++;
                }
            }

            var path = new List<int>(64);
            for (int s = 0; s < slotCount; s++)
            {
                if (state[s] != Unknown || parents[s] == NotANode)
                    continue;
                // Fast path: the parent is already classified (the common case in record order).
                int direct = parents[s];
                if ((uint)direct < (uint)slotCount && direct != s && state[direct] is Attached or Orphaned)
                {
                    state[s] = state[direct];
                    continue;
                }
                path.Clear();
                byte result;
                int cut = -1;
                int x = s;
                while (true)
                {
                    path.Add(x);
                    Bits.Set(onPath, x);
                    int p = parents[x];
                    if ((uint)p >= (uint)slotCount || (p != rootSlot && parents[p] == NotANode && state[p] != Excluded))
                    {
                        result = Orphaned; // missing parent, or parent is not a node
                        cut = x;
                        break;
                    }
                    if (state[p] != Unknown)
                    {
                        result = state[p];
                        break;
                    }
                    if (Bits.IsSet(onPath, p))
                    {
                        result = Orphaned; // loop: cut it where it closes
                        cut = x;
                        break;
                    }
                    x = p;
                }
                foreach (int y in path)
                {
                    state[y] = result;
                    Bits.Clear(onPath, y);
                    if (result == Excluded)
                    {
                        parents[y] = NotANode;
                        excludedNodes++;
                    }
                }
                if (cut >= 0)
                {
                    parents[cut] = orphanSlot;
                    orphanRoots++;
                }
            }
        }
        if (orphanRoots > 0)
            parents[orphanSlot] = rootSlot; // placed as the last child of the root below

        long t1 = Stopwatch.GetTimestamp();

        // CSR: count children per parent, prefix sum, fill.
        int nodes;
        int[] order;
        int[] parentPosition;
        int orphansPosition = -1;
        using (var childStart = new NativeArray<int>(slotCount + 1L, zeroed: true))
        {
            int* start = childStart.Pointer;
            nodes = 1;
            for (int s = 0; s < slotCount; s++)
            {
                int p = parents[s];
                if (p < 0 || s == rootSlot)
                    continue;
                nodes++;
                start[p]++;
            }
            int edges = 0;
            for (int s = 0; s < slotCount; s++)
            {
                int c = start[s];
                start[s] = edges;
                edges += c;
            }
            start[slotCount] = edges;
            using var childList = new NativeArray<int>(Math.Max(edges, 1), zeroed: false);
            int* list = childList.Pointer;
            for (int s = 0; s < slotCount; s++)
            {
                int p = parents[s];
                if (p < 0 || s == rootSlot || s == orphanSlot)
                    continue;
                list[start[p]++] = s;
            }
            if (orphanRoots > 0)
                list[start[rootSlot]++] = orphanSlot; // the slot left free at the end of the root's children
            for (int s = slotCount; s > 0; s--)
                start[s] = start[s - 1];
            start[0] = 0;

            // DFS preorder. The parent links now form a tree: no visited set needed.
            order = orderInParents ? parents : new int[Math.Max(nodes, slotCount)];
            parentPosition = new int[nodes];
            using var stack = new NativeStack<Frame>(256);
            int n = 0;
            order[n] = rootSlot;
            parentPosition[n] = -1;
            stack.Push(new Frame(rootSlot, n++, start[rootSlot]));
            while (stack.Count > 0)
            {
                ref Frame top = ref stack.Top;
                if (top.Cursor < start[top.Slot + 1])
                {
                    int c = list[top.Cursor++];
                    int position = n++;
                    if (position >= nodes)
                        throw new InvalidOperationException("Tree numbering overflow (inconsistent parent links).");
                    order[position] = c;
                    parentPosition[position] = top.Position;
                    if (c == orphanSlot)
                        orphansPosition = position;
                    stack.Push(new Frame(c, position, start[c]));
                }
                else
                {
                    stack.Pop();
                }
            }
            if (n != nodes)
                throw new InvalidOperationException($"Tree numbering reached {n} of {nodes} nodes.");
        }
        long t2 = Stopwatch.GetTimestamp();

        // Subtree ranges from parent positions: sizes summed in reverse preorder.
        var subtreeEnd = new int[nodes];
        Array.Fill(subtreeEnd, 1);
        for (int i = nodes - 1; i > 0; i--)
            subtreeEnd[parentPosition[i]] += subtreeEnd[i];
        for (int i = 0; i < nodes; i++)
            subtreeEnd[i] += i;
        long t3 = Stopwatch.GetTimestamp();

        return new TreeLayout
        {
            NodeCount = nodes,
            Order = order,
            Parent = parentPosition,
            SubtreeEnd = subtreeEnd,
            OrphansPosition = orphansPosition,
            OrphanRoots = orphanRoots,
            OrphanNodes = orphansPosition >= 0 ? subtreeEnd[orphansPosition] - orphansPosition - 1 : 0,
            ExcludedNodes = excludedNodes,
            Breakdown = $"classify {Ms(t1 - t0)}, CSR+DFS {Ms(t2 - t1)}, ranges {Ms(t3 - t2)}",
        };
    }

    private static string Ms(long ticks) => $"{ticks * 1000.0 / Stopwatch.Frequency:F0} ms";

    private struct Frame
    {
        public int Slot;
        public int Position;
        public int Cursor;

        public Frame(int slot, int position, int cursor)
        {
            Slot = slot;
            Position = position;
            Cursor = cursor;
        }
    }
}
