namespace DebugAgents;

public class AgentConfig
{
    public bool Enabled { get; set; } = true;
    public string BasePath { get; set; } = "/agent";
    public LLMConfig LLM { get; set; } = new();

    public static AgentConfig FromEnvironment() => new()
    {
        Enabled = Env("ENABLED", "true") != "false",
        BasePath = Env("BASE_PATH", "/agent"),
        LLM = LLMConfig.FromEnvironment(),
    };

    private static string Env(string key, string def) =>
        Environment.GetEnvironmentVariable($"DEBUG_AGENT__{key}") ??
        Environment.GetEnvironmentVariable(key) ??
        (key == "LLM_API_KEY" ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : null) ?? def;
}

public class LLMConfig
{
    public string BaseUrl { get; set; } = "https://open.bigmodel.cn/api/coding/paas/v4";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "glm-4.6";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public int MaxToolRounds { get; set; } = 25;
    public int TimeoutSeconds { get; set; } = 120;
    public int ContextWindowTokens { get; set; } = 100000;
    public int MaxRetries { get; set; } = 3;
    public long RetryBaseDelayMs { get; set; } = 1000;
    public long RetryMaxDelayMs { get; set; } = 30000;

    public static LLMConfig FromEnvironment() => new()
    {
        BaseUrl = Env("LLM_BASE_URL", "https://open.bigmodel.cn/api/coding/paas/v4"),
        ApiKey = Env("LLM_API_KEY", ""),
        Model = Env("LLM_MODEL", "glm-4.6"),
        Temperature = double.Parse(Env("LLM_TEMPERATURE", "0.3"), System.Globalization.CultureInfo.InvariantCulture),
        MaxTokens = int.Parse(Env("LLM_MAX_TOKENS", "4096")),
        MaxToolRounds = int.Parse(Env("LLM_MAX_TOOL_ROUNDS", "25")),
        TimeoutSeconds = int.Parse(Env("LLM_TIMEOUT_SECONDS", "120")),
        ContextWindowTokens = int.Parse(Env("LLM_CONTEXT_WINDOW_TOKENS", "100000")),
        MaxRetries = int.Parse(Env("LLM_MAX_RETRIES", "3")),
    };

    private static string Env(string key, string def) =>
        Environment.GetEnvironmentVariable($"DEBUG_AGENT__{key}") ??
        Environment.GetEnvironmentVariable(key) ??
        (key == "LLM_API_KEY" ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : null) ?? def;
}
