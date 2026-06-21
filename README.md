# .NET Debug Agent

An AI-powered runtime debugging agent that embeds directly into your ASP.NET Core application. Add one package reference, configure an LLM key, and chat with your live app at `/agent` to inspect memory, GC, thread pool, HTTP requests, and more.

## Quick Start

### 1. Add reference

```xml
<PackageReference Include="DebugAgent" Version="0.1.0" />
```

Or project reference:
```xml
<ProjectReference Include="path/to/DebugAgent.csproj" />
```

### 2. Integrate (Program.cs)

```csharp
using DebugAgents;

var builder = WebApplication.CreateBuilder(args);

// Add Debug Agent
builder.Services.AddDebugAgent();

var app = builder.Build();

// Map endpoints
app.MapDebugAgent();

app.Run();
```

### 3. Configure LLM

```bash
export LLM_API_KEY=your-key
export LLM_BASE_URL=https://api.openai.com/v1  # optional
export LLM_MODEL=gpt-4o                         # optional
```

### 4. Run and open

```
http://localhost:5000/agent
```

## Built-in Tools (10+)

| Tool | Description |
|------|-------------|
| `get_memory_stats` | Managed heap, working set, GC collection counts |
| `trigger_gc` | Force GC with before/after comparison |
| `get_thread_pool_info` | ThreadPool available/min/max threads |
| `get_runtime_info` | .NET version, framework, GC mode |
| `get_process_info` | PID, uptime, CPU time, thread count |
| `get_environment_variables` | Environment variables (masked secrets) |
| `get_disk_usage` | Disk usage for working directory |
| `get_recent_requests` | HTTP request ring buffer |
| `get_error_requests` | Error requests (4xx/5xx) |
| `get_request_stats` | P50/P95/P99 latency, error rate |

## Custom Tools

```csharp
using DebugAgents;

public static class MyTools
{
    [DebugTool("check_cache", "Check cache statistics")]
    public static object CheckCache()
    {
        return new { hits = 42, misses = 7 };
    }
}

// Tools are auto-discovered from the assembly
```

## Run the Demo

```bash
cd demo
dotnet run
# Open http://localhost:5000/agent
```

## License

MIT
