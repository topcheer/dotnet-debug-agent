using Microsoft.EntityFrameworkCore;

namespace DemoApi;

/// <summary>
/// EF Core DbContext for the demo.
/// </summary>
public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Customer).HasMaxLength(100).IsRequired();
            e.Property(x => x.Item).HasMaxLength(200).IsRequired();
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Pending");
            e.HasIndex(x => x.Customer);
        });
    }
}
