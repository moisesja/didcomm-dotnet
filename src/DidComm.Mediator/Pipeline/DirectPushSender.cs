using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The mediator's own single-attempt HTTP push (#110, D6 step 3; invariant D-7): one POST of a stored
/// message to the endpoint the host's <see cref="IRecipientEndpointDirectory"/> named, on a private
/// <see cref="HttpDidCommTransport"/> that never retries, whose circuit breaker can never open, and
/// that follows only 307 redirects, up to the host's <see cref="HttpTransportOptions.MaxRedirectHops"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a private transport.</strong> The HTTP transport's retries and circuit breaker live
/// in each transport instance. Pushing through the host's own transport would let one failing
/// recipient endpoint open the breaker for every other recipient and for the host's own sends, and
/// would repeat each failed POST up to <see cref="HttpTransportOptions.MaxRetryAttempts"/> times. The
/// private instance is built through the transport's public constructor. It is never registered as
/// an <see cref="IDidCommTransport"/>, so the host's own sends never use it.
/// </para>
/// <para>
/// <strong>The named client's innermost handler only.</strong> The push sends through an
/// <see cref="HttpClient"/> of its own over the innermost (primary) handler of the host's named
/// <c>"didcomm"</c> client, which <c>UseHttpTransport()</c> configures: the outbound address check
/// when each connection opens (each followed 307 included), no automatic redirects, direct
/// connections unless the host named a <see cref="HttpTransportOptions.TrustedProxy"/>. Handlers a
/// host puts in front of it — for every client (<c>ConfigureHttpClientDefaults</c>, such as the
/// resilience handler of .NET Aspire's service defaults, with its retries and circuit breaker) or for
/// this one (retry, breaker, authentication or logging handlers, the framework's request logging) —
/// do not apply to direct pushes, so they can neither repeat a push nor share a breaker with the
/// host's own sends. The chain is taken from the factory for each push (the factory hands out its
/// active chain and rotates it after its handler lifetime), and kept reachable until that push's
/// send has ended, so the factory never disposes a handler a push is using.
/// </para>
/// <para>
/// <strong>What is checked at start-up.</strong> Those guarantees live in the named client's
/// primary handler, which <c>UseHttpTransport()</c> configures, so <see cref="Create"/> looks at that
/// handler, not at which transports are registered. It refuses an <see cref="HttpClientHandler"/>
/// (it cannot check a connection's address when it connects) and a <see cref="SocketsHttpHandler"/>
/// that follows redirects on its own, has no connect callback, or uses a proxy other than
/// <see cref="HttpTransportOptions.TrustedProxy"/> (the environment's). Any other primary handler is
/// the host's own connection path, as for every send of that host: the host then decides how and
/// where the push connects, and the mediator's own checks (the endpoint check before the take, the
/// 307 rule and its bound) still apply. On such a host, and on one whose connect callback checks
/// nothing, the check before the take is a name check only: the name is looked up again at connect,
/// and the push connects wherever that second answer points, so the host's connection code must
/// check addresses at connect itself (owner decision OD-7 on #110; as for a transport the host
/// builds by hand, OD-5).
/// </para>
/// <para>
/// <strong>How long a push may take.</strong> A push is bounded by
/// <see cref="MediatorOptions.PushTimeout"/>, its deadline. The private transport keeps the host's
/// <see cref="HttpTransportOptions.RequestTimeout"/>, and the named client its own
/// <see cref="HttpClient.Timeout"/>. Whichever ends a stalled POST first — the deadline, or either
/// timeout — the push did not deliver, and its recipient's direct pushes pause, so an endpoint
/// that never answers holds a worker at most once per pause.
/// </para>
/// <para>
/// <strong>What is checked before anything is taken.</strong> <see cref="CheckTargetAsync"/> requires
/// an absolute URI with the scheme <c>http</c> or <c>https</c> (a WebSocket endpoint gives no
/// receipt, and the queue URI <c>didcomm:transport/queue</c> means "hold it for pickup") and a scheme
/// the transport accepts (<see cref="HttpTransportOptions.AllowedSchemes"/>), and applies the
/// outbound address check of <see cref="DidCommOptions.OutboundEndpointPolicy"/>, as the host's own
/// sends do: a host name is looked up, asynchronously and under the push deadline. An endpoint the
/// check refuses gets no POST; nothing is taken, and the recipient's direct pushes pause. Through a
/// <see cref="HttpTransportOptions.TrustedProxy"/>, the transport checks each followed 307 hop's
/// destination the same way, under the deadline. On a direct connection the named client's connect
/// callback checks every address it connects to. For a host whose primary handler is its own, or
/// whose connect callback does not check addresses, the hops a push follows are that handler's job,
/// as for a transport the host builds itself.
/// </para>
/// <para>
/// No log line or exception text written here carries the endpoint, a redirect's <c>Location</c> or
/// an exception's message: an endpoint can be a secret capability URL or identify a recipient.
/// </para>
/// </remarks>
internal sealed class DirectPushSender : IDirectPushSender
{
    /// <summary><see cref="CheckTargetAsync"/>'s reason when the directory had no endpoint.</summary>
    internal const string ReasonNoEndpoint = "NoEndpoint";

