using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Memory cache inspector — uses reflection to inspect IMemoryCache contents.
/// </summary>
public static class CacheInspector
{
    [DebugTool("get_cache_keys", "List all keys in the in-memory cache")]
    public static object GetCacheKeys()
    {
        var cache = AgentContext.Services?.GetService<IMemoryCache>();
        if (cache == null)
            return new { error = "IMemoryCache not registered" };

        var keys = GetCacheKeysViaReflection(cache);

        if (keys.Count == 0)
            return new { message = "Cache is empty", total = 0 };

        var keyInfo = keys.Select(k => new
        {
            key = k.ToString(),
            key_type = k.GetType().Name,
        }).ToList();

        return new { total = keyInfo.Count, keys = keyInfo };
    }

    [DebugTool("get_cache_stats", "Get in-memory cache statistics: entry count, approximate memory usage")]
    public static object GetCacheStats()
    {
        var cache = AgentContext.Services?.GetService<IMemoryCache>();
        if (cache == null)
            return new { error = "IMemoryCache not registered" };

        var keys = GetCacheKeysViaReflection(cache);

        // Try to get cache options/stats via reflection
        var cacheType = cache.GetType();
        var stats = new Dictionary<string, object>();

        // MemoryCache has internal _stats field in some versions
        var statsField = cacheType.GetField("_stats", BindingFlags.NonPublic | BindingFlags.Instance);
        if (statsField != null)
        {
            var statsObj = statsField.GetValue(cache);
            if (statsObj != null)
            {
                var statsType = statsObj.GetType();
                foreach (var prop in statsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try { stats[prop.Name] = prop.GetValue(statsObj)!; }
                    catch { }
                }
            }
        }

        return new
        {
            total_entries = keys.Count,
            cache_type = cacheType.Name,
            internal_stats = stats.Count > 0 ? stats : null,
        };
    }

    [DebugTool("get_cache_value", "Get the value of a specific cache key")]
    public static object GetCacheValue([ToolParam("Cache key name")] string key)
    {
        var cache = AgentContext.Services?.GetService<IMemoryCache>();
        if (cache == null)
            return new { error = "IMemoryCache not registered" };

        if (cache.TryGetValue(key, out var value))
        {
            return new
            {
                key,
                found = true,
                value_type = value?.GetType().Name ?? "null",
                value = value?.ToString() ?? "(null)",
            };
        }

        // Try case-insensitive match
        var allKeys = GetCacheKeysViaReflection(cache);
        var match = allKeys.FirstOrDefault(k =>
            k.ToString()?.Equals(key, StringComparison.OrdinalIgnoreCase) == true);

        if (match != null && cache.TryGetValue(match, out var ciValue))
        {
            return new
            {
                key = match.ToString(),
                found = true,
                value_type = ciValue?.GetType().Name ?? "null",
                value = ciValue?.ToString() ?? "(null)",
                note = "Matched case-insensitively",
            };
        }

        return new { error = $"Key '{key}' not found in cache. Use get_cache_keys to list all keys." };
    }

    private static List<object> GetCacheKeysViaReflection(IMemoryCache cache)
    {
        var keys = new List<object>();

        // MemoryCache stores entries in a ConcurrentDictionary<string, CacheEntry>
        var cacheType = cache.GetType();
        var entriesField = cacheType.GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? cacheType.GetField("_stringKeyEntries", BindingFlags.NonPublic | BindingFlags.Instance);

        if (entriesField != null)
        {
            var entries = entriesField.GetValue(cache) as System.Collections.IDictionary;
            if (entries != null)
            {
                foreach (var k in entries.Keys)
                    keys.Add(k);
            }
        }

        return keys;
    }
}
