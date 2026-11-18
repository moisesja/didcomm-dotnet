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
/// <example>
/// Registered in <c>samples/02-Cookbook</c> (section A) and used end-to-end in <c>samples/04-MediatorAgent</c>.
/// </example>
public static class HttpDidCommBuilderExtensions
{
    /// <summary>
    /// Register the HTTPS transport. Adds an <see cref="IHttpClientFactory"/> binding for the
    /// named client <c>"didcomm"</c>. Auto-redirect is disabled at the handler level (the transport
    /// follows 307 manually so it can enforce the FR-TRN-06 rule — no 301/308 follow), and a
    /// <c>SocketsHttpHandler.ConnectCallback</c> applies the SSRF
    /// <see cref="HttpTransportOptions.OutboundEndpointPolicy"/> at TCP connect time so every
    /// connection — including each followed redirect — is pinned to a vetted, non-private IP.
    /// Connections are direct: no proxy from the environment is used. Only a proxy the host names in
    /// <see cref="HttpTransportOptions.TrustedProxy"/> carries them; each request is then checked
    /// before it is handed to that proxy.
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
                var options = sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value;
                var policy = options.OutboundEndpointPolicy
                    ?? sp.GetService<IOptions<DidCommOptions>>()?.Value.OutboundEndpointPolicy
                    ?? new OutboundEndpointPolicy();
                var guard = new OutboundEndpointGuard(policy);
                // Read and copied on every handler build, as the policy is (HttpTransportOptions.TrustedProxy).
                var route = TrustedProxyRoute.From(options.TrustedProxy);
                var sockets = new SocketsHttpHandler
                {
                    // The transport follows 307 manually so it can enforce the FR-TRN-06 rule
                    // (no 301/308 follow) — disable auto-redirect here.
                    AllowAutoRedirect = false,
                    // FR-TRN-13: never the process proxy (HttpClient.DefaultProxy: HTTP_PROXY, HTTPS_PROXY,
                    // ALL_PROXY, the system proxy). Through a proxy the callback below would vet the
                    // proxy's socket, not the destination's. Only a proxy the host names is used.
                    UseProxy = route is not null,
                    Proxy = route?.Proxy,
                    // SSRF defense: resolve + vet the target IP and pin the TCP connection to it.
                    // This runs for the initial host and for every manually followed 307 hop, so a
                    // redirect to an internal host (or a DNS-rebind) cannot reach a private address.
                    // The named proxy is the host's choice and is dialled unfiltered; any other
                    // endpoint (a destination the proxy bypasses) gets the full guard.
                    ConnectCallback = async (context, ct) =>
                    {
                        Socket socket;
                        if (route is not null && route.IsProxyEndpoint(context.DnsEndPoint))
                        {
                            socket = await TrustedProxyRoute.ConnectToProxyAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            try
                            {
                                socket = await guard.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                            }
                            catch (SocketException ex) when (route is null)
                            {
                                throw TrustedProxyRoute.WithDirectHint(ex);
                            }
                        }
                        Stream stream = new NetworkStream(socket, ownsSocket: true);
                        return stream;
                    },
                };
                // Through a proxy the callback sees only the proxy, so the checks that need no socket
                // run on each request (each attempt, each followed 307) before the proxy is asked.
                return route is null ? sockets : new ProxiedDestinationCheck(guard) { InnerHandler = sockets };
            });

        builder.Services.AddSingleton<IDidCommTransport, HttpDidCommTransport>();
        // The router is normally registered by AddDidComm, but in case a host calls this
        // extension without going through AddDidComm we register it idempotently.
        builder.Services.TryAddSingleton<ITransportRouter, TransportRouter>();
        return builder;
    }
}