    /// <summary><see cref="CheckTargetAsync"/>'s reason when the endpoint is not an absolute URI.</summary>
    internal const string ReasonNotAbsolute = "NotAbsolute";

    /// <summary><see cref="CheckTargetAsync"/>'s reason when the scheme is neither <c>http</c> nor <c>https</c>.</summary>
    internal const string ReasonScheme = "Scheme";

    /// <summary><see cref="CheckTargetAsync"/>'s reason when the HTTP transport's <see cref="HttpTransportOptions.AllowedSchemes"/> do not include the scheme.</summary>
    internal const string ReasonTransportScheme = "TransportScheme";

    /// <summary><see cref="CheckTargetAsync"/>'s reason when the outbound address check refused the endpoint.</summary>
    internal const string ReasonGuard = "Guard";

    /// <summary>What every start-up refusal of the named client asks the host to do. Names no wire value.</summary>
    internal const string UseHttpTransportHint =
        "Call UseHttpTransport() on the DidComm builder (AddDidComm(b => b.UseHttpTransport(...))): it gives that client " +
        "no automatic redirects, the outbound address check when each connection is made, and no proxy other than " +
        "HttpTransportOptions.TrustedProxy. A change the host makes to that client's handler must keep all three.";

    private readonly IDidCommTransport _transport;
    private readonly OutboundEndpointGuard _guard;
    private readonly ILogger? _logger;

    /// <summary>Build a sender over a given transport (the seam the tests use; <see cref="Create"/> builds the real one).</summary>
    /// <param name="transport">The transport that makes the one send per attempt.</param>
    /// <param name="policy">The outbound address policy <see cref="CheckTargetAsync"/> applies (<see cref="DidCommOptions.OutboundEndpointPolicy"/>).</param>
    /// <param name="logger">Optional logger for the Debug line naming a refusing endpoint's status.</param>
    internal DirectPushSender(IDidCommTransport transport, OutboundEndpointPolicy policy, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(policy);
        _transport = transport;
        // The live policy object, so a later change the host makes applies here as it does to the
        // host's own sends.
        _guard = new OutboundEndpointGuard(policy);
        _logger = logger;
    }

