using System.Diagnostics;

namespace DiskTreeScanner.Core.Scanning;

/// <summary>Live counters of a running scan. Written by scan threads, polled by the UI (lock-free).</summary>
public sealed class ScanProgress
{
    private long _bytesDone;
    private long _bytesTotal;
    private long _items;
    private long _directories;
    private volatile string _phase = string.Empty;
    private volatile string _engine = string.Empty;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public string Phase
    {
        get => _phase;
        set => _phase = value;
    }

    public string Engine
    {
        get => _engine;
        set => _engine = value;
    }

    /// <summary>MFT bytes read so far (MFT engine).</summary>
    public long BytesDone => Interlocked.Read(ref _bytesDone);

    /// <summary>MFT bytes to read (MFT engine), 0 when unknown.</summary>
    public long BytesTotal
    {
        get => Interlocked.Read(ref _bytesTotal);
        set => Interlocked.Exchange(ref _bytesTotal, value);
    }

    /// <summary>Records parsed (MFT) or entries listed (directory engine).</summary>
    public long Items => Interlocked.Read(ref _items);

    public long Directories => Interlocked.Read(ref _directories);

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Fraction done in [0, 1] when known, otherwise -1.</summary>
    public double Fraction
    {
        get
        {
            long total = BytesTotal;
            return total > 0 ? Math.Clamp((double)BytesDone / total, 0, 1) : -1;
        }
    }

    public double ItemsPerSecond
    {
        get
        {
            double seconds = _clock.Elapsed.TotalSeconds;
            return seconds > 0 ? Items / seconds : 0;
        }
    }

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytesDone, bytes);

    public void AddItems(long items) => Interlocked.Add(ref _items, items);

    public void AddDirectories(long directories) => Interlocked.Add(ref _directories, directories);
}
