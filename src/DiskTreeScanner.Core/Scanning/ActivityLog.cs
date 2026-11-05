namespace DiskTreeScanner.Core.Scanning;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} {Level,-7} {Message}";
}

/// <summary>Thread-safe activity log shown in the UI and optionally mirrored to a file. Nothing leaves the machine.</summary>
public sealed class ActivityLog
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _gate = new();

    public ActivityLog(string? filePath = null) => FilePath = filePath;

    /// <summary>File the entries are appended to, or null.</summary>
    public string? FilePath { get; }

    public event Action<LogEntry>? EntryAdded;

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public void Info(string message) => Add(LogLevel.Info, message);

    public void Warn(string message) => Add(LogLevel.Warning, message);

    public void Error(string message) => Add(LogLevel.Error, message);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    private void Add(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_gate)
        {
            _entries.Add(entry);
            if (FilePath is not null)
            {
                try
                {
                    File.AppendAllText(FilePath, entry + Environment.NewLine);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        EntryAdded?.Invoke(entry);
    }
}
