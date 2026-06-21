using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DemoApi;

/// <summary>
/// Order service with caching, logging, and EF Core persistence.
/// </summary>
public class OrderService(
    OrderDbContext db,
    IMemoryCache cache,
    ILogger<OrderService> logger,
    IOptions<AppSettings> settings)
{
    private readonly OrderDbContext _db = db;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<OrderService> _logger = logger;
    private readonly AppSettings _settings = settings.Value;

    public async Task<List<Order>> GetAllAsync()
    {
        if (_cache.TryGetValue("all_orders", out List<Order>? cached) && cached != null)
        {
            _logger.LogDebug("Cache hit for all_orders");
            return cached;
        }

        _logger.LogInformation("Cache miss — loading all orders from database");
        var orders = await _db.Orders.OrderByDescending(o => o.CreatedAt).ToListAsync();
        _cache.Set("all_orders", orders, TimeSpan.FromSeconds(_settings.CacheTtlSeconds));
        return orders;
    }

    public async Task<Order?> GetByIdAsync(int id)
    {
        var cacheKey = $"order_{id}";
        if (_cache.TryGetValue(cacheKey, out Order? cached))
        {
            _logger.LogDebug("Cache hit for {CacheKey}", cacheKey);
            return cached;
        }

        _logger.LogInformation("Loading order {OrderId} from database", id);
        var order = await _db.Orders.FindAsync(id);
        if (order != null)
            _cache.Set(cacheKey, order, TimeSpan.FromSeconds(_settings.CacheTtlSeconds));

        return order;
    }

    public async Task<Order> CreateAsync(OrderCreateDto dto)
    {
        var order = new Order
        {
            Customer = dto.Customer,
            Item = dto.Item,
            Quantity = dto.Quantity,
            Price = dto.Price,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Created order {OrderId} for {Customer} — {Item} x{Quantity}", order.Id, order.Customer, order.Item, order.Quantity);

        InvalidateCache();
        return order;
    }

    public async Task<Order?> CompleteAsync(int id)
    {
        var order = await _db.Orders.FindAsync(id);
        if (order == null)
        {
            _logger.LogWarning("Attempted to complete non-existent order {OrderId}", id);
            return null;
        }

        order.Status = "Completed";
        order.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Completed order {OrderId}", id);

        InvalidateCache();
        return order;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var order = await _db.Orders.FindAsync(id);
        if (order == null) return false;

        _db.Orders.Remove(order);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Deleted order {OrderId}", id);

        InvalidateCache();
        return true;
    }

    private void InvalidateCache()
    {
        _cache.Remove("all_orders");
        _logger.LogDebug("Invalidated order cache entries");
    }
}
