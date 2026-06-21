using System.Collections.Concurrent;
using System.Text.Json;

namespace DebugAgents;

/// <summary>
/// Represents a chat message in the conversation history.
/// Maps to OpenAI message format: role + content + optional tool_calls + tool_call_id.
/// </summary>
public class ChatMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    public List<ToolCallInfo>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };
    public static ChatMessage Assistant(string content, List<ToolCallResult>? toolCalls = null)
    {
        var msg = new ChatMessage { Role = "assistant", Content = content };
        if (toolCalls != null && toolCalls.Count > 0)
        {
            msg.ToolCalls = toolCalls.Select(tc => new ToolCallInfo
            {
                Id = tc.Id,
                Type = "function",
                Function = new ToolCallFunction { Name = tc.Name ?? "", Arguments = tc.Arguments ?? "" }
            }).ToList();
        }
        return msg;
    }
    public static ChatMessage Tool(string toolCallId, string content) => new()
    {
        Role = "tool",
        ToolCallId = toolCallId,
        Content = content,
    };

    /// <summary>
    /// Convert to OpenAI API message object.
    /// </summary>
    public object ToApiObject()
    {
        if (Role == "tool")
            return new { role = "tool", tool_call_id = ToolCallId, content = Content ?? "" };

        if (ToolCalls != null && ToolCalls.Count > 0)
        {
            return new
            {
                role = Role,
                content = string.IsNullOrEmpty(Content) ? null : Content,
                tool_calls = ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Function?.Name ?? "", arguments = tc.Function?.Arguments ?? "" }
                }),
            };
        }

        return new { role = Role, content = Content ?? "" };
    }
}

public class ToolCallInfo
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public ToolCallFunction? Function { get; set; }
}

public class ToolCallFunction
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>
/// Manages conversation sessions in memory.
/// Tracks cumulative token usage for context compression decisions.
/// </summary>
public class ChatSession
{
    public string SessionId { get; }
    public long CreatedAt { get; }
    public long LastActiveAt { get; private set; }

    private readonly List<ChatMessage> _messages = new();
    public int CumulativePromptTokens { get; private set; }
    public int CumulativeCompletionTokens { get; private set; }

    public ChatSession(string sessionId)
    {
        SessionId = sessionId;
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LastActiveAt = CreatedAt;
    }

    public List<ChatMessage> GetMessages() => new(_messages);

    public void AddMessage(ChatMessage msg)
    {
        _messages.Add(msg);
        LastActiveAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void ReplaceMessages(List<ChatMessage> newMessages)
    {
        _messages.Clear();
        _messages.AddRange(newMessages);
        LastActiveAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void RecordTokenUsage(int? promptTokens, int? completionTokens)
    {
        if (promptTokens.HasValue) CumulativePromptTokens = promptTokens.Value;
        if (completionTokens.HasValue) CumulativeCompletionTokens += completionTokens.Value;
    }

    public int CurrentContextTokens => CumulativePromptTokens;

    public void Clear()
    {
        _messages.Clear();
        CumulativePromptTokens = 0;
        CumulativeCompletionTokens = 0;
        LastActiveAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
