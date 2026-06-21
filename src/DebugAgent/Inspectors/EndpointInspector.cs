using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// HTTP endpoint routing inspector — lists all registered routes.
/// </summary>
public static class EndpointInspector
{
    [DebugTool("get_endpoints", "List all registered HTTP endpoints with method, path, and handler")]
    public static object GetEndpoints()
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var endpointDataSource = AgentContext.Services.GetService<EndpointDataSource>();
        if (endpointDataSource == null)
            return new { error = "EndpointDataSource not available" };

        var endpoints = endpointDataSource.Endpoints.OfType<RouteEndpoint>().Select(e =>
        {
            var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ToList() ?? new List<string>();
            var displayName = e.DisplayName ?? e.RoutePattern.RawText ?? "";
            var handlerAttr = e.Metadata.FirstOrDefault(m => m.GetType().Name.Contains("MethodInfo"));

            return new
            {
                pattern = e.RoutePattern.RawText ?? "",
                methods = methods.Count > 0 ? string.Join(",", methods) : "ANY",
                display_name = displayName,
                order = e.Order,
                metadata_count = e.Metadata.Count,
                handler_type = handlerAttr?.GetType().Name ?? "",
            };
        }).ToList();

        return new { total = endpoints.Count, endpoints };
    }

    [DebugTool("get_endpoint_detail", "Get metadata details for endpoints matching a path pattern")]
    public static object GetEndpointDetail([ToolParam("Path pattern to search (partial match)")] string pattern)
    {
        if (AgentContext.Services == null)
            return new { error = "Service provider not available" };

        var endpointDataSource = AgentContext.Services.GetService<EndpointDataSource>();
        if (endpointDataSource == null)
            return new { error = "EndpointDataSource not available" };

        var matches = endpointDataSource.Endpoints.OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .Select(e =>
            {
                var metadata = e.Metadata.Select(m => new
                {
                    type = m.GetType().Name,
                    @namespace = m.GetType().Namespace ?? "",
                }).ToList();

                var httpMethods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ToList() ?? new List<string>();

                return new
                {
                    pattern = e.RoutePattern.RawText,
                    methods = httpMethods.Count > 0 ? string.Join(",", httpMethods) : "ANY",
                    display_name = e.DisplayName ?? "",
                    order = e.Order,
                    metadata,
                };
            })
            .ToList();

        if (matches.Count == 0)
            return new { error = $"No endpoints matching '{pattern}'" };

        return new { count = matches.Count, endpoints = matches };
    }
}
