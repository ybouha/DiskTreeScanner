using System.Diagnostics;

namespace DiskTreeScanner.Core.Platform;

/// <summary>Process memory counters, for the per-phase diagnostics.</summary>
public static class ProcessMemory
{
    public static long PeakWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.PeakWorkingSet64;
    }

    public static long WorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    /// <summary>"ws 123 MB / peak 150 MB" for phase details.</summary>
    public static string Describe()
    {
        using var process = Process.GetCurrentProcess();
        return $"ws {process.WorkingSet64 / 1048576.0:F0} MB, peak {process.PeakWorkingSet64 / 1048576.0:F0} MB";
    }
}
