using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DebugAgents;

/// <summary>
/// Outbound HTTP call record.
/// </summary>
public record OutboundHttpRecord(
    DateTimeOffset Timestamp,
    string Name,
    string Method,
    string Destination,
    int StatusCode,
    double DurationMs,
    bool Success,
    string? Error
);

/// <summary>
/// Outbound HTTP tracker with a ring buffer. Wrap your HttpClient with OutboundHttpHandler to track.
/// </summary>
public static class OutboundHttpTracker
{
    private const int MaxSize = 200;
    private static readonly ConcurrentQueue<OutboundHttpRecord> _buffer = new();
    private static readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public static void RegisterHttpClient(string name, HttpClient client)
    {
        _clients[name] = client;
    }

    public static void Record(OutboundHttpRecord record)
    {
        _buffer.Enqueue(record);
        while (_buffer.Count > MaxSize)
            _buffer.TryDequeue(out _);
    }

    public static List<OutboundHttpRecord> GetAll() => _buffer.ToList();
    public static List<string> GetClientNames() => _clients.Keys.ToList();
}

/// <summary>
/// DelegatingHandler that wraps HttpClient to track outbound calls.
/// Usage: client.InnerHandler = new OutboundHttpHandler("MyClient") { InnerHandler = new HttpClientHandler() };
/// </summary>
public class OutboundHttpHandler : DelegatingHandler
{
    private readonly string _name;

    public OutboundHttpHandler(string name) => _name = name;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        int statusCode = 0;
        bool success = false;
        string? error = null;

        try
        {
            var resp = await base.SendAsync(request, cancellationToken);
            sw.Stop();
            statusCode = (int)resp.StatusCode;
            success = resp.IsSuccessStatusCode;
            return resp;
        }
        catch (Exception ex)
        {
            sw.Stop();
            error = ex.Message;
            throw;
        }
        finally
        {
            OutboundHttpTracker.Record(new OutboundHttpRecord(
                DateTimeOffset.UtcNow,
                _name,
                request.Method.Method,
                request.RequestUri?.ToString() ?? "",
                statusCode,
                sw.Elapsed.TotalMilliseconds,
                success,
                error
            ));
        }
    }
}

/// <summary>
/// Outbound HTTP inspector — summary and error tracking for outbound calls.
/// </summary>
public static class OutboundHttpInspector
{
    [DebugTool("get_outbound_http_summary", "Summary of outbound HTTP calls (total, avg latency, error rate, top destinations)")]
    public static object GetOutboundHttpSummary()
    {
        var calls = OutboundHttpTracker.GetAll();
        if (calls.Count == 0)
            return new
            {
                message = "No outbound HTTP calls tracked yet",
                hint = "Register HttpClients with OutboundHttpTracker.RegisterHttpClient(name, client) and use OutboundHttpHandler.",
            };

        var total = calls.Count;
        var errors = calls.Count(c => !c.Success);
        var avgLatency = calls.Average(c => c.DurationMs);

        var byDestination = calls
            .GroupBy(c => GetHost(c.Destination))
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new
            {
                destination = g.Key,
                count = g.Count(),
                avg_latency_ms = Math.Round(g.Average(c => c.DurationMs), 2),
                error_count = g.Count(c => !c.Success),
            })
            .ToList();

        var byClient = calls
            .GroupBy(c => c.Name)
            .OrderByDescending(g => g.Count())
            .Select(g => new
            {
                client_name = g.Key,
                count = g.Count(),
                avg_latency_ms = Math.Round(g.Average(c => c.DurationMs), 2),
            })
            .ToList();

        return new
        {
            total_calls = total,
            error_count = errors,
            error_rate = $"{errors * 100.0 / total:F1}%",
            avg_latency_ms = Math.Round(avgLatency, 2),
            top_destinations = byDestination,
            by_client_name = byClient,
            registered_clients = OutboundHttpTracker.GetClientNames(),
        };
    }

    [DebugTool("get_outbound_http_errors", "List failed outbound HTTP calls")]
    public static object GetOutboundHttpErrors()
    {
        var errors = OutboundHttpTracker.GetAll()
            .Where(c => !c.Success)
            .OrderByDescending(c => c.Timestamp)
            .Take(50)
            .Select(c => new
            {
                timestamp = c.Timestamp.ToString("o"),
                client_name = c.Name,
                method = c.Method,
                destination = c.Destination,
                status_code = c.StatusCode,
                duration_ms = Math.Round(c.DurationMs, 2),
                error = c.Error ?? $"HTTP {c.StatusCode}",
            })
            .ToList();

        return new
        {
            total_errors = errors.Count,
            errors,
        };
    }

    private static string GetHost(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Host;
        }
        catch { }
        return url;
    }
}
