using System.Text;
using System.Text.Json;

namespace DebugAgents;

/// <summary>
/// Compresses conversation context by asking the LLM to summarize older history.
/// Matches Spring agent ContextCompressor strategy:
/// 1. Split history into [old rounds to summarize] + [recent rounds to keep]
/// 2. Send old rounds to the LLM with a summarization prompt
/// 3. Replace old rounds with the generated summary message
/// </summary>
public class ContextCompressor
{
    private readonly LLMClient _llmClient;
    private readonly int _maxContextTokens;
    private readonly int _targetTokens;
    private readonly int _recentRoundsToKeep;

    public ContextCompressor(LLMClient llmClient, int maxContextTokens, int recentRoundsToKeep = 3)
    {
        _llmClient = llmClient;
        _maxContextTokens = maxContextTokens;
        _targetTokens = (int)(maxContextTokens * 0.75);
        _recentRoundsToKeep = recentRoundsToKeep;
    }

    public bool NeedsCompression(int currentTokens) => currentTokens > _maxContextTokens;

    public CompressionResult? Compress(ChatSession session) => CompressAsync(session).GetAwaiter().GetResult();

    public async Task<CompressionResult?> CompressAsync(ChatSession session)
    {
        var originalTokens = session.CurrentContextTokens;
        if (!NeedsCompression(originalTokens)) return null;

        var allMessages = session.GetMessages();
        var rounds = IdentifyRounds(allMessages);

        int keepCount = Math.Min(_recentRoundsToKeep, rounds.Count - 1);
        if (keepCount < 1)
        {
            return await CompressToolResultsAsync(session, originalTokens, allMessages);
        }

        int summarizeCount = rounds.Count - keepCount;

        var toSummarize = new List<ChatMessage>();
        for (int i = 0; i < summarizeCount; i++)
            toSummarize.AddRange(rounds[i].Messages);

        var toKeep = new List<ChatMessage>();
        for (int i = summarizeCount; i < rounds.Count; i++)
            toKeep.AddRange(rounds[i].Messages);

        string summary;
        try
        {
            summary = await SummarizeWithLlm(toSummarize) ?? "(summary unavailable)";
        }
        catch
        {
            summary = FallbackTruncate(toSummarize);
        }

        var compressed = new List<ChatMessage>
        {
            ChatMessage.System($"[Previous conversation summary — {summarizeCount} rounds compressed]\n\n{summary}")
        };
        compressed.AddRange(toKeep);

        int compressedTokens = EstimateTokens(compressed);
        session.ReplaceMessages(compressed);

        return new CompressionResult(originalTokens, compressedTokens, summarizeCount,
            $"LLM summarized {summarizeCount} rounds");
    }

