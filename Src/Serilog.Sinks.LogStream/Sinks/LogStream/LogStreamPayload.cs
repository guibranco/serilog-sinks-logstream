using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Serilog.Events;

namespace Serilog.Sinks.LogStream;

/// <summary>
/// Writes Serilog events as a LogStream <c>POST /api/logs</c> batch.
/// </summary>
internal static class LogStreamPayload
{
    /// <summary>The longest category LogStream stores.</summary>
    public const int MaxCategoryLength = 100;

    private const int MaxExceptionDepth = 10;
    private const string ExceptionKey = "exception";
    private const string SpanIdKey = "span_id";

    /// <summary>
    /// Maps a Serilog level to a LogStream level.
    /// </summary>
    /// <param name="level">The Serilog level.</param>
    /// <returns>One of <c>debug</c>, <c>info</c>, <c>warning</c>, <c>error</c> or <c>critical</c>.</returns>
    public static string ToLogStreamLevel(LogEventLevel level) =>
        level switch
        {
            LogEventLevel.Verbose => "debug",
            LogEventLevel.Debug => "debug",
            LogEventLevel.Information => "info",
            LogEventLevel.Warning => "warning",
            LogEventLevel.Error => "error",
            LogEventLevel.Fatal => "critical",
            _ => "info",
        };

    /// <summary>
    /// Serializes a batch.
    /// </summary>
    /// <param name="events">The events.</param>
    /// <param name="options">The sink options.</param>
    /// <param name="batchId">The batch identifier.</param>
    /// <returns>The UTF-8 JSON payload.</returns>
    public static byte[] Write(
        IEnumerable<LogEvent> events,
        LogStreamSinkOptions options,
        string batchId
    )
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            if (!string.IsNullOrWhiteSpace(options.AppKey))
            {
                writer.WriteString("app_key", options.AppKey);
            }

            writer.WriteString("app_id", options.AppId);
            writer.WriteString("batch_id", batchId);
            writer.WriteStartArray("logs");

            foreach (var logEvent in events)
            {
                WriteEntry(writer, logEvent, options);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteEntry(
        Utf8JsonWriter writer,
        LogEvent logEvent,
        LogStreamSinkOptions options
    )
    {
        writer.WriteStartObject();

        if (logEvent.TraceId is { } traceId)
        {
            writer.WriteString("trace_id", traceId.ToHexString());
        }

        writer.WriteString("level", ToLogStreamLevel(logEvent.Level));
        writer.WriteString("category", GetCategory(logEvent, options));
        writer.WriteString(
            "message",
            logEvent.RenderMessage(options.FormatProvider ?? CultureInfo.InvariantCulture)
        );

        if (options.IncludeMessageTemplate)
        {
            writer.WriteString("message_template", logEvent.MessageTemplate.Text);
        }

        writer.WriteString(
            "timestamp",
            logEvent
                .Timestamp.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        );

        writer.WritePropertyName("context");
        WriteContext(writer, logEvent, options);

        writer.WriteEndObject();
    }

    private static string GetCategory(LogEvent logEvent, LogStreamSinkOptions options)
    {
        var category = options.DefaultCategory;

        if (logEvent.Properties.TryGetValue(options.CategoryPropertyName, out var value))
        {
            var text = value is ScalarValue { Value: { } scalar }
                ? Convert.ToString(scalar, CultureInfo.InvariantCulture)
                : value.ToString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                category = text!;
            }
        }

        return category.Length > MaxCategoryLength
            ? category.Substring(0, MaxCategoryLength)
            : category;
    }

    private static void WriteContext(
        Utf8JsonWriter writer,
        LogEvent logEvent,
        LogStreamSinkOptions options
    )
    {
        writer.WriteStartObject();

        foreach (var pair in options.StaticContext)
        {
            if (!logEvent.Properties.ContainsKey(pair.Key))
            {
                writer.WritePropertyName(pair.Key);
                WriteObject(writer, pair.Value);
            }
        }

        foreach (var property in logEvent.Properties)
        {
            writer.WritePropertyName(property.Key);
            WriteValue(writer, property.Value);
        }

        if (logEvent.SpanId is { } spanId && !logEvent.Properties.ContainsKey(SpanIdKey))
        {
            writer.WriteString(SpanIdKey, spanId.ToHexString());
        }

        if (logEvent.Exception != null)
        {
            writer.WritePropertyName(
                logEvent.Properties.ContainsKey(ExceptionKey) ? "@" + ExceptionKey : ExceptionKey
            );
            WriteException(writer, logEvent.Exception, 0);
        }

        writer.WriteEndObject();
    }

    private static void WriteException(Utf8JsonWriter writer, Exception exception, int depth)
    {
        writer.WriteStartObject();
        writer.WriteString("type", exception.GetType().FullName);
        writer.WriteString("message", exception.Message);

        if (exception.StackTrace != null)
        {
            writer.WriteString("stack_trace", exception.StackTrace);
        }

        if (exception.Source != null)
        {
            writer.WriteString("source", exception.Source);
        }

        if (depth < MaxExceptionDepth)
        {
            if (exception is AggregateException aggregate)
            {
                writer.WriteStartArray("inner_exceptions");
                foreach (var inner in aggregate.InnerExceptions)
                {
                    WriteException(writer, inner, depth + 1);
                }

                writer.WriteEndArray();
            }
            else if (exception.InnerException != null)
            {
                writer.WritePropertyName("inner_exception");
                WriteException(writer, exception.InnerException, depth + 1);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                WriteObject(writer, scalar.Value);
                break;

            case SequenceValue sequence:
                writer.WriteStartArray();
                foreach (var element in sequence.Elements)
                {
                    WriteValue(writer, element);
                }

                writer.WriteEndArray();
                break;

            case StructureValue structure:
                writer.WriteStartObject();
                if (structure.TypeTag != null)
                {
                    writer.WriteString("$type", structure.TypeTag);
                }

                foreach (var property in structure.Properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case DictionaryValue dictionary:
                writer.WriteStartObject();
                foreach (var element in dictionary.Elements)
                {
                    var key = element.Key.Value is null
                        ? "null"
                        : Convert.ToString(element.Key.Value, CultureInfo.InvariantCulture)
                            ?? string.Empty;
                    writer.WritePropertyName(key);
                    WriteValue(writer, element.Value);
                }

                writer.WriteEndObject();
                break;

            default:
                writer.WriteStringValue(value.ToString(null, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case char c:
                writer.WriteStringValue(c.ToString());
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong ul:
                writer.WriteNumberValue(ul);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                writer.WriteNumberValue(f);
                break;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                writer.WriteNumberValue(d);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
#if NET6_0_OR_GREATER
            case DateOnly date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                break;
#endif
            case TimeSpan ts:
                writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case LogEventPropertyValue propertyValue:
                WriteValue(writer, propertyValue);
                break;
            case IFormattable formattable:
                writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
