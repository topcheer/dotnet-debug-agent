using System.Text.Json;
using DebugAgents;

var builder = WebApplication.CreateBuilder(args);

// --- Debug Agent: one line to integrate ---
builder.Services.AddDebugAgent();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
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

// --- Demo: Order Management API ---

var orders = new Dictionary<int, object>();
var nextId = 1;

app.MapGet("/api/orders", () => orders.Values);

app.MapPost("/api/orders", (OrderInput input) =>
{
    var id = nextId++;
    var order = new { id, input.customer, input.item, input.quantity, input.price, total = input.quantity * input.price };
    orders[id] = order;
    return Results.Created($"/api/orders/{id}", order);
});

app.MapGet("/api/orders/{id:int}", (int id) =>
    orders.TryGetValue(id, out var order) ? Results.Ok(order) : Results.NotFound(new { error = "Order not found" }));

app.MapDelete("/api/orders/{id:int}", (int id) =>
{
    orders.Remove(id);
    return Results.Ok(new { deleted = id });
});

app.MapGet("/api/health", () => new { status = "UP", orders = orders.Count });

app.MapGet("/api/slow", async () =>
{
    await Task.Delay(500);
    return new { message = "This was slow" };
});

app.MapGet("/api/error", () => Results.Json(new { error = "Intentional error" }, statusCode: 500));

Console.WriteLine("\n  .NET Debug Agent Demo");
Console.WriteLine("  Open http://localhost:5000/agent\n");

app.Run();

public record OrderInput(string customer, string item, int quantity, double price);
