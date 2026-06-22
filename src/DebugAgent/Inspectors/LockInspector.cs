using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace DebugAgents;

/// <summary>
/// Lock and contention inspector — thread pool starvation, lock contention, async state.
/// </summary>
public static class LockInspector
{
    private static long _contentionCount;
    private static DateTimeOffset _contentionSince = DateTimeOffset.UtcNow;

    /// <summary>Increment internal contention counter (call from monitoring hooks).</summary>
    public static void RecordContention() => Interlocked.Increment(ref _contentionCount);

    [DebugTool("get_thread_pool_starvation", "Detect thread pool starvation: available IO/worker threads vs thresholds")]
    public static object GetThreadPoolStarvation()
    {
        ThreadPool.GetAvailableThreads(out int availWorker, out int availCompletion);
        ThreadPool.GetMinThreads(out int minWorker, out int minCompletion);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxCompletion);

        // ThreadPool has no direct public API for queue length.
        // Use availableWorkers vs maxWorkers as a proxy for saturation.

        double starvationScore = 0;
        if (maxWorker > 0)
        {
            // Higher score = more starved. Score = percentage of max threads in use.
            var inUse = maxWorker - availWorker;
            starvationScore = Math.Round((double)inUse / maxWorker * 100, 1);
        }

        var thresholds = new
        {
            worker_warning_pct = 80,   // >80% workers in use is concerning
            worker_critical_pct = 95,  // >95% is critical
        };

        var workerUsagePct = maxWorker > 0 ? Math.Round((double)(maxWorker - availWorker) / maxWorker * 100, 1) : 0;
        var completionUsagePct = maxCompletion > 0 ? Math.Round((double)(maxCompletion - availCompletion) / maxCompletion * 100, 1) : 0;

        var status = workerUsagePct > thresholds.worker_critical_pct ? "CRITICAL"
            : workerUsagePct > thresholds.worker_warning_pct ? "WARNING"
            : "HEALTHY";

        return new
        {
            status,
            available_worker_threads = availWorker,
            available_completion_port_threads = availCompletion,
            max_worker_threads = maxWorker,
            max_completion_port_threads = maxCompletion,
            min_worker_threads = minWorker,
            worker_usage_pct = workerUsagePct,
            completion_usage_pct = completionUsagePct,
            starvation_score = starvationScore,
            thresholds,
        };
    }

    [DebugTool("get_lock_contention", "Get .NET lock contention stats (contentions, current queue length)")]
    public static object GetLockContention()
    {
        // .NET exposes contention via the 'lock-contention' counter in the runtime EventSource.
        // We can't directly read it without an EventListener, so we use what's available:
        // - Our own contention tracker (if wired up)
        // - ThreadPool queue length estimation

        ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out _);

        var monitoredContentions = Interlocked.Read(ref _contentionCount);
        var sinceMinutes = Math.Max(0.1, (DateTimeOffset.UtcNow - _contentionSince).TotalMinutes);

        // Try to read .NET runtime counters via reflection on System.Runtime EventSource
        var runtimeInfo = new Dictionary<string, object?>();
        try
        {
            var genSizes = new Dictionary<string, long>();
            var gcInfo = GC.GetGCMemoryInfo();
            var genInfoArr = gcInfo.GenerationInfo;
            for (int gen = 0; gen <= GC.MaxGeneration && gen < genInfoArr.Length; gen++)
                genSizes[$"gen{gen}_heap_size"] = genInfoArr[gen].SizeAfterBytes;
            runtimeInfo["gc_heap_sizes"] = genSizes;
        }
        catch { }

        return new
        {
            monitored_contentions = monitoredContentions,
            contentions_per_minute = Math.Round(monitoredContentions / sinceMinutes, 2),
            monitoring_since = _contentionSince.ToString("o"),
            thread_pool = new
            {
                available_workers = availWorker,
                max_workers = maxWorker,
                in_use = maxWorker - availWorker,
                usage_pct = maxWorker > 0 ? Math.Round((double)(maxWorker - availWorker) / maxWorker * 100, 1) : 0,
            },
            runtime_info = runtimeInfo.Count > 0 ? runtimeInfo : null,
            note = "Lock contention monitoring requires EventListener or dotnet-counters for full accuracy. Call LockInspector.RecordContention() from your code to track contention.",
        };
    }

    [DebugTool("get_async_state", "List pending async operations from the current TaskScheduler")]
    public static object GetAsyncState()
    {
        var scheduler = TaskScheduler.Current;
        var info = new Dictionary<string, object?>
        {
            ["scheduler_type"] = scheduler?.GetType().Name ?? "Unknown",
            ["scheduler_max_concurrency"] = (object?)null,
        };

        // Try to get maximum concurrency level
        try
        {
            var maxConcurrency = scheduler?.MaximumConcurrencyLevel;
            if (maxConcurrency.HasValue)
                info["scheduler_max_concurrency"] = maxConcurrency.Value;
        }
        catch { }

        // Get SynchronizationContext info
        var syncCtx = SynchronizationContext.Current;
        var asyncState = new List<object>();

        // We can't enumerate all pending Tasks from the scheduler directly,
        // but we can provide scheduling-related diagnostics.

        ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
        ThreadPool.GetMinThreads(out int minWorker, out _);
        ThreadPool.GetMaxThreads(out int maxWorker, out _);

        return new
        {
            task_scheduler = info,
            synchronization_context = syncCtx?.GetType().Name ?? "None",
            thread_pool = new
            {
                available_workers = availWorker,
                min_workers = minWorker,
                max_workers = maxWorker,
            },
            pending_tasks_estimate = maxWorker - availWorker,
            note = "TaskScheduler does not expose a public pending-task enumeration. The pending_tasks_estimate is based on worker thread utilization.",
        };
    }
}
