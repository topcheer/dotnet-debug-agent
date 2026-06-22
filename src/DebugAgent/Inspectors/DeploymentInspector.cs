using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;

namespace DebugAgents;

/// <summary>
/// Deployment inspector — build info, deployment environment, and runtime assembly versions.
/// </summary>
public static class DeploymentInspector
{
    [DebugTool("get_build_info", "Get .NET build info: version, runtime, OS, architecture, GC mode")]
    public static object GetBuildInfo()
    {
        try
        {
            var proc = Process.GetCurrentProcess();

            string gcMode;
            try { gcMode = GCSettings.IsServerGC ? "Server" : "Workstation"; }
            catch { gcMode = "Unknown"; }

            string gcLatency;
            try { gcLatency = GCSettings.LatencyMode.ToString(); }
            catch { gcLatency = "Unknown"; }

            int gcMaxGen;
            try { gcMaxGen = GC.MaxGeneration; }
            catch { gcMaxGen = -1; }

            return new
            {
                dotnet_version = Environment.Version.ToString(),
                framework_description = SafeGetString(() => RuntimeInformation.FrameworkDescription),
                os_description = SafeGetString(() => RuntimeInformation.OSDescription),
                os_architecture = SafeGetString(() => RuntimeInformation.OSArchitecture.ToString()),
                process_architecture = SafeGetString(() => RuntimeInformation.ProcessArchitecture.ToString()),
                gc_mode = gcMode,
                gc_latency_mode = gcLatency,
                gc_max_generation = gcMaxGen,
                processor_count = Environment.ProcessorCount,
                clr_version = SafeGetString(() => Environment.GetEnvironmentVariable("DOTNET_RUNTIME_VERSION") ?? ""),
                entry_assembly = SafeGetString(() => Assembly.GetEntryAssembly()?.GetName().FullName ?? "Unknown"),
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("get_deployment_info", "Get deployment environment: hostname, PID, uptime, container detection, environment name")]
    public static object GetDeploymentInfo()
    {
        try
        {
            var proc = Process.GetCurrentProcess();

            string hostname;
            try { hostname = Environment.MachineName; }
            catch { hostname = "Unknown"; }

            DateTime startTime;
            try { startTime = proc.StartTime; }
            catch { startTime = DateTime.MinValue; }

            double uptimeSeconds;
            try { uptimeSeconds = Math.Round((DateTime.Now - startTime).TotalSeconds, 1); }
            catch { uptimeSeconds = -1; }

            bool containerDetected = DetectContainer();
            string envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? "(not set)";

            string userName;
            try { userName = Environment.UserName; }
            catch { userName = "Unknown"; }

            long workingSet = 0;
            long privateMemory = 0;
            try
            {
                workingSet = proc.WorkingSet64;
                privateMemory = proc.PrivateMemorySize64;
            }
            catch { }

            return new
            {
                hostname,
                pid = SafeGetInt(() => proc.Id),
                process_name = SafeGetString(() => proc.ProcessName),
                start_time = startTime != DateTime.MinValue ? startTime.ToString("o") : "Unknown",
                uptime_seconds = uptimeSeconds,
                container_detected = containerDetected,
                container_hints = GetContainerHints(),
                environment_name = envName,
                user = userName,
                current_directory = SafeGetString(() => Environment.CurrentDirectory),
                working_set_mb = workingSet > 0 ? Math.Round(workingSet / 1024.0 / 1024.0, 2) : 0,
                private_memory_mb = privateMemory > 0 ? Math.Round(privateMemory / 1024.0 / 1024.0, 2) : 0,
                thread_count = SafeGetInt(() => proc.Threads.Count),
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("get_runtime_version", "Get key NuGet package versions from loaded assemblies (Microsoft.*, System.*)")]
    public static object GetRuntimeVersion()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a =>
                {
                    var name = a.GetName().Name ?? "";
                    return name.StartsWith("Microsoft.")
                        || name.StartsWith("System.")
                        || name.StartsWith("AspNetCore")
                        || name.StartsWith("Microsoft.AspNetCore");
                })
                .OrderBy(a => a.GetName().Name)
                .Select(a =>
                {
                    var name = a.GetName();
                    var version = name.Version?.ToString() ?? "0.0.0.0";
                    var infoVersion = TryGetInformationalVersion(a) ?? version;

                    return new
                    {
                        assembly_name = name.Name ?? "",
                        assembly_version = version,
                        informational_version = infoVersion,
                    };
                })
                .ToList();

            // Highlight key packages
            var keyPackages = new[]
            {
                "Microsoft.AspNetCore.App",
                "Microsoft.Extensions.DependencyInjection",
                "Microsoft.Extensions.Logging",
                "Microsoft.Extensions.Caching.Memory",
                "Microsoft.EntityFrameworkCore",
                "System.Text.Json",
                "System.Net.Http",
            };

            var highlights = keyPackages
                .Select(kp =>
                {
                    var match = assemblies.FirstOrDefault(a =>
                        a.assembly_name.Equals(kp, StringComparison.OrdinalIgnoreCase) ||
                        a.assembly_name.Contains(kp));
                    return match != null ? new { package = kp, version = match.informational_version } : null;
                })
                .Where(x => x != null)
                .ToList();

            return new
            {
                dotnet_version = Environment.Version.ToString(),
                framework_description = SafeGetString(() => RuntimeInformation.FrameworkDescription),
                key_packages = highlights,
                total_assemblies_scanned = assemblies.Count,
                all_assemblies = assemblies,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static bool DetectContainer()
    {
        try
        {
            // Check DOTNET_RUNNING_IN_CONTAINER environment variable
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
                return true;

            // Check for /.dockerenv file
            if (File.Exists("/.dockerenv"))
                return true;

            // Check cgroup for container indicators
            if (File.Exists("/proc/1/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/1/cgroup");
                if (cgroup.Contains("docker") || cgroup.Contains("containerd") || cgroup.Contains("kubepods"))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> GetContainerHints()
    {
        var hints = new List<string>();

        try
        {
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
                hints.Add("DOTNET_RUNNING_IN_CONTAINER=true");
            if (File.Exists("/.dockerenv"))
                hints.Add("/.dockerenv exists");
            if (Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") != null)
                hints.Add("Kubernetes service host detected");
        }
        catch { }

        return hints;
    }

    private static string? TryGetInformationalVersion(Assembly asm)
    {
        try
        {
            var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion;
        }
        catch { return null; }
    }

    private static string SafeGetString(Func<string> func)
    {
        try { return func(); }
        catch { return "Unknown"; }
    }

    private static int SafeGetInt(Func<int> func)
    {
        try { return func(); }
        catch { return -1; }
    }
}
