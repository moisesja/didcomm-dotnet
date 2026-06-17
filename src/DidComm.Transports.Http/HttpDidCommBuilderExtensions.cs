using System.Net.Sockets;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DidComm.Transports.Http;

/// <summary>
/// Wires <see cref="HttpDidCommTransport"/> into the DI graph from the
/// <see cref="DidCommBuilder"/> chain (Phase 5 / FR-TRN-01).
/// </summary>
public static class HttpDidCommBuilderExtensions
{
    /// <summary>
    /// Register the HTTPS transport. Adds an <see cref="IHttpClientFactory"/> binding for the
    /// named client <c>"didcomm"</c>. Auto-redirect is disabled at the handler level (the transport
    /// follows 307 manually so it can enforce the FR-TRN-06 rule — no 301/308 follow), and a
    /// <c>SocketsHttpHandler.ConnectCallback</c> applies the SSRF
    /// <see cref="HttpTransportOptions.OutboundEndpointPolicy"/> at TCP connect time so every
    /// connection — including each followed redirect — is pinned to a vetted, non-private IP.
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
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                // Single source of truth (#27): use this transport's explicit policy if set, else inherit
                // the core DidCommOptions.OutboundEndpointPolicy (the same instance the facade's pre-send
                // check uses), else a fresh default. So configuring the policy in one place governs both.
                var policy = sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.OutboundEndpointPolicy
                    ?? sp.GetService<IOptions<DidCommOptions>>()?.Value.OutboundEndpointPolicy
                    ?? new OutboundEndpointPolicy();
                var guard = new OutboundEndpointGuard(policy);
                return new SocketsHttpHandler
                {
                    // The transport follows 307 manually so it can enforce the FR-TRN-06 rule
                    // (no 301/308 follow) — disable auto-redirect here.
                    AllowAutoRedirect = false,
                    // SSRF defense: resolve + vet the target IP and pin the TCP connection to it.
                    // This runs for the initial host and for every manually followed 307 hop, so a
                    // redirect to an internal host (or a DNS-rebind) cannot reach a private address.
                    ConnectCallback = async (context, ct) =>
                    {
                        var socket = await guard.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                        Stream stream = new NetworkStream(socket, ownsSocket: true);
                        return stream;
                    },
                };
            });

        builder.Services.AddSingleton<IDidCommTransport, HttpDidCommTransport>();
        // The router is normally registered by AddDidComm, but in case a host calls this
        // extension without going through AddDidComm we register it idempotently.
        builder.Services.TryAddSingleton<ITransportRouter, TransportRouter>();
        return builder;
    }
}
