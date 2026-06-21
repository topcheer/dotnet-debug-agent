using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// EF Core inspector — uses reflection to inspect DbContext registrations.
/// Works without a hard EF Core dependency.
/// </summary>
public static class EfCoreInspector
{
    private static Type? _dbContextType;
    private static Type? GetDbContextType()
    {
        if (_dbContextType != null) return _dbContextType;
        // Load Microsoft.EntityFrameworkCore DbContext from loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            _dbContextType = asm.GetType("Microsoft.EntityFrameworkCore.DbContext");
            if (_dbContextType != null) return _dbContextType;
        }
        return null;
    }

    [DebugTool("get_db_contexts", "List all registered DbContext types with provider and connection info")]
    public static object GetDbContexts()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var dbContextType = GetDbContextType();
        if (dbContextType == null)
            return new { message = "EF Core not loaded in this application" };

        var descriptors = ServiceDescriptorCache.Descriptors
            .Where(d => IsDbContext(d.ServiceType, dbContextType))
            .ToList();

        if (descriptors.Count == 0)
            return new { message = "No DbContext registered in DI container" };

        var contexts = descriptors.Select(d =>
        {
            var contextType = d.ServiceType!;
            var connectionString = TryGetConnectionString(contextType);

            return new
            {
                type = contextType.FullName ?? contextType.Name,
                simple_name = contextType.Name,
                lifetime = d.Lifetime.ToString(),
                connection_string = connectionString != null ? MaskConnectionString(connectionString) : "(not found)",
            };
        }).ToList();

        return new { total = contexts.Count, db_contexts = contexts };
    }

    [DebugTool("get_db_context_info", "Get detailed info about a specific DbContext: entity types, provider, connection")]
    public static object GetDbContextInfo([ToolParam("DbContext type name (partial match)")] string typeName)
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var dbContextType = GetDbContextType();
        if (dbContextType == null)
            return new { error = "EF Core not loaded" };

        var contextType = ServiceDescriptorCache.Descriptors
            .Select(d => d.ServiceType)
            .Where(t => IsDbContext(t, dbContextType))
            .FirstOrDefault(t => t.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase));

        if (contextType == null)
            return new { error = $"No DbContext matching '{typeName}' found" };

        using var scope = AgentContext.Services.CreateScope();
        object? dbContext = null;
        try { dbContext = scope.ServiceProvider.GetService(contextType); }
        catch (Exception ex) { return new { error = $"Failed to resolve DbContext: {ex.Message}" }; }

        if (dbContext == null)
            return new { error = $"Failed to resolve DbContext '{contextType.Name}'" };

        // Get provider name via reflection: context.Database.ProviderName
        string provider = "Unknown";
        string? connStr = null;
        List<string> entityTypes = new();

        try
        {
            var dbProperty = contextType.GetProperty("Database");
            var dbFacade = dbProperty?.GetValue(dbContext);
            if (dbFacade != null)
            {
                var providerProp = dbFacade.GetType().GetProperty("ProviderName");
                provider = providerProp?.GetValue(dbFacade)?.ToString() ?? "Unknown";

                var getConnMethod = dbFacade.GetType().GetMethod("GetConnectionString");
                connStr = getConnMethod?.Invoke(dbFacade, null)?.ToString();
            }
        }
        catch { }

        // Get entity types from the model
        try
        {
            var modelProperty = contextType.GetProperty("Model");
            var model = modelProperty?.GetValue(dbContext);
            if (model != null)
            {
                var getEntitiesMethod = model.GetType().GetMethod("GetEntityTypes");
                var entities = getEntitiesMethod?.Invoke(model, null) as System.Collections.IEnumerable;
                if (entities != null)
                {
                    foreach (var entity in entities)
                    {
                        var clrTypeProp = entity.GetType().GetProperty("ClrType");
                        var clrType = clrTypeProp?.GetValue(entity) as Type;
                        if (clrType != null) entityTypes.Add(clrType.Name);
                    }
                }
            }
        }
        catch { }

        return new
        {
            type = contextType.FullName,
            provider,
            connection_string = connStr != null ? MaskConnectionString(connStr) : "(not available)",
            entity_types = entityTypes,
        };
    }

    [DebugTool("get_db_migrations", "List applied and pending database migrations for all DbContexts")]
    public static object GetDbMigrations()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var dbContextType = GetDbContextType();
        if (dbContextType == null)
            return new { error = "EF Core not loaded" };

        var contextTypes = ServiceDescriptorCache.Descriptors
            .Select(d => d.ServiceType)
            .Where(t => IsDbContext(t, dbContextType))
            .Distinct()
            .ToList();

        if (contextTypes.Count == 0)
            return new { message = "No DbContext registered" };

        using var scope = AgentContext.Services.CreateScope();
        var results = new List<object>();

        foreach (var ctxType in contextTypes)
        {
            var ctx = scope.ServiceProvider.GetService(ctxType);
            if (ctx == null) continue;

            try
            {
                var dbProperty = ctxType.GetProperty("Database");
                var dbFacade = dbProperty?.GetValue(ctx);
                if (dbFacade == null) continue;

                // GetAppliedMigrationsAsync
                var appliedMethod = dbFacade.GetType().GetMethod("GetAppliedMigrationsAsync");
                var appliedTask = appliedMethod?.Invoke(dbFacade, null) as Task<System.Collections.Generic.IEnumerable<string>>;
                var applied = appliedTask != null ? appliedTask.Result.ToList() : new List<string>();

                // GetPendingMigrationsAsync
                var pendingMethod = dbFacade.GetType().GetMethod("GetPendingMigrationsAsync");
                var pendingTask = pendingMethod?.Invoke(dbFacade, null) as Task<System.Collections.Generic.IEnumerable<string>>;
                var pending = pendingTask != null ? pendingTask.Result.ToList() : new List<string>();

                results.Add(new
                {
                    context = ctxType.Name,
                    applied,
                    applied_count = applied.Count,
                    pending,
                    pending_count = pending.Count,
                });
            }
            catch (Exception ex)
            {
                results.Add(new { context = ctxType.Name, error = ex.InnerException?.Message ?? ex.Message });
            }
        }

        return new { contexts = results };
    }

    [DebugTool("get_db_connection_stats", "Get database connection statistics for DbContexts")]
    public static object GetDbConnectionStats()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var dbContextType = GetDbContextType();
        if (dbContextType == null)
            return new { error = "EF Core not loaded" };

        var contextTypes = ServiceDescriptorCache.Descriptors
            .Select(d => d.ServiceType)
            .Where(t => IsDbContext(t, dbContextType))
            .Distinct()
            .ToList();

        if (contextTypes.Count == 0)
            return new { message = "No DbContext registered" };

        using var scope = AgentContext.Services.CreateScope();
        var stats = new List<object>();

        foreach (var ctxType in contextTypes)
        {
            var ctx = scope.ServiceProvider.GetService(ctxType);
            if (ctx == null) continue;

            try
            {
                var dbProperty = ctxType.GetProperty("Database");
                var dbFacade = dbProperty?.GetValue(ctx);
                if (dbFacade == null) continue;

                var providerProp = dbFacade.GetType().GetProperty("ProviderName");
                var provider = providerProp?.GetValue(dbFacade)?.ToString() ?? "Unknown";

                // GetDbConnection
                var getConnMethod = dbFacade.GetType().GetMethod("GetDbConnection");
                var conn = getConnMethod?.Invoke(dbFacade, null);
                var connState = conn?.GetType().GetProperty("State")?.GetValue(conn)?.ToString() ?? "Unknown";
                var connDb = conn?.GetType().GetProperty("Database")?.GetValue(conn)?.ToString() ?? "";
                var connStr = conn?.GetType().GetProperty("ConnectionString")?.GetValue(conn)?.ToString() ?? "";

                stats.Add(new
                {
                    context = ctxType.Name,
                    provider,
                    connection_state = connState,
                    database = connDb,
                    connection_string = MaskConnectionString(connStr),
                });
            }
            catch (Exception ex)
            {
                stats.Add(new { context = ctxType.Name, error = ex.InnerException?.Message ?? ex.Message });
            }
        }

        return new { connections = stats };
    }

    private static bool IsDbContext(Type? type, Type dbContextType)
    {
        if (type == null) return false;
        return type == dbContextType || type.IsSubclassOf(dbContextType);
    }

    private static string? TryGetConnectionString(Type contextType)
    {
        var config = AgentContext.Services?.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (config == null) return null;

        var name = contextType.Name.Replace("Context", "");
        return config[$"ConnectionStrings:{contextType.Name}"] ??
               config[$"ConnectionStrings:{name}"] ??
               config.GetConnectionString(contextType.Name) ??
               config.GetConnectionString(name);
    }

    private static string MaskConnectionString(string connStr)
    {
        if (string.IsNullOrEmpty(connStr)) return "";
        var patterns = new[] { "Password=", "Pwd=", "password=" };
        var result = connStr;
        foreach (var p in patterns)
        {
            var idx = result.IndexOf(p, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx + p.Length;
                var end = result.IndexOf(';', start);
                if (end < 0) end = result.Length;
                result = result[..start] + "***" + result[end..];
            }
        }
        return result;
    }
}
