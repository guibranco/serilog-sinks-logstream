using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Serilog.Sinks.LogStream;

/// <summary>
/// Settings for the LogStream sink.
/// </summary>
/// <remarks>
/// LogStream accepts two kinds of write credentials:
/// <list type="bullet">
/// <item><description>File storage: one shared secret, sent as <c>Authorization: Bearer &lt;API_SECRET&gt;</c>. Set <see cref="ApiSecret"/> and <see cref="AppKey"/>.</description></item>
/// <item><description>MariaDB storage: a per-client key and token, sent as <c>X-Api-Key</c> and <c>X-Api-Token</c>. Set <see cref="ApiKey"/> and <see cref="ApiToken"/>; the server uses the key as the app key.</description></item>
/// </list>
/// </remarks>
public sealed class LogStreamSinkOptions
{
    /// <summary>
    /// The largest batch the LogStream server accepts in one request.
    /// </summary>
    public const int MaxBatchSize = 1000;

    /// <summary>
    /// Gets or sets the base address of the LogStream server, for example <c>https://logs.example.com</c>.
    /// Events are posted to <c>{ServerUrl}/api/logs</c>.
    /// </summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>
    /// Gets or sets the application slug (<c>app_key</c>). Required with <see cref="ApiSecret"/>.
    /// </summary>
    public string? AppKey { get; set; }

    /// <summary>
    /// Gets or sets the instance or environment identifier (<c>app_id</c>), for example <c>production</c>.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// Gets or sets the shared write secret (file storage mode).
    /// </summary>
    public string? ApiSecret { get; set; }

    /// <summary>
    /// Gets or sets the client key (MariaDB storage mode), sent as <c>X-Api-Key</c>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the client token (MariaDB storage mode), sent as <c>X-Api-Token</c>.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// Gets or sets the name of the property whose value becomes the entry category.
    /// Defaults to <c>SourceContext</c>, the logger name set by <c>ForContext&lt;T&gt;()</c>.
    /// </summary>
    public string CategoryPropertyName { get; set; } = "SourceContext";

    /// <summary>
    /// Gets or sets the category used when the event has no category property. Defaults to <c>general</c>.
    /// </summary>
    public string DefaultCategory { get; set; } = "general";

    /// <summary>
    /// Gets or sets a value indicating whether the message template is sent as <c>message_template</c>,
    /// so LogStream can group every event logged from the same line. Defaults to <c>true</c>.
    /// </summary>
    public bool IncludeMessageTemplate { get; set; } = true;

    /// <summary>
    /// Gets the properties added to the context of every entry. Event properties with the same name win.
    /// </summary>
    public IDictionary<string, object?> StaticContext { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the format provider used to render messages. Defaults to the invariant culture.
    /// </summary>
    public IFormatProvider? FormatProvider { get; set; }

    /// <summary>
    /// Gets or sets the <c>User-Agent</c> header, which LogStream stores as the application identifier.
    /// Defaults to <c>{entry assembly}/{version} (Serilog.Sinks.LogStream/{version})</c>.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Gets or sets the HTTP request timeout used when the sink creates its own client. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the HTTP message handler used when the sink creates its own client
    /// (for example to set a proxy). The sink disposes it.
    /// </summary>
    public HttpMessageHandler? MessageHandler { get; set; }

    /// <summary>
    /// Gets or sets an HTTP client to use instead of creating one, for example from <c>IHttpClientFactory</c>.
    /// The sink does not dispose it.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Gets a value indicating whether the shared-secret (Bearer) mode is used.
    /// </summary>
    internal bool UsesSharedSecret => !string.IsNullOrWhiteSpace(ApiSecret);

    /// <summary>
    /// Validates the settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">A required setting is missing or invalid.</exception>
    public void Validate()
    {
        if (ServerUrl is null || !ServerUrl.IsAbsoluteUri)
        {
            throw new InvalidOperationException("An absolute LogStream server URL is required.");
        }

        if (ServerUrl.Scheme != Uri.UriSchemeHttp && ServerUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The LogStream server URL must use http or https.");
        }

        if (string.IsNullOrWhiteSpace(AppId))
        {
            throw new InvalidOperationException("The LogStream app_id is required.");
        }

        var hasClientCredentials =
            !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiToken);
        if (!UsesSharedSecret && !hasClientCredentials)
        {
            throw new InvalidOperationException(
                "Set either ApiSecret (file storage) or both ApiKey and ApiToken (MariaDB storage)."
            );
        }

        if (UsesSharedSecret && string.IsNullOrWhiteSpace(AppKey))
        {
            throw new InvalidOperationException(
                "The LogStream app_key is required with ApiSecret."
            );
        }

        if (string.IsNullOrWhiteSpace(CategoryPropertyName))
        {
            throw new InvalidOperationException("CategoryPropertyName cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(DefaultCategory))
        {
            throw new InvalidOperationException("DefaultCategory cannot be empty.");
        }
    }
}