    /// <summary>
    /// Build the sender from the container: refuse unless the named <c>"didcomm"</c> client keeps what a
    /// push relies on (<see cref="UnvettedClientReason"/>), then build the private single-attempt
    /// transport over that client's innermost handler (with the client's own timeout) and
    /// <see cref="CopyForDirectPush"/>'s settings, and the pre-send check over
    /// <see cref="DidCommOptions.OutboundEndpointPolicy"/>.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <returns>The sender.</returns>
    /// <exception cref="InvalidOperationException">No HTTP client factory is registered, the named client's primary handler would undo a push's guarantees, <see cref="HttpTransportOptions.TrustedProxy"/> is set but invalid, or <see cref="DidCommOptions.OutboundEndpointPolicy"/> is <c>null</c>.</exception>
    internal static DirectPushSender Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var host = services.GetRequiredService<IOptions<HttpTransportOptions>>().Value;
        var clients = services.GetService<IHttpClientFactory>();
        var handlers = services.GetService<IHttpMessageHandlerFactory>();
        if (clients is null || handlers is null)
        {
            throw new InvalidOperationException(
                "Direct push (MediatorOptions.DirectPush) needs the HTTP transport's named client, and no HTTP client factory is registered. " + UseHttpTransportHint);
        }
        // The guarantees a push relies on live in the named client's primary handler (D-7). Building
        // the chain also runs UseHttpTransport()'s handler setup, which checks TrustedProxy.
        if (UnvettedClientReason(handlers.CreateHandler(HttpDidCommTransport.HttpClientName), host.TrustedProxy is not null) is { } reason)
        {
            throw new InvalidOperationException(
                $"Direct push (MediatorOptions.DirectPush) refuses to start: the named HTTP client \"{HttpDidCommTransport.HttpClientName}\" {reason}. " + UseHttpTransportHint);
        }

