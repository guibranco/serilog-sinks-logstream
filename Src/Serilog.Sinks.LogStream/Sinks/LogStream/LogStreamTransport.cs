namespace Serilog.Sinks.LogStream;

/// <summary>
/// How the sink delivers events to the LogStream server.
/// </summary>
public enum LogStreamTransport
{
    /// <summary>
    /// Batches posted to <c>{ServerUrl}/api/logs</c>. The server confirms each batch, and failed
    /// batches are retried.
    /// </summary>
    Http = 0,

    /// <summary>
    /// One HMAC-signed UDP datagram per event, sent to <see cref="LogStreamSinkOptions.UdpHost"/>:<see cref="LogStreamSinkOptions.UdpPort"/>.
    /// Fire-and-forget: the server never replies, so delivery is not confirmed or retried.
    /// </summary>
    Udp = 1,
}
