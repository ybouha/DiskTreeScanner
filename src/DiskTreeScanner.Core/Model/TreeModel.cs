using System.Buffers;
using System.Numerics;

namespace DiskTreeScanner.Core.Model;

/// <summary>
/// The scanned tree as a struct of arrays, numbered in DFS preorder.
/// </summary>
/// <remarks>
/// <para>Node 0 is the root. A node's descendants are exactly the positions
/// <c>[i + 1, SubtreeEnd[i])</c>; its first child is <c>i + 1</c> and the sibling after child
/// <c>c</c> is <c>SubtreeEnd[c]</c>. Any subtree is therefore a contiguous range, which makes
/// "everything under this folder" queries (top N files, search, export) a linear scan.</para>
/// <para>Directories hold aggregated values: <see cref="Size"/> and <see cref="Allocated"/> are
/// sums over the subtree and <see cref="Modified"/> is the most recent change in the subtree.
/// Counts cost no per-node memory: items are the range length, folders are counted in the range
/// with a rank index over a "is directory" bitmap (N/8 + N/16 bytes instead of an int[]).</para>
/// <para>Arrays may be longer than <see cref="Count"/>; entries past it are scratch.</para>
/// </remarks>
public sealed class TreeModel
{
    public const string OrphansName = "<orphans>";

    private ulong[] _directoryBits = Array.Empty<ulong>();
    private int[] _directoryRank = Array.Empty<int>();

    public TreeModel(
        int count,
        int[] parent,
        int[] subtreeEnd,
        long[] size,
        long[] allocated,
        uint[] flags,
        long[] modified,
        int[] nameOffset,
        ushort[] nameLength,
        NameArena names,
        int[]? recordNumber,
        string rootPath,
        UpCaseTable upCase)
    {
        Count = count;
        Parent = parent;
        SubtreeEnd = subtreeEnd;
        Size = size;
        Allocated = allocated;
        Flags = flags;
        Modified = modified;
        NameOffset = nameOffset;
        NameLength = nameLength;
        Names = names;
        RecordNumber = recordNumber;
        RootPath = rootPath;
        UpCase = upCase;
        RebuildDirectoryIndex();
    }

    public int Count { get; }

    /// <summary>Parent position, -1 for the root.</summary>
    public int[] Parent { get; }

    /// <summary>End (exclusive) of each node's DFS range.</summary>
    public int[] SubtreeEnd { get; }

    /// <summary>Logical size in bytes (aggregated for directories).</summary>
    public long[] Size { get; }

    /// <summary>Allocated size on disk in bytes (aggregated for directories).</summary>
    public long[] Allocated { get; }

    /// <summary><see cref="NodeFlags"/> per node.</summary>
    public uint[] Flags { get; }

    /// <summary>Last modification, FILETIME UTC. Directories: most recent change in the subtree.</summary>
    public long[] Modified { get; }

    public int[] NameOffset { get; }

    public ushort[] NameLength { get; }

    public NameArena Names { get; }

    /// <summary>MFT record number of each node (MFT engine only), used by the USN refresh.</summary>
    public int[]? RecordNumber { get; }

    /// <summary>Full path of the root node, e.g. <c>C:\</c> or <c>\\wsl$\Ubuntu</c>.</summary>
    public string RootPath { get; }

    public UpCaseTable UpCase { get; }

    public NodeFlags GetFlags(int node) => (NodeFlags)Flags[node];

    public bool IsDirectory(int node) => (Flags[node] & (uint)NodeFlags.Directory) != 0;

    public bool HasFlag(int node, NodeFlags flag) => (Flags[node] & (uint)flag) != 0;

    /// <summary>Number of files and folders below <paramref name="node"/>.</summary>
    public int ItemCount(int node) => SubtreeEnd[node] - node - 1;

    /// <summary>Number of folders below <paramref name="node"/>.</summary>
    public int FolderCount(int node) => DirectoriesBefore(SubtreeEnd[node]) - DirectoriesBefore(node + 1);

    public int FileCount(int node) => ItemCount(node) - FolderCount(node);

    /// <summary>Rebuilds the folder-count index from <see cref="Flags"/> (after a structural change).</summary>
    public void RebuildDirectoryIndex()
    {
        int words = (Count + 63) >> 6;
        var bits = new ulong[words];
        for (int i = 0; i < Count; i++)
        {
            if ((Flags[i] & (uint)NodeFlags.Directory) != 0)
                bits[i >> 6] |= 1UL << i;
        }
        var rank = new int[words + 1];
        for (int w = 0; w < words; w++)
            rank[w + 1] = rank[w] + BitOperations.PopCount(bits[w]);
        _directoryBits = bits;
        _directoryRank = rank;
    }

