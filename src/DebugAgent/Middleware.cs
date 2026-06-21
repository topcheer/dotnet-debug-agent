using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DebugAgents;

public static class DebugAgentExtensions
{
    /// <summary>
    /// Add the Debug Agent to ASP.NET Core services.
    /// </summary>
    public static IServiceCollection AddDebugAgent(this IServiceCollection services, AgentConfig? config = null)
    {
        var cfg = config ?? AgentConfig.FromEnvironment();
        services.AddSingleton(cfg);
        services.AddSingleton<DebugEngine>();
        services.AddSingleton<InMemoryLoggerProvider>();
        ServiceDescriptorCache.Capture(services);
        return services;
    }

    /// <summary>
    /// Add the Debug Agent using IConfiguration (binds from appsettings.json).
    /// </summary>
    public static IServiceCollection AddDebugAgent(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("DebugAgent");
        var cfg = new AgentConfig
        {
            Enabled = section["Enabled"] != "false",
            BasePath = section["BasePath"] ?? "/agent",
            LLM = new LLMConfig
            {
                BaseUrl = section["Llm:BaseUrl"] ?? "https://open.bigmodel.cn/api/coding/paas/v4",
                ApiKey = section["Llm:ApiKey"] ?? "",
                Model = section["Llm:Model"] ?? "glm-4.6",
                Temperature = double.TryParse(section["Llm:Temperature"], out var t) ? t : 0.3,
                MaxTokens = int.TryParse(section["Llm:MaxTokens"], out var mt) ? mt : 4096,
                MaxToolRounds = int.TryParse(section["Llm:MaxToolRounds"], out var mr) ? mr : 25,
                TimeoutSeconds = int.TryParse(section["Llm:TimeoutSeconds"], out var ts) ? ts : 120,
                ContextWindowTokens = int.TryParse(section["Llm:ContextWindowTokens"], out var cw) ? cw : 100000,
                MaxRetries = int.TryParse(section["Llm:MaxRetries"], out var retry) ? retry : 3,
            }
        };

        // Environment variables always override appsettings
        var envKey = Environment.GetEnvironmentVariable("DEBUG_AGENT__LLM_API_KEY")
                     ?? Environment.GetEnvironmentVariable("LLM_API_KEY")
                     ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(envKey)) cfg.LLM.ApiKey = envKey;

        var envModel = Environment.GetEnvironmentVariable("DEBUG_AGENT__LLM_MODEL")
                       ?? Environment.GetEnvironmentVariable("LLM_MODEL");
        if (!string.IsNullOrEmpty(envModel)) cfg.LLM.Model = envModel;

        var envBaseUrl = Environment.GetEnvironmentVariable("DEBUG_AGENT__LLM_BASE_URL")
                         ?? Environment.GetEnvironmentVariable("LLM_BASE_URL");
        if (!string.IsNullOrEmpty(envBaseUrl)) cfg.LLM.BaseUrl = envBaseUrl;

        return services.AddDebugAgent(cfg);
    }

    /// <summary>
    /// Map the Debug Agent endpoints.
    /// SSE events: content, tool_start, tool_result, done, error, context_compressed
    /// </summary>
    public static WebApplication MapDebugAgent(this WebApplication app, string? basePath = null)
    {
        AgentContext.Initialize(app);

        var cfg = app.Services.GetRequiredService<AgentConfig>();
        var path = basePath ?? cfg.BasePath;
        var engine = app.Services.GetRequiredService<DebugEngine>();
        var sessionId = $"session-{Guid.NewGuid():N}".Substring(0, 16);

        // Chat UI
        app.MapGet(path, () => Results.Text(ChatPage.Html, "text/html"));

        // SSE streaming chat endpoint
        app.MapPost($"{path}/api/chat", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            var message = body.GetProperty("message").GetString()!;
            var sid = body.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() ?? sessionId : sessionId;

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var callback = new ChatCallback
            {
                OnContent = async chunk =>
                {
                    // JSON-encode so newlines survive SSE transport
                    var json = JsonSerializer.Serialize(chunk);
                    await ctx.Response.WriteAsync($"event: content\ndata: {json}\n\n");
                    await ctx.Response.Body.FlushAsync();
                },
                OnToolStart = async toolName =>
                {
                    await ctx.Response.WriteAsync($"event: tool_start\ndata: {toolName}\n\n");
                    await ctx.Response.Body.FlushAsync();
                },
                OnToolResult = async (toolName, result) =>
                {
                    await ctx.Response.WriteAsync($"event: tool_result\ndata: {toolName}: {result}\n\n");
                    await ctx.Response.Body.FlushAsync();
                },
                OnContextCompressed = async (orig, comp, rounds) =>
                {
                    var msg = JsonSerializer.Serialize(new { originalTokens = orig, compressedTokens = comp, removedRounds = rounds });
                    await ctx.Response.WriteAsync($"event: context_compressed\ndata: {msg}\n\n");
                    await ctx.Response.Body.FlushAsync();
                },
                OnError = async msg =>
                {
                    await ctx.Response.WriteAsync($"event: error\ndata: {msg}\n\n");
                    await ctx.Response.Body.FlushAsync();
                },
                OnComplete = async () =>
                {
                    await ctx.Response.WriteAsync("event: done\ndata: \n\n");
                    await ctx.Response.Body.FlushAsync();
                },
            };

            await engine.ChatAsync(message, sid, callback);
        });

        // Clear conversation
        app.MapPost($"{path}/api/clear", (HttpContext ctx) =>
        {
            engine.ClearSession(sessionId);
            return Results.Json(new { status = "cleared" });
        });

        // Health
        app.MapGet($"{path}/api/health", () =>
            Results.Json(new { status = "ok", agent = "dotnet-debug-agent" }));

        // Tools listing
        app.MapGet($"{path}/api/tools", () =>
        {
            var schemas = GlobalRegistry.Instance.AllSchemas();
            return Results.Json(new { tools = schemas });
        });

        return app;
    }
}
