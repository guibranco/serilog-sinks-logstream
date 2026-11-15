# Serilog.Sinks.LogStream

📡🪵 A [Serilog](https://serilog.net) sink that writes events to a [LogStream](https://github.com/guibranco/logstream-server) server.

[![NuGet Version](https://img.shields.io/nuget/v/Serilog.Sinks.LogStream.svg?style=flat)](https://www.nuget.org/packages/Serilog.Sinks.LogStream/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Serilog.Sinks.LogStream.svg?style=flat)](https://www.nuget.org/packages/Serilog.Sinks.LogStream/)
[![Build](https://github.com/guibranco/serilog-sinks-logstream/actions/workflows/build.yml/badge.svg)](https://github.com/guibranco/serilog-sinks-logstream/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Events are sent in batches to `POST /api/logs` with their rendered message, message template, category, level,
properties, exception chain and trace ID, so they can be searched, grouped by template and followed by trace in the
LogStream UI.

Targets `netstandard2.0`, `net8.0`, `net9.0` and `net10.0`. Requires Serilog 4.

## Install

```bash
dotnet add package Serilog.Sinks.LogStream
```

## Quick start

**File storage** — the server has one shared `API_SECRET`; name the application with `appKey`:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.LogStream(
        serverUrl: "https://logs.example.com",
        appId: "production",
        appKey: "billing-api",
        apiSecret: Environment.GetEnvironmentVariable("LOGSTREAM_API_SECRET"))
    .CreateLogger();
```

**MariaDB storage** — each application has its own key and token (`X-Api-Key` / `X-Api-Token`); the key is the app key:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.LogStream(
        serverUrl: "https://logs.example.com",
        appId: "production",
        apiKey: "billing-api",
        apiToken: Environment.GetEnvironmentVariable("LOGSTREAM_API_TOKEN"))
    .CreateLogger();
```

Call `Log.CloseAndFlush()` (or dispose the logger) on shutdown so buffered events are sent.

### appsettings.json

With [Serilog.Settings.Configuration](https://github.com/serilog/serilog-settings-configuration):

```json
{
  "Serilog": {
    "Using": [ "Serilog.Sinks.LogStream" ],
    "MinimumLevel": "Information",
    "WriteTo": [
      {
        "Name": "LogStream",
        "Args": {
          "serverUrl": "https://logs.example.com",
          "appId": "production",
          "apiKey": "billing-api",
          "apiToken": "lss_…",
          "batchSizeLimit": 100,
          "period": "00:00:02"
        }
      }
    ]
  }
}
```

Keep the secret or token out of source control (user secrets, environment variables such as
`Serilog__WriteTo__0__Args__apiToken`, or a vault).

### Full options

```csharp
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.LogStream(
        o =>
        {
            o.ServerUrl = new Uri("https://logs.example.com");
            o.ApiKey = "billing-api";
            o.ApiToken = token;
            o.AppId = builder.Environment.EnvironmentName;
            o.StaticContext["region"] = "eu-west-1";
            o.HttpClient = httpClientFactory.CreateClient("logstream"); // optional
        },
        batching =>
        {
            batching.BatchSizeLimit = 200;
            batching.BufferingTimeLimit = TimeSpan.FromSeconds(1);
            batching.RetryTimeLimit = TimeSpan.FromMinutes(2);
        })
    .CreateLogger();
```

## What is sent

| LogStream field | Taken from |
|---|---|
| `level` | Verbose, Debug → `debug`; Information → `info`; Warning → `warning`; Error → `error`; Fatal → `critical` |
| `category` | The `SourceContext` property (the `ForContext<T>()` logger name), or `CategoryPropertyName`; `general` when missing. Truncated to 100 characters |
| `message` | The rendered message (invariant culture unless `formatProvider` is set) |
| `message_template` | The message template text, so LogStream groups every event from the same line (`IncludeMessageTemplate`) |
| `context` | `StaticContext`, then every event property as JSON (`{@Object}` structures keep their shape and a `$type` tag), `span_id`, and `exception` |
| `context.exception` | `type`, `message`, `stack_trace`, `source` and `inner_exception` (or `inner_exceptions` for an `AggregateException`) |
| `trace_id` | The event's trace ID (Serilog 4 captures `Activity.Current`); without one, the server generates one per entry |
| `timestamp` | The event timestamp, in UTC |
| `app_key`, `app_id`, `batch_id` | The options, and a new GUID per request |
| `User-Agent` header | `{entry assembly}/{version} (Serilog.Sinks.LogStream/{version})`, or `UserAgent` |

## Batching and failures

The sink uses Serilog's built-in batching (`BatchingOptions`):

| Argument | Default | |
|---|---|---|
| `batchSizeLimit` | 100 | Events per request (at most 1000, the server's limit) |
| `period` | 2 s | Longest wait before a partial batch is sent |
| `queueLimit` | 100,000 | Events kept in memory; newer ones are dropped while full |
| `retryTimeLimit` | 10 min | How long a failing batch is retried before it is dropped |

Network errors, timeouts and HTTP 408, 429 and 5xx are retried with back-off. Other rejections (400, 401, 403…)
are reported to [`SelfLog`](https://github.com/serilog/serilog/wiki/Debugging-and-Diagnostics) and the batch is
dropped, because sending it again would fail the same way. Entries the server rejects one by one (the `errors`
list of a 201/202 response) are reported to `SelfLog` too.

```csharp
Serilog.Debugging.SelfLog.Enable(Console.Error);
```

LogStream is meant for back-end services: don't log to it from browser or mobile front ends, where the credentials
would be exposed.

## Related

- [logstream-server](https://github.com/guibranco/logstream-server) — the ingestion and streaming server
- [logstream-ui](https://github.com/guibranco/logstream-ui) — the dashboard
- [CrispyWaffle.LogStream](https://github.com/guibranco/CrispyWaffle) — the log provider for the CrispyWaffle toolkit

## Building

```bash
dotnet build
dotnet test
dotnet csharpier check .
```

## License

[MIT](LICENSE)
