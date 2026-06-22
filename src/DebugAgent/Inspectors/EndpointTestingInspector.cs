using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DebugAgents;

/// <summary>
/// Tracks which endpoints have been tested via the testing inspector.
/// </summary>
public static class EndpointTestTracker
{
    private static readonly ConcurrentDictionary<string, TestRecord> _tested = new();

    public static void RecordTest(string method, string path, int status, bool passed)
    {
        var key = $"{method} {path}";
        _tested[key] = new TestRecord(method, path, status, passed, DateTimeOffset.UtcNow);
    }

    public static List<TestRecord> GetAll() => _tested.Values.ToList();
    public static void Clear() => _tested.Clear();
}

public record TestRecord(string Method, string Path, int Status, bool Passed, DateTimeOffset TestedAt);

/// <summary>
/// Endpoint testing inspector — make HTTP requests to own app, batch tests, coverage.
/// </summary>
public static class EndpointTestingInspector
{
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string GetBaseUrl()
    {
        var config = AgentContext.Services?.GetService<IConfiguration>();
        var urls = config?["Urls"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? Environment.GetEnvironmentVariable("DOTNET_URLS")
            ?? "http://localhost:5000";
        return urls.Split(';')[0].Trim();
    }

    [DebugTool("test_endpoint", "Make an HTTP request to the app's own endpoint. Returns status, headers, body, duration_ms.")]
    public static async Task<object> TestEndpoint(
        [ToolParam("HTTP method (GET, POST, PUT, DELETE, PATCH)")] string method,
        [ToolParam("Request path (e.g. /api/orders)")] string path,
        [ToolParam("JSON string of request headers, e.g. {\"Authorization\":\"Bearer xxx\"}")] string headers = "",
        [ToolParam("Request body (string)")] string body = "")
    {
        var baseUrl = GetBaseUrl();
        var url = baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);

            // Parse headers
            if (!string.IsNullOrEmpty(headers))
            {
                try
                {
                    var headerDict = JsonSerializer.Deserialize<Dictionary<string, string>>(headers);
                    if (headerDict != null)
                    {
                        foreach (var kv in headerDict)
                        {
                            if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                                continue; // handled separately
                            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }
                    }
                }
                catch { /* ignore bad header JSON */ }
            }

            // Add body
            if (!string.IsNullOrEmpty(body))
            {
                var contentType = "application/json";
                try
                {
                    var headerDict = string.IsNullOrEmpty(headers)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(headers);
                    if (headerDict != null)
                    {
                        foreach (var kv in headerDict)
                            if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                                contentType = kv.Value;
                    }
                }
                catch { }

                req.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            using var resp = await _client.SendAsync(req);
            sw.Stop();

            var respBody = await resp.Content.ReadAsStringAsync();
            var respHeaders = resp.Headers
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

            // Track the test
            var passed = resp.IsSuccessStatusCode;
            EndpointTestTracker.RecordTest(method.ToUpperInvariant(), path, (int)resp.StatusCode, passed);

            return new
            {
                url,
                method = method.ToUpperInvariant(),
                status = (int)resp.StatusCode,
                status_text = resp.StatusCode.ToString(),
                duration_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                response_headers = respHeaders,
                body = respBody.Length > 2000 ? respBody[..2000] + "... (truncated)" : respBody,
                body_length = respBody.Length,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new
            {
                url,
                method = method.ToUpperInvariant(),
                error = ex.Message,
                duration_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            };
        }
    }

    [DebugTool("batch_test_endpoints", "Run multiple endpoint tests with expected_status assertions. Pass tests as a JSON array.")]
    public static async Task<object> BatchTestEndpoints(
        [ToolParam("JSON array of tests: [{\"method\":\"GET\",\"path\":\"/api/orders\",\"expected_status\":200}]")] string tests)
    {
        var testList = new List<object>();
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement[]>(tests);
            if (parsed == null)
                return new { error = "Failed to parse tests JSON" };

            foreach (var t in parsed)
            {
                var method = t.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
                var path = t.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                var expectedStatus = t.TryGetProperty("expected_status", out var es) ? es.GetInt32() : 200;
                var headers = t.TryGetProperty("headers", out var h) ? h.GetRawText() : "";
                var body = t.TryGetProperty("body", out var b) ? b.GetRawText() : "";

                var result = await TestEndpoint(method, path, headers, body);

                int actualStatus = 0;
                try
                {
                    var statusProp = result.GetType().GetProperty("status");
                    if (statusProp != null)
                        actualStatus = (int)statusProp.GetValue(result)!;
                }
                catch { }

                var passed = actualStatus == expectedStatus;
                testList.Add(new
                {
                    method,
                    path,
                    expected_status = expectedStatus,
                    actual_status = actualStatus,
                    passed,
                    result,
                });
            }
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to parse tests: {ex.Message}" };
        }

        var total = testList.Count;
        var passedCount = testList.Count(t =>
        {
            var p = t.GetType().GetProperty("passed")?.GetValue(t) as bool?;
            return p == true;
        });

        return new
        {
            total,
            passed = passedCount,
            failed = total - passedCount,
            pass_rate = total > 0 ? $"{passedCount * 100.0 / total:F1}%" : "0%",
            results = testList,
        };
    }

