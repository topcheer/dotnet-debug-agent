using System.Diagnostics;
using DebugAgents;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using DemoApi;

var builder = WebApplication.CreateBuilder(args);

// --- Configure services ---

// EF Core with SQLite
builder.Services.AddDbContext<OrderDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// App settings binding
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

// Memory cache (for demo caching patterns)
builder.Services.AddMemoryCache();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "db", "ready" })
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Service is running"),
        tags: new[] { "live" });

// FluentValidation
builder.Services.AddScoped<IValidator<OrderCreateDto>, OrderCreateValidator>();

// Order service
builder.Services.AddScoped<OrderService>();

// HttpClient with Polly resilience (simulates external API calls)
builder.Services.AddHttpClient("ExternalApi", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Background service for cleanup
builder.Services.AddHostedService<OrderCleanupService>();

// --- Debug Agent: one line to integrate ---
builder.Services.AddDebugAgent();

var app = builder.Build();

// --- Auto-migrate database ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    db.Database.EnsureDeleted(); // Clean start for demo
    db.Database.EnsureCreated(); // Auto-create schema
}

// --- Seed demo data ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    if (!db.Orders.Any())
    {
        db.Orders.AddRange(
            new Order { Customer = "Alice", Item = "Laptop", Quantity = 1, Price = 999.99m, Status = "Completed", CompletedAt = DateTime.UtcNow },
            new Order { Customer = "Bob", Item = "Mouse", Quantity = 3, Price = 29.99m, Status = "Pending" },
            new Order { Customer = "Charlie", Item = "Keyboard", Quantity = 2, Price = 79.50m, Status = "Pending" }
        );
        db.SaveChanges();
    }
}

// --- HTTP request tracking middleware ---
app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();
    HttpRequestTracker.Record(
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Response.StatusCode,
        sw.Elapsed.TotalMilliseconds,
        ctx.Connection.RemoteIpAddress?.ToString() ?? "");
});

// --- Debug Agent: map endpoints ---
app.MapDebugAgent();

// --- Demo API Endpoints ---

// List all orders (with caching)
app.MapGet("/api/orders", async (OrderService svc) =>
{
    var orders = await svc.GetAllAsync();
    return Results.Ok(orders);
});

// Get order by ID
app.MapGet("/api/orders/{id:int}", async (int id, OrderService svc) =>
{
    var order = await svc.GetByIdAsync(id);
    return order != null ? Results.Ok(order) : Results.NotFound(new { error = "Order not found" });
});

// Create order (with validation)
app.MapPost("/api/orders", async (OrderCreateDto dto, IValidator<OrderCreateDto> validator, OrderService svc) =>
{
    var validation = await validator.ValidateAsync(dto);
    if (!validation.IsValid)
        return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });

    var order = await svc.CreateAsync(dto);
    return Results.Created($"/api/orders/{order.Id}", order);
});

// Complete an order
app.MapPost("/api/orders/{id:int}/complete", async (int id, OrderService svc) =>
{
    var order = await svc.CompleteAsync(id);
    return order != null ? Results.Ok(order) : Results.NotFound(new { error = "Order not found" });
});

// Delete order
app.MapDelete("/api/orders/{id:int}", async (int id, OrderService svc) =>
{
    var deleted = await svc.DeleteAsync(id);
    return deleted ? Results.Ok(new { deleted = id }) : Results.NotFound(new { error = "Order not found" });
});

// Health endpoint (direct, not through the agent)
app.MapGet("/api/health", async (OrderDbContext db) =>
{
    var orderCount = await db.Orders.CountAsync();
    return Results.Ok(new { status = "UP", orders = orderCount, uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime });
});

// Slow endpoint (for request stats demo)
app.MapGet("/api/slow", async () =>
{
    await Task.Delay(500);
    return Results.Ok(new { message = "This was slow" });
});

// Error endpoint (for error stats demo)
app.MapGet("/api/error", () =>
    Results.Json(new { error = "Intentional error for demo" }, statusCode: 500));

// External API call (exercises HttpClient factory + Polly)
app.MapGet("/api/external-test", async (IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    var client = httpFactory.CreateClient("ExternalApi");
    try
    {
        logger.LogInformation("Calling external API at httpbin.org/get");
        var resp = await client.GetAsync("/get");
        var content = await resp.Content.ReadAsStringAsync();
        return Results.Ok(new { status = (int)resp.StatusCode, body_preview = content[..Math.Min(200, content.Length)] });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "External API call failed");
        return Results.Problem("External API call failed: " + ex.Message);
    }
});

Console.WriteLine("""
╔══════════════════════════════════════════╗
║  .NET Debug Agent Demo                   ║
║  Open http://localhost:5000/agent        ║
╚══════════════════════════════════════════╝
""");

app.Run();
