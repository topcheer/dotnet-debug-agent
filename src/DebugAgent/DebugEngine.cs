using System.Collections.Concurrent;
using System.Text.Json;

namespace DebugAgents;

public class DebugEngine
{
    private readonly AgentConfig _config;
    private readonly LLMClient _llm;
    private readonly ConcurrentDictionary<string, List<object>> _conversations = new();

    private const string SystemPrompt = """
You are an expert runtime debugging assistant embedded inside a live ASP.NET Core application.
You have access to 50+ diagnostic tools across 10 inspectors that inspect the running process in real-time:
- DI Container (registered services, lifetimes)
- Configuration (sources, keys, values)
- HTTP Endpoints (routing, middleware)
- Health Checks (component status)
- Logging (recent logs, search, stats)
- EF Core (DbContext, migrations, connections)
- Memory Cache (keys, values)
- Background Services (hosted services)
- .NET Runtime (memory, GC, thread pool)
- HTTP Requests (recent, errors, stats)

Your job: understand the problem, call tools to gather data, analyze results, and explain findings clearly.
""";

    public ToolRegistry Tools { get; } = GlobalRegistry.Instance;

    public DebugEngine(AgentConfig? config = null)
    {
        _config = config ?? AgentConfig.FromEnvironment();
        _llm = new LLMClient(_config.LLM);

        // Auto-discover built-in inspectors
        Tools.DiscoverTools(typeof(RuntimeInspector).Assembly);
    }

    public async IAsyncEnumerable<(string EventType, object? Data)> ChatStreamAsync(string message, string sessionId = "default")
    {
        var history = _conversations.GetOrAdd(sessionId, _ => new List<object> { new { role = "system", content = SystemPrompt } });
        history.Add(new { role = "user", content = message });

        var toolSchemas = Tools.AllSchemas();
        var rounds = 0;

        while (rounds <= _config.LLM.MaxToolRounds)
        {
            rounds++;
            var response = await _llm.ChatAsync(history, toolSchemas.Count > 0 ? toolSchemas : null);
            var choice = response.GetProperty("choices")[0];
            var msg = choice.GetProperty("message");

            // Check for tool calls
            if (msg.TryGetProperty("tool_calls", out var toolCalls))
            {
                // Add assistant message with tool calls
                history.Add(JsonSerializer.Deserialize<JsonElement>(msg.GetRawText()));

                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var fn = tc.GetProperty("function");
                    var toolName = fn.GetProperty("name").GetString()!;
                    var argsStr = fn.GetProperty("arguments").GetString() ?? "{}";
                    var args = JsonSerializer.Deserialize<Dictionary<string, object>>(argsStr) ?? new();

                    yield return ("tool_call", new { tool = toolName, args });

                    var result = Tools.Execute(toolName, args);

                    yield return ("tool_result", new { tool = toolName, result });

                    history.Add(new
                    {
                        role = "tool",
                        tool_call_id = tc.GetProperty("id").GetString(),
                        content = JsonSerializer.Serialize(result).Length > 12000
                            ? JsonSerializer.Serialize(result)[..12000]
                            : JsonSerializer.Serialize(result),
                    });
                }
                continue;
            }

            // Final answer
            var content = msg.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
            history.Add(new { role = "assistant", content });

            if (!string.IsNullOrEmpty(content))
                yield return ("token", content);

            yield return ("done", null);
            yield break;
        }

        yield return ("token", "_Reached maximum tool-call rounds._");
        yield return ("done", null);
    }
}
