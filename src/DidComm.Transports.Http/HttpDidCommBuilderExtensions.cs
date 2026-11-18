using System.Net.Sockets;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    /// <see cref="HttpTransportOptions.TrustedProxy"/> carries them; the transport then checks each
    /// destination (and each followed 307) before the request is sent.
    /// </summary>
    /// <param name="builder">The DidComm DI builder.</param>
    /// <param name="configure">Optional <see cref="HttpTransportOptions"/> configuration callback.</param>
    public static DidCommBuilder UseHttpTransport(this DidCommBuilder builder, Action<HttpTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddOptions<HttpTransportOptions>();
        builder.Services.TryAddSingleton(sp => new TrustedProxySnapshot(sp.GetRequiredService<IOptions<HttpTransportOptions>>()));

        builder.Services
            .AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                // Single source of truth (#27): use this transport's explicit policy if set, else inherit
                // the core DidCommOptions.OutboundEndpointPolicy (the same instance the facade's pre-send
                // check uses), else a fresh default. So configuring the policy in one place governs both.
                var guard = new OutboundEndpointGuard(ResolvePolicy(sp));
                // Read and copied once per container, then reused by every handler build (TrustedProxySnapshot).
                var route = sp.GetRequiredService<TrustedProxySnapshot>().Route;
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
                    // The named proxy is the host's choice and is dialled unfiltered, when the handler
                    // uses it as a proxy; any other connection (a destination the proxy bypasses, even
                    // one naming the proxy's own host and port) gets the full guard.
                    ConnectCallback = async (context, ct) =>
                    {
                        Socket socket;
                        if (route is not null && route.IsProxyConnection(context))
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
                // run in the transport itself, before each hop is sent (HttpDidCommTransport.SendAsync).
                // The handler stays a SocketsHttpHandler, so a host's UseSocketsHttpHandler or
                // ConfigurePrimaryHttpMessageHandler tweak adjusts this one instead of replacing it.
                return sockets;
            });

        // An explicit factory: the transport gets the same snapshot and the same inherited policy as
        // the handler, for its own destination check when a TrustedProxy is set.
        builder.Services.AddSingleton<IDidCommTransport>(sp => new HttpDidCommTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>(),
            sp.GetService<ILogger<HttpDidCommTransport>>(),
            sp.GetRequiredService<TrustedProxySnapshot>().Route,
            ResolvePolicy(sp)));
        // The router is normally registered by AddDidComm, but in case a host calls this
        // extension without going through AddDidComm we register it idempotently.
        builder.Services.TryAddSingleton<ITransportRouter, TransportRouter>();
        return builder;
    }

    /// <summary>
    /// Single source of truth (#27): this transport's explicit policy if set, else the core
    /// <c>DidCommOptions.OutboundEndpointPolicy</c> (the same instance the facade's pre-send check
    /// uses), else a fresh default.
    /// </summary>
    /// <param name="sp">The provider.</param>
    private static OutboundEndpointPolicy ResolvePolicy(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.OutboundEndpointPolicy
        ?? sp.GetService<IOptions<DidCommOptions>>()?.Value.OutboundEndpointPolicy
        ?? new OutboundEndpointPolicy();
}
