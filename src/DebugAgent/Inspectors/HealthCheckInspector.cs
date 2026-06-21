using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DebugAgents;

/// <summary>
/// Health check inspector — runs registered health checks and lists them.
/// </summary>
public static class HealthCheckInspector
{
    [DebugTool("get_health_status", "Run all registered health checks and return the overall status with per-component details")]
    public static async Task<object> GetHealthStatus()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var healthService = AgentContext.Services.GetService<HealthCheckService>();
        if (healthService == null)
            return new { error = "HealthCheckService not registered. Add: builder.Services.AddHealthChecks()" };

        var report = await healthService.CheckHealthAsync();

        var entries = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            duration_ms = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            description = e.Value.Description ?? "",
            exception = e.Value.Exception?.Message ?? "",
            tags = e.Value.Tags.ToList(),
            data = e.Value.Data.Count > 0
                ? e.Value.Data.ToDictionary(d => d.Key, d => d.Value?.ToString() ?? "")
                : null,
        }).ToList();

        return new
        {
            overall_status = report.Status.ToString(),
            total_duration_ms = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            components = entries,
        };
    }

    [DebugTool("get_registered_health_checks", "List all registered health checks without executing them")]
    public static object GetRegisteredHealthChecks()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var healthService = AgentContext.Services.GetService<HealthCheckService>();
        if (healthService == null)
            return new { error = "HealthCheckService not registered" };

        // We still need to call CheckHealthAsync to enumerate registrations,
        // but we can filter to only get names without running them deeply.
        var registrations = AgentContext.Services.GetServices<HealthCheckRegistration>();

        if (registrations.Any())
        {
            var checks = registrations.Select(r => new
            {
                name = r.Name,
                failure_status = r.FailureStatus.ToString(),
                tags = r.Tags.ToList(),
            }).ToList();

            return new { total = checks.Count, health_checks = checks };
        }

        // Fallback: enumerate from the report
        var report = healthService.CheckHealthAsync().GetAwaiter().GetResult();
        var entries = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            tags = e.Value.Tags.ToList(),
        }).ToList();

        return new { total = entries.Count, health_checks = entries };
    }
}
