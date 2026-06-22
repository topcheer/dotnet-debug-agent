using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Redis inspector — server info and connection pool stats from IConnectionMultiplexer.
/// Uses reflection to avoid a hard dependency on StackExchange.Redis.
/// </summary>
public static class RedisInspector
{
    private static Type? GetConnectionMultiplexerType()
    {
        try
        {
            return Type.GetType("StackExchange.Redis.IConnectionMultiplexer, StackExchange.Redis")
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "StackExchange.Redis")
                    ?.GetType("StackExchange.Redis.IConnectionMultiplexer");
        }
        catch { return null; }
    }

    private static object? GetMultiplexer()
    {
        var services = AgentContext.Services;
        if (services == null) return null;
        var type = GetConnectionMultiplexerType();
        if (type == null) return null;
        try { return services.GetService(type); }
        catch { return null; }
    }

    [DebugTool("get_redis_info", "Get Redis server info (version, connected clients, memory, uptime) if IConnectionMultiplexer is registered")]
    public static object GetRedisInfo()
    {
        var muxer = GetMultiplexer();
        if (muxer == null)
            return new
            {
                available = false,
                error = "IConnectionMultiplexer not registered. Add StackExchange.Redis and register IConnectionMultiplexer in DI.",
            };

        try
        {
            var muxerType = muxer.GetType();

            // Get the raw configuration string
            string? config = null;
            try
            {
                var configProp = muxerType.GetProperty("Configuration");
                config = configProp?.GetValue(muxer)?.ToString();
            }
            catch { }

            // Database count is available via GetDatabase but requires additional API calls.
            // For pool stats, we focus on server connections.

            // Try to get server info
            var serverInfo = new Dictionary<string, object?>();
            try
            {
                // Get servers
                var getServers = muxerType.GetMethod("GetServers");
                if (getServers != null)
                {
                    var servers = getServers.Invoke(muxer, null) as System.Collections.IEnumerable;
                    if (servers != null)
                    {
                        var serverList = new List<object>();
                        foreach (var server in servers)
                        {
                            var sType = server.GetType();
                            var endpointProp = sType.GetProperty("EndPoint");
                            var isConnectedProp = sType.GetProperty("IsConnected");

                            // Try to get server info
                            var infoResult = new Dictionary<string, object?>();
                            try
                                                       {
                                // Call Info() synchronously via reflection
                                var infoMethod = sType.GetMethods()
                                    .FirstOrDefault(m => m.Name == "Info" && m.GetParameters().Length == 0);
                                if (infoMethod != null)
                                {
                                    var infoTask = infoMethod.Invoke(server, null) as dynamic;
                                    if (infoTask != null)
                                    {
                                        infoTask.Wait();
                                        var infoGroups = infoTask.Result;
                                        foreach (var group in (System.Collections.IEnumerable)infoGroups)
                                        {
                                            var groupType = group.GetType();
                                            var catProp = groupType.GetProperty("Key");
                                            var valsProp = groupType.GetProperty("Value");
                                            var cat = catProp?.GetValue(group)?.ToString() ?? "";
                                            var dict = valsProp?.GetValue(group) as System.Collections.IEnumerable;
                                            if (dict != null)
                                            {
                                                var infoDict = new Dictionary<string, string>();
                                                foreach (var kv in dict)
                                                {
                                                    var kvType = kv.GetType();
                                                    var keyProp = kvType.GetProperty("Key");
                                                    var valProp = kvType.GetProperty("Value");
                                                    infoDict[keyProp?.GetValue(kv)?.ToString() ?? ""] = valProp?.GetValue(kv)?.ToString() ?? "";
                                                }
                                                infoResult[cat] = infoDict;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            serverList.Add(new
                            {
                                endpoint = endpointProp?.GetValue(server)?.ToString() ?? "",
                                is_connected = isConnectedProp?.GetValue(server) as bool? ?? false,
                                info = infoResult.Count > 0 ? infoResult : null,
                            });
                        }
                        serverInfo["servers"] = serverList;
                    }
                }
            }
            catch { }

            // Extract key server info fields
            var extracted = new Dictionary<string, object?>();
            try
            {
                if (serverInfo.TryGetValue("servers", out var serversObj) && serversObj is System.Collections.IEnumerable servers)
                {
                    foreach (var s in servers)
                    {
                        dynamic srv = s;
                        if (srv.info is System.Collections.IDictionary infoDict)
                        {
                            if (infoDict.Contains("Server"))
                            {
                                var serverSection = infoDict["Server"] as System.Collections.IDictionary;
                                if (serverSection != null)
                                {
                                    extracted["redis_version"] = serverSection["redis_version"]?.ToString();
                                    extracted["uptime_in_days"] = serverSection["uptime_in_days"]?.ToString();
                                }
                            }
                            if (infoDict.Contains("Clients"))
                            {
                                var clientsSection = infoDict["Clients"] as System.Collections.IDictionary;
                                if (clientsSection != null)
                                    extracted["connected_clients"] = clientsSection["connected_clients"]?.ToString();
                            }
                            if (infoDict.Contains("Memory"))
                            {
                                var memSection = infoDict["Memory"] as System.Collections.IDictionary;
                                if (memSection != null)
                                {
                                    extracted["used_memory_human"] = memSection["used_memory_human"]?.ToString();
                                    extracted["used_memory_peak_human"] = memSection["used_memory_peak_human"]?.ToString();
                                }
                            }
                        }
                        break; // Just first server
                    }
                }
            }
            catch { }

            return new
            {
                available = true,
                configuration = config,
                key_info = extracted.Count > 0 ? extracted : null,
                servers = serverInfo.GetValueOrDefault("servers"),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to get Redis info: {ex.Message}" };
        }
    }

    [DebugTool("get_redis_pool_stats", "Get Redis connection pool stats from IConnectionMultiplexer")]
    public static object GetRedisPoolStats()
    {
        var muxer = GetMultiplexer();
        if (muxer == null)
            return new
            {
                available = false,
                error = "IConnectionMultiplexer not registered.",
            };

        try
        {
            var muxerType = muxer.GetType();
            var stats = new Dictionary<string, object?>();

            // Try to get connection pool stats via reflection
            try
            {
                // GetServer count
                var getServers = muxerType.GetMethod("GetServers");
                if (getServers != null)
                {
                    var servers = getServers.Invoke(muxer, null) as System.Collections.IEnumerable;
                    var count = 0;
                    if (servers != null)
                        foreach (var s in servers)
                            count++;
                    stats["server_count"] = count;
                }
            }
            catch { }

            // Try to get the raw configuration for pool info
            try
            {
                var config = muxerType.GetProperty("Configuration")?.GetValue(muxer)?.ToString();
                stats["configuration"] = config;
            }
            catch { }

            // Try to get IsConnected status
            try
            {
                var isConnectedProp = muxerType.GetProperty("IsConnected");
                stats["is_connected"] = isConnectedProp?.GetValue(muxer);
            }
            catch { }

            // Try to get connection count from the multiplexer
            try
            {
                // IConnectionMultiplexer might have internal counter info
                var counterProp = muxerType.GetProperty("OperationCount");
                if (counterProp != null)
                    stats["operation_count"] = counterProp.GetValue(muxer);
            }
            catch { }

            // Try to get status summary string
            try
            {
                var getStatus = muxerType.GetMethod("GetStatus");
                if (getStatus != null)
                {
                    var status = getStatus.Invoke(muxer, Array.Empty<object>())?.ToString();
                    stats["status_summary"] = status;
                }
            }
            catch { }

            // Try to get server connections
            try
            {
                var getServers = muxerType.GetMethod("GetServers");
                if (getServers != null)
                {
                    var servers = getServers.Invoke(muxer, null) as System.Collections.IEnumerable;
                    if (servers != null)
                    {
                        var connInfo = new List<object>();
                        foreach (var server in servers)
                        {
                            var sType = server.GetType();
                            connInfo.Add(new
                            {
                                endpoint = sType.GetProperty("EndPoint")?.GetValue(server)?.ToString() ?? "",
                                is_connected = sType.GetProperty("IsConnected")?.GetValue(server) as bool? ?? false,
                            });
                        }
                        stats["server_connections"] = connInfo;
                    }
                }
            }
            catch { }

            return new
            {
                available = true,
                stats,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to get Redis pool stats: {ex.Message}" };
        }
    }
}
