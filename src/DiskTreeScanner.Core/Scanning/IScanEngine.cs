namespace DiskTreeScanner.Core.Scanning;

/// <summary>Whether an engine can scan a target, and why.</summary>
public readonly record struct EngineProbe(bool Supported, string Reason);

/// <summary>A way to build a <see cref="Model.TreeModel"/> from a path. Engines are tried in priority order.</summary>
public interface IScanEngine
{
    /// <summary>Short name shown to the user ("MFT", "Directory").</summary>
    string Name { get; }

    EngineProbe Probe(ScanTargetInfo target, ScanOptions options);

    /// <summary>Runs the scan on the calling thread (callers keep it off the UI thread).</summary>
    ScanResult Scan(ScanTargetInfo target, ScanOptions options, ScanProgress progress, ActivityLog log, CancellationToken cancellationToken);
}
