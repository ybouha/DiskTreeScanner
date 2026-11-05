using System.Text.Json;
using System.Text.Json.Serialization;
using DiskTreeScanner.Core.Analysis;
using DiskTreeScanner.Core.Scanning;

namespace DiskTreeScanner.App.Settings;

public enum SizeUnit
{
    Auto,
    MB,
    GB,
}

/// <summary>User preferences, stored as JSON in %LOCALAPPDATA%\DiskTreeScanner (never sent anywhere).</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public SizeUnit SizeUnit { get; set; } = SizeUnit.Auto;
    public bool IncludeNtfsSpecialFiles { get; set; } = true;
    public bool IncludeAlternateStreams { get; set; }
    public HardLinkMode HardLinks { get; set; } = HardLinkMode.FirstLinkOnly;
    public bool ForceDirectoryScan { get; set; }
    public SortColumn SortColumn { get; set; } = SortColumn.Allocated;
    public bool SortDescending { get; set; } = true;
    public string? LastPath { get; set; }

    /// <summary>Window position (screen pixels) and size (device-independent pixels): [x, y, width, height].</summary>
    public int[]? Window { get; set; }

    public bool Maximized { get; set; }
    public int BottomPaneHeight { get; set; } = 218;
    public bool ShowFilesInTree { get; set; } = true;

    /// <summary>Column widths of the tree grid and of the largest files list, in DIP (0 = default).</summary>
    public int[]? TreeColumnWidths { get; set; }

    public int[]? FileColumnWidths { get; set; }

    [JsonIgnore]
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTreeScanner");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public ScanOptions ToScanOptions() => new()
    {
        IncludeNtfsSpecialFiles = IncludeNtfsSpecialFiles,
        IncludeAlternateStreams = IncludeAlternateStreams,
        HardLinks = HardLinks,
        ForceDirectoryScan = ForceDirectoryScan,
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
