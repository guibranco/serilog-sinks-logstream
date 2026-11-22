using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.LogStream.Tests.Support;
using Xunit;

namespace Serilog.Sinks.LogStream.Tests;

[Collection("SelfLog")]
public class LogStreamSinkTests
{
    private static LogStreamSink CreateSink(
        RecordingHandler handler,
        Action<LogStreamSinkOptions> configure = null
    )
    {
        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri("https://logs.example.com/base"),
            AppKey = "billing-api",
            AppId = "production",
            ApiSecret = "lsa_secret",
            HttpClient = new HttpClient(handler, disposeHandler: false),
        };
        configure?.Invoke(options);
        return new LogStreamSink(options);
    }

    [Fact]
    public async Task ABatchShouldBePostedWithBearerAuthentication()
    {
        using var handler = new RecordingHandler();
        using var sink = CreateSink(handler, o => o.UserAgent = "BillingService/2.1.0");

        await sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://logs.example.com/base/api/logs"), request.Uri);
        Assert.Equal("Bearer lsa_secret", request.Headers["Authorization"]);
        Assert.Equal("BillingService/2.1.0", request.Headers["User-Agent"]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"]);
        Assert.False(request.Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task ClientCredentialsShouldBeSentAsHeaders()
    {
        using var handler = new RecordingHandler();
        using var sink = CreateSink(handler, o =>
        {
            o.ApiSecret = null;
            o.ApiKey = "billing-api";
            o.ApiToken = "lss_token";
        });

        await sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("billing-api", request.Headers["X-Api-Key"]);
        Assert.Equal("lss_token", request.Headers["X-Api-Token"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task TheDefaultUserAgentShouldNameTheSink()
    {
        using var handler = new RecordingHandler();
        using var sink = CreateSink(handler);

        await sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList());

        Assert.Contains("(Serilog.Sinks.LogStream/", handler.Requests[0].Headers["User-Agent"]);
    }

    [Fact]
    public async Task AnEmptyBatchShouldNotBeSent()
    {
        using var handler = new RecordingHandler();
        using var sink = CreateSink(handler);

        await sink.EmitBatchAsync(Array.Empty<LogEvent>());
        await sink.OnEmptyBatchAsync();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BatchesLargerThanTheServerLimitShouldBeSplit()
    {
        using var handler = new RecordingHandler();
        using var sink = CreateSink(handler);
        var events = TestEvents.Capture(l =>
        {
            for (var i = 0; i < 2500; i++)
            {
                l.Information("Event {Index}", i);
            }
        });

        await sink.EmitBatchAsync(events.ToList());

        Assert.Equal(
            new[] { 1000, 1000, 500 },
            handler.Requests.Select(r => r.Logs.GetArrayLength())
        );
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task TransientFailuresShouldThrowSoSerilogRetries(HttpStatusCode status)
    {
        using var handler = new RecordingHandler(status);
        using var sink = CreateSink(handler);

        var error = await Assert.ThrowsAsync<LogStreamException>(() =>
            sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList())
        );

        Assert.Equal((int)status, error.StatusCode);
    }

    [Fact]
    public async Task NetworkErrorsShouldThrowSoSerilogRetries()
    {
        using var handler = new RecordingHandler { Throw = new HttpRequestException("refused") };
        using var sink = CreateSink(handler);

        var error = await Assert.ThrowsAsync<LogStreamException>(() =>
            sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList())
        );

        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PermanentFailuresShouldBeSelfLoggedAndDropped(HttpStatusCode status)
    {
        var selfLog = new StringBuilder();
        SelfLog.Enable(new StringWriter(selfLog));
        try
        {
            using var handler = new RecordingHandler(status);
            using var sink = CreateSink(handler);

            await sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList());

            Assert.Single(handler.Requests);
            Assert.Contains($"HTTP {(int)status}", selfLog.ToString());
        }
        finally
        {
            SelfLog.Disable();
        }
    }

    [Fact]
    public async Task EntryErrorsInAnAcceptedResponseShouldBeSelfLogged()
    {
        var selfLog = new StringBuilder();
        SelfLog.Enable(new StringWriter(selfLog));
        try
        {
            using var handler = new RecordingHandler
            {
                ResponseBody = "{\"accepted\":1,\"entries\":[],\"errors\":[\"Entry #1: bad\"]}",
            };
            using var sink = CreateSink(handler);

            await sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList());

            Assert.Contains("Entry #1: bad", selfLog.ToString());
        }
        finally
        {
            SelfLog.Disable();
        }
    }

    [Theory]
    [InlineData(null, "production", "lsa", null, null)]
    [InlineData("billing-api", null, "lsa", null, null)]
    [InlineData(null, "production", "lsa", "key", null)]
    [InlineData("billing-api", "production", null, "key", null)]
    public void InvalidOptionsShouldBeRejected(
        string appKey,
        string appId,
        string secret,
        string apiKey,
        string apiToken
    )
    {
        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri("https://logs.example.com"),
            AppKey = appKey,
            AppId = appId,
            ApiSecret = secret,
            ApiKey = apiKey,
            ApiToken = apiToken,
        };

        Assert.Throws<InvalidOperationException>(() => new LogStreamSink(options));
    }

    [Fact]
    public void ANonHttpServerUrlShouldBeRejected()
    {
        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri("ftp://logs.example.com"),
            AppKey = "a",
            AppId = "b",
            ApiSecret = "c",
        };

        Assert.Throws<InvalidOperationException>(() => new LogStreamSink(options));
    }

    [Fact]
    public async Task WriteToLogStreamShouldShipEventsThroughTheBatchingPipeline()
    {
        using var handler = new RecordingHandler();

        using (
            var logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.LogStream(
                    "https://logs.example.com",
                    appId: "production",
                    appKey: "billing-api",
                    apiSecret: "lsa_secret",
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    period: TimeSpan.FromMilliseconds(50),
                    messageHandler: handler
                )
                .CreateLogger()
        )
        {
            logger.Debug("Filtered out");
            logger.ForContext<LogStreamSinkTests>().Warning("Disk {Percent}% full", 91);
            Assert.True(await handler.WaitForRequestAsync(TimeSpan.FromSeconds(10)));
        }

        var entry = Assert.Single(handler.Requests.SelectMany(r => r.Logs.EnumerateArray()));
        Assert.Equal("warning", entry.GetProperty("level").GetString());
        Assert.Equal("Disk 91% full", entry.GetProperty("message").GetString());
        Assert.Equal(
            typeof(LogStreamSinkTests).FullName,
            entry.GetProperty("category").GetString()
        );
    }

    [Fact]
    public void WriteToLogStreamWithOptionsShouldSendQueuedEventsOnDispose()
    {
        using var handler = new RecordingHandler();

        using (
            var logger = new LoggerConfiguration()
                .WriteTo.LogStream(
                    o =>
                    {
                        o.ServerUrl = new Uri("http://localhost:8081");
                        o.ApiKey = "billing-api";
                        o.ApiToken = "lss_token";
                        o.AppId = "production";
                        o.HttpClient = new HttpClient(handler, disposeHandler: false);
                    },
                    b => b.BufferingTimeLimit = TimeSpan.FromHours(1)
                )
                .CreateLogger()
        )
        {
            logger.Information("Sent on dispose");
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Sent on dispose", request.Logs[0].GetProperty("message").GetString());
    }

    [Fact]
    public void ABatchSizeAboveTheServerLimitShouldBeRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoggerConfiguration().WriteTo.LogStream(
                "https://logs.example.com",
                "production",
                "billing-api",
                "lsa_secret",
                batchSizeLimit: 5000
            )
        );
    }

    [Fact]
    public void TheSinkShouldBeConfigurableFromAppSettings()
    {
        const string json = """
            {
              "Serilog": {
                "Using": [ "Serilog.Sinks.LogStream" ],
                "WriteTo": [
                  {
                    "Name": "LogStream",
                    "Args": {
                      "serverUrl": "https://logs.example.com",
                      "appId": "production",
                      "apiKey": "billing-api",
                      "apiToken": "lss_token",
                      "batchSizeLimit": 50,
                      "period": "00:00:05",
                      "restrictedToMinimumLevel": "Warning"
                    }
                  }
                ]
              }
            }
            """;
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var selfLog = new StringBuilder();
        SelfLog.Enable(new StringWriter(selfLog));
        try
        {
            using var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            Assert.NotNull(logger);
            Assert.DoesNotContain("Unable to find", selfLog.ToString());
        }
        finally
        {
            SelfLog.Disable();
        }
    }
}

[CollectionDefinition("SelfLog", DisableParallelization = true)]
public class SelfLogCollection { }
