using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DemoApi;

/// <summary>
/// Background service that periodically cleans up stale pending orders.
/// Demonstrates IHostedService for the BackgroundServiceInspector.
/// </summary>
public class OrderCleanupService(
    IServiceProvider serviceProvider,
    ILogger<OrderCleanupService> logger,
    TimeSpan? interval = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMinutes(1);
    private int _cleanupCount;
    private DateTime _lastRun = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OrderCleanupService started — interval: {Interval} seconds", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);

                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

                var cutoff = DateTime.UtcNow.AddHours(-1);
                var staleOrders = db.Orders
                    .Where(o => o.Status == "Pending" && o.CreatedAt < cutoff)
                    .ToList();

                if (staleOrders.Count > 0)
                {
                    db.Orders.RemoveRange(staleOrders);
                    await db.SaveChangesAsync(stoppingToken);
                    _cleanupCount += staleOrders.Count;
                    logger.LogInformation("Cleaned up {Count} stale pending orders (total cleaned: {Total})", staleOrders.Count, _cleanupCount);
                }

                _lastRun = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in OrderCleanupService");
            }
        }
    }

    public int CleanupCount => _cleanupCount;
    public DateTime LastRun => _lastRun;
}
