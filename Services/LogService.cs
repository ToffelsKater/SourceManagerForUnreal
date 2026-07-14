namespace UnrealManager.Services;

public sealed class LogService
{
    public static LogService Instance { get; } = new();

    public event Action<string>? LineLogged;

    private readonly Queue<string> _recent = new();
    private const int MaxRecentLines = 300;

    private LogService() { }

    public void Log(string line)
    {
        lock (_recent)
        {
            _recent.Enqueue(line);
            while (_recent.Count > MaxRecentLines) _recent.Dequeue();
        }
        LineLogged?.Invoke(line);
    }

    public void LogHeader(string title)
    {
        Log("");
        Log("========== " + title + " ==========");
    }

    /// <summary>Most recent log lines (oldest first) — used by bug reports.</summary>
    public string[] RecentLines()
    {
        lock (_recent) return _recent.ToArray();
    }
}
