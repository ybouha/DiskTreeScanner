using System.Buffers.Binary;

namespace DiskTreeScanner.Core.Model;

/// <summary>
/// Case-folding table for case-insensitive name comparison (search and sort). On NTFS the table is the
/// volume's own <c>$UpCase</c> file, which is exactly what the file system uses to compare names.
/// </summary>
public sealed class UpCaseTable
{
    private readonly char[] _map;

    private UpCaseTable(char[] map, string source)
    {
        _map = map;
        Source = source;
    }

    /// <summary>Where the table comes from ("$UpCase" or "invariant").</summary>
    public string Source { get; }

    public static UpCaseTable Invariant { get; } = CreateInvariant();

    private static UpCaseTable CreateInvariant()
    {
        var map = new char[65536];
        for (int i = 0; i < map.Length; i++)
            map[i] = char.ToUpperInvariant((char)i);
        return new UpCaseTable(map, "invariant");
    }

    /// <summary>Builds the table from the raw content of NTFS <c>$UpCase</c> (65 536 little-endian UTF-16 units).</summary>
    public static UpCaseTable? FromNtfs(ReadOnlySpan<byte> data)
    {
        if (data.Length < 131072)
            return null;
        var map = new char[65536];
        for (int i = 0; i < map.Length; i++)
            map[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
        // Sanity check: a corrupt table would make search unusable.
        if (map['a'] != 'A' || map['z'] != 'Z' || map['A'] != 'A')
            return null;
        return new UpCaseTable(map, "$UpCase");
    }

    public char ToUpper(char c) => _map[c];

    public void ToUpper(ReadOnlySpan<char> source, Span<char> destination)
    {
        for (int i = 0; i < source.Length; i++)
            destination[i] = _map[source[i]];
    }

    public int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = _map[a[i]] - _map[b[i]];
            if (d != 0)
                return d;
        }
        return a.Length - b.Length;
    }

    public bool EqualsIgnoreCase(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (_map[a[i]] != _map[b[i]])
                return false;
        }
        return true;
    }
}
