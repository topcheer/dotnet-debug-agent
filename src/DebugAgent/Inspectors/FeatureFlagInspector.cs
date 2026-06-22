using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Feature flag entry record.
/// </summary>
public record FeatureFlagEntry(
    string Name,
    bool Enabled,
    string Variant,
    DateTimeOffset RegisteredAt
);

/// <summary>
/// Static feature flag registry. Call FeatureFlagRegistry.RegisterFeatureFlag() at startup.
/// Also detects Microsoft.FeatureManagement if referenced.
/// </summary>
public static class FeatureFlagRegistry
{
    private static readonly ConcurrentDictionary<string, FeatureFlagEntry> _flags = new();

    public static void RegisterFeatureFlag(string name, bool enabled, string variant = "default")
    {
        _flags[name] = new FeatureFlagEntry(name, enabled, variant, DateTimeOffset.UtcNow);
    }

    public static List<FeatureFlagEntry> GetAll() => _flags.Values.ToList();

    public static FeatureFlagEntry? Get(string name) =>
        _flags.GetValueOrDefault(name);

    public static bool TryEvaluate(string name, out bool enabled)
    {
        var flag = _flags.GetValueOrDefault(name);
        enabled = flag?.Enabled ?? false;
        return flag != null;
    }
}

/// <summary>
/// Feature flag inspector — list and evaluate feature flags.
/// </summary>
public static class FeatureFlagInspector
{
    [DebugTool("get_feature_flags", "List registered feature flags with their state (on/off, variant)")]
    public static object GetFeatureFlags()
    {
        var flags = FeatureFlagRegistry.GetAll();

        // Try to detect Microsoft.FeatureManagement
        var fmFlags = new List<object>();
        try
        {
            // Attempt to resolve IFeatureManager if the library is referenced
            var fmType = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.FeatureManagement")
                ?.GetType("Microsoft.FeatureManagement.IFeatureManager");

            if (fmType != null && AgentContext.Services != null)
            {
                var fm = AgentContext.Services.GetService(fmType);
                if (fm != null)
                {
                    // Try to enumerate feature names asynchronously
                    var getFeatureNamesAsync = fmType.GetMethod("GetFeatureNamesAsync");
                    if (getFeatureNamesAsync != null)
                    {
                        var task = getFeatureNamesAsync.Invoke(fm, null) as dynamic;
                        if (task != null)
                        {
                            task.Wait();
                            var enumerator = task.Result;
                            while (enumerator.MoveNextAsync().Result)
                            {
                                var flagName = enumerator.Current?.ToString() ?? "";
                                var isEnabledAsync = fmType.GetMethod("IsEnabledAsync", new[] { typeof(string) });
                                if (isEnabledAsync != null)
                                {
                                    var enabledTask = isEnabledAsync.Invoke(fm, new object[] { flagName }) as dynamic;
                                    enabledTask?.Wait();
                                    fmFlags.Add(new
                                    {
                                        name = flagName,
                                        enabled = (bool)enabledTask!.Result,
                                        source = "Microsoft.FeatureManagement",
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { /* FeatureManagement not referenced or not configured */ }

        // Merge: registered flags take precedence, then FM-detected ones
        var registeredNames = flags.Select(f => f.Name).ToHashSet();
        var allFlags = flags.Select(f => new
        {
            name = f.Name,
            enabled = f.Enabled,
            variant = f.Variant,
            registered_at = f.RegisteredAt.ToString("o"),
            source = "FeatureFlagRegistry",
        }).Cast<object>().ToList();

        foreach (var f in fmFlags)
        {
            dynamic d = f;
            if (!registeredNames.Contains((string)d.name))
                allFlags.Add(f);
        }

        return new
        {
            total = allFlags.Count,
            flags = allFlags,
            feature_management_detected = fmFlags.Count > 0,
        };
    }

    [DebugTool("evaluate_flag", "Evaluate a specific feature flag by name")]
    public static object EvaluateFlag([ToolParam("Name of the feature flag to evaluate")] string flag_name)
    {
        // First check our registry
        var flag = FeatureFlagRegistry.Get(flag_name);
        if (flag != null)
        {
            return new
            {
                flag_name = flag.Name,
                enabled = flag.Enabled,
                variant = flag.Variant,
                source = "FeatureFlagRegistry",
                evaluated_at = DateTimeOffset.UtcNow.ToString("o"),
            };
        }

        // Try Microsoft.FeatureManagement
        try
        {
            var fmType = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.FeatureManagement")
                ?.GetType("Microsoft.FeatureManagement.IFeatureManager");

            if (fmType != null && AgentContext.Services != null)
            {
                var fm = AgentContext.Services.GetService(fmType);
                if (fm != null)
                {
                    var isEnabledAsync = fmType.GetMethod("IsEnabledAsync", new[] { typeof(string) });
                    if (isEnabledAsync != null)
                    {
                        var task = isEnabledAsync.Invoke(fm, new object[] { flag_name }) as dynamic;
                        task?.Wait();
                        return new
                        {
                            flag_name,
                            enabled = (bool)task!.Result,
                            source = "Microsoft.FeatureManagement",
                            evaluated_at = DateTimeOffset.UtcNow.ToString("o"),
                        };
                    }
                }
            }
        }
        catch { }

        return new
        {
            error = $"Flag '{flag_name}' not found",
            hint = "Register flags with FeatureFlagRegistry.RegisterFeatureFlag(name, enabled, variant) or configure Microsoft.FeatureManagement.",
        };
    }
}
