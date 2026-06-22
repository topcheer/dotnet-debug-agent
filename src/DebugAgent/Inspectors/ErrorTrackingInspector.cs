using System.Collections.Concurrent;
using System.Diagnostics;

namespace DebugAgents;

/// <summary>
/// Error tracking record for unhandled exceptions.
/// </summary>
public record ErrorEntry(
    DateTimeOffset Timestamp,
    string Type,
    string Message,
    string StackTrace,
    string RequestPath
);

/// <summary>
/// Static error tracker with a ring buffer (max 50 entries).
/// Call ErrorTracker.RecordError() from middleware or exception handlers.
/// </summary>
public static class ErrorTracker
{
    private const int MaxSize = 50;
    private static readonly ConcurrentQueue<ErrorEntry> _buffer = new();
    private static long _totalErrors;

    public static void RecordError(Exception ex, string path)
    {
        var entry = new ErrorEntry(
            DateTimeOffset.UtcNow,
            ex.GetType().Name,
            ex.Message,
            ex.StackTrace ?? "",
            path
        );
        _buffer.Enqueue(entry);
        while (_buffer.Count > MaxSize)
            _buffer.TryDequeue(out _);
        Interlocked.Increment(ref _totalErrors);
    }

    public static List<ErrorEntry> GetAll() => _buffer.ToList();
    public static long TotalErrors => _totalErrors;

    /// <summary>Process uptime for rate calculations.</summary>
    private static DateTimeOffset ProcessStart =>
        Process.GetCurrentProcess().StartTime;
}

/// <summary>
/// Error tracking inspector — captures and reports unhandled exceptions.
/// </summary>
public static class ErrorTrackingInspector
{
    [DebugTool("get_recent_errors", "Get recent unhandled exceptions captured by the agent (ring buffer, max 50)")]
    public static object GetRecentErrors()
    {
        var errors = ErrorTracker.GetAll();
        errors.Reverse();
        return new
        {
            total_captured = errors.Count,
            errors = errors.Select(e => new
            {
                timestamp = e.Timestamp.ToString("o"),
                type = e.Type,
                message = e.Message,
                stack_trace = e.StackTrace.Length > 500 ? e.StackTrace[..500] + "..." : e.StackTrace,
                request_path = e.RequestPath,
            }),
        };
    }

    [DebugTool("get_error_stats", "Get error statistics: total count, rate per minute, top error types")]
    public static object GetErrorStats()
    {
        var errors = ErrorTracker.GetAll();
        if (errors.Count == 0)
            return new { message = "No errors recorded yet", total = 0 };

        var proc = Process.GetCurrentProcess();
        var uptimeMinutes = Math.Max(1, (DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime).TotalMinutes);

        var byType = errors
            .GroupBy(e => e.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => new { error_type = g.Key, count = g.Count() })
            .ToList();

        var byPath = errors
            .GroupBy(e => e.RequestPath)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new { path = g.Key, count = g.Count() })
            .ToList();

        var oldest = errors.Min(e => e.Timestamp);
        var newest = errors.Max(e => e.Timestamp);
        var windowMinutes = Math.Max(0.1, (newest - oldest).TotalMinutes);

        return new
        {
            total_lifetime = ErrorTracker.TotalErrors,
            in_buffer = errors.Count,
            rate_per_minute = Math.Round(errors.Count / windowMinutes, 2),
            rate_per_minute_uptime = Math.Round(ErrorTracker.TotalErrors / uptimeMinutes, 2),
            window_start = oldest.ToString("o"),
            window_end = newest.ToString("o"),
            top_error_types = byType,
            top_error_paths = byPath,
        };
    }
}
