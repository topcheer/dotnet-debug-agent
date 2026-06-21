using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Configuration inspector — lists config sources, keys, and values with secret masking.
/// </summary>
public static class ConfigurationInspector
{
    private static readonly string[] SecretPatterns = { "KEY", "SECRET", "PASSWORD", "TOKEN", "CREDENTIAL", "CONN", "API" };

    [DebugTool("get_configuration_sources", "List all configuration providers (JSON, env vars, command line, etc.)")]
    public static object GetConfigurationSources()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var config = AgentContext.Services.GetService<IConfiguration>() as IConfigurationRoot;
        if (config == null)
            return new { error = "IConfigurationRoot not available" };

        var sources = config.Providers.Select((p, i) =>
        {
            var keyCount = 0;
            foreach (var _ in p.GetChildKeys([], null))
                keyCount++;

            return new
            {
                index = i,
                type = p.GetType().Name,
                @namespace = p.GetType().Namespace ?? "",
                top_level_keys = keyCount,
            };
        }).ToList();

        return new { total = sources.Count, sources };
    }

    [DebugTool("get_configuration_keys", "List all configuration keys and values (secrets masked)")]
    public static object GetConfigurationKeys(string prefix = "")
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var config = AgentContext.Services.GetService<IConfiguration>() as IConfigurationRoot;
        if (config == null)
            return new { error = "IConfigurationRoot not available" };

        var result = new Dictionary<string, string>();
        CollectKeys(config, prefix, result);

        return new { total = result.Count, configuration = result };
    }

    [DebugTool("get_configuration_value", "Get the value of a specific configuration key and which provider it comes from")]
    public static object GetConfigurationValue([ToolParam("Configuration key (e.g. 'ConnectionStrings:DefaultConnection')")] string key)
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var config = AgentContext.Services.GetService<IConfiguration>() as IConfigurationRoot;
        if (config == null)
            return new { error = "IConfigurationRoot not available" };

        var value = config[key];
        if (value == null)
            return new { error = $"Key '{key}' not found in any configuration source" };

        // Find which provider provides this value
        string? sourceProvider = null;
        foreach (var provider in config.Providers)
        {
            if (provider.TryGet(key, out var providerValue))
            {
                sourceProvider = provider.GetType().Name;
                break;
            }
        }

        return new
        {
            key,
            value = IsSecret(key) ? "***masked***" : value,
            source_provider = sourceProvider ?? "Unknown",
        };
    }

    private static void CollectKeys(IConfiguration config, string prefix, Dictionary<string, string> result)
    {
        foreach (var child in config.GetChildren())
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? child.Key : $"{prefix}:{child.Key}";

            if (child.Value != null)
            {
                result[fullKey] = IsSecret(fullKey) ? "***masked***" : child.Value;
            }

            var children = child.GetChildren().ToList();
            if (children.Count > 0)
            {
                CollectKeys(child, fullKey, result);
            }
        }
    }

    private static bool IsSecret(string key)
    {
        var upper = key.ToUpperInvariant();
        return SecretPatterns.Any(s => upper.Contains(s));
    }
}
