using System.Diagnostics;
using DiskTreeScanner.Core.Platform;

namespace DiskTreeScanner.Core.Scanning;

/// <summary>
/// Picks the engine for a target (MFT when possible, directory enumeration otherwise) and falls
/// back to the next engine if one fails at run time (e.g. raw volume access blocked by an EDR).
/// </summary>
public sealed class ScanCoordinator
{
    private readonly IScanEngine[] _engines;

    /// <param name="engines">Engines in priority order; the last one must support every target.</param>
    public ScanCoordinator(params IScanEngine[] engines)
    {
        if (engines.Length == 0)
            throw new ArgumentException("At least one engine is required.", nameof(engines));
        _engines = engines;
    }

    public IReadOnlyList<IScanEngine> Engines => _engines;

    /// <summary>Engine that will be tried first for a target, with the reason.</summary>
    public (IScanEngine Engine, string Reason) Select(ScanTargetInfo target, ScanOptions options)
    {
        foreach (IScanEngine engine in _engines)
        {
            EngineProbe probe = engine.Probe(target, options);
            if (probe.Supported)
                return (engine, probe.Reason);
        }
        return (_engines[^1], "last resort");
    }

    public ScanResult Scan(string path, ScanOptions options, ScanProgress progress, ActivityLog log, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        ScanTargetInfo target = ScanTargetInfo.Analyze(path);
        log.Info($"Scan of {target.Path}: {Describe(target)}");

        Exception? lastError = null;
        for (int i = 0; i < _engines.Length; i++)
        {
            IScanEngine engine = _engines[i];
            EngineProbe probe = engine.Probe(target, options);
            if (!probe.Supported)
            {
                log.Info($"{engine.Name} engine skipped: {probe.Reason}");
                continue;
            }
            log.Info($"{engine.Name} engine selected: {probe.Reason}");
            progress.Engine = engine.Name;
            try
            {
                ScanResult result = engine.Scan(target, options, progress, log, cancellationToken);
                result.Duration = Stopwatch.GetElapsedTime(start);
                result.CompletedAt = DateTime.Now;
                if (lastError is not null)
                    result.Warnings.Add($"Fell back to the {engine.Name} engine: {lastError.Message}");
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (i < _engines.Length - 1)
            {
                lastError = ex;
                log.Warn($"{engine.Name} engine failed ({ex.GetType().Name}: {ex.Message}); trying the next engine.");
            }
        }
        throw new InvalidOperationException("No scan engine accepted the target.", lastError);
    }

    private static string Describe(ScanTargetInfo t)
    {
        string kind = t.Kind == DriveKind.Wsl ? $"WSL distribution {t.WslDistribution}" : t.Kind.ToString();
        string fs = t.FileSystem.Length > 0 ? t.FileSystem : "unknown file system";
        return $"{kind}, {fs}, {(t.IsElevated ? "elevated" : "not elevated")}{(t.IsVolumeRoot ? ", volume root" : ", subfolder")}";
    }
}
