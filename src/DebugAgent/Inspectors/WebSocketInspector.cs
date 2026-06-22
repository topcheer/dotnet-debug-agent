using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace DebugAgents;

/// <summary>
/// WebSocket connection entry.
/// </summary>
public record WebSocketEntry(
    string Id,
    DateTimeOffset ConnectedAt,
    string RemoteIp,
    string Path,
    long MessagesSent,
    long MessagesReceived,
    bool IsActive
);

/// <summary>
/// Static WebSocket connection registry. Call WebSocketRegistry.RegisterWebSocket() for each connection.
/// </summary>
public static class WebSocketRegistry
{
    private static readonly ConcurrentDictionary<string, WebSocketEntry> _connections = new();
    private static long _totalConnections;
    private static long _totalMessagesSent;
    private static long _totalMessagesReceived;

    public static string RegisterWebSocket(string id, WebSocket connection, string remoteIp = "", string path = "")
    {
        var entry = new WebSocketEntry(id, DateTimeOffset.UtcNow, remoteIp, path, 0, 0, true);
        _connections[id] = entry;
        Interlocked.Increment(ref _totalConnections);
        return id;
    }

    public static void UnregisterWebSocket(string id)
    {
        if (_connections.TryRemove(id, out var entry))
        {
            // Mark as inactive for historical purposes is not possible with record immutability,
            // but we remove it to keep the active list clean.
        }
    }

    public static void RecordMessage(string id, bool sent)
    {
        if (_connections.TryGetValue(id, out var entry))
        {
            var updated = sent
                ? entry with { MessagesSent = entry.MessagesSent + 1 }
                : entry with { MessagesReceived = entry.MessagesReceived + 1 };
            _connections[id] = updated;
        }
        if (sent) Interlocked.Increment(ref _totalMessagesSent);
        else Interlocked.Increment(ref _totalMessagesReceived);
    }

    public static List<WebSocketEntry> GetActiveConnections() => _connections.Values.ToList();

    public static long TotalConnections => _totalConnections;
    public static long TotalMessagesSent => _totalMessagesSent;
    public static long TotalMessagesReceived => _totalMessagesReceived;
}

/// <summary>
/// WebSocket inspector — list active WebSocket connections and get statistics.
/// </summary>
public static class WebSocketInspector
{
    [DebugTool("get_ws_connections", "List active WebSocket connections with details")]
    public static object GetWsConnections()
    {
        var connections = WebSocketRegistry.GetActiveConnections();

        return new
        {
            active_connections = connections.Count,
            connections = connections.Select(c => new
            {
                id = c.Id,
                connected_at = c.ConnectedAt.ToString("o"),
                duration_seconds = Math.Round((DateTimeOffset.UtcNow - c.ConnectedAt).TotalSeconds, 1),
                remote_ip = c.RemoteIp,
                path = c.Path,
                messages_sent = c.MessagesSent,
                messages_received = c.MessagesReceived,
                is_active = c.IsActive,
            }),
        };
    }

    [DebugTool("get_ws_stats", "Get WebSocket statistics (total connections, active, messages sent/received)")]
    public static object GetWsStats()
    {
        var active = WebSocketRegistry.GetActiveConnections();

        return new
        {
            total_connections = WebSocketRegistry.TotalConnections,
            active_connections = active.Count,
            total_messages_sent = WebSocketRegistry.TotalMessagesSent,
            total_messages_received = WebSocketRegistry.TotalMessagesReceived,
            total_messages = WebSocketRegistry.TotalMessagesSent + WebSocketRegistry.TotalMessagesReceived,
            connections_by_path = active
                .GroupBy(c => c.Path)
                .Select(g => new { path = g.Key, count = g.Count() })
                .ToList(),
            note = active.Count == 0 && WebSocketRegistry.TotalConnections == 0
                ? "No WebSocket connections tracked. Register connections with WebSocketRegistry.RegisterWebSocket(id, connection, ip, path)."
                : null,
        };
    }
}
