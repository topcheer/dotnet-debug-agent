using System.Reflection;
using System.Text.Json.Serialization;

namespace DebugAgent;

/// <summary>
/// Marks a method as a debug tool discoverable by the agent.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class DebugToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }
    public DebugToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

/// <summary>
/// Describes a tool parameter for schema generation.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class ToolParamAttribute : Attribute
{
    public string Description { get; }
    public bool Required { get; set; } = true;
    public ToolParamAttribute(string description)
    {
        Description = description;
    }
}

public class ToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Func<Dictionary<string, object>, object> Func { get; set; } = _ => new();
    public Dictionary<string, ToolParamInfo> Params { get; set; } = new();
}

public class ToolParamInfo
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool Required { get; set; } = true;
}

/// <summary>
/// Global registry for debug tools. Tools are auto-discovered via [DebugTool] attribute.
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new();

    public void Register(ToolDefinition tool) => _tools[tool.Name] = tool;

    public ToolDefinition? Get(string name) => _tools.GetValueOrDefault(name);

    public List<object> AllSchemas() => _tools.Values.Select(t => (object)new
    {
        type = "function",
        function = new
        {
            name = t.Name,
            description = t.Description,
            parameters = new
            {
                type = "object",
                properties = t.Params.ToDictionary(p => p.Key, p => (object)new { type = p.Value.Type, description = p.Value.Description }),
                required = t.Params.Where(p => p.Value.Required).Select(p => p.Key).ToList(),
            },
        },
    }).ToList();

    public object Execute(string name, Dictionary<string, object> args)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return new { error = $"Unknown tool: {name}" };
        try
        {
            return tool.Func(args);
        }
        catch (Exception e)
        {
            return new { error = e.Message };
        }
    }

    public List<string> Names() => _tools.Keys.ToList();

    /// <summary>
    /// Auto-discover [DebugTool] methods in the calling assembly.
    /// </summary>
    public void DiscoverTools(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetCallingAssembly();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<DebugToolAttribute>();
                if (attr == null) continue;

                var parameters = new Dictionary<string, ToolParamInfo>();
                foreach (var p in method.GetParameters())
                {
                    var pAttr = p.GetCustomAttribute<ToolParamAttribute>();
                    parameters[p.Name ?? ""] = new ToolParamInfo
                    {
                        Type = MapTypeToSchema(p.ParameterType),
                        Description = pAttr?.Description ?? "",
                        Required = pAttr?.Required ?? !p.HasDefaultValue,
                    };
                }

                // Create delegate - handles both static and instance methods
                Func<Dictionary<string, object>, object> func = args =>
                {
                    object? instance = method.IsStatic ? null : Activator.CreateInstance(type);
                    var callArgs = method.GetParameters()
                        .Select(p => args.TryGetValue(p.Name ?? "", out var val) ? Convert.ChangeType(val, p.ParameterType) : p.DefaultValue)
                        .ToArray();
                    return method.Invoke(instance, callArgs) ?? new { };
                };

                Register(new ToolDefinition
                {
                    Name = attr.Name,
                    Description = attr.Description,
                    Func = func,
                    Params = parameters,
                });
            }
        }
    }

    private static string MapTypeToSchema(Type t) => t switch
    {
        _ when t == typeof(int) || t == typeof(long) => "integer",
        _ when t == typeof(double) || t == typeof(float) || t == typeof(decimal) => "number",
        _ when t == typeof(bool) => "boolean",
        _ when t == typeof(string) => "string",
        _ => "string",
    };
}

/// <summary>
/// Global singleton registry.
/// </summary>
public static class GlobalRegistry
{
    public static ToolRegistry Instance { get; } = new();
}
