# Serilog.Sinks.LogStream

📡🪵 A [Serilog](https://serilog.net) sink that writes events to a [LogStream](https://github.com/guibranco/logstream-server) server.

[![GitHub license](https://img.shields.io/github/license/guibranco/serilog-sinks-logstream)](https://github.com/guibranco/serilog-sinks-logstream)
[![Time tracker](https://wakatime.com/badge/github/guibranco/serilog-sinks-logstream.svg)](https://wakatime.com/badge/github/guibranco/serilog-sinks-logstream)

![Serilog.Sinks.LogStream logo](https://raw.githubusercontent.com/guibranco/serilog-sinks-logstream/main/logo.png)

Documentation: [GitHub Pages](https://guibranco.github.io/serilog-sinks-logstream/)

---

## Table of content

- [Features](#features): What the sink does.
- [CI/CD](#cicd): Current project status in the build pipeline (GitHub Actions).
- [Code Quality](#code-quality): Metrics about code quality.
- [.NET support](#net-support): Supported target frameworks.
- [Installation](#installation): How to install/download the package.
- [Usage](#usage): How to set up, configure and use the sink.
- [What is sent](#what-is-sent): How Serilog events map to LogStream entries.
- [Batching and failures](#batching-and-failures): Buffering, retries and diagnostics.
- [Related projects](#related-projects): The rest of the LogStream ecosystem.
- [Building](#building): How to build and test locally.
- [Support](#support): How to get support.
- [Contributing](#contributing): How to contribute.

---

## Features

- **Batched delivery** — events are sent in batches to `POST /api/logs` using Serilog's built-in batching.
- **Structured** — rendered message, message template, category, level, properties, exception chain and trace ID,
  so events can be searched, grouped by template and followed by trace in the LogStream UI.
- **Both storage modes** — file storage (shared `API_SECRET` + `appKey`) and MariaDB storage (per-app
  `X-Api-Key` / `X-Api-Token`).
- **Resilient** — transient failures (network errors, timeouts, 408, 429, 5xx) are retried with back-off;
  permanent rejections are reported to `SelfLog` and dropped.
- **Configurable from code or `appsettings.json`** — works with
  [Serilog.Settings.Configuration](https://github.com/serilog/serilog-settings-configuration).
- **Trace-aware** — Serilog 4 captures `Activity.Current`, so the `trace_id` and `span_id` flow to LogStream.

---

## CI/CD

| Build and tests | Release | Last commit |
| --- | --- | --- |
| [![Build and Test](https://github.com/guibranco/serilog-sinks-logstream/actions/workflows/build.yml/badge.svg)](https://github.com/guibranco/serilog-sinks-logstream/actions/workflows/build.yml) | [![Release](https://github.com/guibranco/serilog-sinks-logstream/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/guibranco/serilog-sinks-logstream/actions/workflows/release.yml) | [![GitHub last commit](https://img.shields.io/github/last-commit/guibranco/serilog-sinks-logstream/main)](https://github.com/guibranco/serilog-sinks-logstream) |

---

## Code Quality

[![codecov](https://codecov.io/gh/guibranco/serilog-sinks-logstream/branch/main/graph/badge.svg)](https://codecov.io/gh/guibranco/serilog-sinks-logstream)

---

## .NET support

| Target framework | Supported |
| --- | :---: |
| .NET Standard 2.0 (.NET Framework 4.6.2+, .NET Core 2.0+) | ✅ |
| .NET 8.0 | ✅ |
| .NET 9.0 | ✅ |
| .NET 10.0 | ✅ |

Requires **Serilog 4** or later.

---

## Installation

### GitHub Releases

[![GitHub last release](https://img.shields.io/github/release-date/guibranco/serilog-sinks-logstream.svg?style=flat)](https://github.com/guibranco/serilog-sinks-logstream/releases) [![GitHub All Releases](https://img.shields.io/github/downloads/guibranco/serilog-sinks-logstream/total.svg?style=flat)](https://github.com/guibranco/serilog-sinks-logstream/releases)

Download the latest `.nupkg` from the [Releases](https://github.com/guibranco/serilog-sinks-logstream/releases) page.

### NuGet package manager

| Package | Version | Downloads |
| --- | :---: | :---: |
| **Serilog.Sinks.LogStream** | [![Serilog.Sinks.LogStream NuGet Version](https://img.shields.io/nuget/v/Serilog.Sinks.LogStream.svg?style=flat)](https://www.nuget.org/packages/Serilog.Sinks.LogStream/) | [![Serilog.Sinks.LogStream NuGet Downloads](https://img.shields.io/nuget/dt/Serilog.Sinks.LogStream.svg?style=flat)](https://www.nuget.org/packages/Serilog.Sinks.LogStream/) |

```bash
dotnet add package Serilog.Sinks.LogStream
```

---

## Usage

### File storage

The server has one shared `API_SECRET`; name the application with `appKey`:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.LogStream(
        serverUrl: "https://logs.example.com",
        appId: "production",
        appKey: "billing-api",
        apiSecret: Environment.GetEnvironmentVariable("LOGSTREAM_API_SECRET"))
    .CreateLogger();
```

### MariaDB storage

Each application has its own key and token (`X-Api-Key` / `X-Api-Token`); the key is the app key:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.LogStream(
        serverUrl: "https://logs.example.com",
        appId: "production",
        apiKey: "billing-api",
        apiToken: Environment.GetEnvironmentVariable("LOGSTREAM_API_TOKEN"))
    .CreateLogger();
```

> [!IMPORTANT]
> Call `Log.CloseAndFlush()` (or dispose the logger) on shutdown so buffered events are sent.

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

### Arguments

| Argument | Default | Description |
| --- | --- | --- |
| `serverUrl` | — | Base address of the server, e.g. `https://logs.example.com` |
| `appId` | — | Instance or environment identifier (`app_id`) |
| `appKey` | `null` | Application slug (`app_key`); required with `apiSecret` |
| `apiSecret` | `null` | Shared write secret (file storage) |
| `apiKey` | `null` | Client key (MariaDB storage), sent as `X-Api-Key` |
| `apiToken` | `null` | Client token (MariaDB storage), sent as `X-Api-Token` |
| `restrictedToMinimumLevel` | `Verbose` | Minimum level passed through the sink |
| `batchSizeLimit` | `100` | Events per request (at most 1000) |
| `period` | `00:00:02` | Longest wait before a partial batch is sent |
| `queueLimit` | `100000` | Events buffered in memory; `null` for no limit |
| `retryTimeLimit` | 10 minutes | How long a failing batch is retried before it is dropped |
| `categoryPropertyName` | `SourceContext` | Property that becomes the entry category |
| `levelSwitch` | `null` | Switch to change the minimum level at runtime |
| `formatProvider` | invariant culture | Culture used to render messages |
| `messageHandler` | `null` | HTTP message handler for the sink's client (e.g. a proxy) |

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

> [!WARNING]
> LogStream is meant for back-end services: don't log to it from browser or mobile front ends, where the
> credentials would be exposed.

---

## What is sent

| LogStream field | Taken from |
| --- | --- |
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

---

## Batching and failures

The sink uses Serilog's built-in batching (`BatchingOptions`):

| Argument | Default | Description |
| --- | --- | --- |
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

---

## Related projects

| Project | Description |
| --- | --- |
| [logstream-server](https://github.com/guibranco/logstream-server) | The ingestion and streaming server |
| [logstream-ui](https://github.com/guibranco/logstream-ui) | The dashboard |
| [CrispyWaffle.LogStream](https://guibranco.github.io/CrispyWaffle/packages/logstream/) | The LogStream log provider for the [CrispyWaffle](https://github.com/guibranco/CrispyWaffle) toolkit |

---

## Building

```bash
dotnet build
dotnet test
dotnet csharpier check .
```

---

## Support

If you need help, feel free to [open an issue](https://github.com/guibranco/serilog-sinks-logstream/issues/new).

---

## Contributing

Contributions are welcome! Fork the repository, create a branch, and open a pull request. Please run
`dotnet csharpier format .` and make sure `dotnet test` passes before submitting.

---

## Contributors

Thanks to all contributors! 💙

<!-- readme: collaborators,contributors,snyk-bot/- -start -->
<table>
	<tbody>
		<tr>
            <td align="center">
                <a href="https://github.com/guibranco">
                    <img src="https://avatars.githubusercontent.com/u/3362854?v=4" width="100;" alt="guibranco"/>
                    <br />
                    <sub><b>Guilherme Branco Stracini</b></sub>
                </a>
            </td>
		</tr>
	</tbody>
</table>
<!-- readme: collaborators,contributors,snyk-bot/- -end -->

### Bots

<!-- readme: bots,snyk-bot -start -->
<table>
	<tbody>
		<tr>
            <td align="center">
                <a href="https://github.com/dependabot[bot]">
                    <img src="https://avatars.githubusercontent.com/in/29110?v=4" width="100;" alt="dependabot[bot]"/>
                    <br />
                    <sub><b>dependabot[bot]</b></sub>
                </a>
            </td>
            <td align="center">
                <a href="https://github.com/snyk-bot">
                    <img src="https://avatars.githubusercontent.com/u/19733683?v=4" width="100;" alt="snyk-bot"/>
                    <br />
                    <sub><b>Snyk bot</b></sub>
                </a>
            </td>
		</tr>
	</tbody>
</table>
<!-- readme: bots,snyk-bot -end -->

---

## License

This project is licensed under the [MIT License](LICENSE).
