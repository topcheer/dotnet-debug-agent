using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// DI Container inspector — lists registered services, lifetimes, and details.
/// </summary>
public static class ServiceCollectionInspector
{
    [DebugTool("get_registered_services", "List all DI container service registrations with types and lifetimes")]
    public static object GetRegisteredServices(string typeFilter = "")
    {
        if (AgentContext.Services is not ServiceProvider sp)
            return new { error = "Service provider not available" };

        // Use reflection to access internal service descriptors
        var serviceProviderType = sp.GetType();
        var realizedServicesField = serviceProviderType.GetField("_realizedServices", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? serviceProviderType.GetField("_descriptors", BindingFlags.NonPublic | BindingFlags.Instance);

        // Fallback: enumerate via IServiceCollection snapshot from configuration
        // Since ServiceProvider doesn't expose descriptors, we use the engine's snapshot
        var descriptors = ServiceDescriptorCache.Descriptors;
        if (descriptors.Count == 0)
        {
            return new
            {
                error = "No service descriptors available. Ensure AddDebugAgent() is called during startup.",
                hint = "The agent captures service descriptors during DI registration.",
            };
        }

        var filtered = descriptors.AsEnumerable();
        if (!string.IsNullOrEmpty(typeFilter))
        {
            filtered = filtered.Where(d =>
                d.ServiceType?.Name?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true ||
                d.ImplementationType?.Name?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true);
        }

        var services = filtered.Select(d => new
        {
            service_type = d.ServiceType?.Name ?? "",
            service_namespace = d.ServiceType?.Namespace ?? "",
            implementation_type = d.ImplementationType?.Name ?? (d.ImplementationFactory != null ? "Factory" : d.ImplementationInstance?.GetType().Name ?? "Unknown"),
            lifetime = d.Lifetime.ToString(),
        }).ToList();

        return new { total = services.Count, services };
    }

    [DebugTool("get_service_count", "Count registered services by lifetime (Singleton/Scoped/Transient)")]
    public static object GetServiceCount()
    {
        var descriptors = ServiceDescriptorCache.Descriptors;
        if (descriptors.Count == 0)
            return new { error = "No service descriptors available" };

        var byLifetime = descriptors
            .GroupBy(d => d.Lifetime)
            .OrderBy(g => g.Key.ToString())
            .Select(g => new { lifetime = g.Key.ToString(), count = g.Count() })
            .ToList();

        var byNamespace = descriptors
            .GroupBy(d => d.ServiceType?.Namespace ?? "Unknown")
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new { @namespace = g.Key, count = g.Count() })
            .ToList();

        return new { total = descriptors.Count, by_lifetime = byLifetime, top_namespaces = byNamespace };
    }

    [DebugTool("get_service_detail", "Get registration details for a specific service type")]
    public static object GetServiceDetail([ToolParam("Service type name (partial match, case-insensitive)")] string typeName)
    {
        var descriptors = ServiceDescriptorCache.Descriptors;
        if (descriptors.Count == 0)
            return new { error = "No service descriptors available" };

        var matches = descriptors
            .Where(d => d.ServiceType?.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (matches.Count == 0)
            return new { error = $"No service type matching '{typeName}' found" };

        var details = matches.Select(d => new
        {
            service_type = d.ServiceType?.FullName ?? "",
            implementation_type = d.ImplementationType?.FullName ?? (d.ImplementationFactory != null ? "Factory delegate" : d.ImplementationInstance?.GetType().FullName ?? "Unknown"),
            lifetime = d.Lifetime.ToString(),
            is_open_generic = d.ServiceType?.IsGenericTypeDefinition ?? false,
        }).ToList();

        return new { count = details.Count, services = details };
    }

    [DebugTool("resolve_service", "Attempt to resolve a service from the DI container and report its type and status")]
    public static object ResolveService([ToolParam("Service type name (partial match)")] string typeName)
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var descriptors = ServiceDescriptorCache.Descriptors;
        var match = descriptors
            .FirstOrDefault(d => d.ServiceType?.Name?.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true)
            ?? descriptors.FirstOrDefault(d => d.ServiceType?.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) == true);

        if (match == null)
            return new { error = $"No service type matching '{typeName}'" };

        var svcType = match.ServiceType!;
        object? instance = null;
        string? error = null;

        try
        {
            instance = AgentContext.Services.GetService(svcType);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new
        {
            requested_type = svcType.FullName,
            resolved = instance != null,
            actual_type = instance?.GetType().FullName,
            lifetime = match.Lifetime.ToString(),
            error,
        };
    }
}

/// <summary>
/// Captures IServiceCollection snapshot during AddDebugAgent() so inspectors can enumerate registrations.
/// </summary>
public static class ServiceDescriptorCache
{
    public static List<ServiceDescriptor> Descriptors { get; } = new();

    public static void Capture(IServiceCollection services)
    {
        Descriptors.Clear();
        Descriptors.AddRange(services);
    }
}
