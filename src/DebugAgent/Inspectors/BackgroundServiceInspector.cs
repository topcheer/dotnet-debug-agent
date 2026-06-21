using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DebugAgents;

/// <summary>
/// Background service / hosted service inspector.
/// </summary>
public static class BackgroundServiceInspector
{
    [DebugTool("get_hosted_services", "List all registered IHostedService / BackgroundService implementations")]
    public static object GetHostedServices()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var descriptors = ServiceDescriptorCache.Descriptors
            .Where(d => typeof(IHostedService).IsAssignableFrom(d.ServiceType))
            .ToList();

        if (descriptors.Count == 0)
            return new { message = "No IHostedService registered" };

        var services = descriptors.Select(d =>
        {
            var implType = d.ImplementationType ?? d.ServiceType;
            var isBackgroundService = implType != null && IsBackgroundService(implType);

            return new
            {
                service_type = d.ServiceType?.Name ?? "",
                implementation_type = implType?.Name ?? "Unknown",
                full_name = implType?.FullName ?? "",
                is_background_service = isBackgroundService,
                lifetime = d.Lifetime.ToString(),
            };
        }).ToList();

        return new { total = services.Count, hosted_services = services };
    }

    [DebugTool("get_background_service_detail", "Get detail about a specific background service including its fields and status")]
    public static object GetBackgroundServiceDetail([ToolParam("Service type name (partial match)")] string typeName)
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var match = ServiceDescriptorCache.Descriptors
            .Where(d => typeof(IHostedService).IsAssignableFrom(d.ServiceType))
            .FirstOrDefault(d => (d.ImplementationType?.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                  (d.ServiceType?.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) ?? false));

        if (match == null)
            return new { error = $"No hosted service matching '{typeName}'" };

        var implType = match.ImplementationType ?? match.ServiceType!;

        // List public and protected instance fields/properties
        var members = new List<object>();

        foreach (var field in implType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     .Where(f => !f.Name.StartsWith("<") && !f.Name.Contains("k__BackingField")))
        {
            var fieldType = field.FieldType;
            members.Add(new
            {
                kind = "field",
                name = field.Name,
                type = fieldType.Name,
                is_readonly = field.IsInitOnly,
            });
        }

        foreach (var prop in implType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            members.Add(new
            {
                kind = "property",
                name = prop.Name,
                type = prop.PropertyType.Name,
                is_readonly = !prop.CanWrite,
            });
        }

        // Get base class hierarchy
        var hierarchy = new List<string>();
        var current = implType;
        while (current != null && current != typeof(object))
        {
            hierarchy.Add(current.Name);
            current = current.BaseType;
        }

        return new
        {
            type = implType.FullName,
            name = implType.Name,
            inheritance_chain = hierarchy,
            is_background_service = IsBackgroundService(implType),
            members,
        };
    }

    private static bool IsBackgroundService(Type type)
    {
        var current = type;
        while (current != null && current != typeof(object))
        {
            if (current.Name == "BackgroundService") return true;
            current = current.BaseType;
        }
        return false;
    }
}
