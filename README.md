# .NET Debug Agent

An AI-powered runtime debugging agent that embeds directly into your ASP.NET Core application. Add one package reference, configure an LLM key, and chat with your live app at `/agent` to inspect DI services, configuration, health checks, logs, EF Core, cache, endpoints, memory, GC, and much more.

[![NuGet](https://img.shields.io/nuget/v/DebugAgent.svg)](https://www.nuget.org/packages/DebugAgent/)
![Tools](https://img.shields.io/badge/tools-70-blue)
![Inspectors](https://img.shields.io/badge/inspectors-24-green)
![.NET](https://img.shields.io/badge/.NET-8.0%2B-512BD4)
![NuGet](https://img.shields.io/badge/NuGet-DebugAgent-004880)

## Version Support

| .NET Version | Type | Status |
|-------------|------|--------|
| 6.0 (EOL)   | LTS  | Not supported |
| 7.0 (EOL)   | STS  | Not supported |
| 8.0         | LTS  | Minimum supported |
| 9.0         | STS  | Supported |
| 10.0        | STS  | Supported |

> Multi-targets `net8.0;net9.0;net10.0`. Uses C# 12 features (primary constructors, raw string literals). `RollForward: LatestMajor` ensures compatibility with future versions.

## Quick Start

### 1. Add the agent to your project

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <ProjectReference Include="path/to/DebugAgent.csproj" />
  </ItemGroup>
</Project>
```

### 2. Register and map the agent

```csharp
// Program.cs
using DebugAgents;

var builder = WebApplication.CreateBuilder(args);

// One line to register services
builder.Services.AddDebugAgent();

var app = builder.Build();

// One line to map endpoints
app.MapDebugAgent();

app.Run();
```

### 3. Set your LLM API key

```bash
# OpenAI
export OPENAI_API_KEY="sk-..."

# Or any OpenAI-compatible endpoint
export DEBUG_AGENT__LLM__API_KEY="your-key"
export DEBUG_AGENT__LLM__BASE_URL="https://api.openai.com/v1"
export DEBUG_AGENT__LLM__MODEL="gpt-4o"
```

### 4. Run and chat

```bash
dotnet run
# Open http://localhost:5000/agent
```

## Inspectors & Tools (70 tools across 24 inspectors)

### DI Container (ServiceCollectionInspector)

Inspect the `Microsoft.Extensions.DependencyInjection` container — the heart of every ASP.NET Core app.

| Tool | Description |
|------|-------------|
| `get_registered_services` | List all DI registrations with service type, implementation, and lifetime |
| `get_service_count` | Count services by lifetime (Singleton/Scoped/Transient) and namespace |
| `get_service_detail` | Get registration details for a specific service type |
| `resolve_service` | Attempt to resolve a service and report its actual runtime type |

### Configuration (ConfigurationInspector)

Inspect `Microsoft.Extensions.Configuration` — the unified config system (appsettings.json, env vars, CLI, etc.).

| Tool | Description |
|------|-------------|
| `get_configuration_sources` | List all configuration providers (JSON, env, CLI, etc.) |
| `get_configuration_keys` | List all config keys and values (secrets automatically masked) |
| `get_configuration_value` | Get a specific key's value and which provider it comes from |

### HTTP Endpoints (EndpointInspector)

Inspect ASP.NET Core endpoint routing — all registered routes from controllers and minimal APIs.

| Tool | Description |
|------|-------------|
| `get_endpoints` | List all registered HTTP endpoints (path, method, display name) |
| `get_endpoint_detail` | Get metadata for endpoints matching a path pattern |

### Health Checks (HealthCheckInspector)

Run and inspect `Microsoft.Extensions.Diagnostics.HealthChecks`.

| Tool | Description |
|------|-------------|
| `get_health_status` | Run all health checks and return per-component status with timing |
| `get_registered_health_checks` | List all registered health checks without executing them |

### Logging (LoggingInspector)

Query recent application logs captured by the built-in in-memory ring buffer (500 entries).

| Tool | Description |
|------|-------------|
| `get_recent_logs` | Get recent log entries (filter by level, configurable limit) |
| `search_logs` | Search log messages by keyword |
| `get_log_stats` | Log statistics: count by level, top categories, time range |
| `get_log_levels` | Show configured log levels per category |

### EF Core (EfCoreInspector)

Inspect Entity Framework Core `DbContext` registrations, entities, providers, and migrations. Uses reflection — no hard EF Core dependency.

| Tool | Description |
|------|-------------|
| `get_db_contexts` | List all registered DbContext types |
| `get_db_context_info` | Get entity types, provider, and connection for a DbContext |
| `get_db_migrations` | List applied and pending migrations for all DbContexts |
| `get_db_connection_stats` | Get connection state, provider, and database info |

### Memory Cache (CacheInspector)

Inspect `IMemoryCache` contents via reflection.

| Tool | Description |
|------|-------------|
| `get_cache_keys` | List all keys in the in-memory cache |
| `get_cache_stats` | Get cache entry count and internal statistics |
| `get_cache_value` | Get the value of a specific cache key |

### Background Services (BackgroundServiceInspector)

Inspect `IHostedService` / `BackgroundService` implementations.

| Tool | Description |
|------|-------------|
| `get_hosted_services` | List all registered hosted services |
| `get_background_service_detail` | Get fields, properties, and inheritance chain for a service |

### .NET Runtime (RuntimeInspector)

Inspect CLR runtime: memory, GC, thread pool, and process info.

| Tool | Description |
|------|-------------|
| `get_memory_summary` | Managed memory, working set, GC counts |
| `get_gc_info` | GC generation sizes, LOH, pinned object count |
| `get_thread_pool_info` | Thread pool worker and IO threads, queue length |
| `get_process_info` | Process ID, uptime, CPU, thread count |
| `force_gc` | Trigger a full garbage collection (Gen 0-2) |

### HTTP Requests (HttpInspector)

Track HTTP requests passing through the middleware pipeline.

| Tool | Description |
|------|-------------|
| `get_recent_requests` | Recent HTTP requests (method, path, status, duration) |
| `get_request_stats` | Request statistics: count by status code, avg duration |
| `get_slow_requests` | Requests slower than a threshold (default 500ms) |
| `get_error_requests` | Requests with 4xx/5xx status codes |

### Security (SecurityInspector)

Inspect authentication, authorization policies, and CORS configuration.

| Tool | Description |
|------|-------------|
| `get_auth_config` | Get authentication configuration (schemes, handlers, default scheme) |
| `get_authorization_policies` | List all authorization policies and their requirements |
| `get_cors_config` | Get CORS policy details (allowed origins, methods, headers) |

### Error Tracking (ErrorTrackingInspector)

Track and analyze recent application errors and exception statistics.

| Tool | Description |
|------|-------------|
| `get_recent_errors` | Get recent unhandled exceptions and error-level events |
| `get_error_stats` | Aggregate error statistics (count by type, frequency, trends) |

### Locks & Concurrency (LockInspector)

Diagnose thread pool starvation, lock contention, and async state issues.

| Tool | Description |
|------|-------------|
| `get_thread_pool_starvation` | Detect thread pool starvation indicators and queue depth |
| `get_lock_contention` | Get lock contention statistics and contention rate |
| `get_async_state` | Inspect async state machine details and pending continuations |

### Feature Flags (FeatureFlagInspector)

Inspect and evaluate feature flags at runtime.

| Tool | Description |
|------|-------------|
| `get_feature_flags` | List all registered feature flags and their current state |
| `evaluate_flag` | Evaluate a specific feature flag for a given context |

### Endpoint Testing (EndpointTestingInspector)

Test endpoints directly from within the running application.

| Tool | Description |
|------|-------------|
| `test_endpoint` | Send a test request to an endpoint and get the response |
| `batch_test_endpoints` | Run multiple endpoint tests in a single batch |
| `get_endpoint_coverage` | Get endpoint test coverage report (tested vs untested) |

### Metrics (MetricsInspector)

Inspect system-level metrics, custom counters, and event counters.

| Tool | Description |
|------|-------------|
| `get_system_metrics` | Get system-level metrics (CPU, memory, GC, throughput) |
| `get_custom_counters` | List all custom performance counters registered in the app |
| `get_event_counters` | Get EventCounters data (runtime and custom event sources) |

### File Handles (FileHandleInspector)

Monitor open file handles and handle limits.

| Tool | Description |
|------|-------------|
| `get_handle_count` | Get current count of open OS handles (files, sockets, pipes) |
| `get_handle_limit` | Get the maximum handle limit and current utilization |

### Outbound HTTP (OutboundHttpInspector)

Track outbound HTTP calls made by the application.

| Tool | Description |
|------|-------------|
| `get_outbound_http_summary` | Summary of outbound HTTP calls (hosts, methods, status codes) |
| `get_outbound_http_errors` | Outbound HTTP calls that resulted in errors or timeouts |

### Redis (RedisInspector)

Inspect Redis connection info and connection pool statistics.

| Tool | Description |
|------|-------------|
| `get_redis_info` | Get Redis server info (version, memory, connected clients) |
| `get_redis_pool_stats` | Get Redis connection pool stats (pool size, utilization) |

### WebSockets (WebSocketInspector)

Monitor active WebSocket connections and statistics.

| Tool | Description |
|------|-------------|
| `get_ws_connections` | List active WebSocket connections (path, duration, remote IP) |
| `get_ws_stats` | WebSocket statistics (total connections, messages sent/received) |

### CPU Profiler (CpuProfilerInspector) (v0.7.0)

Profile CPU usage and identify hot paths in managed code.

| Tool | Description |
|------|-------------|
| `start_cpu_profile` | Start a CPU profiling session (EventPipe/dotnet-trace) |
| `stop_cpu_profile` | Stop CPU profiling and return collected profile data |
| `get_top_functions` | Get top CPU-consuming methods from the current profile |

### Memory Leak Detector (MemoryLeakInspector) (v0.7.0)

Detect memory leaks via GC heap snapshots and object graph analysis.

| Tool | Description |
|------|-------------|
| `take_heap_snapshot` | Capture a GC heap snapshot for leak analysis |
| `compare_heap_snapshots` | Compare two heap snapshots to identify retained object growth |
| `get_leak_candidates` | Identify objects likely to be memory leaks |

### Deployment/Build Info (DeploymentInfoInspector) (v0.7.0)

Inspect build metadata, deployment environment, and .NET runtime version.

| Tool | Description |
|------|-------------|
| `get_build_info` | Assembly version, commit hash, and build configuration |
| `get_deployment_info` | Deployment environment, container, and orchestration metadata |
| `get_runtime_version` | .NET runtime version, framework, and feature flags |

### Snapshot & Diff (SnapshotDiffInspector) (v0.7.0)

Capture and compare runtime state snapshots to track changes over time.

| Tool | Description |
|------|-------------|
| `take_snapshot` | Capture a runtime state snapshot |
| `compare_snapshots` | Compare two snapshots to identify state changes |
| `list_snapshots` | List all saved snapshots with timestamps |

## Configuration

```json
{
  "DebugAgent": {
    "Enabled": true,
    "BasePath": "/agent",
    "Llm": {
      "Provider": "openai",
      "Model": "gpt-4o",
      "ApiKey": "",
      "BaseUrl": "https://api.openai.com/v1"
    }
  }
}
```

Or via environment variables:

```bash
DEBUG_AGENT__ENABLED=true
DEBUG_AGENT__BASE_PATH=/agent
DEBUG_AGENT__LLM__API_KEY=sk-...
DEBUG_AGENT__LLM__MODEL=gpt-4o
```

## Demo Application

The `demo/` directory contains a complete Order Management API that exercises every inspector:

- **EF Core + SQLite** — `OrderDbContext` with entity configuration and auto-seeded data
- **DI Container** — Services registered with all three lifetimes (Singleton, Scoped, Transient)
- **Health Checks** — Custom `DatabaseHealthCheck` + self-check
- **IMemoryCache** — Order caching with TTL and invalidation
- **BackgroundService** — `OrderCleanupService` for stale order cleanup
- **FluentValidation** — Input validation for order creation
- **HttpClient Factory** — Named client for external API calls
- **Logging** — Structured logging across multiple categories
- **Configuration** — `appsettings.json` with typed `AppSettings` binding

### Run the demo

```bash
cd demo
export OPENAI_API_KEY="sk-..."
dotnet run
```

Then open `http://localhost:5000/agent` and try asking:
- "List all registered services"
- "Show me recent logs"
- "What's the health status?"
- "What endpoints are available?"
- "Show me EF Core DbContext info"
- "What's in the memory cache?"

### Demo API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/orders` | List all orders (cached) |
| GET | `/api/orders/{id}` | Get order by ID |
| POST | `/api/orders` | Create order (validated) |
| POST | `/api/orders/{id}/complete` | Complete an order |
| DELETE | `/api/orders/{id}` | Delete an order |
| GET | `/api/health` | Simple health check |
| GET | `/api/slow` | Slow endpoint (500ms) |
| GET | `/api/error` | Returns 500 error |
| GET | `/api/external-test` | External API call via HttpClient |

## Architecture

```
DebugAgent/
├── AgentContext.cs          # Static IServiceProvider holder
├── Config.cs                # AgentConfig + LLMConfig
├── DebugEngine.cs           # LLM orchestration with tool calling
├── LLMClient.cs             # OpenAI-compatible HTTP client
├── ToolRegistry.cs          # [DebugTool] auto-discovery + schema gen
├── ChatPage.cs              # Embedded chat UI (single HTML page)
├── LogCapture.cs            # In-memory log ring buffer (500 entries)
├── Middleware.cs            # AddDebugAgent() + MapDebugAgent() extensions
└── Inspectors/
    ├── RuntimeInspector.cs          # Memory, GC, thread pool
    ├── HttpInspector.cs             # HTTP request tracking
    ├── ServiceCollectionInspector.cs # DI container
    ├── ConfigurationInspector.cs    # IConfiguration
    ├── EndpointInspector.cs         # Route endpoints
    ├── HealthCheckInspector.cs      # Health checks
    ├── LoggingInspector.cs          # Log capture and search
    ├── EfCoreInspector.cs           # EF Core DbContext
    ├── CacheInspector.cs            # IMemoryCache
    ├── BackgroundServiceInspector.cs # IHostedService
    ├── SecurityInspector.cs          # Auth, authorization, CORS
    ├── ErrorTrackingInspector.cs     # Error tracking and stats
    ├── LockInspector.cs             # Locks, thread pool, async state
    ├── FeatureFlagInspector.cs      # Feature flags
    ├── EndpointTestingInspector.cs   # Endpoint testing
    ├── MetricsInspector.cs          # System metrics and counters
    ├── FileHandleInspector.cs       # File handle monitoring
    ├── OutboundHttpInspector.cs     # Outbound HTTP tracking
    ├── RedisInspector.cs            # Redis info and pool stats
    └── WebSocketInspector.cs        # WebSocket connections
```

## How It Works

1. **AddDebugAgent()** captures the DI service collection snapshot and registers `DebugEngine`, `InMemoryLoggerProvider`, and `AgentConfig`

2. **MapDebugAgent()** initializes `AgentContext` with the `WebApplication` and maps two endpoints:
   - `GET /agent` — Chat UI
   - `POST /agent/api/chat` — SSE streaming chat

3. **ToolRegistry** auto-discovers all `[DebugTool]` methods via reflection at startup

4. **DebugEngine** uses function calling to let the LLM invoke tools, gather data, and explain findings

## Built With

[![ggcode](https://img.shields.io/badge/built%20with-ggcode-blue)](https://github.com/topcheer/ggcode)

This project was built using [ggcode](https://github.com/topcheer/ggcode) — an AI coding assistant for terminal-based development.

## License

MIT
