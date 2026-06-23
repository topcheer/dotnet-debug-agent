using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DebugAgents;

/// <summary>
/// OpenAI-compatible LLM client with streaming and non-streaming support.
/// Handles SSE parsing for stream=true responses, including tool_calls deltas.
/// </summary>
public class LLMClient
{
    private readonly LLMConfig _cfg;
    private readonly HttpClient _client;

    public LLMConfig Config => _cfg;

    // Static shared handler to avoid socket exhaustion (.NET HttpClient anti-pattern).
    // All LLMClient instances reuse the same underlying socket pool.
    private static readonly HttpClientHandler _sharedHandler = new()
    {
        UseProxy = false,
        MaxConnectionsPerServer = 8,
    };

    public LLMClient(LLMConfig cfg)
    {
        _cfg = cfg;
        _client = new HttpClient(_sharedHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds),
        };
    }

    /// <summary>
    /// Send a streaming chat completion request.
    /// Calls onContent for each text delta, onComplete with tool calls and usage when done.
    /// </summary>
    public async Task StreamChatAsync(
        List<object> messages,
        List<object> tools,
        string toolChoice,
        Action<string> onContent,
        Action<List<ToolCallResult>, string?> onComplete,
        Action<Exception> onError)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _cfg.Model,
            ["messages"] = messages,
            ["temperature"] = _cfg.Temperature,
            ["max_tokens"] = _cfg.MaxTokens,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };

        if (tools.Count > 0 && toolChoice != "none")
        {
            body["tools"] = tools;
            if (toolChoice != "auto")
                body["tool_choice"] = toolChoice;
        }
        else if (toolChoice == "none")
        {
            body["tools"] = new List<object>();
            body["tool_choice"] = "none";
        }
        else if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.BaseUrl}/chat/completions");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(_cfg.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);

        // Retry with exponential backoff
        Exception? lastError = null;
        for (int attempt = 0; attempt <= _cfg.MaxRetries; attempt++)
        {
            try
            {
                using var resp = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (!resp.IsSuccessStatusCode)
                {
                    var errContent = await resp.Content.ReadAsStringAsync();
                    if ((int)resp.StatusCode is 429 or >= 500 && attempt < _cfg.MaxRetries)
                    {
                        var delay = Math.Min(_cfg.RetryBaseDelayMs * (1L << attempt), _cfg.RetryMaxDelayMs);
                        await Task.Delay((int)delay);
                        // Recreate request (content already consumed)
                        request = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.BaseUrl}/chat/completions");
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        if (!string.IsNullOrEmpty(_cfg.ApiKey))
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
                        continue;
                    }
                    throw new Exception($"LLM API error {(int)resp.StatusCode}: {errContent}");
                }

                // Parse SSE stream
                using var stream = await resp.Content.ReadAsStreamAsync();
                var toolCallAccumulator = new Dictionary<int, ToolCallResult>();
                var contentBuilder = new StringBuilder();
                string? finishReason = null;
                int? promptTokens = null;
                int? completionTokens = null;

                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line.Substring(6);
                    if (data == "[DONE]") break;

                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        var delta = choice.TryGetProperty("delta", out var d) ? d : default;

                        // Content delta
                        if (delta.ValueKind == JsonValueKind.Object &&
                            delta.TryGetProperty("content", out var contentEl) &&
                            contentEl.ValueKind == JsonValueKind.String)
                        {
                            var chunk = contentEl.GetString();
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                contentBuilder.Append(chunk);
                                onContent(chunk);
                            }
                        }

                        // Tool calls delta
                        if (delta.ValueKind == JsonValueKind.Object &&
                            delta.TryGetProperty("tool_calls", out var tcEl) &&
                            tcEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in tcEl.EnumerateArray())
                            {
                                var index = tc.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                                if (!toolCallAccumulator.ContainsKey(index))
                                    toolCallAccumulator[index] = new ToolCallResult();

                                var tcObj = toolCallAccumulator[index];

                                if (tc.TryGetProperty("id", out var idEl))
                                    tcObj.Id = idEl.GetString() ?? "";

                                if (tc.TryGetProperty("function", out var fnEl))
                                {
                                    if (fnEl.TryGetProperty("name", out var nameEl))
                                        tcObj.Name = (tcObj.Name ?? "") + nameEl.GetString();
                                    if (fnEl.TryGetProperty("arguments", out var argEl))
                                        tcObj.Arguments = (tcObj.Arguments ?? "") + argEl.GetString();
                                }
                            }
                        }

                        // Finish reason
                        if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                        {
                            finishReason = frEl.GetString();
                        }
                    }

                    // Usage
                    if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        promptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : null;
                        completionTokens = usageEl.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : null;
                    }
                }

                var toolCalls = toolCallAccumulator.OrderBy(x => x.Key).Select(x => x.Value).ToList();
                onComplete(toolCalls, finishReason);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < _cfg.MaxRetries && ex is not HttpRequestException)
                {
                    var delay = Math.Min(_cfg.RetryBaseDelayMs * (1L << attempt), _cfg.RetryMaxDelayMs);
                    await Task.Delay((int)delay);
                    // Recreate request
                    request = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.BaseUrl}/chat/completions");
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrEmpty(_cfg.ApiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
                    continue;
                }
            }
        }

        onError(lastError ?? new Exception("Unknown LLM error"));
    }

    /// <summary>
    /// Send a non-streaming chat completion (for context summarization).
    /// </summary>
    public async Task<string?> CompleteAsync(List<object> messages, int maxTokens = 1024)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _cfg.Model,
            ["messages"] = messages,
            ["temperature"] = 0.0,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
        };

        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.BaseUrl}/chat/completions");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(_cfg.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);

        using var resp = await _client.SendAsync(request);
        var content = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            return choices[0].GetProperty("message").GetProperty("content").GetString();
        }
        return null;
    }
}

/// <summary>
/// Accumulated tool call from streaming deltas.
/// </summary>
public class ToolCallResult
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}
