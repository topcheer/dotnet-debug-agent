using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Comprehensive runtime snapshot for cross-inspector comparison.
/// </summary>
public record RuntimeSnapshot(
    string Id,
    DateTimeOffset Timestamp,
    Dictionary<string, object> Metrics
);

/// <summary>
/// Snapshot inspector — collects metrics across all inspectors for point-in-time comparison.
/// </summary>
public static class SnapshotInspector
{
    private static readonly List<RuntimeSnapshot> _snapshots = new();
    private static readonly object _lock = new();
    private const int MaxSnapshots = 30;
    private static int _snapshotCounter;

    [DebugTool("take_snapshot", "Collect metrics across ALL inspectors: thread pool, heap, GC, errors, HTTP, cache. Returns snapshot ID + summary.")]
    public static object TakeSnapshot()
    {
        try
        {
            var metrics = CollectAllMetrics();

            string id;
            lock (_lock)
            {
                id = $"snap-{++_snapshotCounter:D3}";
                var snapshot = new RuntimeSnapshot(id, DateTimeOffset.UtcNow, metrics);
                _snapshots.Add(snapshot);

                while (_snapshots.Count > MaxSnapshots)
                    _snapshots.RemoveAt(0);
            }

            // Build summary
            var summary = new
            {
                thread_pool = GetMetricGroup(metrics, "thread_pool"),
                memory = GetMetricGroup(metrics, "memory"),
                gc = GetMetricGroup(metrics, "gc"),
                errors = GetMetricGroup(metrics, "errors"),
                http = GetMetricGroup(metrics, "http"),
                cache = GetMetricGroup(metrics, "cache"),
                process = GetMetricGroup(metrics, "process"),
            };

            return new
            {
                snapshot_id = id,
                timestamp = metrics.TryGetValue("__timestamp", out var ts) ? ts : DateTimeOffset.UtcNow.ToString("o"),
                total_metrics = metrics.Count,
                summary,
                total_snapshots = _snapshots.Count,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("compare_snapshots", "Compare two snapshots by ID. Returns all changed values with delta and percentage.")]
    public static object CompareSnapshots(
        [ToolParam("First (older) snapshot ID")] string snapshotId1,
        [ToolParam("Second (newer) snapshot ID")] string snapshotId2)
    {
        try
        {
            RuntimeSnapshot? snap1, snap2;
            lock (_lock)
            {
                snap1 = _snapshots.FirstOrDefault(s => s.Id == snapshotId1);
                snap2 = _snapshots.FirstOrDefault(s => s.Id == snapshotId2);
            }

            if (snap1 == null)
                return new { error = $"Snapshot '{snapshotId1}' not found" };
            if (snap2 == null)
                return new { error = $"Snapshot '{snapshotId2}' not found" };

            var changes = new List<object>();
            var unchangedCount = 0;

            var allKeys = snap1.Metrics.Keys
                .Union(snap2.Metrics.Keys)
                .Where(k => !k.StartsWith("__"))
                .OrderBy(k => k)
                .ToList();

            foreach (var key in allKeys)
            {
                var val1 = snap1.Metrics.TryGetValue(key, out var v1) ? v1 : null;
                var val2 = snap2.Metrics.TryGetValue(key, out var v2) ? v2 : null;

                if (val1 == null && val2 == null)
                {
                    unchangedCount++;
                    continue;
                }

                var d1 = TryToDouble(val1);
                var d2 = TryToDouble(val2);

                if (d1.HasValue && d2.HasValue)
                {
                    var delta = d2.Value - d1.Value;
                    if (Math.Abs(delta) < 0.0001)
                    {
                        unchangedCount++;
                        continue;
                    }

                    var pct = d1.Value != 0
                        ? Math.Round(delta / d1.Value * 100, 1)
                        : delta != 0 ? (double?)null : 0;

                    changes.Add(new
                    {
                        metric = key,
                        before = FormatValue(val1),
                        after = FormatValue(val2),
                        delta = Math.Round(delta, 4),
                        change_pct = pct,
                        direction = delta > 0 ? "up" : "down",
                    });
                }
                else
                {
                    // Non-numeric or changed type
                    if (!Equals(val1?.ToString(), val2?.ToString()))
                    {
                        changes.Add(new
                        {
                            metric = key,
                            before = FormatValue(val1),
                            after = FormatValue(val2),
                            delta = (double?)null,
                            change_pct = (double?)null,
                            direction = "changed",
                        });
                    }
                    else
                    {
                        unchangedCount++;
                    }
                }
            }

            return new
            {
                snapshot1 = new { id = snap1.Id, timestamp = snap1.Timestamp.ToString("o") },
                snapshot2 = new { id = snap2.Id, timestamp = snap2.Timestamp.ToString("o") },
                time_between_seconds = Math.Round((snap2.Timestamp - snap1.Timestamp).TotalSeconds, 1),
                changed_metrics = changes.Count,
                unchanged_metrics = unchangedCount,
                changes = changes.OrderByDescending(c => Math.Abs(((dynamic)c).delta ?? 0)).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("list_snapshots", "List all stored runtime snapshots")]
    public static object ListSnapshots()
    {
        try
        {
            List<RuntimeSnapshot> snapshots;
            lock (_lock)
            {
                snapshots = _snapshots.ToList();
            }

            if (snapshots.Count == 0)
                return new
                {
                    message = "No snapshots stored. Call take_snapshot to capture a point-in-time view.",
                };

            var list = snapshots.Select(s =>
            {
                var summary = new
                {
                    managed_memory_mb = GetMetric(s.Metrics, "memory.managed_heap_mb"),
                    working_set_mb = GetMetric(s.Metrics, "process.working_set_mb"),
                    gen2_collections = GetMetric(s.Metrics, "gc.gen2_collections"),
                    thread_count = GetMetric(s.Metrics, "process.thread_count"),
                    error_count = GetMetric(s.Metrics, "errors.total_lifetime"),
                };
                return new
                {
                    id = s.Id,
                    timestamp = s.Timestamp.ToString("o"),
                    metric_count = s.Metrics.Count,
                    summary,
                };
            }).ToList();

            return new
            {
                total = list.Count,
                snapshots = list,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static Dictionary<string, object> CollectAllMetrics()
    {
        var metrics = new Dictionary<string, object>();

        // Thread pool stats
        try
        {
            ThreadPool.GetAvailableThreads(out int worker, out int completion);
            ThreadPool.GetMinThreads(out int minWorker, out int minCompletion);
            ThreadPool.GetMaxThreads(out int maxWorker, out int maxCompletion);

            metrics["thread_pool.available_worker"] = worker;
            metrics["thread_pool.available_completion"] = completion;
            metrics["thread_pool.min_worker"] = minWorker;
            metrics["thread_pool.max_worker"] = maxWorker;
            metrics["thread_pool.min_completion"] = minCompletion;
            metrics["thread_pool.max_completion"] = maxCompletion;
        }
        catch { }

        // Memory stats
        try
        {
            var proc = Process.GetCurrentProcess();
            var managedHeap = GC.GetTotalMemory(false);

            metrics["memory.managed_heap_bytes"] = managedHeap;
            metrics["memory.managed_heap_mb"] = Math.Round(managedHeap / 1024.0 / 1024.0, 2);
            metrics["memory.working_set_bytes"] = proc.WorkingSet64;
            metrics["memory.working_set_mb"] = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 2);
            metrics["memory.private_memory_bytes"] = proc.PrivateMemorySize64;
            metrics["memory.private_memory_mb"] = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 2);

            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                metrics["memory.heap_size_bytes"] = (long)gcInfo.HeapSizeBytes;
                metrics["memory.heap_size_mb"] = Math.Round(gcInfo.HeapSizeBytes / 1024.0 / 1024.0, 2);
                metrics["memory.fragmented_bytes"] = (long)gcInfo.FragmentedBytes;
                metrics["memory.pinned_objects"] = (long)gcInfo.PinnedObjectsCount;
            }
            catch { }
        }
        catch { }

        // GC stats
        try
        {
            metrics["gc.gen0_collections"] = GC.CollectionCount(0);
            metrics["gc.gen1_collections"] = GC.CollectionCount(1);
            metrics["gc.gen2_collections"] = GC.CollectionCount(2);
            metrics["gc.max_generation"] = GC.MaxGeneration;
            metrics["gc.total_allocated_bytes"] = GC.GetTotalAllocatedBytes();

            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                metrics["gc.finalization_pending"] = (long)gcInfo.FinalizationPendingCount;
            }
            catch { }
        }
        catch { }

        // Error stats
        try
        {
            metrics["errors.total_lifetime"] = ErrorTracker.TotalErrors;
            metrics["errors.in_buffer"] = ErrorTracker.GetAll().Count;
        }
        catch { }

        // HTTP stats
        try
        {
            var httpCalls = OutboundHttpTracker.GetAll();
            metrics["http.total_calls"] = httpCalls.Count;
            metrics["http.error_count"] = httpCalls.Count(c => !c.Success);
            metrics["http.avg_latency_ms"] = httpCalls.Count > 0
                ? Math.Round(httpCalls.Average(c => c.DurationMs), 2)
                : 0;
            metrics["http.registered_clients"] = OutboundHttpTracker.GetClientNames().Count;
        }
        catch { }

        // Cache stats
        try
        {
            var cache = AgentContext.Services?.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()
                ?? (AgentContext.Services?.GetService(typeof(Microsoft.Extensions.Caching.Memory.IMemoryCache)) as Microsoft.Extensions.Caching.Memory.IMemoryCache);
            metrics["cache.registered"] = cache != null;
        }
        catch { }

        // Process stats
        try
        {
            var proc = Process.GetCurrentProcess();
            metrics["process.pid"] = proc.Id;
            metrics["process.thread_count"] = proc.Threads.Count;
            metrics["process.handle_count"] = proc.HandleCount;
            metrics["process.uptime_seconds"] = Math.Round((DateTime.Now - proc.StartTime).TotalSeconds, 1);
            metrics["process.cpu_time_total_seconds"] = Math.Round(proc.TotalProcessorTime.TotalSeconds, 2);
            metrics["process.cpu_time_user_seconds"] = Math.Round(proc.UserProcessorTime.TotalSeconds, 2);
        }
        catch { }

        metrics["__timestamp"] = DateTimeOffset.UtcNow.ToString("o");

        return metrics;
    }

    private static object? GetMetric(Dictionary<string, object> metrics, string key)
    {
        return metrics.TryGetValue(key, out var val) ? val : null;
    }

    private static Dictionary<string, object>? GetMetricGroup(Dictionary<string, object> metrics, string prefix)
    {
        var group = metrics
            .Where(kvp => kvp.Key.StartsWith(prefix + "."))
            .ToDictionary(
                kvp => kvp.Key.Substring(prefix.Length + 1),
                kvp => kvp.Value
            );
        return group.Count > 0 ? group : null;
    }

    private static double? TryToDouble(object? val)
    {
        if (val == null) return null;
        if (val is double d) return d;
        if (val is int i) return i;
        if (val is long l) return l;
        if (val is float f) return f;
        if (val is decimal dec) return (double)dec;
        if (val is bool b) return b ? 1 : 0;
        if (double.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static string FormatValue(object? val)
    {
        if (val == null) return "null";
        if (val is double d) return Math.Round(d, 4).ToString();
        if (val is bool b) return b.ToString().ToLowerInvariant();
        return val.ToString() ?? "null";
    }
}
