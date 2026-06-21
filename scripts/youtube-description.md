# YouTube Video Description

## Title

.NET Debug Agent — AI-Powered In-Process Diagnostics for ASP.NET Core (10 Inspectors / 34 Tools)

## Description

Chat with your LIVE ASP.NET Core application at runtime. The .NET Debug Agent embeds directly into your app and gives an AI assistant access to 34 diagnostic tools across 10 inspectors — DI container, configuration, endpoints, health checks, logs, EF Core, memory cache, background services, .NET runtime, and HTTP requests.

No external agents. No attach-to-process. No separate monitoring stack. Just one NuGet-style package reference, one line of code, and you're chatting with your running app.

### What you'll see in this demo

**Section 1 — .NET Runtime Deep Dive**
Memory stats, GC collections, thread pool info, process info, and forcing a garbage collection — all through natural language.

**Section 2 — DI Container + Configuration**
Enumerating 140+ registered services by lifetime (Singleton/Scoped/Transient), resolving services at runtime, inspecting configuration sources and values.

**Section 3 — HTTP Endpoints + Request Tracking**
Discovering all registered routes, analyzing recent HTTP traffic, identifying slow and error requests.

**Section 4 — Health Checks + Logging**
Running all health checks with per-component status, searching in-memory log ring buffer, log level statistics.

**Section 5 — EF Core + Database**
Listing DbContexts, entity types, provider info, connection state, and migration status.

**Section 6 — Memory Cache + Background Services**
Inspecting IMemoryCache keys and values, enumerating IHostedService implementations.

**Section 7 — Comprehensive Debugging**
Multi-tool correlation: memory + GC + health + logs + requests + DI + cache — all in one analysis.

### Quick Start

```csharp
// Program.cs
using DebugAgents;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDebugAgent(builder.Configuration);

var app = builder.Build();
app.MapDebugAgent();
app.Run();
```

Open `http://localhost:5000/agent` and start chatting with your app.

### Features

- 34 diagnostic tools across 10 inspectors
- Streaming AI responses with real-time tool call badges
- LLM-based context compression for long conversations
- Custom tool registration via [DebugTool] attribute
- Works with any OpenAI-compatible LLM endpoint
- Zero external dependencies (no Datadog, no Application Insights, no Grafana)
- Dark-themed chat UI built-in (single HTML page, no frontend framework)

### Inspector Coverage

| Inspector | Tools | What it inspects |
|-----------|-------|-----------------|
| DI Container | 4 | Service registrations, lifetimes, resolution |
| Configuration | 3 | Config sources, keys, values (with secret masking) |
| HTTP Endpoints | 2 | Route discovery, endpoint metadata |
| Health Checks | 2 | Health status execution, registration listing |
| Logging | 4 | Log ring buffer, search, stats, log levels |
| EF Core | 4 | DbContext, entity types, migrations, connections |
| Memory Cache | 3 | Cache keys, stats, values |
| Background Services | 2 | Hosted service enumeration, detail inspection |
| .NET Runtime | 5 | Memory, GC, thread pool, process, environment |
| HTTP Requests | 4 | Recent requests, stats, slow, errors |

### GitHub

https://github.com/topcheer/dotnet-debug-agent

### Tags

#dotnet #aspnetcore #AI #Debugging #Diagnostics #EntityFramework #LLM #GLM #DeveloperTools #DevOps #ApplicationMonitoring #CSharp #dotnetcore #AIOps #Observability

## Chapters

00:00 Introduction
01:15 .NET Runtime — Memory, GC, Thread Pool
03:20 DI Container + Configuration
05:30 HTTP Endpoints + Request Tracking
07:10 Health Checks + Logging
09:15 EF Core + Database
10:50 Memory Cache + Background Services
12:20 Comprehensive Multi-Tool Debugging
14:00 Summary + Quick Start Guide

---

## Thumbnail Text (for image)

.NET Debug Agent
Chat with your LIVE app
34 tools / 10 inspectors

---

## Playlist

AI Debug Agents Collection
(Spring / .NET / Go / Node.js / Python / Ruby)

---

## Category

Science & Technology

## Language

English

## Visibility

Public

## Made for Kids

No
