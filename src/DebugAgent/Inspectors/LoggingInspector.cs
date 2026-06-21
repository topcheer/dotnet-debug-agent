using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DebugAgents;

/// <summary>
/// Logging inspector — captures and queries recent application logs.
/// </summary>
public static class LoggingInspector
{
    [DebugTool("get_recent_logs", "Get recent log entries from the in-memory ring buffer")]
    public static object GetRecentLogs(
        [ToolParam("Maximum number of entries to return (default 20)")] int limit = 20,
        [ToolParam("Minimum log level (Trace/Debug/Information/Warning/Error/Critical)")] string level = ""
    )
    {
        var provider = AgentContext.Services?.GetService<InMemoryLoggerProvider>();
        if (provider == null)
            return new { error = "InMemoryLoggerProvider not registered" };

        var entries = provider.GetEntries();

        // Filter by level
        if (!string.IsNullOrEmpty(level) && Enum.TryParse<LogLevel>(level, true, out var logLevel))
            entries = entries.Where(e => e.Level >= logLevel).ToList();

        entries = entries.OrderByDescending(e => e.Timestamp).Take(limit).ToList();

        var result = entries.Select(e => new
        {
            timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
            level = e.Level.ToString(),
            category = e.Category,
            message = e.Message,
            exception = e.Exception?.Message ?? "",
        }).ToList();

        return new { total = result.Count, entries = result };
    }

    [DebugTool("search_logs", "Search log entries by keyword in message or category")]
    public static object SearchLogs(
        [ToolParam("Search keyword (case-insensitive)")] string keyword,
        [ToolParam("Maximum results (default 30)")] int limit = 30
    )
    {
        var provider = AgentContext.Services?.GetService<InMemoryLoggerProvider>();
        if (provider == null)
            return new { error = "InMemoryLoggerProvider not registered" };

        var entries = provider.GetEntries()
            .Where(e => e.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        e.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .Select(e => new
            {
                timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
                level = e.Level.ToString(),
                category = e.Category,
                message = e.Message,
                exception = e.Exception?.Message ?? "",
            })
            .ToList();

        return new { keyword, total = entries.Count, entries };
    }

    [DebugTool("get_log_stats", "Get log entry statistics: count by level, categories, time range")]
    public static object GetLogStats()
    {
        var provider = AgentContext.Services?.GetService<InMemoryLoggerProvider>();
        if (provider == null)
            return new { error = "InMemoryLoggerProvider not registered" };

        var entries = provider.GetEntries();

        if (entries.Count == 0)
            return new { message = "No logs recorded yet" };

        var byLevel = entries
            .GroupBy(e => e.Level)
            .OrderBy(g => (int)g.Key)
            .Select(g => new { level = g.Key.ToString(), count = g.Count() })
            .ToList();

        var byCategory = entries
            .GroupBy(e => e.Category)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new { category = g.Key, count = g.Count() })
            .ToList();

        var earliest = entries.Min(e => e.Timestamp);
        var latest = entries.Max(e => e.Timestamp);

        return new
        {
            total_entries = entries.Count,
            time_range = new
            {
                earliest = earliest.ToString("HH:mm:ss"),
                latest = latest.ToString("HH:mm:ss"),
                span_seconds = Math.Round((latest - earliest).TotalSeconds, 1),
            },
            by_level = byLevel,
            top_categories = byCategory,
        };
    }

    [DebugTool("get_log_levels", "Get the effective minimum log levels per category from logging configuration")]
    public static object GetLogLevels()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var loggerFactory = AgentContext.Services.GetService<ILoggerFactory>();
        if (loggerFactory == null)
            return new { error = "ILoggerFactory not available" };

        // Try to read filter rules from the factory
        var factoryType = loggerFactory.GetType();
        var filterField = factoryType.GetField("_filters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Since internal filter access is limited, we provide info from configuration instead
        var config = AgentContext.Services.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var loggingSection = config?.GetSection("Logging")
            ?.Get<Dictionary<string, object>>();

        if (loggingSection != null)
        {
            return new
            {
                source = "appsettings.json Logging section",
                configuration = loggingSection,
                note = "Log levels are configured in Logging:LogLevel section of configuration",
            };
        }

        return new
        {
            source = "runtime defaults",
            levels = new
            {
                Default = "Information",
                Microsoft_AspNetCore = "Warning",
            },
            note = "Configure log levels in Logging:LogLevel section of appsettings.json",
        };
    }
}
