using System;
using System.Collections.Generic;
using Serilog.Core;
using Serilog.Events;

namespace Serilog.Sinks.LogStream.Tests.Support;

/// <summary>
/// Produces real <see cref="LogEvent"/>s through a Serilog logger, so properties are captured as in production.
/// </summary>
public static class TestEvents
{
    public static IReadOnlyList<LogEvent> Capture(Action<ILogger> write)
    {
        var sink = new CollectingSink();
        using (
            var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(sink)
                .CreateLogger()
        )
        {
            write(logger);
        }

        return sink.Events;
    }

    public static LogEvent Single(Action<ILogger> write) => Capture(write)[0];

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
