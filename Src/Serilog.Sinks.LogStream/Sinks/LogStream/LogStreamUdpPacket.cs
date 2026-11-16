using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Serilog.Sinks.LogStream;

/// <summary>
/// Builds LogStream UDP packets (protocol v1):
/// <c>{"v":1,"key":"…","ts":&lt;ms&gt;,"nonce":"…","sig":"&lt;hex&gt;","log":{…}}</c>.
/// </summary>
/// <remarks>
/// <c>sig</c> is <c>hex(HMAC-SHA256(secret, "{key}:{ts}:{nonce}:{log}"))</c>, where <c>{log}</c> is the
/// exact JSON text embedded in the packet, so the log bytes are signed and then written unchanged.
/// </remarks>
internal static class LogStreamUdpPacket
{
    /// <summary>The protocol version.</summary>
    public const int Version = 1;

    /// <summary>The server's default <c>UDP_PORT</c>.</summary>
    public const int DefaultPort = 8082;

    /// <summary>The server's default <c>UDP_MAX_PACKET_SIZE</c>, in bytes.</summary>
    public const int DefaultMaxPacketSize = 8192;

    /// <summary>The smallest packet size limit the server accepts.</summary>
    public const int MinPacketSize = 256;

    /// <summary>The largest payload a UDP datagram can carry over IPv4.</summary>
    public const int MaxDatagramSize = 65_507;

    private const int NonceBytes = 16;

#if !NET6_0_OR_GREATER
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();
#endif

    /// <summary>
    /// Builds a signed packet.
    /// </summary>
    /// <param name="key">The app key (the client's API key in MariaDB mode).</param>
    /// <param name="secret">The client's API token, or the shared API secret in file mode.</param>
    /// <param name="timestampMs">The send time, in Unix milliseconds.</param>
    /// <param name="nonce">A unique value of 16–64 characters of <c>A-Z a-z 0-9 _ -</c>.</param>
    /// <param name="log">The UTF-8 JSON of one log entry.</param>
    /// <returns>The UTF-8 packet.</returns>
    public static byte[] Build(
        string key,
        string secret,
        long timestampMs,
        string nonce,
        byte[] log
    )
    {
        var signature = Sign(key, secret, timestampMs, nonce, log);

        using var stream = new MemoryStream(log.Length + 192);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("key", key);
            writer.WriteNumber("ts", timestampMs);
            writer.WriteString("nonce", nonce);
            writer.WriteString("sig", signature);
            writer.WritePropertyName("log");
            writer.WriteRawValue(log, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Computes the packet signature.
    /// </summary>
    /// <param name="key">The app key.</param>
    /// <param name="secret">The signing secret.</param>
    /// <param name="timestampMs">The packet timestamp.</param>
    /// <param name="nonce">The packet nonce.</param>
    /// <param name="log">The UTF-8 JSON of the log entry, as embedded in the packet.</param>
    /// <returns>The lowercase hex HMAC-SHA256.</returns>
    public static string Sign(string key, string secret, long timestampMs, string nonce, byte[] log)
    {
        var prefix = Encoding.UTF8.GetBytes(
            string.Concat(
                key,
                ":",
                timestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ":",
                nonce,
                ":"
            )
        );
        var message = new byte[prefix.Length + log.Length];
        Buffer.BlockCopy(prefix, 0, message, 0, prefix.Length);
        Buffer.BlockCopy(log, 0, message, prefix.Length, log.Length);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return ToHex(hmac.ComputeHash(message));
    }

    /// <summary>
    /// Creates a random nonce: 16 random bytes as 32 lowercase hex characters.
    /// </summary>
    /// <returns>The nonce.</returns>
    public static string NewNonce()
    {
        var bytes = new byte[NonceBytes];
#if NET6_0_OR_GREATER
        RandomNumberGenerator.Fill(bytes);
#else
        lock (Random)
        {
            Random.GetBytes(bytes);
        }
#endif
        return ToHex(bytes);
    }

    /// <summary>
    /// Gets the current time in Unix milliseconds.
    /// </summary>
    /// <returns>The timestamp.</returns>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i * 2] = HexDigit(b >> 4);
            chars[(i * 2) + 1] = HexDigit(b & 0xF);
        }

        return new string(chars);
    }

    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);
}
