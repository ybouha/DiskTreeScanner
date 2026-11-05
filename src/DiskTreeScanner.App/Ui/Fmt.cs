using DiskTreeScanner.App.Settings;

namespace DiskTreeScanner.App.Ui;

/// <summary>Size, count and date formatting into caller buffers (no allocation while painting).</summary>
internal static class Fmt
{
    private static string[] s_units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static SizeUnit Unit { get; set; } = SizeUnit.Auto;

    public static void RefreshUnits() => s_units = [L.S("UnitB"), L.S("UnitKB"), L.S("UnitMB"), L.S("UnitGB"), L.S("UnitTB"), "PB"];

    public static int Size(long bytes, Span<char> destination) => Size(bytes, destination, Unit);

    public static int Size(long bytes, Span<char> destination, SizeUnit unit)
    {
        double value = bytes;
        int index;
        string format;
        switch (unit)
        {
            case SizeUnit.MB:
                value /= 1024.0 * 1024.0;
                index = 2;
                format = "N1";
                break;
            case SizeUnit.GB:
                value /= 1024.0 * 1024.0 * 1024.0;
                index = 3;
                format = "N2";
                break;
            default:
                index = 0;
                while (Math.Abs(value) >= 1024 && index < s_units.Length - 1)
                {
                    value /= 1024;
                    index++;
                }
                format = index == 0 ? "N0" : "N1";
                break;
        }
        value.TryFormat(destination, out int written, format, L.Numbers);
        destination[written++] = ' ';
        string u = s_units[index];
        u.CopyTo(destination[written..]);
        return written + u.Length;
    }

    public static string Size(long bytes)
    {
        Span<char> buffer = stackalloc char[48];
        return new string(buffer[..Size(bytes, buffer)]);
    }

    public static string SizeAuto(long bytes)
    {
        Span<char> buffer = stackalloc char[48];
        return new string(buffer[..Size(bytes, buffer, SizeUnit.Auto)]);
    }

    public static int Count(long value, Span<char> destination)
    {
        value.TryFormat(destination, out int written, "N0", L.Numbers);
        return written;
    }

    public static string Count(long value) => value.ToString("N0", L.Numbers);

    /// <summary>"1.53 M", "453 k".</summary>
    public static string Compact(long value)
    {
        if (value >= 1_000_000)
            return (value / 1_000_000.0).ToString("0.00", L.Numbers) + " M";
        if (value >= 10_000)
            return (value / 1000.0).ToString("0", L.Numbers) + " k";
        return Count(value);
    }

    public static int Percent(double percent, Span<char> destination)
    {
        double shown = percent < 0.05 ? 0 : percent;
        shown.TryFormat(destination, out int written, "0.0", L.Numbers);
        " %".CopyTo(destination[written..]);
        return written + 2;
    }

    public static string Percent(double percent)
    {
        Span<char> buffer = stackalloc char[24];
        return new string(buffer[..Percent(percent, buffer)]);
    }

    public static int Date(long fileTime, Span<char> destination)
    {
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
            return 0;
        DateTime local = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        local.TryFormat(destination, out int written, "dd/MM/yyyy HH:mm", L.Numbers);
        return written;
    }

    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
            return duration.TotalSeconds.ToString("0.00", L.Numbers) + " s";
        return $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s";
    }

    public static string Time(DateTime time) => time.ToString("HH:mm:ss", L.Numbers);
}
