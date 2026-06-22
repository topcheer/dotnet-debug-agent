using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace DebugAgents;

/// <summary>
/// Metrics inspector — .NET runtime metrics, custom meters, and event counters.
/// </summary>
public static class MetricsInspector
{
    [DebugTool("get_system_metrics", "Get .NET runtime metrics: GC collections, heap sizes, exception count, time in GC, allocation rate")]
    public static object GetSystemMetrics()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        var proc = Process.GetCurrentProcess();

        // Use System.Runtime.EventCounters for detailed runtime metrics
        var runtimeMetrics = new Dictionary<string, object?>();

        try
        {
            // GC stats from GCMemoryInfo
            runtimeMetrics["gc_stats"] = new
            {
                gen0_collections = GC.CollectionCount(0),
                gen1_collections = GC.CollectionCount(1),
                gen2_collections = GC.CollectionCount(2),
                total_allocated_mb = Math.Round((double)gcInfo.TotalAvailableMemoryBytes / 1024 / 1024, 2),
                heap_size_bytes = gcInfo.HeapSizeBytes,
                heap_size_mb = Math.Round(gcInfo.HeapSizeBytes / 1024.0 / 1024.0, 2),
                fragmented_bytes = gcInfo.FragmentedBytes,
                pinned_object_count = gcInfo.PinnedObjectsCount,
                finalization_pending = gcInfo.FinalizationPendingCount,
            };

            // Generation-specific heap sizes via GenerationInfo
            var genSizes = new Dictionary<string, long>();
            var genInfo = gcInfo.GenerationInfo;
            for (int gen = 0; gen <= 2 && gen < genInfo.Length; gen++)
                genSizes[$"gen{gen}_size"] = genInfo[gen].SizeAfterBytes;
            runtimeMetrics["generation_sizes"] = genSizes;

            // Process metrics
            runtimeMetrics["process"] = new
            {
                working_set_mb = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 2),
                private_memory_mb = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 2),
                gc_heap_mb = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 2),
                thread_count = proc.Threads.Count,
                handle_count = proc.HandleCount,
            };

            // Exception count from the runtime
            // Note: We use the 'exception-count' event counter from System.Runtime, but it requires an EventListener.
            // For now, we report what's available directly.
            runtimeMetrics["note"] = "For exception count and time-in-GC, use get_event_counters to read System.Runtime EventCounters.";
        }
        catch (Exception ex)
        {
            runtimeMetrics["error"] = ex.Message;
        }

        return runtimeMetrics;
    }

    [DebugTool("get_custom_counters", "List registered System.Diagnostics.Metrics instruments and their current values")]
    public static object GetCustomCounters()
    {
        var instruments = new List<object>();

        try
        {
            // Access the internal global meter listener to enumerate instruments
            // System.Diagnostics.Metrics doesn't expose a public API to enumerate all instruments,
            // but we can use reflection or a MeterListener to capture them.

            // Try to enumerate known meters via reflection
            var listener = new MeterListener();
            var recordedValues = new ConcurrentDictionary<string, List<object>>();

            listener.InstrumentPublished = (instrument, l) =>
            {
                try
                {
                    instruments.Add(new
                    {
                        name = instrument.Name,
                        description = instrument.Description,
                        unit = instrument.Unit,
                        type = instrument.GetType().Name,
                        meter_name = instrument.Meter?.Name ?? "Unknown",
                    });
                    l.EnableMeasurementEvents(instrument);
                }
                catch { }
            };

            // Set up handlers for different measurement types
            listener.SetMeasurementEventCallback<double>((inst, value, tags, state) =>
            {
                var key = inst.Name;
                recordedValues.GetOrAdd(key, _ => new List<object>()).Add(new { value, type = "double", unit = inst.Unit });
            });
            listener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
            {
                var key = inst.Name;
                recordedValues.GetOrAdd(key, _ => new List<object>()).Add(new { value, type = "long", unit = inst.Unit });
            });
            listener.SetMeasurementEventCallback<int>((inst, value, tags, state) =>
            {
                var key = inst.Name;
                recordedValues.GetOrAdd(key, _ => new List<object>()).Add(new { value, type = "int", unit = inst.Unit });
            });

            listener.Start();

            // Wait briefly for measurements to be recorded
            Thread.Sleep(200);

            listener.Dispose();

            // Merge instrument info with recorded values
            var result = instruments.Select(i =>
            {
                dynamic inst = i;
                var name = (string)inst.name;
                var hasValues = recordedValues.TryGetValue(name, out var vals);
                return new
                {
                    name = inst.name,
                    description = inst.description,
                    unit = inst.unit,
                    type = inst.type,
                    meter_name = inst.meter_name,
                    last_value = (hasValues && vals!.Count > 0) ? vals.Last() : null,
                    measurement_count = hasValues ? vals!.Count : 0,
                };
            }).ToList();

            return new
            {
                total_instruments = result.Count,
                instruments = result,
                note = result.Count == 0 ? "No custom meters detected. Use System.Diagnostics.Metrics to create instruments." : null,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to enumerate meters: {ex.Message}" };
        }
    }

    [DebugTool("get_event_counters", "List registered EventCounters and their values")]
    public static object GetEventCounters()
    {
        var counters = new List<object>();

        try
        {
            // Use an EventListener to capture EventCounter values
            using var listener = new CounterCaptureListener();
            listener.Enable();

            // Wait for counters to be reported
            Thread.Sleep(500);

            counters = listener.GetCounters();

            return new
            {
                total = counters.Count,
                counters,
                note = counters.Count == 0 ? "No EventCounters detected. The System.Runtime event source provides counters like cpu-usage, working-set, gc-heap-size, etc." : null,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to read event counters: {ex.Message}" };
        }
    }
}

/// <summary>
/// Internal EventListener that captures EventCounter values for a brief window.
/// </summary>
internal class CounterCaptureListener : EventListener
{
    private readonly List<object> _counters = new();
    private readonly List<string> _enabledSources = new();

    public void Enable()
    {
        // Will be activated when event sources are created
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        try
        {
            // Enable all event sources that might contain counters
            if (eventSource.Name.StartsWith("System.Runtime")
                || eventSource.Name.StartsWith("Microsoft-")
                || eventSource.Name.Contains("AspNetCore")
                || eventSource.Name.Contains("Http"))
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
                _enabledSources.Add(eventSource.Name);
            }
        }
        catch { }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            // EventCounters have a specific payload structure
            if (eventData.EventName == "EventCounters" || eventData.EventName == "counter")
            {
                var payload = eventData.Payload;
                if (payload != null && payload.Count > 0)
                {
                    foreach (var item in payload)
                    {
                        if (item is System.Collections.IDictionary dict)
                        {
                            var name = dict.Contains("DisplayName") ? dict["DisplayName"]?.ToString() : "";
                            var mean = dict.Contains("Mean") ? dict["Mean"] : null;
                            var increment = dict.Contains("Increment") ? dict["Increment"] : null;
                            var displayUnits = dict.Contains("DisplayUnits") ? dict["DisplayUnits"]?.ToString() : "";

                            _counters.Add(new
                            {
                                name,
                                value = mean ?? increment,
                                units = displayUnits,
                                source = eventData.EventSource.Name,
                            });
                        }
                    }
                }
            }
        }
        catch { }
    }

    public List<object> GetCounters()
    {
        // Deduplicate by name, keeping the last value
        var result = new Dictionary<string, object>();
        foreach (var c in _counters)
        {
            dynamic d = c;
            result[(string)d.name] = c;
        }
        return result.Values.ToList();
    }
}
