using System.Diagnostics;
using System.Collections.Concurrent;

namespace DebugAgents;

/// <summary>
/// HTTP request ring buffer tracker.
/// </summary>
public static class HttpRequestTracker
{
    private const int MaxSize = 500;
    private static readonly ConcurrentQueue<RequestRecord> _buffer = new();

    public static void Record(string method, string path, int status, double durationMs, string client = "")
    {
        _buffer.Enqueue(new RequestRecord(DateTimeOffset.UtcNow, method, path, status, durationMs, client));
        while (_buffer.Count > MaxSize)
            _buffer.TryDequeue(out _);
    }

    public static List<RequestRecord> GetAll() => _buffer.ToList();
}

public record RequestRecord(DateTimeOffset Timestamp, string Method, string Path, int Status, double DurationMs, string Client);

public static class HttpInspector
{
    [DebugTool("get_recent_requests", "Get recent HTTP requests from the in-memory ring buffer")]
    public static object GetRecentRequests()
    {
        var reqs = HttpRequestTracker.GetAll();
        reqs.Reverse();
        return new { total = reqs.Count, requests = reqs.Take(50) };
    }

    [DebugTool("get_error_requests", "Get all error requests (4xx/5xx status codes)")]
    public static object GetErrorRequests()
    {
        var reqs = HttpRequestTracker.GetAll()
            .Where(r => r.Status >= 400)
            .OrderByDescending(r => r.DurationMs)
            .ToList();
        return new { count = reqs.Count, requests = reqs };
    }

    [DebugTool("get_request_stats", "Get HTTP request statistics: count, P50/P95/P99 latency, error rate")]
    public static object GetRequestStats()
    {
        var reqs = HttpRequestTracker.GetAll();
        if (reqs.Count == 0)
            return new { message = "No requests recorded yet" };

        var durations = reqs.Select(r => r.DurationMs).OrderBy(d => d).ToList();
        var n = durations.Count;
        var errors = reqs.Count(r => r.Status >= 400);

        return new
        {
            total_requests = n,
            error_count = errors,
            error_rate = $"{errors * 100.0 / n:F1}%",
            latency_ms = new
            {
                min = durations[0],
                p50 = durations[Math.Min((int)(0.5 * n), n - 1)],
                p95 = durations[Math.Min((int)(0.95 * n), n - 1)],
                p99 = durations[Math.Min((int)(0.99 * n), n - 1)],
                max = durations[n - 1],
            },
        };
    }
}
