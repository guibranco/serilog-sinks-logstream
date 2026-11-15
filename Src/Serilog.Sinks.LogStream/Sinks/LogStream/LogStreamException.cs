using System;

namespace Serilog.Sinks.LogStream;

/// <summary>
/// Thrown to Serilog's batching pipeline when a batch should be retried (network errors, timeouts,
/// HTTP 408, 429 and 5xx).
/// </summary>
public class LogStreamException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LogStreamException"/> class.
    /// </summary>
    public LogStreamException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogStreamException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public LogStreamException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogStreamException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public LogStreamException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogStreamException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="statusCode">The HTTP status code returned by the server.</param>
    /// <param name="responseBody">The response body returned by the server.</param>
    public LogStreamException(string message, int statusCode, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>
    /// Gets the HTTP status code returned by the server, when there was a response.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Gets the response body returned by the server, when there was a response.
    /// </summary>
    public string? ResponseBody { get; }
}
