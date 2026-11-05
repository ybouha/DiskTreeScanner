namespace DiskTreeScanner.Core.Scanning;

/// <summary>How the bytes of a file with several names (hard links) are counted.</summary>
public enum HardLinkMode
{
    /// <summary>Size and allocation counted once, on the first link (default).</summary>
    FirstLinkOnly,

    /// <summary>Size and allocation counted on every link (paths add up like in Explorer).</summary>
    EveryLink,

    /// <summary>Size on every link, allocation once (disk usage stays exact).</summary>
    SizeEveryLinkAllocatedOnce,
}

public sealed record ScanOptions
{
    /// <summary>Include $MFT, $LogFile, $Bitmap, $Extend… ("Include NTFS special files").</summary>
    public bool IncludeNtfsSpecialFiles { get; init; } = true;

    /// <summary>Add named data streams (ADS) to file sizes. They are always counted apart in the statistics.</summary>
    public bool IncludeAlternateStreams { get; init; }

    public HardLinkMode HardLinks { get; init; } = HardLinkMode.FirstLinkOnly;

    /// <summary>Never open the raw volume; always enumerate directories (corporate machines with EDR).</summary>
    public bool ForceDirectoryScan { get; init; }

    /// <summary>Directory engine thread count; 0 = automatic (cores locally, fewer on network and WSL).</summary>
    public int DirectoryParallelism { get; init; }

    /// <summary>MFT read block size in MiB (4 to 16).</summary>
    public int MftBlockMegabytes { get; init; } = 4;

    /// <summary>Number of MFT read buffers (in flight + being parsed); 0 = automatic.</summary>
    public int MftBufferCount { get; init; }

    /// <summary>Overlapped MFT reads kept in flight by the reader thread (measured: deeper queues add memory, not speed, on the reference NVMe).</summary>
    public int MftQueueDepth { get; init; } = 2;

    /// <summary>MFT parser threads; 0 = cores − 1.</summary>
    public int MftParserThreads { get; init; }
}
