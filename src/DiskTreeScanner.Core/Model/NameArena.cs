using System.Runtime.InteropServices;
using System.Text;

namespace DiskTreeScanner.Core.Model;

/// <summary>
/// Append-only store holding every file name of a scan; there is no string object per file.
/// </summary>
/// <remarks>
/// <para>A name is addressed by a global byte offset (stored per node in an <c>int[]</c>) and a
/// 16-bit length word (stored per node in a <c>ushort[]</c>).</para>
/// <para>Names made only of Latin-1 characters (measured: 99.998 % of the 2 M names of a Windows
/// system drive) are stored narrow, one byte per char; the others are stored as UTF-16 and flagged
/// with <see cref="WideFlag"/>. On the reference volume this halves the arena from 100 MB to 50 MB,
/// which is what keeps a 2 M item scan under the 200 MB peak target.</para>
/// <para>Storage is a list of fixed 512 KiB chunks, so the arena never grows by copying (a growing
/// single array would double the peak). Chunks are filled by one writer each; readers may run
/// concurrently with writers as long as they only read names published to them.</para>
/// </remarks>
public sealed class NameArena
{
    public const int ChunkShift = 19;
    public const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;
    private const int MaxChunks = 1 << (31 - ChunkShift);

    /// <summary>Bit set in a length word when the name is stored as UTF-16.</summary>
    public const ushort WideFlag = 0x8000;
    public const int MaxNameLength = 0x7FFF;

    // Sized once for the whole 2 GiB offset space, so readers never race with a resize.
    private readonly byte[]?[] _chunks = new byte[MaxChunks][];
    private int _chunkCount;
    private readonly object _gate = new();

    public int ChunkCount => Volatile.Read(ref _chunkCount);

    /// <summary>Bytes held by the chunks (allocated, not necessarily used).</summary>
    public long AllocatedBytes => (long)ChunkCount * ChunkSize;

    public static int LengthOf(ushort lengthWord) => lengthWord & MaxNameLength;

    public static bool IsWide(ushort lengthWord) => (lengthWord & WideFlag) != 0;

    /// <summary>Decodes a name into <paramref name="destination"/> and returns its length in chars.</summary>
    public int GetChars(int offset, ushort lengthWord, Span<char> destination)
    {
        int length = lengthWord & MaxNameLength;
        if (length == 0)
            return 0;
        byte[] chunk = _chunks[offset >> ChunkShift]!;
        int local = offset & ChunkMask;
        if ((lengthWord & WideFlag) == 0)
            return Encoding.Latin1.GetChars(chunk.AsSpan(local, length), destination);
        MemoryMarshal.Cast<byte, char>(chunk.AsSpan(local, length * 2)).CopyTo(destination);
        return length;
    }

    public string GetString(int offset, ushort lengthWord)
    {
        int length = lengthWord & MaxNameLength;
        if (length == 0)
            return string.Empty;
        return string.Create(length, (this, offset, lengthWord), static (span, s) => s.Item1.GetChars(s.offset, s.lengthWord, span));
    }

    public Writer CreateWriter() => new(this);

    private byte[] NewChunk(out int index)
    {
        byte[] chunk = GC.AllocateUninitializedArray<byte>(ChunkSize);
        lock (_gate)
        {
            if (_chunkCount == MaxChunks)
                throw new InvalidOperationException("Name arena is full (2 GiB of names).");
            index = _chunkCount;
            _chunks[index] = chunk;
            Volatile.Write(ref _chunkCount, index + 1);
        }
        return chunk;
    }

    /// <summary>Single-threaded appender owning its current chunk.</summary>
    public sealed class Writer
    {
        private readonly NameArena _arena;
        private byte[]? _chunk;
        private int _chunkIndex;
        private int _position;

        internal Writer(NameArena arena) => _arena = arena;

        /// <summary>Appends a name; returns its offset and the length word to store with it.</summary>
        public int Append(ReadOnlySpan<char> name, out ushort lengthWord)
        {
            if (name.Length > MaxNameLength)
                name = name[..MaxNameLength];
            bool wide = name.IndexOfAnyExceptInRange('\0', 'ÿ') >= 0;
            int bytes = wide ? name.Length * 2 : name.Length;
            if (_chunk is null || _position + bytes > ChunkSize)
            {
                _chunk = _arena.NewChunk(out _chunkIndex);
                _position = 0;
            }
            Span<byte> destination = _chunk.AsSpan(_position, bytes);
            if (wide)
                MemoryMarshal.AsBytes(name).CopyTo(destination);
            else
                Encoding.Latin1.GetBytes(name, destination);
            int offset = (_chunkIndex << ChunkShift) | _position;
            _position += bytes;
            lengthWord = (ushort)(name.Length | (wide ? WideFlag : 0));
            return offset;
        }
    }
}
