namespace DebugAgent;

public class AgentConfig
{
    public bool Enabled { get; set; } = true;
    public string BasePath { get; set; } = "/agent";
    public LLMConfig LLM { get; set; } = new();

    public static AgentConfig FromEnvironment() => new()
    {
        Enabled = Environment.GetEnvironmentVariable("DEBUG_AGENT_ENABLED")?.ToLower() != "false",
        BasePath = Environment.GetEnvironmentVariable("DEBUG_AGENT_BASE_PATH") ?? "/agent",
        LLM = LLMConfig.FromEnvironment(),
    };
}

public class LLMConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public int MaxToolRounds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 120;

    public static LLMConfig FromEnvironment() => new()
    {
        BaseUrl = Env("LLM_BASE_URL", "https://api.openai.com/v1"),
        ApiKey = Env("LLM_API_KEY", ""),
        Model = Env("LLM_MODEL", "gpt-4o"),
        Temperature = double.Parse(Env("LLM_TEMPERATURE", "0.3")),
        MaxTokens = int.Parse(Env("LLM_MAX_TOKENS", "4096")),
        MaxToolRounds = int.Parse(Env("LLM_MAX_TOOL_ROUNDS", "10")),
        TimeoutSeconds = int.Parse(Env("LLM_TIMEOUT_SECONDS", "120")),
    };

    private static string Env(string key, string def) =>
        Environment.GetEnvironmentVariable(key) ?? def;
}
