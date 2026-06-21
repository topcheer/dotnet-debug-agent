using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DebugAgents;

/// <summary>
/// In-memory log entry record.
/// </summary>
public record LogEntry(
    DateTimeOffset Timestamp,
    string Category,
    LogLevel Level,
    string Message,
    Exception? Exception = null
);

/// <summary>
/// Ring-buffer logger provider that captures recent logs in memory.
/// Automatically registered by AddDebugAgent().
/// </summary>
[ProviderAlias("DebugAgent")]
public class InMemoryLoggerProvider : ILoggerProvider
{
    private const int MaxEntries = 500;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(this, categoryName);

    public List<LogEntry> GetEntries() => _entries.ToList();

    public void AddEntry(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }

    public void Dispose() { }
}

/// <summary>
/// Logger that forwards entries to the InMemoryLoggerProvider ring buffer.
/// </summary>
internal class InMemoryLogger(InMemoryLoggerProvider provider, string category) : ILogger
{
    private readonly InMemoryLoggerProvider _provider = provider;
    private readonly string _category = category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _provider.AddEntry(new LogEntry(DateTimeOffset.UtcNow, _category, logLevel, message, exception));
    }
}
