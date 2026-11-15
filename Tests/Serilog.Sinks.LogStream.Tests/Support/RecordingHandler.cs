using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Serilog.Sinks.LogStream.Tests.Support;

public sealed class RecordedRequest
{
    public HttpMethod Method { get; init; }

    public Uri Uri { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; }

    public JsonElement Json { get; init; }

    public JsonElement Logs => Json.GetProperty("logs");
}

/// <summary>
/// Records every request and answers with the queued status codes (the last one repeats).
/// </summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<HttpStatusCode> _statuses;
    private readonly HttpStatusCode _last;
    private readonly List<RecordedRequest> _requests = new();
    private readonly SemaphoreSlim _received = new(0);

    public RecordingHandler(params HttpStatusCode[] statuses)
    {
        _statuses = new Queue<HttpStatusCode>(statuses);
        _last = statuses.Length > 0 ? statuses[^1] : HttpStatusCode.Accepted;
    }

    public string ResponseBody { get; init; } = "{\"accepted\":1,\"entries\":[],\"errors\":null}";

    public Exception Throw { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public async Task<bool> WaitForRequestAsync(TimeSpan timeout) =>
        await _received.WaitAsync(timeout);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (Throw != null)
        {
            throw Throw;
        }

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var headers = request
            .Headers.Concat(request.Content.Headers)
            .ToDictionary(
                h => h.Key,
                h => string.Join(",", h.Value),
                StringComparer.OrdinalIgnoreCase
            );

        lock (_requests)
        {
            _requests.Add(
                new RecordedRequest
                {
                    Method = request.Method,
                    Uri = request.RequestUri,
                    Headers = headers,
                    Json = JsonDocument.Parse(body).RootElement.Clone(),
                }
            );
        }

        HttpStatusCode status;
        lock (_statuses)
        {
            status = _statuses.Count > 0 ? _statuses.Dequeue() : _last;
        }

        _received.Release();

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _received.Dispose();
        }

        base.Dispose(disposing);
    }
}
