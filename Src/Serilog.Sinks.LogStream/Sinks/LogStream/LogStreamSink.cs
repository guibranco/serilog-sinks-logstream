using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.LogStream;

/// <summary>
/// Sends batches of Serilog events to a LogStream server's <c>POST /api/logs</c> endpoint.
/// </summary>
/// <remarks>
/// Register it through <c>WriteTo.LogStream(...)</c>, which wraps it in Serilog's batching sink.
/// Transient failures (network errors, timeouts, HTTP 408, 429 and 5xx) throw, so Serilog retries
/// the batch with back-off until <c>BatchingOptions.RetryTimeLimit</c>. Other rejections are written
/// to <see cref="SelfLog"/> and the batch is dropped, since sending it again would fail the same way.
/// </remarks>
public sealed class LogStreamSink : IBatchedLogEventSink, IDisposable
{
    private readonly LogStreamSinkOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _ingestUri;
    private readonly string _userAgent;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogStreamSink"/> class.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The settings are invalid.</exception>
    public LogStreamSink(LogStreamSinkOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        if (options.HttpClient != null)
        {
            _httpClient = options.HttpClient;
        }
        else
        {
            _httpClient =
                options.MessageHandler != null
                    ? new HttpClient(options.MessageHandler, disposeHandler: true)
                    : new HttpClient();
            _httpClient.Timeout = options.RequestTimeout;
            _ownsHttpClient = true;
        }

        var baseUri = options.ServerUrl!.AbsoluteUri;
        _ingestUri = new Uri(
            new Uri(baseUri.EndsWith("/", StringComparison.Ordinal) ? baseUri : baseUri + "/"),
            "api/logs"
        );
        _userAgent = string.IsNullOrWhiteSpace(options.UserAgent)
            ? DefaultUserAgent()
            : options.UserAgent!;
    }

    /// <summary>
    /// Gets the ingest endpoint, <c>{ServerUrl}/api/logs</c>.
    /// </summary>
    public Uri IngestUri => _ingestUri;

    /// <inheritdoc />
    public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var events =
            batch.Count <= LogStreamSinkOptions.MaxBatchSize
                ? (IEnumerable<IReadOnlyCollection<LogEvent>>)new[] { batch }
                : Chunk(batch, LogStreamSinkOptions.MaxBatchSize);

        foreach (var chunk in events)
        {
            await SendAsync(chunk).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task OnEmptyBatchAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Determines whether a failed request should be retried.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns><c>true</c> for 408, 429 and 5xx.</returns>
    internal static bool IsTransient(int statusCode) =>
        statusCode == 408 || statusCode == 429 || statusCode >= 500;

    private static IEnumerable<IReadOnlyCollection<LogEvent>> Chunk(
        IEnumerable<LogEvent> source,
        int size
    )
    {
        var chunk = new List<LogEvent>(size);
        foreach (var item in source)
        {
            chunk.Add(item);
            if (chunk.Count == size)
            {
                yield return chunk;
                chunk = new List<LogEvent>(size);
            }
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    private static string DefaultUserAgent()
    {
        var sink = typeof(LogStreamSink).Assembly;
        var sinkVersion = StripMetadata(
            sink.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? sink.GetName().Version?.ToString()
                ?? "0.0.0"
        );

        var entry = Assembly.GetEntryAssembly()?.GetName();
        var product = entry?.Name is { Length: > 0 } name
            ? string.Concat(name.Replace(' ', '-'), "/", entry.Version?.ToString(3) ?? "0.0.0", " ")
            : string.Empty;

        return string.Concat(product, "(Serilog.Sinks.LogStream/", sinkVersion, ")");
    }

    private static string StripMetadata(string version)
    {
        var plus = version.IndexOf('+');
        return plus > 0 ? version.Substring(0, plus) : version;
    }

    private async Task SendAsync(IReadOnlyCollection<LogEvent> batch)
    {
        var payload = LogStreamPayload.Write(batch, _options, Guid.NewGuid().ToString());

        using var request = new HttpRequestMessage(HttpMethod.Post, _ingestUri)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        if (_options.UsesSharedSecret)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _options.ApiSecret
            );
        }
        else
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
            request.Headers.TryAddWithoutValidation("X-Api-Token", _options.ApiToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new LogStreamException("Could not reach the LogStream server.", e);
        }
        catch (TaskCanceledException e)
        {
            throw new LogStreamException("The LogStream request timed out.", e);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body =
                response.Content == null
                    ? null
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                ReportEntryErrors(body);
                return;
            }

            var message = string.Format(
                CultureInfo.InvariantCulture,
                "LogStream rejected a batch of {0} events with HTTP {1}: {2}",
                batch.Count,
                status,
                body
            );

            if (IsTransient(status))
            {
                throw new LogStreamException(message, status, body);
            }

            SelfLog.WriteLine("{0} The batch was dropped.", message);
        }
    }

    private static void ReportEntryErrors(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body!);
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
            )
            {
                SelfLog.WriteLine(
                    "LogStream rejected {0} events of a batch: {1}",
                    errors.GetArrayLength(),
                    string.Join("; ", errors.EnumerateArray().Select(e => e.ToString()))
                );
            }
        }
        catch (JsonException)
        {
            // Not JSON; the batch was still accepted.
        }
    }
}
