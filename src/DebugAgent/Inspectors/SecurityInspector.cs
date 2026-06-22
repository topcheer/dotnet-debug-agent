using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Security inspector — authentication schemes, authorization policies, CORS configuration.
/// </summary>
public static class SecurityInspector
{
    [DebugTool("get_auth_config", "List registered authentication schemes (JWT bearer, cookies, OpenID, etc.)")]
    public static object GetAuthConfig()
    {
        var services = AgentContext.Services;
        if (services == null)
            return new { error = "Service provider not available" };

        var schemes = new List<object>();

        try
        {
            // AuthenticationOptions lives in Microsoft.AspNetCore.Authentication
            var authOptions = services.GetService(
                Type.GetType("Microsoft.AspNetCore.Authentication.AuthenticationOptions, Microsoft.AspNetCore.Authentication")
                ?? Type.GetType("Microsoft.AspNetCore.Authentication.AuthenticationOptions, Microsoft.AspNetCore.Authentication.Core")
                ?? Type.GetType("Microsoft.AspNetCore.Authentication.AuthenticationOptions")!);

            if (authOptions != null)
            {
                var schemeMapProp = authOptions.GetType().GetProperty("SchemeMap");
                if (schemeMapProp?.GetValue(authOptions) is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        var scheme = entry.Value;
                        var sType = scheme?.GetType();
                        schemes.Add(new
                        {
                            name = sType?.GetProperty("Name")?.GetValue(scheme)?.ToString() ?? entry.Key?.ToString() ?? "",
                            handler_type = sType?.GetProperty("HandlerType")?.GetValue(scheme)?.ToString() ?? "",
                            display_name = sType?.GetProperty("DisplayName")?.GetValue(scheme)?.ToString() ?? "",
                        });
                    }
                }
            }
        }
        catch { /* Authentication not configured */ }

        // Also try reading from IConfiguration
        var configSchemes = new Dictionary<string, object?>();
        try
        {
            var config = services.GetService<IConfiguration>();
            var authSection = config?.GetSection("Authentication");
            if (authSection != null)
            {
                foreach (var child in authSection.GetChildren())
                    configSchemes[child.Key] = child.Value;
            }
        }
        catch { }

