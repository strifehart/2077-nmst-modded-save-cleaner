namespace Nmst.SaveCleaner.Core.Logging;

public enum LogLevel { Info, Ok, Skip, Warn, Error, Done }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} {Level.ToString().ToUpperInvariant(),-5} {Message}";
}

/// <summary>Thread-safe activity log: raises <see cref="EntryAdded"/> for the UI and appends to a file.</summary>
public sealed class ActivityLog
{
    private const long MaxFileBytes = 1024 * 1024;
    private readonly object _lock = new();
    private string? _filePath;

    public event Action<LogEntry>? EntryAdded;

    public ActivityLog(string? filePath)
    {
        _filePath = filePath;
        if (filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            // keep one previous file once the log passes 1 MB
            if (File.Exists(filePath) && new FileInfo(filePath).Length > MaxFileBytes) File.Move(filePath, filePath + ".old", true);
            File.AppendAllText(filePath, $"{Environment.NewLine}##### {DateTime.Now:yyyy-MM-dd HH:mm:ss} session start{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _filePath = null;   // carry on with the in-app log only
        }
    }

    public string? FilePath => _filePath;

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            if (_filePath is not null)
            {
                try { File.AppendAllText(_filePath, entry + Environment.NewLine); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* logging must never break an operation */ }
            }
        }
        EntryAdded?.Invoke(entry);
    }

    public void Info(string m) => Write(LogLevel.Info, m);
    public void Ok(string m) => Write(LogLevel.Ok, m);
    public void Skip(string m) => Write(LogLevel.Skip, m);
    public void Warn(string m) => Write(LogLevel.Warn, m);
    public void Error(string m) => Write(LogLevel.Error, m);
    public void Done(string m) => Write(LogLevel.Done, m);
}
