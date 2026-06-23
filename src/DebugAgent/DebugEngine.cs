using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DebugAgents;

/// <summary>
/// Callback interface for streaming agent responses to the UI.
/// </summary>
public class ChatCallback
{
    public Action<string>? OnContent { get; set; }
    public Action<string>? OnToolStart { get; set; }
    public Action<string, string>? OnToolResult { get; set; }
    public Action? OnComplete { get; set; }
    public Action<string>? OnError { get; set; }
    public Action<int, int, int>? OnContextCompressed { get; set; }
}

/// <summary>
/// Core agent engine: orchestrates LLM calls, tool execution, and context compression.
/// Matches Spring DebugAgentEngine logic exactly.
/// </summary>
public class DebugEngine
{
    private readonly AgentConfig _config;
    private readonly LLMClient _llm;
    private readonly ToolRegistry _tools;
    private readonly ContextCompressor _compressor;
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();

    public DebugEngine(AgentConfig? config = null)
    {
        _config = config ?? AgentConfig.FromEnvironment();
        _llm = new LLMClient(_config.LLM);
        _tools = GlobalRegistry.Instance;

        // Auto-discover tools
        _tools.DiscoverTools(typeof(DebugEngine).Assembly);

        _compressor = new ContextCompressor(_llm, _config.LLM.ContextWindowTokens, 3);
    }

    /// <summary>
    /// Process a user message with streaming output via callback.
    /// </summary>
    public async Task ChatAsync(string userMessage, string sessionId, ChatCallback callback)
    {
        try
        {
            var session = _sessions.GetOrAdd(sessionId, id => new ChatSession(id));
            session.AddMessage(ChatMessage.User(userMessage));

            await RunToolLoop(session, callback);
        }
        catch (Exception e)
        {
            callback.OnError?.Invoke($"Internal error: {e.Message}");
        }
    }

