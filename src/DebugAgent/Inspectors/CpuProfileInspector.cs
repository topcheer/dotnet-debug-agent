using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace DebugAgents;

/// <summary>
/// Represents a single profiled function with timing statistics.
/// </summary>
public record FunctionProfile(
    string MethodName,
    string ClassName,
    double SelfMs,
    double CumulativeMs,
    int SampleCount
);

/// <summary>
/// CPU profiler state — captures runtime metrics and method-level timing via registration.
/// </summary>
public static class CpuProfileState
{
    private static readonly ConcurrentDictionary<string, FunctionProfile> _profiles = new();
    private static readonly ConcurrentDictionary<string, long> _sampleCounts = new();
    private static CpuMetricsListener? _listener;
    private static Timer? _autoStopTimer;
    private static volatile bool _isProfiling;
    private static DateTimeOffset _profileStart;
    private static DateTimeOffset _profileEnd;
    private static readonly object _lock = new();

    internal static bool IsProfiling => _isProfiling;

    internal static DateTimeOffset ProfileStart => _profileStart;
    internal static DateTimeOffset ProfileEnd => _profileEnd;

    /// <summary>
    /// Record a method execution for profiling. Call from interceptors or manually.
    /// </summary>
    public static void RecordMethod(string className, string methodName, double elapsedMs)
    {
        var key = $"{className}.{methodName}";
        _sampleCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
        _profiles.AddOrUpdate(key,
            _ => new FunctionProfile(methodName, className, elapsedMs, elapsedMs, 1),
            (_, existing) => new FunctionProfile(
                existing.MethodName,
                existing.ClassName,
                existing.SelfMs + elapsedMs,
                existing.CumulativeMs + elapsedMs,
                existing.SampleCount + 1
            ));
    }

    internal static void StartProfiling()
    {
        lock (_lock)
        {
            _isProfiling = true;
            _profileStart = DateTimeOffset.UtcNow;

            // Start listening to System.Runtime event counters for CPU/GC metrics
            try
            {
                _listener?.Dispose();
                _listener = new CpuMetricsListener();
            }
            catch
            {
                // EventListener creation may fail in restricted environments
            }
        }
    }

