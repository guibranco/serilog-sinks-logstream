using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Serilog.Events;
using Serilog.Sinks.LogStream.Tests.Support;
using Xunit;

namespace Serilog.Sinks.LogStream.Tests;

public class LogStreamPayloadTests
{
    private static LogStreamSinkOptions Options(Action<LogStreamSinkOptions> configure = null)
    {
        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri("https://logs.example.com"),
            AppKey = "billing-api",
            AppId = "production",
            ApiSecret = "lsa_secret",
        };
        configure?.Invoke(options);
        return options;
    }

    private static JsonElement Serialize(
        IEnumerable<LogEvent> events,
        LogStreamSinkOptions options = null
    ) =>
        JsonDocument
            .Parse(LogStreamPayload.Write(events, options ?? Options(), "batch-1"))
            .RootElement;

    private static JsonElement Entry(Action<ILogger> write, LogStreamSinkOptions options = null) =>
        Serialize(TestEvents.Capture(write), options).GetProperty("logs")[0];

    [Theory]
    [InlineData(LogEventLevel.Verbose, "debug")]
    [InlineData(LogEventLevel.Debug, "debug")]
    [InlineData(LogEventLevel.Information, "info")]
    [InlineData(LogEventLevel.Warning, "warning")]
    [InlineData(LogEventLevel.Error, "error")]
    [InlineData(LogEventLevel.Fatal, "critical")]
    public void LevelsShouldMapToLogStreamLevels(LogEventLevel level, string expected) =>
        Assert.Equal(expected, LogStreamPayload.ToLogStreamLevel(level));

    [Fact]
    public void TheBatchShouldCarryTheApplicationAndBatchIds()
    {
        var json = Serialize(TestEvents.Capture(l => l.Information("Hello")));

        Assert.Equal("billing-api", json.GetProperty("app_key").GetString());
        Assert.Equal("production", json.GetProperty("app_id").GetString());
        Assert.Equal("batch-1", json.GetProperty("batch_id").GetString());
        Assert.Equal(1, json.GetProperty("logs").GetArrayLength());
    }

    [Fact]
    public void TheAppKeyShouldBeOmittedWhenNotSet()
    {
        var options = Options(o =>
        {
            o.AppKey = null;
            o.ApiSecret = null;
            o.ApiKey = "billing-api";
            o.ApiToken = "lss_token";
        });

        var json = Serialize(TestEvents.Capture(l => l.Information("Hello")), options);

        Assert.False(json.TryGetProperty("app_key", out _));
    }

    [Fact]
    public void TheMessageShouldBeRenderedAndTheTemplateKept()
    {
        var entry = Entry(l =>
            l.Information("Charged {Amount} for {@Order}", 12.5m, new { Id = 42 })
        );

        Assert.Equal("info", entry.GetProperty("level").GetString());
        Assert.Equal("Charged 12.5 for { Id: 42 }", entry.GetProperty("message").GetString());
        Assert.Equal(
            "Charged {Amount} for {@Order}",
            entry.GetProperty("message_template").GetString()
        );
        Assert.Equal(12.5m, entry.GetProperty("context").GetProperty("Amount").GetDecimal());
        Assert.Equal(
            42,
            entry.GetProperty("context").GetProperty("Order").GetProperty("Id").GetInt32()
        );
    }

    [Fact]
    public void TheTemplateCanBeLeftOut()
    {
        var entry = Entry(
            l => l.Information("Hello {Name}", "x"),
            Options(o => o.IncludeMessageTemplate = false)
        );

        Assert.False(entry.TryGetProperty("message_template", out _));
    }

    [Fact]
    public void TheSourceContextShouldBecomeTheCategory()
    {
        var entry = Entry(l => l.ForContext<LogStreamPayloadTests>().Information("Hello"));

        Assert.Equal(
            typeof(LogStreamPayloadTests).FullName,
            entry.GetProperty("category").GetString()
        );
    }

    [Fact]
    public void ACustomCategoryPropertyCanBeUsed()
    {
        var entry = Entry(
            l => l.ForContext("Area", "payments").Information("Hello"),
            Options(o => o.CategoryPropertyName = "Area")
        );

        Assert.Equal("payments", entry.GetProperty("category").GetString());
    }

    [Fact]
    public void EventsWithoutACategoryShouldUseTheDefault()
    {
        Assert.Equal(
            "general",
            Entry(l => l.Information("Hello")).GetProperty("category").GetString()
        );
        Assert.Equal(
            "misc",
            Entry(l => l.Information("Hello"), Options(o => o.DefaultCategory = "misc"))
                .GetProperty("category")
                .GetString()
        );
    }

    [Fact]
    public void LongCategoriesShouldBeTruncated()
    {
        var entry = Entry(l =>
            l.ForContext("SourceContext", new string('c', 150)).Information("Hello")
        );

        Assert.Equal(100, entry.GetProperty("category").GetString()!.Length);
    }

    [Fact]
    public void TheTimestampShouldBeUtcIso8601()
    {
        var entry = Entry(l => l.Information("Hello"));
        var timestamp = entry.GetProperty("timestamp").GetString();

        Assert.EndsWith("Z", timestamp);
        Assert.True(DateTimeOffset.TryParse(timestamp, out _));
    }

    [Fact]
    public void PropertyValuesShouldKeepTheirShape()
    {
        var entry = Entry(l =>
            l.Information(
                "{Int} {Text} {Flag} {Nothing} {Items} {Map} {When} {Span} {Guid} {Level} {NaN}",
                7,
                "text",
                true,
                null,
                new[] { 1, 2, 3 },
                new Dictionary<string, int> { ["a"] = 1 },
                new DateTimeOffset(2026, 11, 15, 13, 0, 0, TimeSpan.Zero),
                TimeSpan.FromMinutes(90),
                Guid.Empty,
                LogEventLevel.Warning,
                double.NaN
            )
        );
        var context = entry.GetProperty("context");

        Assert.Equal(7, context.GetProperty("Int").GetInt32());
        Assert.Equal("text", context.GetProperty("Text").GetString());
        Assert.True(context.GetProperty("Flag").GetBoolean());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("Nothing").ValueKind);
        Assert.Equal(
            new[] { 1, 2, 3 },
            context.GetProperty("Items").EnumerateArray().Select(e => e.GetInt32())
        );
        Assert.Equal(1, context.GetProperty("Map").GetProperty("a").GetInt32());
        Assert.Equal("2026-11-15T13:00:00.0000000+00:00", context.GetProperty("When").GetString());
        Assert.Equal("01:30:00", context.GetProperty("Span").GetString());
        Assert.Equal(Guid.Empty.ToString(), context.GetProperty("Guid").GetString());
        Assert.Equal("Warning", context.GetProperty("Level").GetString());
        Assert.Equal("NaN", context.GetProperty("NaN").GetString());
    }

    [Fact]
    public void DestructuredObjectsShouldCarryTheirTypeTag()
    {
        var entry = Entry(l => l.Information("{@Sample}", new Sample { Name = "waffle" }));
        var sample = entry.GetProperty("context").GetProperty("Sample");

        Assert.Equal("Sample", sample.GetProperty("$type").GetString());
        Assert.Equal("waffle", sample.GetProperty("Name").GetString());
    }

    [Fact]
    public void ExceptionsShouldBeWrittenWithTheirChain()
    {
        var exception = Thrown(
            new InvalidOperationException("Charge failed", new TimeoutException("Gateway timeout"))
        );

        var entry = Entry(l => l.Error(exception, "Could not charge"));
        var details = entry.GetProperty("context").GetProperty("exception");

        Assert.Equal("error", entry.GetProperty("level").GetString());
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            details.GetProperty("type").GetString()
        );
        Assert.Equal("Charge failed", details.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(details.GetProperty("stack_trace").GetString()));
        Assert.Equal(
            typeof(TimeoutException).FullName,
            details.GetProperty("inner_exception").GetProperty("type").GetString()
        );
    }

    [Fact]
    public void AggregateExceptionsShouldListEveryInnerException()
    {
        var exception = new AggregateException(
            new InvalidOperationException("a"),
            new ArgumentException("b")
        );

        var details = Entry(l => l.Error(exception, "Many"))
            .GetProperty("context")
            .GetProperty("exception");

        Assert.Equal(2, details.GetProperty("inner_exceptions").GetArrayLength());
    }

    [Fact]
    public void AnExceptionPropertyShouldNotBeOverwritten()
    {
        var context = Entry(l =>
                l.ForContext("exception", "mine").Error(new InvalidOperationException("x"), "Boom")
            )
            .GetProperty("context");

        Assert.Equal("mine", context.GetProperty("exception").GetString());
        Assert.Equal("x", context.GetProperty("@exception").GetProperty("message").GetString());
    }

    [Fact]
    public void TheActivityTraceAndSpanIdsShouldBeSent()
    {
        using var activity = new Activity("checkout").SetIdFormat(ActivityIdFormat.W3C).Start();

        var entry = Entry(l => l.Information("Inside"));

        Assert.Equal(activity.TraceId.ToHexString(), entry.GetProperty("trace_id").GetString());
        Assert.Equal(
            activity.SpanId.ToHexString(),
            entry.GetProperty("context").GetProperty("span_id").GetString()
        );
    }

    [Fact]
    public void EventsWithoutATraceShouldLetTheServerGenerateOne()
    {
        Activity.Current = null;

        var entry = Entry(l => l.Information("Outside"));

        Assert.False(entry.TryGetProperty("trace_id", out _));
    }

    [Fact]
    public void StaticContextShouldBeAddedAndEventPropertiesShouldWin()
    {
        var options = Options(o =>
        {
            o.StaticContext["region"] = "eu-west-1";
            o.StaticContext["Name"] = "static";
        });

        var context = Entry(l => l.Information("Hello {Name}", "event"), options)
            .GetProperty("context");

        Assert.Equal("eu-west-1", context.GetProperty("region").GetString());
        Assert.Equal("event", context.GetProperty("Name").GetString());
        Assert.Equal(2, context.EnumerateObject().Count());
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    public sealed class Sample
    {
        public string Name { get; set; }
    }
}
