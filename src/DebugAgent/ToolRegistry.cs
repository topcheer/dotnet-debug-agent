using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebugAgents;

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
    public bool IsAsync { get; set; }
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
/// Supports both sync and async methods (returning Task or Task{T}).
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
            var result = tool.Func(args);

            // Unwrap Task results for async tools
            if (tool.IsAsync && result is Task task)
            {
                task.Wait();
                var resultProp = task.GetType().GetProperty("Result");
                return resultProp?.GetValue(task) ?? new { };
            }

            return result;
        }
        catch (Exception e)
        {
            return new { error = e is TargetInvocationException tie ? tie.InnerException?.Message ?? e.Message : e.Message };
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

                var isAsync = typeof(Task).IsAssignableFrom(method.ReturnType);
                var methodRef = method;
                var typeRef = type;

                // Create delegate - handles both static and instance methods, sync and async
                Func<Dictionary<string, object>, object> func = args =>
                {
                    object? instance = methodRef.IsStatic ? null : Activator.CreateInstance(typeRef);
                    var callArgs = methodRef.GetParameters()
                        .Select(p => args.TryGetValue(p.Name ?? "", out var val) ? ConvertValue(val, p.ParameterType) : p.DefaultValue)
                        .ToArray();
                    return methodRef.Invoke(instance, callArgs) ?? new { };
                };

                Register(new ToolDefinition
                {
                    Name = attr.Name,
                    Description = attr.Description,
                    Func = func,
                    IsAsync = isAsync,
                    Params = parameters,
                });
            }
        }
    }

    private static object? ConvertValue(object val, Type targetType)
    {
        if (val == null) return null;
        if (targetType == typeof(string) && val is JsonElement je)
            return je.GetString();
        if (targetType == typeof(int) || targetType == typeof(long))
        {
            if (val is JsonElement jeInt) return jeInt.GetInt32();
            return Convert.ChangeType(val, targetType);
        }
        if (targetType == typeof(double) || targetType == typeof(float) || targetType == typeof(decimal))
        {
            if (val is JsonElement jeNum) return jeNum.GetDouble();
            return Convert.ChangeType(val, targetType);
        }
        if (targetType == typeof(bool))
        {
            if (val is JsonElement jeBool) return jeBool.GetBoolean();
            return Convert.ChangeType(val, targetType);
        }
        if (val is JsonElement jeStr && targetType == typeof(string))
            return jeStr.GetString();
        return Convert.ChangeType(val, targetType);
    }

    private static string MapTypeToSchema(Type t) => t switch
    {
        _ when t == typeof(int) || t == typeof(long) => "integer",
        _ when t == typeof(double) || t == typeof(float) || t == typeof(decimal) => "number",
        _ when t == typeof(bool) => "boolean",
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
