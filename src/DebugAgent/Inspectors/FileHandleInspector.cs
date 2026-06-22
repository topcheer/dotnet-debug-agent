using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DebugAgents;

/// <summary>
/// File handle inspector — open handle count and handle/file-descriptor limits.
/// </summary>
public static class FileHandleInspector
{
    [DebugTool("get_handle_count", "Get count of open file handles/descriptors for the current process")]
    public static object GetHandleCount()
    {
        try
        {
            var proc = Process.GetCurrentProcess();

            var result = new Dictionary<string, object?>
            {
                ["handle_count"] = proc.HandleCount,
                ["pid"] = proc.Id,
                ["process_name"] = proc.ProcessName,
            };

            // On Linux, also count open file descriptors from /proc/self/fd
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var fdDir = "/proc/self/fd";
                    if (Directory.Exists(fdDir))
                    {
                        var fdCount = Directory.GetFiles(fdDir).Length;
                        result["open_fds"] = fdCount;
                    }
                }
                catch { }
            }

            return result;
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to get handle count: {ex.Message}" };
        }
    }

    [DebugTool("get_handle_limit", "Get file descriptor / handle limit (ulimit equivalent)")]
    public static object GetHandleLimit()
    {
        try
        {
            var result = new Dictionary<string, object?>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: default handle limit is 16 million, but can be queried via GetProcessHandleCount
                try
                {
                    var proc = Process.GetCurrentProcess();
                    result["platform"] = "Windows";
                    result["current_handle_count"] = proc.HandleCount;
                    result["windows_default_limit"] = 16777216; // 16M default
                    result["note"] = "Windows does not have a hard file descriptor limit like Linux ulimit. The default handle limit is 16,777,216.";
                }
                catch (Exception ex)
                {
                    result["error"] = $"Windows handle query failed: {ex.Message}";
                }
            }
            else
            {
                // Linux/macOS: read /proc/self/limits or use getrlimit
                result["platform"] = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Linux";

                try
                {
                    // Try reading /proc/self/limits (Linux)
                    var limitsPath = "/proc/self/limits";
                    if (File.Exists(limitsPath))
                    {
                        var lines = File.ReadAllLines(limitsPath);
                        foreach (var line in lines)
                        {
                            if (line.Contains("open files", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("Max open files", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                var softIdx = Array.FindIndex(parts, p => p.All(char.IsDigit) && p.Length > 0);
                                if (softIdx >= 0 && softIdx + 1 < parts.Length)
                                {
                                    result["soft_limit"] = int.Parse(parts[softIdx]);
                                    result["hard_limit"] = int.Parse(parts[softIdx + 1]);
                                }
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Try running ulimit command
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "/bin/sh",
                                Arguments = "-c \"ulimit -n\"",
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                            };
                            var ulimitProc = Process.Start(psi);
                            if (ulimitProc != null)
                            {
                                var output = ulimitProc.StandardOutput.ReadToEnd().Trim();
                                ulimitProc.WaitForExit(2000);
                                if (int.TryParse(output, out var fdLimit))
                                    result["soft_limit"] = fdLimit;
                            }
                        }
                        catch { }
                    }

                    // Also try reading /proc/self/fd for current count
                    var fdDir = "/proc/self/fd";
                    if (Directory.Exists(fdDir))
                        result["current_open_fds"] = Directory.GetFiles(fdDir).Length;
                }
                catch (Exception ex)
                {
                    result["error"] = $"Failed to read limits: {ex.Message}";
                }
            }

            // Add process handle info
            try
            {
                var proc = Process.GetCurrentProcess();
                result["current_handle_count"] = proc.HandleCount;
            }
            catch { }

            return result;
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to get handle limit: {ex.Message}" };
        }
    }
}
