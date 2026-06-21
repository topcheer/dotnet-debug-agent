using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DebugAgents;

public static class DebugAgentExtensions
{
    /// <summary>
    /// Add the Debug Agent to ASP.NET Core services.
    /// Usage in Program.cs:
    ///   builder.Services.AddDebugAgent();
    /// </summary>
    public static IServiceCollection AddDebugAgent(this IServiceCollection services, AgentConfig? config = null)
    {
        var cfg = config ?? AgentConfig.FromEnvironment();
        services.AddSingleton(cfg);
        services.AddSingleton<DebugEngine>();

        // Register in-memory logger provider to capture logs
        services.AddSingleton<InMemoryLoggerProvider>();

        // Capture service descriptors for DI inspection
        ServiceDescriptorCache.Capture(services);

        return services;
    }

    /// <summary>
    /// Map the Debug Agent endpoints.
    /// Usage in Program.cs:
    ///   app.MapDebugAgent();
    /// </summary>
    public static WebApplication MapDebugAgent(this WebApplication app, string? basePath = null)
    {
        // Initialize global context for framework inspectors
        AgentContext.Initialize(app);

        // Capture the latest service descriptors (in case more were added after AddDebugAgent)
        // Re-snapshot from the app's service provider descriptor info
        var cfg = app.Services.GetRequiredService<AgentConfig>();
        var path = basePath ?? cfg.BasePath;
        var engine = app.Services.GetRequiredService<DebugEngine>();

        // Ensure InMemoryLoggerProvider is hooked into the logging pipeline
        // (The provider was registered in DI; the factory picks it up automatically.)

        app.MapGet(path, () => Results.Text(ChatPage.Html, "text/html"));
        app.MapPost($"{path}/api/chat", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            var message = body.GetProperty("message").GetString()!;

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            await foreach (var (eventType, data) in engine.ChatStreamAsync(message))
            {
                var json = JsonSerializer.Serialize(data);
                await ctx.Response.WriteAsync($"event: {eventType}\ndata: {json}\n\n");
                await ctx.Response.Body.FlushAsync();
            }
        });

        app.MapGet($"{path}/api/tools", () =>
        {
            var schemas = GlobalRegistry.Instance.AllSchemas();
            return Results.Json(new { tools = schemas });
        });

        return app;
    }
}