    private async Task<CompressionResult?> CompressToolResultsAsync(ChatSession session, int originalTokens, List<ChatMessage> messages)
    {
        // Identify tool-call blocks
        var blocks = new List<ToolBlock>();
        ToolBlock? currentBlock = null;

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role == "assistant" && msg.ToolCalls?.Count > 0)
            {
                currentBlock = new ToolBlock { StartIndex = i };
                currentBlock.Messages.Add(msg);
                blocks.Add(currentBlock);
            }
            else if (msg.Role == "tool" && currentBlock != null)
            {
                currentBlock.Messages.Add(msg);
                currentBlock.EndIndex = i;
            }
            else
            {
                currentBlock = null;
            }
        }

        if (blocks.Count == 0) return null;

        int keepRecent = Math.Min(1, blocks.Count - 1);
        int summarizeCount = blocks.Count - keepRecent;
        if (summarizeCount < 1)
        {
            var onlyBlock = blocks[0];
            if (onlyBlock.Messages.Count <= 3) return null;
            summarizeCount = 1;
            keepRecent = 0;
        }

        var toSummarize = new List<ChatMessage>();
        for (int i = 0; i < summarizeCount; i++)
            toSummarize.AddRange(blocks[i].Messages);

        string summary;
        try { summary = await SummarizeToolResultsWithLlm(toSummarize) ?? "(summary unavailable)"; }
        catch { return null; }

        // Rebuild
        var skipIndices = new HashSet<int>();
        for (int i = 0; i < summarizeCount; i++)
        {
            var b = blocks[i];
            for (int j = b.StartIndex; j <= b.EndIndex; j++)
                skipIndices.Add(j);
        }

        var compressed = new List<ChatMessage>();
        bool summaryInserted = false;
        for (int i = 0; i < messages.Count; i++)
        {
            if (skipIndices.Contains(i))
            {
                if (!summaryInserted)
                {
                    compressed.Add(ChatMessage.System(
                        $"[Previous diagnostic results summary — {summarizeCount} tool-call round(s) compressed]\n\n{summary}"));
                    summaryInserted = true;
                }
                continue;
            }
            compressed.Add(messages[i]);
        }

        if (!summaryInserted)
            compressed.Add(ChatMessage.System($"[Diagnostic summary]\n\n{summary}"));

        int compressedTokens = EstimateTokens(compressed);
        session.ReplaceMessages(compressed);

        return new CompressionResult(originalTokens, compressedTokens, 0,
            $"LLM summarized {summarizeCount} tool-call blocks");
    }

    private async Task<string?> SummarizeWithLlm(List<ChatMessage> oldMessages)
    {
        var conversationText = new StringBuilder();
        foreach (var msg in oldMessages)
        {
            switch (msg.Role)
            {
                case "user":
                    conversationText.Append($"[User] {msg.Content}\n\n");
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(msg.Content))
                        conversationText.Append($"[Assistant] {msg.Content}\n\n");
                    if (msg.ToolCalls != null)
                    {
                        foreach (var tc in msg.ToolCalls)
                            conversationText.Append($"[Tool Call] {tc.Function?.Name}({tc.Function?.Arguments})\n\n");
                    }
                    break;
                case "tool":
                    var content = msg.Content ?? "";
                    if (content.Length > 2000)
                        content = content.Substring(0, 2000) + "...[truncated]";
                    conversationText.Append($"[Tool Result] {content}\n\n");
                    break;
            }
        }

        var prompt = """
You are a conversation summarizer for an ASP.NET Core debugging assistant.
Summarize the KEY diagnostic findings from the conversation below concisely.

Focus on preserving:
- Problems investigated and their root causes (if found)
- Key tool results: actual numbers, statuses, error messages, configuration values
- Recommendations or fixes already suggested
- Any unresolved issues or follow-up actions pending

Rules:
- Be concise but preserve ALL important data points (memory sizes, thread counts, error codes, etc.)
- Use bullet points
- Do NOT include full JSON dumps — extract only the meaningful values
- Keep it under 600 words
""";

        var messages = new List<object>
        {
            new { role = "system", content = prompt },
            new { role = "user", content = $"Conversation to summarize:\n\n{conversationText}" },
        };

        return await _llmClient.CompleteAsync(messages, 1024);
    }

    private async Task<string?> SummarizeToolResultsWithLlm(List<ChatMessage> toolMessages)
    {
        var toolText = new StringBuilder();
        foreach (var msg in toolMessages)
        {
            var content = msg.Content ?? "";
            if (content.Length > 3000)
                content = content.Substring(0, 3000) + "...[truncated]";
            toolText.Append($"[Tool Result] {content}\n\n---\n\n");
        }

        var prompt = """
You are summarizing diagnostic tool results from an ASP.NET Core debugging session.
Below are tool results that need to be compressed to save context space.

For each tool result, extract:
- The tool name (if identifiable from the data)
- The KEY metrics: actual numbers, statuses, error messages, configuration values
- Any anomalies or issues detected

Format as concise bullet points. Do NOT include full JSON — extract only meaningful values.
Keep it under 400 words.
""";

        var messages = new List<object>
        {
            new { role = "system", content = prompt },
            new { role = "user", content = $"Tool results to summarize:\n\n{toolText}" },
        };

        return await _llmClient.CompleteAsync(messages, 800);
    }

    private string FallbackTruncate(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.Append("Previous conversation summary (fallback — LLM summarization failed):\n\n");
        foreach (var msg in messages)
        {
            if (msg.Role == "user" && msg.Content != null)
            {
                var q = msg.Content.Length > 100 ? msg.Content.Substring(0, 100) + "..." : msg.Content;
                sb.Append($"- User asked: {q}\n");
            }
            if (msg.Role == "assistant" && msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                    sb.Append($"- Called tool: {tc.Function?.Name}\n");
            }
        }
        return sb.ToString();
    }

    private List<Round> IdentifyRounds(List<ChatMessage> messages)
    {
        var rounds = new List<Round>();
        var current = new Round();

        foreach (var msg in messages)
        {
            if (msg.Role == "user")
            {
                if (current.Messages.Count > 0) { rounds.Add(current); current = new Round(); }
                current.Messages.Add(msg);
            }
            else if (msg.Role == "assistant")
            {
                if (current.HasAssistant) { rounds.Add(current); current = new Round(); }
                current.Messages.Add(msg);
                current.HasAssistant = true;
            }
            else
            {
                current.Messages.Add(msg);
            }
        }
        if (current.Messages.Count > 0) rounds.Add(current);
        return rounds;
    }

    private static int EstimateTokens(List<ChatMessage> messages)
    {
        int chars = 0;
        foreach (var msg in messages)
        {
            chars += msg.Content?.Length ?? 0;
            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    chars += tc.Function?.Name?.Length ?? 0;
                    chars += tc.Function?.Arguments?.Length ?? 0;
                }
            }
        }
        return chars / 4;
    }

    private class Round { public List<ChatMessage> Messages { get; } = new(); public bool HasAssistant; }
    private class ToolBlock { public int StartIndex = -1; public int EndIndex = -1; public List<ChatMessage> Messages { get; } = new(); }
}

public record CompressionResult(int OriginalTokens, int CompressedTokens, int RemovedRounds, string Strategy);
