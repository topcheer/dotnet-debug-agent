using System.Diagnostics;
using System.Net.WebSockets;
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

// CORS configuration (for SecurityInspector demo)
builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(p => p
        .WithOrigins("http://localhost:3000", "https://localhost:3000")
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});

// --- Debug Agent: one line to integrate ---
builder.Services.AddDebugAgent(builder.Configuration);

// --- Feature flag registration (for FeatureFlagInspector demo) ---
FeatureFlagRegistry.RegisterFeatureFlag("new-order-ui", true, "v2");
FeatureFlagRegistry.RegisterFeatureFlag("bulk-export", false, "beta");
FeatureFlagRegistry.RegisterFeatureFlag("advanced-search", true, "experimental");
FeatureFlagRegistry.RegisterFeatureFlag("legacy-api", true, "default");

// --- Outbound HTTP tracking (for OutboundHttpInspector demo) ---
builder.Services.AddHttpClient("TrackedExternalApi", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org");
    client.Timeout = TimeSpan.FromSeconds(10);
}).AddHttpMessageHandler(() => new OutboundHttpHandler("TrackedExternalApi"));

var app = builder.Build();

// --- Enable CORS and WebSockets ---
app.UseCors();
app.UseWebSockets();

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
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        // Error tracking: capture unhandled exceptions
        ErrorTracker.RecordError(ex, ctx.Request.Path);
        sw.Stop();
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error", message = ex.Message });
        return;
    }
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

// Error endpoint (for error tracking demo - actually throws)
app.MapGet("/api/error", () =>
{
    throw new InvalidOperationException("Intentional error for demo - captured by ErrorTracker");
});

// Another error endpoint with different exception type
app.MapGet("/api/error/null-ref", () =>
{
    throw new NullReferenceException("Simulated null reference for error stats demo");
});

// Feature flag check endpoint
app.MapGet("/api/feature-flag/{name}", (string name) =>
{
    if (FeatureFlagRegistry.TryEvaluate(name, out var enabled))
        return Results.Ok(new { flag = name, enabled, variant = FeatureFlagRegistry.Get(name)?.Variant ?? "default" });
    return Results.NotFound(new { error = $"Flag '{name}' not registered" });
});

// Outbound HTTP call with tracking (for OutboundHttpInspector demo)
app.MapGet("/api/tracked-external", async (IHttpClientFactory httpFactory) =>
{
    var client = httpFactory.CreateClient("TrackedExternalApi");
    try
    {
        var resp = await client.GetAsync("/get");
        var content = await resp.Content.ReadAsStringAsync();
        return Results.Ok(new { status = (int)resp.StatusCode, body_preview = content[..Math.Min(200, content.Length)] });
    }
    catch (Exception ex)
    {
        return Results.Problem("External call failed: " + ex.Message);
    }
});

// WebSocket endpoint (for WebSocketInspector demo)
app.MapGet("/ws", async (HttpContext ctx, ILogger<Program> logger) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket connection required");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var connId = Guid.NewGuid().ToString("N")[..8];
    var remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    WebSocketRegistry.RegisterWebSocket(connId, ws, remoteIp, "/ws");
    logger.LogInformation("WebSocket connected: {ConnId} from {Ip}", connId, remoteIp);

    var buffer = new byte[4096];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            WebSocketRegistry.RecordMessage(connId, false);

            // Echo back
            var msg = System.Text.Encoding.UTF8.GetBytes($"Echo [{connId}]: {System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count)}");
            await ws.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Text, true, CancellationToken.None);
            WebSocketRegistry.RecordMessage(connId, true);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning("WebSocket {ConnId} error: {Error}", connId, ex.Message);
    }
    finally
    {
        WebSocketRegistry.UnregisterWebSocket(connId);
        logger.LogInformation("WebSocket disconnected: {ConnId}", connId);
    }
});

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
