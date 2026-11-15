using System;
using System.Net.Http;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.LogStream;

namespace Serilog;

/// <summary>
/// Adds <c>WriteTo.LogStream()</c> to the logger configuration.
/// </summary>
public static class LoggerConfigurationLogStreamExtensions
{
    /// <summary>The default number of events per request.</summary>
    public const int DefaultBatchSizeLimit = 100;

    /// <summary>The default number of events buffered in memory before new ones are dropped.</summary>
    public const int DefaultQueueLimit = 100_000;

    /// <summary>The default longest wait before a partial batch is sent.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Writes log events to a LogStream server. Use <paramref name="apiSecret"/> with
    /// <paramref name="appKey"/> for file storage, or <paramref name="apiKey"/> and
    /// <paramref name="apiToken"/> for MariaDB storage.
    /// </summary>
    /// <param name="sinkConfiguration">The logger sink configuration.</param>
    /// <param name="serverUrl">The base address of the server, for example <c>https://logs.example.com</c>.</param>
    /// <param name="appId">The instance or environment identifier (<c>app_id</c>).</param>
    /// <param name="appKey">The application slug (<c>app_key</c>); required with <paramref name="apiSecret"/>.</param>
    /// <param name="apiSecret">The shared write secret (file storage).</param>
    /// <param name="apiKey">The client key (MariaDB storage), sent as <c>X-Api-Key</c>.</param>
    /// <param name="apiToken">The client token (MariaDB storage), sent as <c>X-Api-Token</c>.</param>
    /// <param name="restrictedToMinimumLevel">The minimum level for events passed through the sink.</param>
    /// <param name="batchSizeLimit">The most events sent in one request (at most 1000).</param>
    /// <param name="period">The longest wait before a partial batch is sent. Defaults to 2 seconds.</param>
    /// <param name="queueLimit">The most events buffered in memory; newer ones are dropped while full. <c>null</c> for no limit.</param>
    /// <param name="retryTimeLimit">How long a failing batch is retried before it is dropped. Defaults to Serilog's 10 minutes.</param>
    /// <param name="categoryPropertyName">The property that becomes the entry category. Defaults to <c>SourceContext</c>.</param>
    /// <param name="levelSwitch">A switch allowing the pass-through minimum level to be changed at runtime.</param>
    /// <param name="formatProvider">Supplies culture-specific formatting information, or <c>null</c> for the invariant culture.</param>
    /// <param name="messageHandler">An HTTP message handler for the sink's client, for example to set a proxy.</param>
    /// <returns>Configuration object allowing method chaining.</returns>
    public static LoggerConfiguration LogStream(
        this LoggerSinkConfiguration sinkConfiguration,
        string serverUrl,
        string appId,
        string? appKey = null,
        string? apiSecret = null,
        string? apiKey = null,
        string? apiToken = null,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        int batchSizeLimit = DefaultBatchSizeLimit,
        TimeSpan? period = null,
        int? queueLimit = DefaultQueueLimit,
        TimeSpan? retryTimeLimit = null,
        string categoryPropertyName = "SourceContext",
        LoggingLevelSwitch? levelSwitch = null,
        IFormatProvider? formatProvider = null,
        HttpMessageHandler? messageHandler = null
    )
    {
        if (serverUrl is null)
        {
            throw new ArgumentNullException(nameof(serverUrl));
        }

        var options = new LogStreamSinkOptions
        {
            ServerUrl = new Uri(serverUrl, UriKind.Absolute),
            AppId = appId,
            AppKey = appKey,
            ApiSecret = apiSecret,
            ApiKey = apiKey,
            ApiToken = apiToken,
            CategoryPropertyName = categoryPropertyName,
            FormatProvider = formatProvider,
            MessageHandler = messageHandler,
        };

        var batching = new BatchingOptions
        {
            BatchSizeLimit = batchSizeLimit,
            BufferingTimeLimit = period ?? DefaultPeriod,
            QueueLimit = queueLimit,
        };

        if (retryTimeLimit.HasValue)
        {
            batching.RetryTimeLimit = retryTimeLimit.Value;
        }

        return sinkConfiguration.LogStream(
            options,
            batching,
            restrictedToMinimumLevel,
            levelSwitch
        );
    }

    /// <summary>
    /// Writes log events to a LogStream server, configured through <see cref="LogStreamSinkOptions"/>.
    /// </summary>
    /// <param name="sinkConfiguration">The logger sink configuration.</param>
    /// <param name="configure">Sets the sink options.</param>
    /// <param name="configureBatching">Optionally changes the batching options (batch size, period, queue and retry limits).</param>
    /// <param name="restrictedToMinimumLevel">The minimum level for events passed through the sink.</param>
    /// <param name="levelSwitch">A switch allowing the pass-through minimum level to be changed at runtime.</param>
    /// <returns>Configuration object allowing method chaining.</returns>
    public static LoggerConfiguration LogStream(
        this LoggerSinkConfiguration sinkConfiguration,
        Action<LogStreamSinkOptions> configure,
        Action<BatchingOptions>? configureBatching = null,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        LoggingLevelSwitch? levelSwitch = null
    )
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new LogStreamSinkOptions();
        configure(options);

        var batching = new BatchingOptions
        {
            BatchSizeLimit = DefaultBatchSizeLimit,
            BufferingTimeLimit = DefaultPeriod,
            QueueLimit = DefaultQueueLimit,
        };
        configureBatching?.Invoke(batching);

        return sinkConfiguration.LogStream(
            options,
            batching,
            restrictedToMinimumLevel,
            levelSwitch
        );
    }

    private static LoggerConfiguration LogStream(
        this LoggerSinkConfiguration sinkConfiguration,
        LogStreamSinkOptions options,
        BatchingOptions batching,
        LogEventLevel restrictedToMinimumLevel,
        LoggingLevelSwitch? levelSwitch
    )
    {
        if (sinkConfiguration is null)
        {
            throw new ArgumentNullException(nameof(sinkConfiguration));
        }

        if (
            batching.BatchSizeLimit < 1
            || batching.BatchSizeLimit > LogStreamSinkOptions.MaxBatchSize
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(batching),
                batching.BatchSizeLimit,
                "The batch size limit must be between 1 and 1000."
            );
        }

        var sink = new LogStreamSink(options);
        return sinkConfiguration.Sink(sink, batching, restrictedToMinimumLevel, levelSwitch);
    }
}
