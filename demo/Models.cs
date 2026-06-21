namespace DemoApi;

/// <summary>
/// EF Core entity for orders.
/// </summary>
public class Order
{
    public int Id { get; set; }
    public string Customer { get; set; } = "";
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Total => Quantity * Price;
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class OrderCreateDto
{
    public string Customer { get; set; } = "";
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public class AppSettings
{
    public string AppName { get; set; } = "";
    public int MaxItems { get; set; }
    public int CacheTtlSeconds { get; set; }
    public int CleanupIntervalSeconds { get; set; }
}