    [DebugTool("get_endpoint_coverage", "Compare registered endpoints vs tested endpoints")]
    public static object GetEndpointCoverage()
    {
        var tested = EndpointTestTracker.GetAll();
        var testedKeys = tested.Select(t => $"{t.Method} {t.Path}").ToHashSet();

        // Get registered endpoints from the endpoint inspector's data if available
        var registeredEndpoints = new List<string>();
        try
        {
            // Try to read from the ASP.NET Core endpoint data source
            if (AgentContext.Services != null)
            {
                var endpointDataSourceType = Type.GetType("Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, Microsoft.AspNetCore.Routing")
                    ?? Type.GetType("Microsoft.AspNetCore.Routing.EndpointDataSource, Microsoft.AspNetCore.Http.Abstractions")
                    ?? Type.GetType("Microsoft.AspNetCore.Routing.EndpointDataSource, Microsoft.AspNetCore.Routing");

                if (endpointDataSourceType != null)
                {
                    // Get all registered EndpointDataSource instances
                    var sources = (System.Collections.IEnumerable?)AgentContext.Services.GetService(endpointDataSourceType);
                    // If multiple, GetServices returns enumerable
                    if (sources == null)
                    {
                        var getServicesMethod = typeof(ServiceProviderServiceExtensions)
                            .GetMethods()
                            .FirstOrDefault(m => m.Name == "GetServices")
                            ?.MakeGenericMethod(endpointDataSourceType);
                        if (getServicesMethod != null)
                            sources = (System.Collections.IEnumerable?)getServicesMethod.Invoke(null, new object[] { AgentContext.Services });
                    }

                    if (sources != null)
                    {
                        foreach (var source in sources)
                        {
                            var endpointsProp = source.GetType().GetProperty("Endpoints");
                            if (endpointsProp?.GetValue(source) is System.Collections.IEnumerable epList)
                            {
                                foreach (var ep in epList)
                                {
                                    var epType = ep.GetType();
                                    var displayNameProp = epType.GetProperty("DisplayName");
                                    var displayName = displayNameProp?.GetValue(ep)?.ToString() ?? "";

                                    // Try to get route pattern and methods
                                    var metadataProp = epType.GetProperty("Metadata");
                                    var routePattern = "";
                                    var httpMethods = new List<string>();

                                    if (metadataProp?.GetValue(ep) is System.Collections.IEnumerable metadata)
                                    {
                                        foreach (var m in metadata)
                                        {
                                            var mType = m.GetType();
                                            if (mType.Name == "RouteNameMetadata" || mType.Name.Contains("Route"))
                                            {
                                                var patternProp = mType.GetProperty("RoutePattern")
                                                    ?? mType.GetProperty("Template")
                                                    ?? mType.GetProperty("Name");
                                                if (patternProp != null)
                                                    routePattern = patternProp.GetValue(m)?.ToString() ?? routePattern;
                                            }
                                            if (mType.Name == "HttpMethodMetadata" || mType.Name.Contains("HttpMethod"))
                                            {
                                                var methodsProp = mType.GetProperty("HttpMethods");
                                                if (methodsProp?.GetValue(m) is System.Collections.Generic.IReadOnlyList<string> methods)
                                                    httpMethods.AddRange(methods);
                                            }
                                        }
                                    }

                                    registeredEndpoints.Add(displayName);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        var registeredSet = registeredEndpoints.Select(e => e.Trim()).Where(e => !string.IsNullOrEmpty(e)).ToHashSet();
        var untested = registeredSet.Except(testedKeys).ToList();

        return new
        {
            tested_endpoints = tested.Select(t => new { t.Method, t.Path, t.Status, passed = t.Passed }),
            tested_count = tested.Count,
            registered_endpoints = registeredSet,
            registered_count = registeredSet.Count,
            untested_endpoints = untested,
            coverage_pct = registeredSet.Count > 0
                ? $"{Math.Round((double)(registeredSet.Count - untested.Count) / registeredSet.Count * 100, 1)}%"
                : "N/A",
        };
    }
}
