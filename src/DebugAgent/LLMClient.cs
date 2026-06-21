using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DebugAgents;

public class LLMClient
{
    private readonly LLMConfig _cfg;
    private readonly HttpClient _client;

    public LLMClient(LLMConfig config)
    {
        _cfg = config;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
    }

    /// <summary>
    /// Non-streaming chat completion with optional tool calling.
    /// </summary>
    public async Task<JsonElement> ChatAsync(List<object> messages, List<object>? tools = null)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _cfg.Model,
            ["messages"] = messages,
            ["temperature"] = _cfg.Temperature,
            ["max_tokens"] = _cfg.MaxTokens,
        };
        if (tools != null && tools.Count > 0)
            body["tools"] = tools;

        var json = JsonSerializer.Serialize(body);
        var req = new HttpRequestMessage(HttpMethod.Post, _cfg.BaseUrl + "/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);

        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var content = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(content);
    }
}
