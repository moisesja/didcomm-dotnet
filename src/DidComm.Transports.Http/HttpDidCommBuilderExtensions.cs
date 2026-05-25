using DidComm.Extensions.DependencyInjection;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DidComm.Transports.Http;

/// <summary>
/// Wires <see cref="HttpDidCommTransport"/> into the DI graph from the
/// <see cref="DidCommBuilder"/> chain (Phase 5 / FR-TRN-01).
/// </summary>
public static class HttpDidCommBuilderExtensions
{
    /// <summary>
    /// Register the HTTPS transport. Adds an <see cref="IHttpClientFactory"/> binding for the
    /// named client <c>"didcomm"</c> and enables auto-redirect at the handler level (the
    /// transport implementation overrides this so 307 is followed manually and 301/308 are
    /// refused per FR-TRN-06).
    /// </summary>
    /// <param name="builder">The DidComm DI builder.</param>
    /// <param name="configure">Optional <see cref="HttpTransportOptions"/> configuration callback.</param>
    public static DidCommBuilder UseHttpTransport(this DidCommBuilder builder, Action<HttpTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddOptions<HttpTransportOptions>();

        builder.Services
            .AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => new HttpClientHandler
            {
                // The transport follows 307 manually so it can enforce the FR-TRN-06 rule
                // (no 301/308 follow) — disable auto-redirect here.
                AllowAutoRedirect = false,
            });

        builder.Services.AddSingleton<IDidCommTransport, HttpDidCommTransport>();
        // The router is normally registered by AddDidComm, but in case a host calls this
        // extension without going through AddDidComm we register it idempotently.
        builder.Services.TryAddSingleton<ITransportRouter, TransportRouter>();
        return builder;
    }
}
