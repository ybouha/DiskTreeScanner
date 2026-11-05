using System.Globalization;
using DiskTreeScanner.Core.Analysis;

namespace DiskTreeScanner.App.Ui;

/// <summary>Filterable columns of the tree grid.</summary>
internal enum FilterColumn
{
    Name,
    Percent,
    Size,
    Allocated,
    Items,
    Files,
    Folders,
    Modified,
}

/// <summary>Inclusive bounds; null = unbounded.</summary>
internal readonly record struct Bounds(long? Min, long? Max)
{
    public bool IsSet => Min is not null || Max is not null;

    public bool Contains(long value) => (Min is not { } min || value >= min) && (Max is not { } max || value <= max);
}

/// <summary>
/// Criteria set from the column headers. Name, Size, Allocated and Modified select files (folder
/// totals are recomputed from the kept files); % of Parent, Items, Files and Folders hide the rows
/// outside their bounds. Percentages are stored in hundredths, dates as FILETIME (UTC).
/// </summary>
internal sealed class ColumnFilters
{
    private readonly Bounds[] _bounds = new Bounds[8];

    /// <summary>Name pattern applied to the tree (the search box text when the filter is on).</summary>
    public string? Name { get; set; }

    public Bounds this[FilterColumn column]
    {
        get => _bounds[(int)column];
        set => _bounds[(int)column] = value;
    }

    public bool IsActive(FilterColumn column) => column == FilterColumn.Name ? Name is not null : _bounds[(int)column].IsSet;

    public bool HasRowCriteria => this[FilterColumn.Percent].IsSet || this[FilterColumn.Items].IsSet || this[FilterColumn.Files].IsSet || this[FilterColumn.Folders].IsSet;

    public bool HasValueCriteria => this[FilterColumn.Size].IsSet || this[FilterColumn.Allocated].IsSet || this[FilterColumn.Modified].IsSet;

    public bool Any => Name is not null || HasRowCriteria || HasValueCriteria;

    public void ClearValues() => Array.Clear(_bounds);

    public ItemFilter ToItemFilter(NamePattern? name) => new()
    {
        Name = name,
        MinSize = this[FilterColumn.Size].Min,
        MaxSize = this[FilterColumn.Size].Max,
        MinAllocated = this[FilterColumn.Allocated].Min,
        MaxAllocated = this[FilterColumn.Allocated].Max,
        ModifiedFrom = this[FilterColumn.Modified].Min,
        ModifiedTo = this[FilterColumn.Modified].Max,
    };

    /// <summary>Short text of a column's criterion ("≥ 1 GB", "2024-01-01 – …"), for captions.</summary>
    public string Describe(FilterColumn column)
    {
        if (column == FilterColumn.Name)
            return Name ?? string.Empty;
        Bounds b = this[column];
        string Value(long v) => column switch
        {
            FilterColumn.Size or FilterColumn.Allocated => ValueText.Bytes(v),
            FilterColumn.Percent => (v / 100.0).ToString("0.##", L.Numbers) + " %",
            FilterColumn.Modified => DateTime.FromFileTimeUtc(v).ToLocalTime().ToString("d", L.Numbers),
            _ => v.ToString("N0", L.Numbers),
        };
        return (b.Min, b.Max) switch
        {
            ({ } min, { } max) => $"{Value(min)} – {Value(max)}",
            ({ } min, null) => $"≥ {Value(min)}",
            (null, { } max) => $"≤ {Value(max)}",
            _ => string.Empty,
        };
    }
}

/// <summary>Parsing and formatting of the filter fields.</summary>
internal static class ValueText
{
    private static readonly (string Suffix, long Factor)[] Units =
    [
        ("tb", 1L << 40), ("to", 1L << 40), ("t", 1L << 40),
        ("gb", 1L << 30), ("go", 1L << 30), ("g", 1L << 30),
        ("mb", 1L << 20), ("mo", 1L << 20), ("m", 1L << 20),
        ("kb", 1L << 10), ("ko", 1L << 10), ("k", 1L << 10),
        ("b", 1), ("o", 1),
    ];

    /// <summary>"1.5 GB", "500 Mo", "200k", "100" (no unit: MB). Binary units, as displayed.</summary>
    public static bool TryParseBytes(string text, out long? bytes)
    {
        bytes = null;
        string t = text.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace(' '.ToString(), string.Empty);
        if (t.Length == 0)
            return true;
        long factor = 1L << 20;
        foreach (var (suffix, f) in Units)
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
            {
                factor = f;
                t = t[..^suffix.Length];
                break;
            }
        }
        if (!TryParseNumber(t, out double value) || value < 0)
            return false;
        bytes = (long)Math.Round(value * factor);
        return true;
    }

    public static bool TryParseCount(string text, out long? count)
    {
        count = null;
        string t = text.Trim().Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty);
        if (t.Length == 0)
            return true;
        if (!long.TryParse(t, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long value) || value < 0)
            return false;
        count = value;
        return true;
    }

    /// <summary>Percent in hundredths ("12,5" → 1250).</summary>
    public static bool TryParsePercent(string text, out long? hundredths)
    {
        hundredths = null;
        string t = text.Trim().TrimEnd('%').Trim();
        if (t.Length == 0)
            return true;
        if (!TryParseNumber(t, out double value) || value < 0 || value > 100)
            return false;
        hundredths = (long)Math.Round(value * 100);
        return true;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Bytes in the largest binary unit that keeps the value readable ("1.5 GB").</summary>
    public static string Bytes(long bytes)
    {
        (string unit, long factor) = bytes >= 1L << 40 ? ("TB", 1L << 40) : bytes >= 1L << 30 ? ("GB", 1L << 30) : bytes >= 1L << 20 ? ("MB", 1L << 20) : bytes >= 1L << 10 ? ("KB", 1L << 10) : ("B", 1L);
        if (L.Language == "fr")
            unit = unit.Replace('B', 'o');
        return (bytes / (double)factor).ToString("0.##", L.Numbers) + " " + unit;
    }
}
