using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DemoApi;

/// <summary>
/// Custom health check that verifies database connectivity.
/// </summary>
public class DatabaseHealthCheck(OrderDbContext db) : IHealthCheck
{
    private readonly OrderDbContext _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);
            if (canConnect)
            {
                var orderCount = await _db.Orders.CountAsync(cancellationToken);
                return HealthCheckResult.Healthy("Database is reachable",
                    new Dictionary<string, object> { ["order_count"] = orderCount });
            }

            return HealthCheckResult.Unhealthy("Database is not reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database health check failed", ex);
        }
    }
}
