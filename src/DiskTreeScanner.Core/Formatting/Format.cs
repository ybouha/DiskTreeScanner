using System.Globalization;

namespace DiskTreeScanner.Core.Formatting;

/// <summary>Invariant formatting for logs and the CLI (binary units, like Explorer).</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? bytes.ToString("N0", CultureInfo.InvariantCulture) + " B"
            : value.ToString("N1", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Milliseconds(TimeSpan duration) => duration.TotalMilliseconds.ToString("N1", CultureInfo.InvariantCulture) + " ms";
}