    internal static void StopProfiling()
    {
        lock (_lock)
        {
            _isProfiling = false;
            _profileEnd = DateTimeOffset.UtcNow;
            _autoStopTimer?.Dispose();
            _autoStopTimer = null;

            try
            {
                _listener?.Dispose();
                _listener = null;
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    internal static void ScheduleAutoStop(int durationSeconds)
    {
        _autoStopTimer?.Dispose();
        _autoStopTimer = new Timer(_ => StopProfiling(), null, durationSeconds * 1000, Timeout.Infinite);
    }

    internal static Dictionary<string, double> GetRuntimeCounters() =>
        _listener?.GetCounters() ?? new Dictionary<string, double>();

    internal static List<FunctionProfile> GetProfiles() => _profiles.Values.ToList();

    internal static void ClearProfiles()
    {
        _profiles.Clear();
        _sampleCounts.Clear();
    }
}

/// <summary>
/// EventListener that captures System.Runtime CPU/memory counters during profiling.
/// </summary>
internal class CpuMetricsListener : EventListener
{
    private readonly ConcurrentDictionary<string, double> _counters = new();


    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        try
        {
            if (eventSource.Name == "System.Runtime"
                || eventSource.Name.Contains("AspNetCore")
                || eventSource.Name.StartsWith("Microsoft-"))
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }
        catch { }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            if (eventData.EventName == "EventCounters" && eventData.Payload != null)
            {
                foreach (var item in eventData.Payload)
                {
                    if (item is System.Collections.IDictionary dict)
                    {
                        var name = dict.Contains("DisplayName") ? dict["DisplayName"]?.ToString() : null;
                        if (name == null) continue;

                        double value = 0;
                        if (dict.Contains("Mean") && dict["Mean"] is IConvertible meanVal)
                            value = Convert.ToDouble(meanVal);
                        else if (dict.Contains("Increment") && dict["Increment"] is IConvertible incVal)
                            value = Convert.ToDouble(incVal);

                        if (value != 0)
                            _counters[name] = value;
                    }
                }
            }
        }
        catch { }
    }

    public Dictionary<string, double> GetCounters()
    {
        var snapshot = new Dictionary<string, double>();
        foreach (var kvp in _counters)
            snapshot[kvp.Key] = kvp.Value;
        return snapshot;
    }
}

/// <summary>
/// CPU profiling inspector — sampling-based profiling with runtime counters and method timing.
/// </summary>
public static class CpuProfileInspector
{
    [DebugTool("start_cpu_profile", "Start CPU profiling. Captures runtime counters (CPU%, GC%, thread pool) and method timings via registration. Auto-stops after duration_seconds.")]
    public static object StartCpuProfile(int duration_seconds = 10)
    {
        try
        {
            if (CpuProfileState.IsProfiling)
                return new { error = "CPU profiling is already active. Call stop_cpu_profile first." };

            var duration = Math.Clamp(duration_seconds, 1, 300);

            CpuProfileState.ClearProfiles();
            CpuProfileState.StartProfiling();
            CpuProfileState.ScheduleAutoStop(duration);

            return new
            {
                status = "profiling_started",
                duration_seconds = duration,
                message = $"CPU profiling started. Will auto-stop after {duration}s. Call stop_cpu_profile to get results early.",
                hint = "Use CpuProfileState.RecordMethod(class, method, elapsedMs) from your code for method-level profiling.",
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("stop_cpu_profile", "Stop CPU profiling and return top 20 methods by cumulative time")]
    public static object StopCpuProfile()
    {
        try
        {
            if (!CpuProfileState.IsProfiling)
                return new { error = "No active CPU profiling session. Call start_cpu_profile first." };

            CpuProfileState.StopProfiling();

            var profiles = CpuProfileState.GetProfiles();
            var duration = CpuProfileState.ProfileEnd - CpuProfileState.ProfileStart;
            var counters = CpuProfileState.GetRuntimeCounters();

            var topMethods = profiles
                .OrderByDescending(p => p.CumulativeMs)
                .Take(20)
                .Select(p => new
                {
                    method_name = p.MethodName,
                    class_name = p.ClassName,
                    self_ms = Math.Round(p.SelfMs, 2),
                    cumulative_ms = Math.Round(p.CumulativeMs, 2),
                    sample_count = p.SampleCount,
                    avg_ms = p.SampleCount > 0 ? Math.Round(p.CumulativeMs / p.SampleCount, 2) : 0,
                })
                .ToList();

            return new
            {
                status = "profiling_stopped",
                profile_duration_seconds = Math.Round(duration.TotalSeconds, 2),
                total_methods_profiled = profiles.Count,
                top_methods = topMethods,
                runtime_counters = counters.Count > 0 ? counters : null,
                counter_note = counters.Count == 0
                    ? "System.Runtime event counters not available. CPU% and GC% require event source support."
                    : null,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("get_top_functions", "Return top N profiled functions from the last profiling session. Params: limit (default 20), sort_by (cumulative/self/count)")]
    public static object GetTopFunctions(
        int limit = 20,
        [ToolParam("Sort by: cumulative (default), self, or count")] string sort_by = "cumulative")
    {
        try
        {
            var profiles = CpuProfileState.GetProfiles();
            if (profiles.Count == 0)
                return new
                {
                    message = "No profiling data available. Run start_cpu_profile then stop_cpu_profile first.",
                };

            var clampedLimit = Math.Clamp(limit, 1, 100);

            IEnumerable<FunctionProfile> sorted = sort_by.ToLowerInvariant() switch
            {
                "self" => profiles.OrderByDescending(p => p.SelfMs),
                "count" => profiles.OrderByDescending(p => p.SampleCount),
                _ => profiles.OrderByDescending(p => p.CumulativeMs),
            };

            var topFunctions = sorted
                .Take(clampedLimit)
                .Select(p => new
                {
                    method_name = p.MethodName,
                    class_name = p.ClassName,
                    self_ms = Math.Round(p.SelfMs, 2),
                    cumulative_ms = Math.Round(p.CumulativeMs, 2),
                    sample_count = p.SampleCount,
                    avg_ms = p.SampleCount > 0 ? Math.Round(p.CumulativeMs / p.SampleCount, 2) : 0,
                })
                .ToList();

            return new
            {
                total_methods = profiles.Count,
                returned = topFunctions.Count,
                sort_by,
                functions = topFunctions,
                currently_profiling = CpuProfileState.IsProfiling,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }
}
