# .NET Debug Agent

An AI-powered runtime debugging agent that embeds directly into your ASP.NET Core application. Add one package reference, configure an LLM key, and chat with your live app at `/agent` to inspect DI services, configuration, health checks, logs, EF Core, cache, endpoints, memory, GC, and much more.

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

## Inspectors & Tools (50+ tools across 10 inspectors)

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
    └── BackgroundServiceInspector.cs # IHostedService
```

## How It Works

1. **AddDebugAgent()** captures the DI service collection snapshot and registers `DebugEngine`, `InMemoryLoggerProvider`, and `AgentConfig`

2. **MapDebugAgent()** initializes `AgentContext` with the `WebApplication` and maps two endpoints:
   - `GET /agent` — Chat UI
   - `POST /agent/api/chat` — SSE streaming chat

3. **ToolRegistry** auto-discovers all `[DebugTool]` methods via reflection at startup

4. **DebugEngine** uses function calling to let the LLM invoke tools, gather data, and explain findings

## License

MIT
