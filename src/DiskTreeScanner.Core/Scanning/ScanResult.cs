using System.Diagnostics;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Platform;

namespace DiskTreeScanner.Core.Scanning;

public readonly record struct PhaseTiming(string Name, TimeSpan Duration, string? Detail);

/// <summary>Duration of each scan phase (open, MFT read, parse, tree, aggregation…).</summary>
public sealed class PhaseTimings
{
    private readonly List<PhaseTiming> _phases = new();

    public IReadOnlyList<PhaseTiming> Phases => _phases;

    public void Add(string name, TimeSpan duration, string? detail = null) => _phases.Add(new PhaseTiming(name, duration, detail));

    /// <summary>Adds a phase measured from <paramref name="start"/> (a Stopwatch timestamp) to now.</summary>
    public long Mark(string name, long start, string? detail = null)
    {
        long now = Stopwatch.GetTimestamp();
        Add(name, Stopwatch.GetElapsedTime(start, now), detail);
        return now;
    }
}

/// <summary>Counters describing what a scan saw, used to explain differences with other tools.</summary>
public sealed class ScanStatistics
{
    public long MftRecords { get; set; }
    public long MftRecordsInUse { get; set; }
    public long MftBytesRead { get; set; }
    public long MftBytesSkipped { get; set; }
    public int MftExtents { get; set; }
    public long ExtensionRecords { get; set; }
    public long BadRecords { get; set; }
    public long StaleExtensions { get; set; }
    public long HardLinkExtraNames { get; set; }
    public long HardLinkDuplicateSize { get; set; }
    public long HardLinkDuplicateAllocated { get; set; }
    public long AlternateStreams { get; set; }
    public long AlternateStreamSize { get; set; }
    public long AlternateStreamAllocated { get; set; }
    public long WofCompressedFiles { get; set; }
    public long SpecialFilesSize { get; set; }
    public long SpecialFilesAllocated { get; set; }
    public int OrphanRoots { get; set; }
    public int OrphanNodes { get; set; }
    public int ExcludedNodes { get; set; }
    public long DirectoriesListed { get; set; }
    public long AccessDenied { get; set; }
    public double ReaderStallMilliseconds { get; set; }
    public double ParseCpuMilliseconds { get; set; }
    public int ParserThreads { get; set; }
    public int NameArenaChunks { get; set; }
    public long NameArenaBytes { get; set; }
}

/// <summary>USN journal position recorded after an MFT scan, for incremental refresh.</summary>
public sealed record UsnCheckpoint(ulong JournalId, long NextUsn);

/// <summary>Outcome of a scan: the model plus everything the UI shows around it.</summary>
public sealed class ScanResult
{
    public required TreeModel Model { get; init; }

    /// <summary>Node of the folder that was asked for (0 unless a subfolder of an MFT volume was scanned).</summary>
    public int ViewRoot { get; set; }

    /// <summary>"MFT" or "Directory".</summary>
    public required string Engine { get; init; }

    public required ScanTargetInfo Target { get; init; }

    public required ScanOptions Options { get; init; }

    public long VolumeTotalBytes { get; set; }

    public long VolumeFreeBytes { get; set; }

    public MediaKind Media { get; set; }

    public ScanStatistics Statistics { get; init; } = new();

    public PhaseTimings Timings { get; init; } = new();

    public TimeSpan Duration { get; set; }

    public DateTime CompletedAt { get; set; } = DateTime.Now;

    public UsnCheckpoint? Usn { get; set; }

    public List<string> Warnings { get; } = new();
}