    /// <summary>Number of directories at positions <c>[0, position)</c>.</summary>
    private int DirectoriesBefore(int position)
    {
        int word = position >> 6;
        int rank = _directoryRank[word];
        int bit = position & 63;
        return bit == 0 ? rank : rank + BitOperations.PopCount(_directoryBits[word] & ((1UL << bit) - 1));
    }

    public bool HasChildren(int node) => SubtreeEnd[node] > node + 1;

    public bool Contains(int ancestor, int node) => node >= ancestor && node < SubtreeEnd[ancestor];

    public int NameLengthOf(int node) => NameArena.LengthOf(NameLength[node]);

    /// <summary>Copies the node name into <paramref name="destination"/> (at least <see cref="NameLengthOf"/> chars).</summary>
    public int GetName(int node, Span<char> destination) => Names.GetChars(NameOffset[node], NameLength[node], destination);

    public string GetName(int node) => Names.GetString(NameOffset[node], NameLength[node]);

    public ChildEnumerable Children(int node) => new(this, node);

    public int ChildCount(int node)
    {
        int count = 0;
        for (int c = node + 1, end = SubtreeEnd[node]; c < end; c = SubtreeEnd[c])
            count++;
        return count;
    }

    public int Depth(int node)
    {
        int depth = 0;
        for (int x = node; x > 0; x = Parent[x])
            depth++;
        return depth;
    }

    /// <summary>Full path of a node (the root path for node 0).</summary>
    public string GetPath(int node)
    {
        if (node == 0)
            return RootPath;
        int depth = Depth(node);
        int[] chain = ArrayPool<int>.Shared.Rent(depth);
        try
        {
            int k = 0;
            int length = RootPath.Length;
            bool rootHasSeparator = RootPath.EndsWith('\\');
            for (int x = node; x > 0; x = Parent[x])
            {
                chain[k++] = x;
                length += NameLengthOf(x) + 1;
            }
            if (rootHasSeparator)
                length--;
            return string.Create(length, (this, chain, k, rootHasSeparator), static (span, s) =>
            {
                var (model, chain, k, rootHasSeparator) = s;
                model.RootPath.AsSpan().CopyTo(span);
                int pos = model.RootPath.Length;
                for (int i = k - 1; i >= 0; i--)
                {
                    if (i != k - 1 || !rootHasSeparator)
                        span[pos++] = '\\';
                    pos += model.GetName(chain[i], span[pos..]);
                }
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(chain);
        }
    }

    /// <summary>Finds a direct child by name, case-insensitively (using the volume's case table).</summary>
    public int FindChild(int parent, ReadOnlySpan<char> name)
    {
        Span<char> buffer = name.Length <= 512 ? stackalloc char[512] : new char[name.Length];
        foreach (int c in Children(parent))
        {
            if (NameLengthOf(c) != name.Length)
                continue;
            int n = GetName(c, buffer);
            if (UpCase.EqualsIgnoreCase(buffer[..n], name))
                return c;
        }
        return -1;
    }

    /// <summary>Resolves a path relative to the root (e.g. <c>Users\Public</c>); -1 if absent.</summary>
    public int FindRelative(ReadOnlySpan<char> relativePath)
    {
        int node = 0;
        while (!relativePath.IsEmpty)
        {
            int separator = relativePath.IndexOf('\\');
            ReadOnlySpan<char> name = separator < 0 ? relativePath : relativePath[..separator];
            relativePath = separator < 0 ? ReadOnlySpan<char>.Empty : relativePath[(separator + 1)..];
            if (name.IsEmpty)
                continue;
            node = FindChild(node, name);
            if (node < 0)
                return -1;
        }
        return node;
    }

    public readonly struct ChildEnumerable
    {
        private readonly TreeModel _model;
        private readonly int _node;

        public ChildEnumerable(TreeModel model, int node)
        {
            _model = model;
            _node = node;
        }

        public Enumerator GetEnumerator() => new(_model.SubtreeEnd, _node);
    }

    public struct Enumerator
    {
        private readonly int[] _subtreeEnd;
        private readonly int _end;
        private int _next;

        public Enumerator(int[] subtreeEnd, int node)
        {
            _subtreeEnd = subtreeEnd;
            _end = subtreeEnd[node];
            _next = node + 1;
            Current = -1;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            if (_next >= _end)
                return false;
            Current = _next;
            _next = _subtreeEnd[_next];
            return true;
        }
    }
}