    public void ClearSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.Clear();
    }

    private async Task RunToolLoop(ChatSession session, ChatCallback callback)
    {
        int maxRounds = _config.LLM.MaxToolRounds;

        for (int round = 0; round < maxRounds; round++)
        {
            // Check if context compression is needed
            if (round > 0 && _compressor.NeedsCompression(session.CurrentContextTokens))
            {
                var result = await _compressor.CompressAsync(session);
                if (result != null)
                {
                    callback.OnContent?.Invoke($"\n\n> [Context auto-compressed: {result.OriginalTokens} → ~{result.CompressedTokens} tokens ({result.Strategy})]\n\n");
                    callback.OnContextCompressed?.Invoke(result.OriginalTokens, result.CompressedTokens, result.RemovedRounds);
                }
            }

            // Build the request
            var messages = new List<object>();
            messages.Add(new { role = "system", content = SystemPrompt });
            foreach (var msg in session.GetMessages())
                messages.Add(msg.ToApiObject());

            var toolSchemas = _tools.AllSchemas();

            var contentBuilder = new StringBuilder();
            List<ToolCallResult>? toolCalls = null;
            string? finishReason = null;
            bool hadError = false;

            await _llm.StreamChatAsync(
                messages,
                toolSchemas,
                "auto",
                chunk =>
                {
                    contentBuilder.Append(chunk);
                    callback.OnContent?.Invoke(chunk);
                },
                (calls, fr) =>
                {
                    toolCalls = calls;
                    finishReason = fr;
                },
                error =>
                {
                    hadError = true;
                    callback.OnError?.Invoke($"LLM API error: {error.Message}");
                }
            );

            if (hadError) return;

            if (toolCalls == null || toolCalls.Count == 0)
            {
                var content = contentBuilder.ToString();

                // If empty content after tool calls, ask the LLM to summarize
                if (string.IsNullOrEmpty(content) && round > 0)
                {
                    session.AddMessage(ChatMessage.Assistant(""));
                    session.AddMessage(ChatMessage.User("Based on the tool results above, please provide a clear analysis and summary."));
                    continue;
                }

                // No tool calls — final answer
                session.AddMessage(ChatMessage.Assistant(content));
                callback.OnComplete?.Invoke();
                return;
            }

            // LLM wants tools
            session.AddMessage(ChatMessage.Assistant(contentBuilder.ToString(), toolCalls));

            // Execute each tool call
            foreach (var tc in toolCalls)
            {
                var toolName = tc.Name ?? "";
                var arguments = tc.Arguments ?? "";

                callback.OnToolStart?.Invoke(toolName);

                try
                {
                    var args = string.IsNullOrEmpty(arguments)
                        ? new Dictionary<string, object>()
                        : JsonSerializer.Deserialize<Dictionary<string, object>>(arguments) ?? new();
                    var result = _tools.Execute(toolName, args);
                    var jsonResult = JsonSerializer.Serialize(result);
                    // Truncate very long results
                    if (jsonResult.Length > 8000)
                        jsonResult = jsonResult.Substring(0, 8000) + $"\n... (truncated, total {jsonResult.Length} chars)";

                    callback.OnToolResult?.Invoke(toolName, jsonResult);
                    session.AddMessage(ChatMessage.Tool(tc.Id, jsonResult));
                }
                catch (Exception e)
                {
                    var errorResult = $"{{\"error\": \"{e.Message}\"}}";
                    callback.OnToolResult?.Invoke(toolName, errorResult);
                    session.AddMessage(ChatMessage.Tool(tc.Id, errorResult));
                }
            }
        }

        // Reached max rounds — force final summary with tool_choice=none
        var finalMessages = new List<object>();
        finalMessages.Add(new { role = "system", content = SystemPrompt });
        foreach (var msg in session.GetMessages())
            finalMessages.Add(msg.ToApiObject());
        finalMessages.Add(new
        {
            role = "system",
            content = "You have reached the maximum number of tool-calling rounds. "
                    + "Based on all the diagnostic data you have gathered so far, "
                    + "provide a comprehensive analysis and actionable recommendations NOW. "
                    + "Do not attempt to call more tools."
        });

        await _llm.StreamChatAsync(
            finalMessages,
            new List<object>(),
            "none",
            chunk => callback.OnContent?.Invoke(chunk),
            (_, _) => callback.OnComplete?.Invoke(),
            error =>
            {
                callback.OnContent?.Invoke("\n\n*I've gathered diagnostic data from multiple tools "
                    + "but reached the analysis limit. Please ask a more specific question "
                    + "about a particular component for deeper analysis.*");
                callback.OnComplete?.Invoke();
            }
        );
    }

    private string SystemPrompt =>
        $"""
        You are an expert ASP.NET Core debugging assistant.
        You are running INSIDE the developer's application and have direct access
        to its runtime state through diagnostic tools.

        ## Your Capabilities
        You can call tools to inspect the live application. You have {_tools.Names().Count} tools available
        across {CategorizeTools()} diagnostic categories. Key capabilities include:
        - Runtime & GC diagnostics (heap, threads, GC stats)
        - HTTP request analysis (active, slow, outbound)
        - Database & EF Core queries, migrations
        - Security, error tracking, health checks
        - Cache, Redis, WebSocket connections
        - CPU profiling, memory leak detection, snapshots
        - Configuration, feature flags, endpoint testing
        - Service collection (DI), background services, logging
        - Deployment info, metrics, file handles

        ## Workflow
        1. Understand the developer's problem description
        2. Proactively call the most relevant tools to gather diagnostic data — DO NOT just ask questions
        3. Analyze the collected data to identify root causes
        4. Provide clear, actionable solutions with data evidence

        ## Guidelines
        - Be proactive: gather data with tools before answering
        - Always present data in a readable format (tables, bullet points)
        - Respond in the same language the developer uses (Chinese/English/etc.)
        - When you find a problem, explain the root cause and give concrete fix suggestions
        - You can call multiple tools in parallel if they are independent
        """;

    private string CategorizeTools()
    {
        var names = _tools.Names();
        var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var cat = ExtractCategory(name);
            categories.TryGetValue(cat, out var count);
            categories[cat] = count + 1;
        }
        return $"{categories.Count} categories";
    }

    private static string ExtractCategory(string toolName)
    {
        var parts = toolName.Split('_');
        var keyword = parts.Length >= 2 ? parts[1] : (parts.Length > 0 ? parts[0] : "");
        return keyword switch
        {
            "heap" or "memory" or "gc" or "leak" => "Memory & GC",
            "snapshot" or "compare" => "Snapshots",
            "thread" or "lock" or "deadlock" or "contention" => "Threading",
            "health" => "Health Checks",
            "error" => "Error Tracking",
            "config" or "env" => "Configuration",
            "cache" => "Cache",
            "http" or "outbound" or "request" => "HTTP",
            "ef" or "db" or "migration" => "Database",
            "redis" => "Redis",
            "ws" or "websocket" => "WebSocket",
            "cpu" or "profile" => "Profiling",
            "feature" or "flag" => "Feature Flags",
            "test" or "endpoint" => "Endpoint Testing",
            "pool" or "connection" => "Connection Pool",
            "metric" or "counter" => "Metrics",
            "build" or "deployment" or "version" => "Build & Deployment",
            "service" or "registered" => "Service Registry",
            "log" => "Logging",
            "fd" or "handle" => "File Handles",
            "security" or "auth" or "cors" => "Security",
            "background" => "Background Services",
            "module" or "loaded" => "Modules",
            _ => "Runtime",
        };
    }
}
