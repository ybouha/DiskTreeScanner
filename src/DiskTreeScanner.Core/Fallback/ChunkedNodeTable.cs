namespace DiskTreeScanner.Core.Fallback;

/// <summary>
/// Append-only struct of arrays in fixed 64 K-entry chunks, filled concurrently by the directory
/// engine. Chunks never move, so a worker can read the entries of the ancestors of the folder it
/// lists (to build its path) while other workers append.
/// </summary>
internal sealed class ChunkedNodeTable
{
    public const int Shift = 16;
    public const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;
    private const int MaxChunks = 1 << (31 - Shift);

    private readonly int[]?[] _parent = new int[MaxChunks][];
    private readonly long[]?[] _size = new long[MaxChunks][];
    private readonly long[]?[] _allocated = new long[MaxChunks][];
    private readonly long[]?[] _modified = new long[MaxChunks][];
    private readonly long[]?[] _fileId = new long[MaxChunks][];
    private readonly uint[]?[] _flags = new uint[MaxChunks][];
    private readonly int[]?[] _nameOffset = new int[MaxChunks][];
    private readonly ushort[]?[] _nameLength = new ushort[MaxChunks][];
    private readonly object _gate = new();
    private int _count;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Reserves <paramref name="n"/> consecutive entries and returns the first index.</summary>
    public int Reserve(int n)
    {
        lock (_gate)
        {
            int first = _count;
            if (n <= 0)
                return first;
            int end = checked(first + n);
            for (int c = first >> Shift; c <= (end - 1) >> Shift; c++)
            {
                if (_parent[c] is not null)
                    continue;
                _size[c] = new long[ChunkSize];
                _allocated[c] = new long[ChunkSize];
                _modified[c] = new long[ChunkSize];
                _fileId[c] = new long[ChunkSize];
                _flags[c] = new uint[ChunkSize];
                _nameOffset[c] = new int[ChunkSize];
                _nameLength[c] = new ushort[ChunkSize];
                _parent[c] = new int[ChunkSize];
            }
            Volatile.Write(ref _count, end);
            return first;
        }
    }

    public ref int Parent(int i) => ref _parent[i >> Shift]![i & Mask];

    public ref long Size(int i) => ref _size[i >> Shift]![i & Mask];

    public ref long Allocated(int i) => ref _allocated[i >> Shift]![i & Mask];

    public ref long Modified(int i) => ref _modified[i >> Shift]![i & Mask];

    public ref long FileId(int i) => ref _fileId[i >> Shift]![i & Mask];

    public ref uint Flags(int i) => ref _flags[i >> Shift]![i & Mask];

    public ref int NameOffset(int i) => ref _nameOffset[i >> Shift]![i & Mask];

    public ref ushort NameLength(int i) => ref _nameLength[i >> Shift]![i & Mask];

    /// <summary>Drops the file-id column once hard links have been resolved.</summary>
    public void ReleaseFileIds() => Array.Clear(_fileId);

    /// <summary>Copies one column into a flat array in the given order, then frees the column.</summary>
    public T[] GatherAndRelease<T>(Column column, int[] order, int count) where T : unmanaged
    {
        object?[] chunks = column switch
        {
            Column.Size => _size,
            Column.Allocated => _allocated,
            Column.Modified => _modified,
            Column.Flags => _flags,
            Column.NameOffset => _nameOffset,
            Column.NameLength => _nameLength,
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
        var result = new T[count];
        var typed = (T[]?[])(object)chunks;
        for (int i = 0; i < count; i++)
        {
            int s = order[i];
            result[i] = typed[s >> Shift]![s & Mask];
        }
        Array.Clear(chunks);
        return result;
    }

    public enum Column
    {
        Size,
        Allocated,
        Modified,
        Flags,
        NameOffset,
        NameLength,
    }
}
