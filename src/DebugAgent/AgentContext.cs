using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Global static context providing access to the ASP.NET Core service provider.
/// Set during MapDebugAgent(), used by framework-level inspectors.
/// </summary>
public static class AgentContext
{
    /// <summary>The root service provider, available after MapDebugAgent().</summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>The WebApplication instance, available after MapDebugAgent().</summary>
    public static WebApplication? App { get; private set; }

    internal static void Initialize(WebApplication app)
    {
        App = app;
        Services = app.Services;
    }
}