        return new
        {
            registered_schemes = schemes,
            scheme_count = schemes.Count,
            config_authentication = configSchemes.Count > 0 ? configSchemes : null,
        };
    }

    [DebugTool("get_authorization_policies", "List authorization policies and their requirements")]
    public static object GetAuthorizationPolicies()
    {
        var services = AgentContext.Services;
        if (services == null)
            return new { error = "Service provider not available" };

        var policies = new List<object>();

        try
        {
            // AuthorizationOptions is in Microsoft.AspNetCore.Authorization
            var authzOptionsType = Type.GetType("Microsoft.AspNetCore.Authorization.AuthorizationOptions, Microsoft.AspNetCore.Authorization")
                ?? Type.GetType("Microsoft.AspNetCore.Authorization.AuthorizationOptions, Microsoft.AspNetCore.Authorization.Core")
                ?? Type.GetType("Microsoft.AspNetCore.Authorization.AuthorizationOptions")!;

            if (authzOptionsType != null)
            {
                var authzOptions = services.GetService(authzOptionsType);
                if (authzOptions != null)
                {
                    // Access the internal policy map
                    var policyMapField = authzOptionsType.GetField("_policies", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? authzOptionsType.GetField("_policyMap", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? authzOptionsType.GetField("_policies", BindingFlags.NonPublic | BindingFlags.Instance);

                    var policyMapProp = authzOptionsType.GetProperty("PolicyMap", BindingFlags.NonPublic | BindingFlags.Instance);

                    System.Collections.IDictionary? policyMap = null;
                    if (policyMapField != null)
                        policyMap = policyMapField.GetValue(authzOptions) as System.Collections.IDictionary;
                    else if (policyMapProp != null)
                        policyMap = policyMapProp.GetValue(authzOptions) as System.Collections.IDictionary;

                    if (policyMap != null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in policyMap)
                        {
                            var policy = entry.Value;
                            var pType = policy?.GetType();
                            var requirements = pType?.GetProperty("Requirements")?.GetValue(policy) as System.Collections.IEnumerable;
                            var reqList = new List<string>();
                            if (requirements != null)
                            {
                                foreach (var req in requirements)
                                    reqList.Add(req?.GetType().Name ?? "Unknown");
                            }
                            policies.Add(new
                            {
                                name = entry.Key?.ToString() ?? "",
                                requirements = reqList,
                            });
                        }
                    }
                }
            }
        }
        catch { /* Authorization not configured */ }

        return new
        {
            policies,
            count = policies.Count,
        };
    }

    [DebugTool("get_cors_config", "Show CORS configuration (allowed origins, methods, headers, credentials)")]
    public static object GetCorsConfig()
    {
        var services = AgentContext.Services;
        if (services == null)
            return new { error = "Service provider not available" };

        try
        {
            var corsOptionsType = Type.GetType("Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions, Microsoft.AspNetCore.Cors")
                ?? Type.GetType("Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions, Microsoft.AspNetCore.Cors.Infrastructure")!;

            if (corsOptionsType == null)
                return new { error = "CorsOptions type not found (CORS not referenced)" };

            var corsOptions = services.GetService(corsOptionsType);
            if (corsOptions == null)
                return new { error = "CORS not configured. Call AddCors() in startup." };

            // Access the policy map
            var policyMapField = corsOptionsType.GetField("_policies", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? corsOptionsType.GetField("_policyMap", BindingFlags.NonPublic | BindingFlags.Instance);
            var policyMapProp = corsOptionsType.GetProperty("PolicyMap", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? corsOptionsType.GetProperty("DefaultPolicyName", BindingFlags.Public | BindingFlags.Instance);

            var result = new Dictionary<string, object?>();
            result["default_policy_name"] = corsOptionsType.GetProperty("DefaultPolicyName")?.GetValue(corsOptions);

            // Try to get default policy
            var getDefaultPolicy = corsOptionsType.GetMethod("GetPolicy", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getDefaultPolicy != null)
            {
                try
                {
                    var defaultPolicy = getDefaultPolicy.Invoke(corsOptions, new object?[] { null });
                    if (defaultPolicy != null)
                    {
                        var pType = defaultPolicy.GetType();
                        var originsProp = pType.GetProperty("Origins");
                        var methodsProp = pType.GetProperty("Methods");
                        var headersProp = pType.GetProperty("Headers");
                        var allowAnyOriginProp = pType.GetProperty("AllowAnyOrigin");
                        var allowAnyMethodProp = pType.GetProperty("AllowAnyMethod");
                        var allowAnyHeaderProp = pType.GetProperty("AllowAnyHeader");
                        var allowCredentialsProp = pType.GetProperty("AllowCredentials");
                        var allowAnyOrigin = allowAnyOriginProp?.GetValue(defaultPolicy) as bool?;
                        var allowAnyMethod = allowAnyMethodProp?.GetValue(defaultPolicy) as bool?;
                        var allowAnyHeader = allowAnyHeaderProp?.GetValue(defaultPolicy) as bool?;
                        var allowCredentials = allowCredentialsProp?.GetValue(defaultPolicy) as bool?;

                        var origins = originsProp?.GetValue(defaultPolicy) as System.Collections.Generic.HashSet<string>;
                        var methods = methodsProp?.GetValue(defaultPolicy) as System.Collections.Generic.HashSet<string>;
                        var headers = headersProp?.GetValue(defaultPolicy) as System.Collections.Generic.HashSet<string>;

                        result["default_policy"] = new
                        {
                            allow_any_origin = allowAnyOrigin,
                            allow_any_method = allowAnyMethod,
                            allow_any_header = allowAnyHeader,
                            allow_credentials = allowCredentials,
                            allowed_origins = origins != null ? string.Join(", ", origins) : "",
                            allowed_methods = methods != null ? string.Join(", ", methods) : "",
                            allowed_headers = headers != null ? string.Join(", ", headers) : "",
                        };
                    }
                }
                catch { }
            }

            return result;
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to read CORS config: {ex.Message}" };
        }
    }
}
