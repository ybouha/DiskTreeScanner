using DiskTreeScanner.App.Settings;
using DiskTreeScanner.App.Ui;
using DiskTreeScanner.Core.Platform;
using DiskTreeScanner.Core.Scanning;

namespace DiskTreeScanner.App;

internal static class Program
{
    /// <summary>DiskTreeScanner.exe [path] — scans the path at startup when given.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        AppSettings settings = AppSettings.Load();
        L.SetLanguage(settings.Language);
        Fmt.Unit = settings.SizeUnit;

        string logFolder = Path.Combine(AppSettings.Folder, "logs");
        string? logFile = null;
        try
        {
            Directory.CreateDirectory(logFolder);
            logFile = Path.Combine(logFolder, $"activity-{DateTime.Now:yyyyMMdd}.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var log = new ActivityLog(logFile);
        log.Info($"DiskTreeScanner {typeof(Program).Assembly.GetName().Version?.ToString(3)} started ({(Elevation.IsElevated ? "elevated" : "not elevated")}, {Environment.ProcessorCount} logical processors).");

        // Diagnostics: DiskTreeScanner.exe --screenshot out.png [--select relative\path] [--lang fr] path
        string? screenshot = null, select = null, path = null, benchmark = null, popup = null;
        var filters = new List<string>();
        var resizes = new List<string>();
        Size? size = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--screenshot" && i + 1 < args.Length)
                screenshot = args[++i];
            else if (args[i] == "--popup" && i + 1 < args.Length)
                popup = args[++i];
            else if (args[i] == "--filter" && i + 1 < args.Length)
                filters.Add(args[++i]);
            else if (args[i] == "--resize" && i + 1 < args.Length)
                resizes.Add(args[++i]);
            else if (args[i] == "--ui-bench" && i + 1 < args.Length)
                benchmark = args[++i];
            else if (args[i] == "--select" && i + 1 < args.Length)
                select = args[++i];
            else if (args[i] == "--lang" && i + 1 < args.Length)
                L.SetLanguage(args[++i]);
            else if (args[i] == "--scale" && i + 1 < args.Length && float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale))
                Theme.ScaleOverride = Math.Clamp(scale, 1f, 3f);
            else if (args[i] == "--size" && i + 1 < args.Length && args[++i].Split('x') is [string w, string h] && int.TryParse(w, out int width) && int.TryParse(h, out int height))
                size = new Size(width, height);
            else if (!string.IsNullOrWhiteSpace(args[i]))
                path = args[i].Trim('"');
        }
        Application.ThreadException += (_, e) => log.Error($"Unexpected error: {e.Exception}");
        var form = new MainForm(settings, log, path) { Diagnostic = screenshot is not null || benchmark is not null };
        if (size is { } logical)
            form.Size = new Size(form.Px(logical.Width), form.Px(logical.Height));
        if (screenshot is not null)
            form.CaptureWhenIdle(screenshot, select, popup, filters, resizes);
        else if (benchmark is not null)
            form.BenchmarkWhenIdle(benchmark);
        Application.Run(form);
    }
}
