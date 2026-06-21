using System.Diagnostics;
using System.Runtime;

namespace DebugAgents;

/// <summary>
/// Built-in .NET runtime inspector tools.
/// </summary>
public static class RuntimeInspector
{
    [DebugTool("get_memory_stats", "Get .NET memory statistics: GC, working set, managed heap")]
    public static object GetMemoryStats()
    {
        var mem = GC.GetTotalMemory(false);
        var proc = Process.GetCurrentProcess();
        return new
        {
            managed_heap_mb = Math.Round(mem / 1024.0 / 1024.0, 2),
            working_set_mb = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 2),
            private_memory_mb = Math.Round(proc.PrivateMemorySize64 / 1024.0 / 1024.0, 2),
            gc_collection_counts = Enumerable.Range(0, GC.MaxGeneration + 1)
                .Select(g => new { generation = g, count = GC.CollectionCount(g) }),
            gc_max_generation = GC.MaxGeneration,
            is_server_gc = GCSettings.IsServerGC,
            gc_latency_mode = GCSettings.LatencyMode.ToString(),
        };
    }

    [DebugTool("trigger_gc", "Trigger garbage collection and show before/after comparison")]
    public static object TriggerGc()
    {
        var before = GC.GetTotalMemory(false);
        var beforeWs = Process.GetCurrentProcess().WorkingSet64;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(false);
        var afterWs = Process.GetCurrentProcess().WorkingSet64;
        return new
        {
            managed_before_mb = Math.Round(before / 1024.0 / 1024.0, 2),
            managed_after_mb = Math.Round(after / 1024.0 / 1024.0, 2),
            working_set_before_mb = Math.Round(beforeWs / 1024.0 / 1024.0, 2),
            working_set_after_mb = Math.Round(afterWs / 1024.0 / 1024.0, 2),
            freed_mb = Math.Round((before - after) / 1024.0 / 1024.0, 2),
            total_gc_count = GC.MaxGeneration >= 0 ? GC.CollectionCount(GC.MaxGeneration) : 0,
        };
    }

    [DebugTool("get_thread_pool_info", "Get ThreadPool stats: available threads, queue length")]
    public static object GetThreadPoolInfo()
    {
        ThreadPool.GetAvailableThreads(out int worker, out int completion);
        ThreadPool.GetMinThreads(out int minWorker, out int minCompletion);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxCompletion);
        return new
        {
            available_worker_threads = worker,
            available_completion_port_threads = completion,
            min_worker_threads = minWorker,
            max_worker_threads = maxWorker,
            min_completion_threads = minCompletion,
            max_completion_threads = maxCompletion,
        };
    }

    [DebugTool("get_runtime_info", "Get .NET runtime info: version, framework, GC mode")]
    public static object GetRuntimeInfo()
    {
        var proc = Process.GetCurrentProcess();
        return new
        {
            dotnet_version = Environment.Version.ToString(),
            framework_description = RuntimeInformation.FrameworkDescription,
            os_description = RuntimeInformation.OSDescription,
            process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            gc_server_mode = GCSettings.IsServerGC,
            gc_latency_mode = GCSettings.LatencyMode.ToString(),
            pid = proc.Id,
            machine_name = Environment.MachineName,
            processor_count = Environment.ProcessorCount,
        };
    }

    [DebugTool("get_process_info", "Get process info: PID, uptime, CPU time")]
    public static object GetProcessInfo()
    {
        var proc = Process.GetCurrentProcess();
        return new
        {
            pid = proc.Id,
            ppid = 0, // .NET doesn't expose PPID directly
            start_time = proc.StartTime.ToString("o"),
            uptime_seconds = Math.Round((DateTime.Now - proc.StartTime).TotalSeconds),
            cpu_time = new
            {
                user_seconds = Math.Round(proc.UserProcessorTime.TotalSeconds, 2),
                system_seconds = Math.Round(proc.PrivilegedProcessorTime.TotalSeconds, 2),
                total_seconds = Math.Round(proc.TotalProcessorTime.TotalSeconds, 2),
            },
            threads = proc.Threads.Count,
            modules = proc.Modules.Count,
        };
    }

    [DebugTool("get_environment_variables", "List environment variables (potential secrets masked)")]
    public static object GetEnvironmentVariables(string prefix = "")
    {
        var vars = Environment.GetEnvironmentVariables();
        var secretPatterns = new[] { "KEY", "SECRET", "PASSWORD", "TOKEN", "CREDENTIAL" };
        var result = new Dictionary<string, string>();

        foreach (DictionaryEntry entry in vars)
        {
            var key = entry.Key?.ToString() ?? "";
            var val = entry.Value?.ToString() ?? "";

            if (!string.IsNullOrEmpty(prefix) &&
                !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (secretPatterns.Any(s => key.ToUpperInvariant().Contains(s)))
                result[key] = "***masked***";
            else
                result[key] = val;
        }

        return new { variables = result, count = result.Count };
    }

    [DebugTool("get_disk_usage", "Get disk usage for current working directory")]
    public static object GetDiskUsage()
    {
        var drive = new DriveInfo(Directory.GetCurrentDirectory());
        return new
        {
            drive_name = drive.Name,
            total_gb = Math.Round(drive.TotalSize / 1024.0 / 1024.0 / 1024.0, 2),
            free_gb = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0, 2),
            used_pct = Math.Round((1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100, 1),
            drive_format = drive.DriveFormat,
            drive_type = drive.DriveType.ToString(),
        };
    }
}