        // The policy DidCommClient.SendPackedAsync checks a relayed message's endpoint with. Without
        // one that method refuses every send, so direct push refuses to start.
        var corePolicy = services.GetRequiredService<IOptions<DidCommOptions>>().Value.OutboundEndpointPolicy
            ?? throw new InvalidOperationException(
                "Direct push (MediatorOptions.DirectPush) checks every endpoint with DidCommOptions.OutboundEndpointPolicy, which is null. Leave it set: the default refuses private, loopback, link-local and cloud-metadata addresses.");
        // The same resolution UseHttpTransport() uses for the host's own transport and its handler:
        // the transport's own policy, else the core one (the default when neither is set).
        var resolved = host.OutboundEndpointPolicy ?? corePolicy;
        var settings = CopyForDirectPush(host, resolved);
        // The named client's own timeout (ConfigureHttpClient, ConfigureHttpClientDefaults) is kept;
        // its handlers in front of the primary one are not (InnermostClients).
        TimeSpan timeout;
        using (var named = clients.CreateClient(HttpDidCommTransport.HttpClientName))
            timeout = named.Timeout;
        var push = new HttpDidCommTransport(new InnermostClients(handlers, timeout), Options.Create(settings), services.GetService<ILogger<HttpDidCommTransport>>());
        return new DirectPushSender(push, corePolicy, services.GetService<ILogger<DirectPushSender>>())
        {
            TransportSettings = settings,
        };
    }

    /// <summary>The innermost (primary) handler of <paramref name="chain"/>, through its <see cref="DelegatingHandler.InnerHandler"/>s.</summary>
    /// <param name="chain">A handler chain, outermost first.</param>
    /// <returns>The innermost handler.</returns>
    internal static HttpMessageHandler Innermost(HttpMessageHandler chain)
    {
        var primary = chain;
        while (primary is DelegatingHandler { InnerHandler: { } inner })
            primary = inner;
        return primary;
    }

    /// <summary>
    /// The private transport's client factory: for each send, an <see cref="HttpClient"/> over the
    /// innermost handler of the named <c>"didcomm"</c> client's current chain, so no handler a host
    /// put in front of it applies to a push.
    /// </summary>
    /// <param name="handlers">The container's handler factory.</param>
    /// <param name="timeout">The named client's <see cref="HttpClient.Timeout"/>.</param>
    private sealed class InnermostClients(IHttpMessageHandlerFactory handlers, TimeSpan timeout) : IHttpClientFactory
    {
        /// <inheritdoc />
        public HttpClient CreateClient(string name)
        {
            // A fresh chain per send: the factory hands out its active one (one per handler lifetime)
            // and disposes an expired one once nothing references its outer handler, so the client
            // holds the outer handler for as long as the send uses it.
            var chain = handlers.CreateHandler(HttpDidCommTransport.HttpClientName);
            return new ChainHoldingClient(chain) { Timeout = timeout };
        }
    }

    /// <summary>
    /// An <see cref="HttpClient"/> over the innermost handler of <paramref name="chain"/> that keeps the
    /// whole chain reachable while the client is: the transport holds the client through its send.
    /// </summary>
    /// <param name="chain">The chain the factory handed out, outermost first.</param>
    private sealed class ChainHoldingClient(HttpMessageHandler chain) : HttpClient(Innermost(chain), disposeHandler: false)
    {
        private readonly HttpMessageHandler _chain = chain;

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            // The chain is the factory's: never disposed here, only kept reachable until now.
            GC.KeepAlive(_chain);
            base.Dispose(disposing);
        }
    }

    /// <summary>The private transport's settings when <see cref="Create"/> built this sender; <c>null</c> through the test constructor.</summary>
    internal HttpTransportOptions? TransportSettings { get; private init; }

    /// <summary>
    /// Why the named client's handler chain would undo what a push relies on (D-7), in plain words for
    /// the start-up refusal; <c>null</c> when it keeps it. The chain is followed through its
    /// <see cref="DelegatingHandler.InnerHandler"/>s to the primary handler. Refused: an
    /// <see cref="HttpClientHandler"/> (it cannot check a connection's address when it connects), and a
    /// <see cref="SocketsHttpHandler"/> that follows redirects on its own (it would follow a 301, 302,
    /// 303 or 308, OD-1), has no connect callback (no address check when a connection is made), or
    /// uses a proxy without <see cref="HttpTransportOptions.TrustedProxy"/> or without a proxy of its
    /// own (the environment's proxy would see the destination instead of the check). Any other
    /// primary handler is the host's own connection path and is allowed.
    /// </summary>
    /// <param name="chain">The named client's handler chain, outermost first.</param>
    /// <param name="trustedProxy">Whether the host set <see cref="HttpTransportOptions.TrustedProxy"/>.</param>
    /// <returns>The reason, or <c>null</c>.</returns>
    internal static string? UnvettedClientReason(HttpMessageHandler chain, bool trustedProxy)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return Innermost(chain) switch
        {
            HttpClientHandler => "uses an HttpClientHandler, which cannot check a connection's address when it connects",
            SocketsHttpHandler { AllowAutoRedirect: true } => "follows redirects on its own",
            SocketsHttpHandler { ConnectCallback: null } => "has no connect callback, so no connection's address is checked when it is made",
            SocketsHttpHandler { UseProxy: true } sockets when !trustedProxy || sockets.Proxy is null => "uses a proxy that is not HttpTransportOptions.TrustedProxy",
            _ => null,
        };
    }

    /// <summary>
    /// The private transport's settings: every public setting of <see cref="HttpTransportOptions"/>
    /// copied from the host's, except no retry (<see cref="HttpTransportOptions.MaxRetryAttempts"/> 0:
    /// the transport then adds no retry step), a circuit breaker that can never open
    /// (<see cref="HttpTransportOptions.CircuitBreakerFailureThreshold"/> <see cref="int.MaxValue"/>, the
    /// largest minimum throughput Polly accepts), and the resolved outbound address policy (the
    /// transport's public constructor does not see <see cref="DidCommOptions"/>, and through a
    /// <see cref="HttpTransportOptions.TrustedProxy"/> it checks each hop's destination with this
    /// policy, names looked up asynchronously under the push deadline). The request timeout is the host's: a timeout that ends a stalled POST before the
    /// push deadline ends that push as not delivered, which pauses its recipient's direct pushes as
    /// the deadline does. The 307 bound stays the host's <see cref="HttpTransportOptions.MaxRedirectHops"/>
    /// (owner decision OD-1 on #110: follow 307 only, tightly bounded).
    /// </summary>
    /// <param name="host">The host's transport settings.</param>
    /// <param name="resolvedPolicy">The policy the host's own HTTP transport uses: its own, else <see cref="DidCommOptions.OutboundEndpointPolicy"/>.</param>
    /// <returns>A new settings object; the host's is not changed.</returns>
    internal static HttpTransportOptions CopyForDirectPush(HttpTransportOptions host, OutboundEndpointPolicy resolvedPolicy)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resolvedPolicy);
        return new HttpTransportOptions
        {
            MaxRetryAttempts = 0,
            CircuitBreakerFailureThreshold = int.MaxValue,
            MaxRedirectHops = host.MaxRedirectHops,
            OutboundEndpointPolicy = resolvedPolicy,
            // The same instance: the transport's constructor checks it again and copies it.
            TrustedProxy = host.TrustedProxy,
            AllowedSchemes = host.AllowedSchemes.ToArray(),
            RequestTimeout = host.RequestTimeout,
            CircuitBreakerOpenDuration = host.CircuitBreakerOpenDuration,
            RetryBaseDelay = host.RetryBaseDelay,
        };
    }

    /// <inheritdoc />
    public async ValueTask<PushTargetCheck> CheckTargetAsync(Uri? endpoint, CancellationToken ct)
    {
        if (endpoint is null)
            return new PushTargetCheck(PushTarget.NoEndpoint, ReasonNoEndpoint);
        if (!endpoint.IsAbsoluteUri)
            return new PushTargetCheck(PushTarget.NotUsable, ReasonNotAbsolute);
        // HTTP only: a WebSocket endpoint gives no receipt, and the queue URI means "hold it for pickup".
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new PushTargetCheck(PushTarget.NotUsable, ReasonScheme);
        }
        if (!_transport.CanHandle(endpoint))
            return new PushTargetCheck(PushTarget.NotUsable, ReasonTransportScheme);
        try
        {
            await _guard.ValidateAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (TransportException)
        {
            // A blocked IP literal, a name that resolves to a blocked address or does not resolve, or
            // a host that cannot be put in its wire form.
            return new PushTargetCheck(PushTarget.Refused, ReasonGuard);
        }
        return new PushTargetCheck(PushTarget.Accepted, string.Empty);
    }

    /// <inheritdoc />
    public async Task<DirectSendResult> SendAsync(Uri endpoint, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        // No return-route fields and no MaxResponseBytes: no response body is ever read.
        var request = new TransportRequest(endpoint, payload, MediaTypes.Encrypted);
        try
        {
            var result = await _transport.SendAsync(request, ct).ConfigureAwait(false);
            return result.Accepted ? DirectSendResult.Delivered : DirectSendResult.Failed;
        }
        catch (TransportException ex) when (ex.HttpStatusCode is int status)
        {
            // The endpoint answered, and not with a 2xx at the end of the chain: a non-2xx, a 301, 302,
            // 303 or 308 (never followed), a 307 without a usable Location or to a scheme the transport
            // refuses, or more 307s than the bound. The status only: the message names the URI.
            MediatorLog.Write(_logger, LogLevel.Debug, "Direct push not delivered: the endpoint answered {Status}.", status);
            // A 4xx other than 408 (Request Timeout) and 429 (Too Many Requests) is the endpoint's
            // definite "not this message"; every other answer (a 5xx, 408, 429, a 3xx at the end of
            // the chain) says it is unwell or wants "later".
            return status is >= 400 and <= 499 and not 408 and not 429 ? DirectSendResult.Refused : DirectSendResult.Failed;
        }
    }
}
