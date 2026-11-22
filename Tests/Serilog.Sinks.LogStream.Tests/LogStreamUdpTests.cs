using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog.Debugging;
using Serilog.Sinks.LogStream.Tests.Support;
using Xunit;

namespace Serilog.Sinks.LogStream.Tests;

[Collection("SelfLog")]
public sealed class LogStreamUdpTests : IDisposable
{
    private readonly UdpClient _receiver = new(new IPEndPoint(IPAddress.Loopback, 0));

    private int Port => ((IPEndPoint)_receiver.Client.LocalEndPoint!).Port;

    public void Dispose() => _receiver.Dispose();

    private LogStreamSink CreateSink(
        RecordingHandler handler = null,
        Action<LogStreamSinkOptions> configure = null
    )
    {
        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri("http://127.0.0.1"),
            AppId = "production",
            ApiKey = "billing-api",
            ApiToken = "lss_token",
            UserAgent = "BillingService/2.1.0",
            Transport = LogStreamTransport.Udp,
            UdpPort = Port,
            HttpClient = new HttpClient(handler ?? new RecordingHandler(), disposeHandler: false),
        };
        configure?.Invoke(options);
        return new LogStreamSink(options);
    }

    private async Task<byte[]> ReceiveAsync()
    {
        var receive = _receiver.ReceiveAsync();
        var winner = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == receive, "No datagram received.");
        return receive.Result.Buffer;
    }

    private void AssertNothingReceived()
    {
        _receiver.Client.ReceiveTimeout = 300;
        var remote = new IPEndPoint(IPAddress.Any, 0);
        Assert.Throws<SocketException>(() => _receiver.Receive(ref remote));
    }

    /// <summary>The raw text of the top-level "log" value: the packet ends with it.</summary>
    private static string RawLog(byte[] packet)
    {
        var text = Encoding.UTF8.GetString(packet);
        var start = text.IndexOf("\"log\":", StringComparison.Ordinal) + 6;
        return text.Substring(start, text.Length - start - 1);
    }

    private static void AssertSignedWith(byte[] packet, string secret)
    {
        using var document = JsonDocument.Parse(packet);
        var root = document.RootElement;
        var message =
            $"{root.GetProperty("key").GetString()}:{root.GetProperty("ts").GetInt64()}:{root.GetProperty("nonce").GetString()}:{RawLog(packet)}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert
            .ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)))
            .ToLowerInvariant();
        Assert.Equal(expected, root.GetProperty("sig").GetString());
    }

    [Fact]
    public async Task EachEventShouldBeSentAsOneSignedDatagram()
    {
        using var handler = new RecordingHandler();
        using var sink = CreateSink(handler);

        await sink.EmitBatchAsync(
            TestEvents
                .Capture(l =>
                {
                    l.ForContext("SourceContext", "Billing.Charges")
                        .Information("Charged {Amount} to {Customer}", 12.5m, "café ü");
                    l.Error(new InvalidOperationException("Boom"), "Charge failed");
                })
                .ToList()
        );

        var first = await ReceiveAsync();
        var second = await ReceiveAsync();
        Assert.Empty(handler.Requests);

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("billing-api", root.GetProperty("key").GetString());
        Assert.InRange(
            root.GetProperty("ts").GetInt64(),
            DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()
        );
        Assert.Matches("^[0-9a-f]{32}$", root.GetProperty("nonce").GetString());
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("sig").GetString());

        var log = root.GetProperty("log");
        Assert.Equal("production", log.GetProperty("app_id").GetString());
        Assert.Equal("BillingService/2.1.0", log.GetProperty("user_agent").GetString());
        Assert.Equal("info", log.GetProperty("level").GetString());
        Assert.Equal("Billing.Charges", log.GetProperty("category").GetString());
        Assert.Equal("Charged 12.5 to \"café ü\"", log.GetProperty("message").GetString());
        Assert.Equal(12.5m, log.GetProperty("context").GetProperty("Amount").GetDecimal());
        Assert.False(log.TryGetProperty("app_key", out _));
        AssertSignedWith(first, "lss_token");

        using var secondDocument = JsonDocument.Parse(second);
        var secondLog = secondDocument.RootElement.GetProperty("log");
        Assert.Equal("error", secondLog.GetProperty("level").GetString());
        Assert.Equal(
            "Boom",
            secondLog
                .GetProperty("context")
                .GetProperty("exception")
                .GetProperty("message")
                .GetString()
        );
        Assert.Equal(
            log.GetProperty("batch_id").GetString(),
            secondLog.GetProperty("batch_id").GetString()
        );
        Assert.NotEqual(
            root.GetProperty("nonce").GetString(),
            secondDocument.RootElement.GetProperty("nonce").GetString()
        );
        AssertSignedWith(second, "lss_token");
    }

    [Fact]
    public async Task FileStorageShouldSignWithTheAppKeyAndSharedSecret()
    {
        using var sink = CreateSink(configure: o =>
        {
            o.ApiKey = null;
            o.ApiToken = null;
            o.AppKey = "billing-api-file";
            o.ApiSecret = "lsa_secret";
        });

        await sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList());

        var packet = await ReceiveAsync();
        using var document = JsonDocument.Parse(packet);
        Assert.Equal("billing-api-file", document.RootElement.GetProperty("key").GetString());
        AssertSignedWith(packet, "lsa_secret");
    }

    [Fact]
    public async Task OversizedEventsShouldBePostedOverHttp()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Created);
        using var sink = CreateSink(handler, o => o.UdpMaxPacketSize = 1024);

        await sink.EmitBatchAsync(
            TestEvents
                .Capture(l =>
                {
                    l.Information("small");
                    l.Information("{Big}", new string('x', 4000));
                })
                .ToList()
        );

        var packet = await ReceiveAsync();
        using var document = JsonDocument.Parse(packet);
        Assert.Equal(
            "small",
            document.RootElement.GetProperty("log").GetProperty("message").GetString()
        );

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("http://127.0.0.1/api/logs"), request.Uri);
        var entry = Assert.Single(request.Logs.EnumerateArray());
        Assert.Equal(4002, entry.GetProperty("message").GetString()!.Length);
        AssertNothingReceived();
    }

    [Fact]
    public async Task AFailedHttpFallbackShouldThrowBeforeAnyDatagramIsSent()
    {
        using var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var sink = CreateSink(handler, o => o.UdpMaxPacketSize = 1024);

        await Assert.ThrowsAsync<LogStreamException>(() =>
            sink.EmitBatchAsync(
                TestEvents
                    .Capture(l =>
                    {
                        l.Information("small");
                        l.Information("{Big}", new string('x', 4000));
                    })
                    .ToList()
            )
        );

        AssertNothingReceived();
    }

    [Fact]
    public async Task OversizedEventsShouldBeSelfLoggedAndDroppedWithoutFallback()
    {
        var selfLog = new StringBuilder();
        SelfLog.Enable(new StringWriter(selfLog));
        try
        {
            using var handler = new RecordingHandler();
            using var sink = CreateSink(handler, o =>
            {
                o.UdpMaxPacketSize = 1024;
                o.UdpFallbackToHttp = false;
            });

            await sink.EmitBatchAsync(
                TestEvents.Capture(l => l.Information("{Big}", new string('x', 4000))).ToList()
            );

            Assert.Empty(handler.Requests);
            Assert.Contains(
                "dropped 1 events larger than the 1024-byte UDP packet limit",
                selfLog.ToString()
            );
            AssertNothingReceived();
        }
        finally
        {
            SelfLog.Disable();
        }
    }

    [Fact]
    public async Task AnUnresolvableHostShouldThrowSoSerilogRetries()
    {
        using var sink = CreateSink(configure: o => o.UdpHost = "logstream.invalid");

        await Assert.ThrowsAsync<LogStreamException>(() =>
            sink.EmitBatchAsync(TestEvents.Capture(l => l.Information("Hello")).ToList())
        );
    }

    [Fact]
    public void TheUdpHostShouldDefaultToTheServerHost()
    {
        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri("https://logs.example.com:8443/base"),
        };
        Assert.Equal("logs.example.com", options.ResolvedUdpHost);

        options.UdpHost = "10.0.0.5";
        Assert.Equal("10.0.0.5", options.ResolvedUdpHost);
    }

    [Theory]
    [InlineData(0, 8192)]
    [InlineData(65536, 8192)]
    [InlineData(8082, 255)]
    [InlineData(8082, 65508)]
    public void InvalidUdpSettingsShouldBeRejected(int port, int maxPacketSize)
    {
        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri("https://logs.example.com"),
            AppId = "production",
            ApiKey = "billing-api",
            ApiToken = "lss_token",
            Transport = LogStreamTransport.Udp,
            UdpPort = port,
            UdpMaxPacketSize = maxPacketSize,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ThePacketShouldMatchTheDocumentedFormat()
    {
        var log = Encoding.UTF8.GetBytes(
            "{\"app_id\":\"production\",\"message\":\"Charge processed\"}"
        );

        var packet = LogStreamUdpPacket.Build(
            "billing-api",
            "secret",
            1748000000000,
            "9f2c4e1ab37d5c60",
            log
        );

        // Reference value: PHP hash_hmac('sha256', 'billing-api:1748000000000:9f2c4e1ab37d5c60:' . $log, 'secret').
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("secret"));
        var sig = Convert
            .ToHexString(
                hmac.ComputeHash(
                    Encoding.UTF8.GetBytes(
                        "billing-api:1748000000000:9f2c4e1ab37d5c60:{\"app_id\":\"production\",\"message\":\"Charge processed\"}"
                    )
                )
            )
            .ToLowerInvariant();
        Assert.Equal(
            "{\"v\":1,\"key\":\"billing-api\",\"ts\":1748000000000,\"nonce\":\"9f2c4e1ab37d5c60\",\"sig\":\""
                + sig
                + "\",\"log\":{\"app_id\":\"production\",\"message\":\"Charge processed\"}}",
            Encoding.UTF8.GetString(packet)
        );
    }

    [Fact]
    public void TheTransportShouldBeConfigurableFromSettings()
    {
        using var logger = new LoggerConfiguration()
            .WriteTo.LogStream(
                serverUrl: "http://127.0.0.1",
                appId: "production",
                apiKey: "billing-api",
                apiToken: "lss_token",
                transport: LogStreamTransport.Udp,
                udpHost: "127.0.0.1",
                udpPort: Port,
                period: TimeSpan.FromMilliseconds(10)
            )
            .CreateLogger();

        logger.Information("From the extension method");
        logger.Dispose();

        var remote = new IPEndPoint(IPAddress.Any, 0);
        _receiver.Client.ReceiveTimeout = 5000;
        var packet = _receiver.Receive(ref remote);
        using var document = JsonDocument.Parse(packet);
        Assert.Equal(
            "From the extension method",
            document.RootElement.GetProperty("log").GetProperty("message").GetString()
        );
    }
}
