using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Unicode;
using DidComm.Exceptions;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.Stomp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace DidComm.Transports.WebSocket;

/// <summary>
/// WebSocket-flavored <see cref="IDidCommTransport"/> (PRD §9.3 / FR-TRN-09..11). Sends each
/// packed DIDComm envelope as exactly one WebSocket text message (FR-TRN-09; STOMP frames are
/// binary), and refuses a payload that is not valid UTF-8 before writing anything; the receiver
/// MUST reassemble fragmented frames before processing. Connections are
/// pooled by endpoint and reconnect with exponential backoff (1s / 30s / 0.5 jitter — DD-05) on
/// send failures. Optionally wraps each envelope in minimal STOMP 1.2 framing when
/// <see cref="WebSocketTransportOptions.UseStomp"/> is enabled (FR-TRN-12); plain framing is
/// the default and is byte-for-byte unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Return-route session sockets (PRD §8a, FR-RR-07).</strong> The receiver binds a
/// return-route session to the socket the opening request arrived on (FR-RR-05), so a request whose
/// message carries <c>return_route</c> <c>all</c> or <c>thread</c> and that the facade authcrypted
/// directly to one DID (<see cref="TransportRequest.LocalDid"/> and
/// <see cref="TransportRequest.PeerDid"/> set) does not use the endpoint's pooled socket: it opens a
/// session socket of its own, S(E, L, P), one per endpoint, local DID and peer DID. Every later send
/// to the same (E, L, P), with or without a <c>return_route</c> header, travels over S while it
/// exists, so a header-less acknowledgement reaches the session it acknowledges. An explicit
/// <c>none</c> is sent over S and then retires it: S stops taking sends, a close frame is sent, and
/// the messages already on their way are still read until the peer's close frame (bounded by one
/// <see cref="WebSocketTransportOptions.SendTimeout"/>, or by 30 seconds when that is infinite or not
/// positive).
/// </para>
/// <para>
/// <strong>Ordering.</strong> A send that starts after an <c>all</c>/<c>thread</c> send to
/// (E, L, P) returned uses S while S exists. A send that starts after a <c>none</c> send to
/// (E, L, P) returned uses the pooled socket. Concurrent sends are not ordered. A send that fails on
/// S drops it; its retry opens a fresh S when it asks for a return route, and uses the pooled socket
/// otherwise.
/// </para>
/// <para>
/// <strong>Receiving.</strong> Each session socket is read by its own loop. With
/// <see cref="WebSocketTransportOptions.ReceiveInbound"/> every whole pushed message (fragments
/// reassembled, FR-TRN-09), at most <c>DidCommOptions.MaxReceiveBytes</c> long, is handed to the
/// <see cref="IInboundFrameConsumer"/>, one at a time: the next message is read only after the
/// consumer returned. A bigger message closes S with status 1009 and nothing past the limit is kept.
/// A message not finished within <see cref="WebSocketTransportOptions.MessageReceiveTimeout"/> of its
/// first part closes S with status 1008 and is dropped; the wait for the next message is never timed.
/// The loop never sends anything but close frames. Once <see cref="DisposeAsync"/> begins, the
/// messages pushed on a session socket are read and dropped, never handed to the consumer; the
/// consumer's token is cancelled once the drain is done (see <see cref="DisposeAsync"/>). Pooled
/// sockets are never read. Under
/// <see cref="WebSocketTransportOptions.UseStomp"/> no session socket is opened.
/// </para>
/// <para>
/// <strong>Resource bound.</strong> One send gate (a <see cref="SemaphoreSlim"/>) is kept per
/// endpoint and per (E, L, P) the application ever sent to, until the transport is disposed.
/// </para>
/// </remarks>
/// <example>
/// Bidirectional chat with reconnect runs in <c>samples/05-WebSocketChat</c>; one-message-per-frame receive in <c>samples/02-Cookbook</c> (section R).
/// </example>
public sealed class WebSocketDidCommTransport : IDidCommTransport, IAsyncDisposable, IInboundReceivingTransport
{
    private readonly WebSocketTransportOptions _options;
    private readonly ILogger<WebSocketDidCommTransport> _logger;
    private readonly ResiliencePipeline _reconnectPipeline;
    private readonly OutboundEndpointGuard _guard;
    // The default connect path's handler: pins every ClientWebSocket connection to a guard-vetted IP
    // via SocketsHttpHandler.ConnectCallback, or hands it to the named TrustedProxy (see constructor).
    // Always built, so DefaultConnect never connects without it; a custom Connect delegate never uses it.
    private readonly HttpMessageInvoker _pinnedInvoker;
    // WebSocketTransportOptions.TrustedProxy is set (read once). The pre-send check then runs on every
    // send, whatever connects it: past a proxy, no connect-time check sees the destination.
    private readonly bool _proxied;
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _pool = new(StringComparer.Ordinal);
    // One connect gate per pool key so establishing a connection to one endpoint doesn't block
    // connects to a different endpoint.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectLocks = new(StringComparer.Ordinal);
    // One send gate per pool key: a WebSocket allows only ONE outstanding SendAsync, so two
    // concurrent sends to the same endpoint would otherwise collide — the loser throws
    // InvalidOperationException, its retry path disposes the healthy pooled socket, and the
    // winner's in-flight send dies with an unretried ObjectDisposedException. Sends to
    // different endpoints stay fully parallel.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new(StringComparer.Ordinal);
    // Dispose fail-fast flag, set FIRST in DisposeAsync (see the invariant documented there).
    // volatile: read on every send path, written once from the disposing thread.
    private volatile bool _disposed;
    // Set once the drain is over and no pool entry may survive: a send whose drain budget expired
    // publishes its socket and then re-reads this flag (see GetOrConnectAsync). Written BEFORE the
    // final sweep so the sweep and the late writer can never both miss the same entry.
    private volatile bool _poolSealed;
    // Dispose-once latch: 0 until a caller claims the teardown. Concurrent (and repeat) callers
    // await _disposeCompleted instead of racing a second teardown over the same gates and sockets.
    private int _disposeClaimed;
    private readonly TaskCompletionSource _disposeCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- Return-route session sockets (PRD §8a, FR-RR-07) ----
    // The consumer of pushed messages; null when none was supplied. Called only when _receiveInbound.
    private readonly IInboundFrameConsumer? _consumer;
    // WebSocketTransportOptions.ReceiveInbound, read once at construction (the options object is mutable).
    private readonly bool _receiveInbound;
    // DidCommOptions.MaxReceiveBytes: the largest pushed message a session loop keeps (JC-11, JC-16).
    private readonly int _inboundCap;
    // WebSocketTransportOptions.MessageReceiveTimeout, read once at construction: how long a pushed message
    // may take from its first part to its last frame (Timeout.InfiniteTimeSpan: no limit).
    private readonly TimeSpan _messageReceiveTimeout;
    // The clock that limit is timed on: the container's TimeProvider under UseWebSocketTransport, else the system's.
    private readonly TimeProvider _time;
    // The ACTIVE session socket of each session key (PoolKey + "\n" + L + "\n" + P, see SendAsync).
    // Written only under that key's send gate; removed conditionally (by instance) by the socket's
    // own receive loop and by dispose. A key lives in exactly one of _pool and _sessions: a session
    // key contains line feeds, which neither a pool key nor a validated DID can.
    private readonly ConcurrentDictionary<string, SessionSocket> _sessions = new(StringComparer.Ordinal);
    // Every session socket whose receive loop has not finished: active ones and retiring ones (no
    // longer in _sessions, still reading the frames already sent). Dispose sweeps and awaits these.
    // Once dispose has begun, a loop that ends leaves its socket here for the sweep, which owns it.
    private readonly ConcurrentDictionary<SessionSocket, byte> _liveSessionSockets = new();
    // One-time notices (0 = not yet logged).
    private int _stompSessionNotice;
    private int _discardNotice;
    // Rate-limiter counters for the flood-shaped session log lines (first and every 1000th at Warning).
    private long _consumerFaults;
    private long _oversizedPushes;
    private long _timedOutPushes;
    // Whole pushed messages read and dropped without reaching the consumer (ReceiveInbound off, or
    // the socket is discarding after a 1009 or a 1008). Diagnostics for tests only; never logged per message.
    private long _inboundMessagesDropped;
    private const long WarnEvery = 1000;
    // FR-API-09: the longest LocalDid / PeerDid a session key accepts.
    private const int MaxSessionDidLength = 2048;

    /// <summary>Fires when the transport opens, closes, or fails to send (FR-TRN-11).</summary>
    /// <remarks>
    /// Handlers run synchronously on transport code paths — including from inside
    /// <see cref="DisposeAsync"/> itself (the <c>Disconnected</c> events) and, before dispose begins,
    /// from the receive loop of a return-route session socket (its <c>Disconnected</c>, raised once
    /// per socket and naming the endpoint it was opened to). A handler must
    /// therefore never block on this transport's own teardown:
    /// <c>DisposeAsync().GetAwaiter().GetResult()</c> from a handler deadlocks permanently,
    /// because dispose completes only after the handler returns while the handler is waiting for
    /// dispose to complete. Sending on the transport from a handler is safe (events are raised
    /// outside the send gates); tearing the transport down from one is not — schedule that on
    /// another context instead.
    /// </remarks>
    public event EventHandler<WebSocketLifecycleEventArgs>? Lifecycle;

    /// <summary>Initialize the transport with bound options and no inbound consumer.</summary>
    /// <param name="options">Bound <see cref="WebSocketTransportOptions"/>.</param>
    /// <param name="logger">Optional logger; pass <see cref="NullLogger{T}.Instance"/> outside DI.</param>
    /// <param name="coreOptions">
    /// Optional core <see cref="DidComm.Facade.DidCommOptions"/>; its <c>OutboundEndpointPolicy</c> is
    /// inherited as the single source of truth when this transport's policy is left unset (#27).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// When <see cref="WebSocketTransportOptions.ReceiveInbound"/> is set: it needs an
    /// <see cref="IInboundFrameConsumer"/>, which only the four-parameter constructor accepts.
    /// </exception>
    public WebSocketDidCommTransport(
        IOptions<WebSocketTransportOptions> options,
        ILogger<WebSocketDidCommTransport>? logger = null,
        IOptions<DidComm.Facade.DidCommOptions>? coreOptions = null)
        : this(options, logger, coreOptions, inboundConsumer: null)
    {
    }

    /// <summary>
    /// Initialize the transport with bound options and the consumer of the messages peers push on
    /// return-route session sockets (PRD §8a, FR-RR-07).
    /// </summary>
    /// <param name="options">Bound <see cref="WebSocketTransportOptions"/>.</param>
    /// <param name="logger">Optional logger; <c>null</c> logs nothing.</param>
    /// <param name="coreOptions">
    /// Optional core <see cref="DidComm.Facade.DidCommOptions"/>. Its <c>OutboundEndpointPolicy</c> is
    /// inherited when this transport's policy is left unset (#27), and its <c>MaxReceiveBytes</c>
    /// caps every pushed message a session socket keeps; <c>null</c> uses the defaults.
    /// </param>
    /// <param name="inboundConsumer">
    /// Receives each whole message pushed on a session socket when
    /// <see cref="WebSocketTransportOptions.ReceiveInbound"/> is set; <c>null</c> for none.
    /// <c>DidCommBuilder.UseInboundDispatch()</c> registers the built-in consumer, and
    /// <c>UseWebSocketTransport</c> passes the registered one here.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// When <see cref="WebSocketTransportOptions.ReceiveInbound"/> is set and
    /// <paramref name="inboundConsumer"/> is <c>null</c>.
    /// </exception>
    public WebSocketDidCommTransport(
        IOptions<WebSocketTransportOptions> options,
        ILogger<WebSocketDidCommTransport>? logger,
        IOptions<DidComm.Facade.DidCommOptions>? coreOptions,
        IInboundFrameConsumer? inboundConsumer)
        : this(options, logger, coreOptions, inboundConsumer, TimeProvider.System)
    {
    }

    /// <summary>
    /// The four-parameter constructor with the clock that
    /// <see cref="WebSocketTransportOptions.MessageReceiveTimeout"/> is timed on:
    /// <c>UseWebSocketTransport</c> passes the container's <see cref="TimeProvider"/>, the public
    /// constructors the system clock.
    /// </summary>
    /// <param name="options">Bound <see cref="WebSocketTransportOptions"/>.</param>
    /// <param name="logger">Optional logger; <c>null</c> logs nothing.</param>
    /// <param name="coreOptions">Optional core <see cref="DidComm.Facade.DidCommOptions"/>; <c>null</c> uses the defaults.</param>
    /// <param name="inboundConsumer">The consumer of pushed messages; <c>null</c> for none.</param>
    /// <param name="time">The clock of the per-message receive limit.</param>
    /// <exception cref="InvalidOperationException">
    /// When <see cref="WebSocketTransportOptions.ReceiveInbound"/> is set and
    /// <paramref name="inboundConsumer"/> is <c>null</c>.
    /// </exception>
    internal WebSocketDidCommTransport(
        IOptions<WebSocketTransportOptions> options,
        ILogger<WebSocketDidCommTransport>? logger,
        IOptions<DidComm.Facade.DidCommOptions>? coreOptions,
        IInboundFrameConsumer? inboundConsumer,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options.Value;
        _time = time;
        // Read once, like ReceiveInbound: the session loops must time every message with the same limit.
        _messageReceiveTimeout = _options.MessageReceiveTimeout;
        // Read once: the options object stays mutable after construction, and the receive loops must
        // never see the flag turn on without the consumer it requires.
        _receiveInbound = _options.ReceiveInbound;
        if (_receiveInbound && inboundConsumer is null)
        {
            throw new InvalidOperationException(
                "WebSocketTransportOptions.ReceiveInbound requires an IInboundFrameConsumer: call DidCommBuilder.UseInboundDispatch() " +
                "(or register your own IInboundFrameConsumer) before resolving the transport, or pass one to the WebSocketDidCommTransport constructor.");
        }
        _consumer = inboundConsumer;
        _inboundCap = coreOptions?.Value.MaxReceiveBytes ?? new DidComm.Facade.DidCommOptions().MaxReceiveBytes;
        _logger = logger ?? NullLogger<WebSocketDidCommTransport>.Instance;
        if (inboundConsumer is not null && !_receiveInbound)
        {
            Log(LogLevel.Information,
                "WebSocketDidCommTransport: an IInboundFrameConsumer is registered but WebSocketTransportOptions.ReceiveInbound is off; messages pushed on return-route session sockets are discarded.");
        }
        _reconnectPipeline = BuildReconnectPipeline(_options);
        // Single source of truth (#27): inherit the core policy when this transport's is unset.
        var policy = _options.OutboundEndpointPolicy
            ?? coreOptions?.Value.OutboundEndpointPolicy
            ?? new OutboundEndpointPolicy();
        _guard = new OutboundEndpointGuard(policy);
        // SSRF defense for the default ClientWebSocket path: pin every connection — the initial
        // handshake and each reconnect — to a guard-vetted IP via a SocketsHttpHandler.ConnectCallback,
        // exactly as the HTTP transport does. OutboundEndpointGuard.ConnectAsync resolves at connect
        // time regardless of OutboundEndpointPolicy.ResolveDnsNames, so this also defeats a DNS rebind
        // between the pre-send Validate() and the handshake. TLS still uses the original host for SNI
        // and certificate validation; only the TCP target IP is constrained. A custom Connect delegate
        // owns its own vetting and never uses this invoker; it is built anyway, so the default path can
        // never connect without it, even when Connect is changed after construction.
        // Read and copied once (WebSocketTransportOptions.TrustedProxy).
        var route = TrustedProxyRoute.From(_options.TrustedProxy);
        _proxied = route is not null;
        var sockets = new SocketsHttpHandler
        {
            // FR-TRN-13: never the process proxy (HttpClient.DefaultProxy: HTTP_PROXY, HTTPS_PROXY,
            // ALL_PROXY, the system proxy). Through a proxy the callback below would vet the proxy's
            // socket, not the destination's. Only a proxy the host names is used.
            UseProxy = route is not null,
            Proxy = route?.Proxy,
            // Direct, a handshake redirect is followed and the callback pins each hop. Through a proxy
            // the callback sees only the proxy, and the destination check covers the first request
            // only, so no redirect is followed.
            AllowAutoRedirect = route is null,
            ConnectCallback = async (context, ct) =>
            {
                if (route is not null && route.IsProxyConnection(context))
                {
                    // The named proxy is the host's choice: dialled unfiltered, on any network, when the
                    // handler uses it as a proxy. A destination the proxy bypasses gets the full guard,
                    // even when it names the proxy's own host and port.
                    var proxySocket = await TrustedProxyRoute.ConnectToProxyAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(proxySocket, ownsSocket: true);
                }
                try
                {
                    var socket = await _guard.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    Stream stream = new NetworkStream(socket, ownsSocket: true);
                    return stream;
                }
                catch (TransportException ex)
                {
                    // The guard's refusal is deterministic — every retry re-resolves to the
                    // same blocked addresses — but ClientWebSocket wraps anything thrown here
                    // in a retriable WebSocketException. Mark the refusal at its throw site so
                    // ConnectPinnedAsync can reclassify it as fatal (issue #72).
                    throw new GuardRefusalException(ex);
                }
                catch (SocketException ex) when (route is null)
                {
                    throw TrustedProxyRoute.WithDirectHint(ex);
                }
            },
        };
        // Through a proxy, the destination's checks run before every send instead (SendAsync, _proxied).
        _pinnedInvoker = new HttpMessageInvoker(sockets);
    }

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for one endpoint's in-flight send to drain. The
    /// send gate is held across connect + STOMP handshake + send, so the worst case for a HEALTHY
    /// hold is <c>ConnectTimeout + SendTimeout</c> — waiting only <c>SendTimeout</c> would expire
    /// mid-connect and abandon (and then hard-kill) a send the transport could still have waited
    /// for. Endpoints drain concurrently, so this is the bound for the whole dispose, not per
    /// endpoint.
    /// </summary>
    private TimeSpan DrainBudget => AsWaitBudget(AsWaitBudget(_options.ConnectTimeout) + AsWaitBudget(_options.SendTimeout));

    /// <summary>
    /// How long dispose's polite shutdown of one socket (STOMP DISCONNECT, close frame, and the wait
    /// for the peer's close frame) and its wait for the session receive loops may take. One
    /// per-send budget: the writes are ordinary socket writes, and the peer is under no obligation to
    /// read them or to answer. Dispose only: an infinite or non-positive
    /// <see cref="WebSocketTransportOptions.SendTimeout"/> maps to zero here (see
    /// <see cref="AsWaitBudget"/>), which is right for a teardown that must be bounded and wrong for
    /// a session socket that is still meant to be read (see <see cref="CloseGrace"/>).
    /// </summary>
    private TimeSpan CloseBudget => AsWaitBudget(_options.SendTimeout);

    /// <summary>
    /// How long a session socket that is closing outside dispose may take: the close frame sent on a
    /// retire (<c>return_route: none</c>), on an oversized message (1009), on a message not finished in
    /// time (1008) or in answer to the peer's close, and the backstop after which a retiring or
    /// discarding socket stops being read. One
    /// <see cref="WebSocketTransportOptions.SendTimeout"/> when it is finite and positive (clamped to
    /// the largest legal wait), else <see cref="DefaultCloseGrace"/>: unlike dispose, these paths must
    /// leave the messages already on their way time to arrive, so an infinite <c>SendTimeout</c> must
    /// not turn into a zero grace that stops the loop at once.
    /// </summary>
    private TimeSpan CloseGrace => _options.SendTimeout > TimeSpan.Zero ? AsWaitBudget(_options.SendTimeout) : DefaultCloseGrace;

    /// <summary>The <see cref="CloseGrace"/> used when <see cref="WebSocketTransportOptions.SendTimeout"/> is infinite or not positive.</summary>
    private static readonly TimeSpan DefaultCloseGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Clamp a caller-configured timeout into something legal — and finite — as a dispose-time
    /// wait. <see cref="SemaphoreSlim.WaitAsync(TimeSpan)"/> and
    /// <see cref="CancellationTokenSource(TimeSpan)"/> reject anything below zero or above
    /// <see cref="int.MaxValue"/> ms, except <see cref="Timeout.InfiniteTimeSpan"/> — which is
    /// worse, since it turns dispose into an unbounded wait on a peer that may never answer. Both
    /// map to zero: a configuration that gives dispose no bound it can honor buys the in-flight
    /// send no grace, rather than hanging the caller or throwing out of the cleanup path.
    /// </summary>
    /// <param name="value">The configured timeout.</param>
    /// <returns>A value in <c>[0, int.MaxValue ms]</c>.</returns>
    private static TimeSpan AsWaitBudget(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero
        : value > MaxWaitBudget ? MaxWaitBudget
        : value;

    /// <summary>The largest wait <see cref="SemaphoreSlim"/> and <see cref="CancellationTokenSource"/> accept.</summary>
    private static readonly TimeSpan MaxWaitBudget = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Session sockets that currently take sends (diagnostics for tests).</summary>
    internal int ActiveSessionSocketCount => _sessions.Count;

    /// <summary>Session sockets whose receive loop has not finished, retiring ones included (diagnostics for tests).</summary>
    internal int LiveSessionSocketCount => _liveSessionSockets.Count;

    /// <summary>
    /// The end of the receive loop of the session socket over <paramref name="socket"/> while that
    /// socket is in <see cref="_liveSessionSockets"/>, else a completed task (diagnostics for tests).
    /// </summary>
    /// <param name="socket">The socket the transport's factory created.</param>
    internal Task SessionLoopDone(System.Net.WebSockets.WebSocket socket)
    {
        foreach (var live in _liveSessionSockets.Keys)
        {
            if (ReferenceEquals(live.Socket, socket))
                return live.LoopDone;
        }
        return Task.CompletedTask;
    }

    /// <summary>Whole pushed messages read and dropped without reaching the consumer (diagnostics for tests).</summary>
    internal long InboundMessagesDropped => Interlocked.Read(ref _inboundMessagesDropped);

    /// <summary>
    /// Whether a message a peer pushes on a session socket reaches the inbound consumer: only with
    /// <see cref="WebSocketTransportOptions.ReceiveInbound"/> on (read once, at construction) and
    /// <see cref="WebSocketTransportOptions.UseStomp"/> off (read as every send reads it), since under
    /// STOMP no session socket is opened and pooled sockets are never read. The recipient-side
    /// mediator client asks before a send whose answer can only come back this way (#96).
    /// </summary>
    bool IInboundReceivingTransport.ReceivesInbound => _receiveInbound && !_options.UseStomp;

    /// <inheritdoc />
    public string Scheme => "wss";

    /// <inheritdoc />
    public bool CanHandle(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        foreach (var allowed in _options.AllowedSchemes)
        {
            if (string.Equals(endpoint.Scheme, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Which socket carries the send (PRD §8a, FR-RR-07): the session socket S(E, L, P) when the
    /// request names both <see cref="TransportRequest.LocalDid"/> and
    /// <see cref="TransportRequest.PeerDid"/> and either asks for <c>return_route</c> <c>all</c> /
    /// <c>thread</c> (which opens S when there is none) or S already exists; the endpoint's pooled
    /// socket otherwise. See the class remarks for the ordering guarantee. Under
    /// <see cref="WebSocketTransportOptions.UseStomp"/> every send uses the pooled socket.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// When the request would open a session socket (<see cref="TransportRequest.ReturnRoute"/>
    /// <c>all</c> or <c>thread</c>, both identities set, no STOMP) and
    /// <see cref="TransportRequest.LocalDid"/> or <see cref="TransportRequest.PeerDid"/> is not a DID
    /// (<c>did:</c> prefix) of at most 2048 characters without whitespace or control characters. The
    /// value is not repeated in the message. Any other request treats such identities as absent and
    /// uses the pooled socket: no session socket can exist under them.
    /// </exception>
    /// <exception cref="TransportException">
    /// The endpoint's scheme is not allowed; the destination is refused (SSRF guard); the payload is
    /// not valid UTF-8 (#98), refused before any socket is touched or anything is written, so a
    /// connection already open stays usable; or the send failed (see the class remarks).
    /// </exception>
    public async Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Endpoint);
        // FR-RR-07 (JC-10, JC-21): a request that names both identities may use the return-route
        // session socket S(E, L, P). `opens`: this send asks for a return route and opens S when there
        // is none; `retires`: an explicit `none` ends S after it was sent over it.
        var opens = request.ReturnRoute is ReturnRouteMode.All or ReturnRouteMode.Thread;
        var retires = request.ReturnRoute == ReturnRouteMode.None;
        // The session key is built from the identities, so they are shape-checked before any state is
        // touched: a line feed in either would let two identities share a key. Only a send that would
        // open S refuses an invalid identity (QA-F02). S is only ever created under a valid key, so a
        // header-less or `none` send naming an invalid one cannot belong to an S: it is delivered over
        // the pooled socket, as before session sockets existed. Under STOMP no session socket exists,
        // so the identities are never used as a key and never refused there.
        var keyable = request.LocalDid is not null && request.PeerDid is not null && !_options.UseStomp;
        var localOk = keyable && IsSessionDid(request.LocalDid!);
        var peerOk = keyable && IsSessionDid(request.PeerDid!);
        if (opens && keyable && !(localOk && peerOk))
            throw InvalidSessionIdentity(localOk ? nameof(TransportRequest.PeerDid) : nameof(TransportRequest.LocalDid));
        // Fail fast before touching any pool state: once DisposeAsync has begun, no send may
        // start (see the dispose invariant on DisposeAsync).
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanHandle(request.Endpoint))
        {
            throw new TransportException(
                $"WebSocketDidCommTransport refuses scheme '{request.Endpoint.Scheme}'. Allowed schemes: [{string.Join(", ", _options.AllowedSchemes)}].",
                httpStatusCode: null,
                scheme: request.Endpoint.Scheme);
        }

        // FR-TRN-09 (#98): a DIDComm message goes out as a text frame, and a text frame must be UTF-8
        // (RFC 6455 §5.6): a receiver fails the whole connection on one that is not (§8.1), losing
        // what follows it. A DIDComm message is UTF-8 JSON, so bytes that are not UTF-8 are not one;
        // refused here, before any socket is used. Under STOMP too: the SEND body is the message.
        if (!Utf8.IsValid(request.Payload.Span))
        {
            throw new TransportException(
                "WebSocketDidCommTransport refuses a payload that is not valid UTF-8: a DIDComm message is UTF-8 JSON, and a WebSocket text frame must be UTF-8 (FR-TRN-09, RFC 6455 §5.6).",
                httpStatusCode: null,
                scheme: request.Endpoint.Scheme);
        }

        // SSRF defense, layer 1: reject obvious private / loopback / metadata hosts up front with a
        // clear error. The authoritative defense is the connect-time IP pinning in _pinnedInvoker
        // (see constructor), which additionally covers DNS rebinding and ResolveDnsNames = false. A
        // custom Connect delegate (e.g. tests against an in-process TestServer) owns its own vetting,
        // so both layers are skipped there, unless a TrustedProxy is set (FR-TRN-13): then this check
        // is the destination's only one and runs on every send.
        if (_options.Connect is null || _proxied)
            _guard.Validate(request.Endpoint);

        var key = PoolKey(request.Endpoint);
        var attempt = 0;

        // FR-TRN-12 (opt-in): wrap the packed envelope in one STOMP SEND frame — destination +
        // content-type (the request's DIDComm media type, application/didcomm-encrypted+json on
        // the SendAsync path) + codec-computed content-length, one packed message per SEND body.
        // Built once; identical on every reconnect attempt. Default OFF: the payload bytes go on
        // the wire untouched, exactly as before.
        var payload = _options.UseStomp ? BuildStompSendPayload(request) : request.Payload;

        // Under STOMP there are no session sockets (a spec-shaped push needs broker semantics outside
        // the minimal subset).
        string? sessionKey = localOk && peerOk ? key + "\n" + request.LocalDid + "\n" + request.PeerDid : null;
        if (_options.UseStomp && opens && request.LocalDid is not null && request.PeerDid is not null
            && Interlocked.Exchange(ref _stompSessionNotice, 1) == 0)
        {
            Log(LogLevel.Information, "WebSocketDidCommTransport: return-route session sockets are not available under STOMP (FR-RR-05); return-route sends use the pooled socket and nothing is received.");
        }

        try
        {
            await _reconnectPipeline.ExecuteAsync(async token =>
            {
                // The reconnect pipeline runs attempts sequentially, so a plain counter is safe.
                // attempt 0 is the first try; > 0 means this attempt is a recovery after a failure.
                var isReconnect = attempt++ > 0;
                // Routing is decided inside every attempt (JC-10), so a retry after S failed, or a send
                // that finds S retired once it holds S's gate, goes where the current state says.
                if (sessionKey is not null
                    && (opens || _sessions.ContainsKey(sessionKey))
                    && await SendOnSessionAsync(sessionKey, request, payload, opens, retires, isReconnect, token).ConfigureAwait(false))
                {
                    return;
                }
                await SendOnPoolAsync(key, request, payload, isReconnect, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation is not a transport failure — let it propagate as-is.
            throw;
        }
        catch (ObjectDisposedException)
        {
            // A send that passed the entry check but lost the race to DisposeAsync observes the
            // flag inside the gate (or hits the pool seal) and throws this. It is a lifecycle
            // fact, not a connectivity failure — wrapping it in the budget-exhausted
            // TransportException below would tell a reconnect story about a transport that was
            // simply shut down.
            throw;
        }
        catch (FatalSendException ex)
        {
            // Permanently fatal (see FatalSendException): retrying would burn the whole reconnect
            // budget — every attempt a full connect holding this endpoint's send gate,
            // head-of-line-blocking every other sender — on a failure that can never succeed. Say
            // so instead of claiming an exhausted budget, and hand back the classified cause.
            // `attempt` names the attempt the fatal classification arrived on: transient failures
            // may legitimately have consumed earlier attempts.
            throw new TransportException(
                $"WebSocket send to '{request.Endpoint}' failed on attempt {attempt}: {ex.Reason} — permanently fatal, so the remaining reconnect budget was not spent.",
                ex.InnerException!,
                httpStatusCode: null,
                scheme: request.Endpoint.Scheme);
        }
        catch (Exception ex)
        {
            // FR-TRN-11 / FR-API-07: every failure that escapes the pipeline — an exhausted
            // reconnect budget, a STOMP handshake refusal, or a zero-budget first failure —
            // surfaces as ONE consistent shape: a TransportException naming the budget, with the
            // last underlying failure as InnerException. The pre-pipeline refusals (scheme check,
            // SSRF guard) are outside this try and keep their own specific messages.
            throw new TransportException(
                $"WebSocket send to '{request.Endpoint}' failed after exhausting the reconnect budget ({_options.MaxReconnectAttempts} attempt(s)).",
                ex,
                httpStatusCode: null,
                scheme: request.Endpoint.Scheme);
        }

        return new TransportResult(Accepted: true, HttpStatusCode: null);
    }

    /// <summary>
    /// One attempt over the endpoint's pooled socket: connect it when there is none, then send.
    /// Every pool read and write happens under the endpoint's send gate (see <see cref="DisposeAsync"/>).
    /// </summary>
    /// <param name="key">Pool key of the endpoint.</param>
    /// <param name="request">The request (for its endpoint).</param>
    /// <param name="payload">The bytes to send (STOMP-framed when enabled).</param>
    /// <param name="isReconnect">Whether this attempt follows a failed one (raises <c>Reconnected</c> on success).</param>
    /// <param name="token">The attempt's cancellation token.</param>
    private async Task SendOnPoolAsync(string key, TransportRequest request, ReadOnlyMemory<byte> payload, bool isReconnect, CancellationToken token)
    {
        // Serialize socket use per endpoint (see _sendLocks): acquisition + send under one
        // gate so a concurrent SendAsync can never collide on (or dispose) the socket that
        // another send is actively using.
        var sendGate = _sendLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        // Lifecycle handlers must never run while this gate is held: SemaphoreSlim is not
        // reentrant, so a handler that sends to this same endpoint would deadlock forever on
        // the gate its own notification is holding (DisposeAsync already releases first).
        // Record the events here, raise them after Release() — recorded order IS raise order,
        // so the Connected → Disconnected → SendFailed → Reconnected sequencing is unchanged.
        List<(WebSocketLifecycleEventKind Kind, Exception? Error)>? pending = null;
        try
        {
            // Dispose invariant: _pool and the pooled socket are only touched inside the
            // send gate and only after re-checking _disposed HERE, inside the gate.
            // DisposeAsync sets _disposed first and then acquires this gate, so a send
            // that wins the gate after disposal has begun observes the flag and bails
            // before it can resurrect a pool entry or race the closing socket.
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var (socket, opened) = await GetOrConnectAsync(key, request.Endpoint, token).ConfigureAwait(false);
                if (opened)
                    (pending ??= new()).Add((WebSocketLifecycleEventKind.Connected, null));
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                sendCts.CancelAfter(_options.SendTimeout);
                // FR-TRN-09: one logical WebSocket message per packed envelope, as a text frame: a
                // DIDComm message is UTF-8 JSON, and some peers (the Identus mediator, browser
                // wallets) read only text frames. A STOMP SEND frame stays binary (FR-TRN-12). We
                // always send the full buffer with EndOfMessage = true; receivers MUST loop until
                // they see EndOfMessage to handle fragmentation at the wire layer.
                var frameType = _options.UseStomp ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
                await socket.SendAsync(payload, frameType, endOfMessage: true, sendCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Drop the broken socket so the next attempt opens a fresh connection. A socket
                // only lands in the pool once it has connected, so a non-null entry here means an
                // established connection was lost (Disconnected); a connect failure leaves the
                // pool empty and registers as SendFailed only.
                _pool.TryRemove(key, out var broken);
                if (broken is not null)
                {
                    broken.Dispose();
                    (pending ??= new()).Add((WebSocketLifecycleEventKind.Disconnected, ex));
                }
                (pending ??= new()).Add((WebSocketLifecycleEventKind.SendFailed, ex));
                throw;
            }

            if (isReconnect)
                (pending ??= new()).Add((WebSocketLifecycleEventKind.Reconnected, null));
        }
        finally
        {
            sendGate.Release();
            if (pending is not null)
            {
                foreach (var (kind, error) in pending)
                    RaiseLifecycle(kind, request.Endpoint, error);
            }
        }
    }

    /// <summary>
    /// One attempt over the session socket S(E, L, P) of <paramref name="sessionKey"/>, under that
    /// key's send gate (FR-RR-07, JC-10): use S when it is open, open it when this send asks for a
    /// return route, and retire it after an explicit <c>none</c> was sent over it.
    /// </summary>
    /// <param name="sessionKey">The session key (pool key, local DID, peer DID).</param>
    /// <param name="request">The request (endpoint and identities).</param>
    /// <param name="payload">The bytes to send.</param>
    /// <param name="opens">Whether this send opens S when there is none (<c>all</c> / <c>thread</c>).</param>
    /// <param name="retires">Whether this send retires S once sent (<c>none</c>).</param>
    /// <param name="isReconnect">Whether this attempt follows a failed one (raises <c>Reconnected</c> on success).</param>
    /// <param name="token">The attempt's cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the payload was sent over S; <c>false</c>, having sent nothing, when S is
    /// gone and this send does not open one: the caller then uses the pooled socket in the same
    /// attempt. A send that queued behind a retiring S therefore lands on the pooled socket.
    /// </returns>
    private async Task<bool> SendOnSessionAsync(
        string sessionKey,
        TransportRequest request,
        ReadOnlyMemory<byte> payload,
        bool opens,
        bool retires,
        bool isReconnect,
        CancellationToken token)
    {
        // The same per-key gate dictionary as the pool (JC-21), so the dispose drain covers session
        // sockets with its existing logic. Minted once per key and kept until dispose (L-028).
        var sendGate = _sendLocks.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        // Events are recorded under the gate and raised after it is released, as on the pool path.
        List<(WebSocketLifecycleEventKind Kind, Exception? Error)>? pending = null;
        // Receive loops to stop once the gate is released: cancelling a loop's token runs the inbound
        // consumer's cancellation callbacks synchronously, and host code never runs under a send gate.
        List<SessionSocket>? toStop = null;
        try
        {
            // The dispose invariant, as on the pool path: _sessions is only written under this gate,
            // after re-checking the flag here.
            ObjectDisposedException.ThrowIf(_disposed, this);
            SessionSocket? session = null;
            try
            {
                if (_sessions.TryGetValue(sessionKey, out var current))
                {
                    if (current.Socket.State == WebSocketState.Open)
                    {
                        session = current;
                    }
                    else if (current.Socket.State == WebSocketState.CloseReceived)
                    {
                        // The peer's close was read (a receiver eviction, say) and the loop has not
                        // answered it yet: never send on it again. Only routing changes here. The
                        // socket's end, the answer to that close included (RFC 6455 §5.5.1), stays
                        // with its own loop, which reads that close and answers it under this gate
                        // once it is free, or with dispose's sweep once dispose has begun (TryEnd
                        // decides). This send writes nothing on it, so its hold stays connect + send,
                        // inside DrainBudget.
                        _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(sessionKey, current));
                    }
                    else
                    {
                        // A dead connection (aborted under us): never send on it again. Nothing can be
                        // answered on it, so it ends now.
                        _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(sessionKey, current));
                        current.DisposeSocket();
                        (toStop ??= new()).Add(current);
                        if (current.TryEnd())
                            (pending ??= new()).Add((WebSocketLifecycleEventKind.Disconnected, null));
                    }
                }

                if (session is null)
                {
                    if (!opens)
                        return false;
                    session = await OpenSessionAsync(sessionKey, request, token).ConfigureAwait(false);
                    (pending ??= new()).Add((WebSocketLifecycleEventKind.Connected, null));
                }

                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                sendCts.CancelAfter(_options.SendTimeout);
                // FR-TRN-09: one logical WebSocket message per packed envelope, as a text frame (see
                // SendOnPoolAsync). Never STOMP-framed: session sockets are off under UseStomp.
                await session.Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // E9: a failed send drops S, so the retry re-routes (all/thread: a fresh S; header-less
                // or none: the pooled socket). Disconnected is raised once per socket: by whoever
                // ends it first, this path or its receive loop. A close the peer started is not
                // answered here: the failed (or cancelled) write has already ended the connection.
                if (session is not null)
                {
                    _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(sessionKey, session));
                    session.DisposeSocket();
                    (toStop ??= new()).Add(session);
                    if (session.TryEnd())
                        (pending ??= new()).Add((WebSocketLifecycleEventKind.Disconnected, ex));
                }
                (pending ??= new()).Add((WebSocketLifecycleEventKind.SendFailed, ex));
                throw;
            }

            if (retires)
                await RetireAsync(sessionKey, session).ConfigureAwait(false);
            if (isReconnect)
                (pending ??= new()).Add((WebSocketLifecycleEventKind.Reconnected, null));
            return true;
        }
        finally
        {
            sendGate.Release();
            if (toStop is not null)
            {
                foreach (var stopped in toStop)
                    stopped.StopLoop();
            }
            if (pending is not null)
            {
                foreach (var (kind, error) in pending)
                    RaiseLifecycle(kind, request.Endpoint, error);
            }
        }
    }

    /// <summary>
    /// Connect a fresh session socket for <paramref name="sessionKey"/>, publish it and start its
    /// receive loop. Called only under the key's send gate, when the key has no open socket.
    /// </summary>
    /// <param name="sessionKey">The session key.</param>
    /// <param name="request">The request (endpoint and identities, both non-null here).</param>
    /// <param name="token">Cancels the connect.</param>
    /// <returns>The new, published session socket.</returns>
    private async Task<SessionSocket> OpenSessionAsync(string sessionKey, TransportRequest request, CancellationToken token)
    {
        var factory = _options.WebSocketFactory ?? (() => new ClientWebSocket());
        var connect = _options.Connect ?? DefaultConnect;
        var socket = factory();
        try
        {
            // The same connect budget as a pooled socket; no STOMP handshake (no sessions under STOMP).
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(_options.ConnectTimeout);
            await connect(socket, request.Endpoint, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var session = new SessionSocket(sessionKey, request.Endpoint, request.LocalDid!, request.PeerDid!, socket);
        // Publish BEFORE reading the seal; dispose seals BEFORE its final sweep, so at least one side
        // always sees the other (the pool's rule, see GetOrConnectAsync).
        _liveSessionSockets[session] = 0;
        _sessions[sessionKey] = session;
        if (_poolSealed)
        {
            _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(sessionKey, session));
            _liveSessionSockets.TryRemove(session, out _);
            session.TryEnd();
            session.DisposeSocket();
            session.MarkLoopDone();
            throw new ObjectDisposedException(nameof(WebSocketDidCommTransport));
        }
        // A send past the entry check can still open a socket once dispose has begun (it held its
        // gate). Published BEFORE this read, and dispose sets the flag BEFORE it marks every live
        // socket as dropping: at least one side sees the other, so nothing this socket reads reaches
        // the consumer either way.
        if (_disposed)
            session.DiscardInbound = true;

        StartReceiveLoop(session);
        if (!_receiveInbound && Interlocked.Exchange(ref _discardNotice, 1) == 0)
        {
            Log(LogLevel.Information,
                "WebSocketDidCommTransport: messages pushed on return-route session sockets are discarded (WebSocketTransportOptions.ReceiveInbound is off).");
        }
        return session;
    }

    /// <summary>
    /// Retire <paramref name="session"/> after an explicit <c>return_route: none</c> was sent over it
    /// (FR-RR-05), still under its send gate: stop routing to it, send the close frame (bounded by
    /// <see cref="CloseGrace"/>), and arm the backstop that ends its loop if the peer never answers.
    /// The loop keeps surfacing the messages already on their way until the peer's close frame.
    /// Never throws: a failed close is caught and logged by its type name, and arming the backstop
    /// does not throw (see <see cref="SessionSocket.ArmBackstop"/>).
    /// </summary>
    /// <param name="sessionKey">The session key.</param>
    /// <param name="session">The session socket the <c>none</c> was sent over.</param>
    private async Task RetireAsync(string sessionKey, SessionSocket session)
    {
        _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(sessionKey, session));
        try
        {
            using var closeCts = new CancellationTokenSource(CloseGrace);
            await session.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "return-route session ended", closeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "WebSocketDidCommTransport: closing a retired return-route session socket failed ({ExceptionType}).", ex.GetType().Name);
        }
        session.ArmBackstop(CloseGrace);
    }

    /// <summary>
    /// Start <paramref name="session"/>'s receive loop without the caller's
    /// <see cref="ExecutionContext"/>: the loop lives as long as the socket, and must not hold the
    /// sending caller's ambient state (async locals, activity) for that long.
    /// </summary>
    /// <param name="session">The published session socket.</param>
    private void StartReceiveLoop(SessionSocket session)
    {
        // SuppressFlow throws when the flow is already suppressed, so only suppress when it is not.
        // The loop never faults (it catches everything), so discarding its task leaves nothing unobserved.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = Task.Run(() => ReceiveLoopAsync(session));
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => ReceiveLoopAsync(session));
    }

    /// <summary>
    /// Read <paramref name="session"/> until it closes (E7): reassemble each message (FR-TRN-09),
    /// close the socket with 1009 when one exceeds the receive cap (JC-11) and with 1008 when one is
    /// not finished within <see cref="WebSocketTransportOptions.MessageReceiveTimeout"/> of its first
    /// part, and hand each whole message to the inbound consumer, one at a time (JC-8), when
    /// <c>ReceiveInbound</c> is on. Never sends anything but close frames, never throws (L-043), and
    /// always ends through <see cref="EndReceiveLoopAsync"/>.
    /// </summary>
    /// <remarks>
    /// The time limit never cancels the loop's token: cancelling an outstanding receive aborts the
    /// socket, so no close frame could follow. On expiry the close frame goes out while the receive is
    /// still outstanding, and the loop keeps awaiting that same receive, discarding, until the peer's
    /// close or the backstop. Every exit awaits the receive it started before the buffer goes back to
    /// the pool.
    /// </remarks>
    /// <param name="session">The session socket to read.</param>
    private async Task ReceiveLoopAsync(SessionSocket session)
    {
        byte[]? buffer = null;
        // The receive last started on the buffer: the buffer is returned to the pool only once it completed.
        Task<WebSocketReceiveResult>? receive = null;
        // The time limit of the message being read: started at its first part and stopped at its last,
        // so the wait for the next message is never timed; none while S is discarding (its backstop
        // bounds it then) or when MessageReceiveTimeout is infinite.
        ReceiveDeadline? deadline = null;
        var token = session.LoopToken;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
            while (true)
            {
                using var message = new MemoryStream();
                // Buffer only what may be surfaced: with ReceiveInbound off, or once S is discarding,
                // frames are read and dropped without being kept.
                var surface = _receiveInbound && !session.DiscardInbound;
                var firstPart = true;
                long total = 0;
                WebSocketReceiveResult result;
                do
                {
                    receive = session.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (!await ReceiveDeadline.WaitAsync(receive, deadline).ConfigureAwait(false))
                    {
                        // The peer stalled mid-message past the limit: close with 1008 now, while this
                        // receive is still outstanding, then go on awaiting it, discarding. Once
                        // dispose began the socket is already discarding and closing: only the timer
                        // stops.
                        surface = false;
                        if (session.DiscardInbound)
                            DropMessage(message, deadline);
                        else
                            await CloseTimedOutAsync(session, message, deadline!).ConfigureAwait(false);
                        deadline = null;
                    }
                    result = await receive.ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (session.DiscardInbound && (surface || deadline is not null))
                    {
                        // Dispose began while this message was being read: nothing more of it is
                        // kept, and it is no longer timed (dispose's close bounds the socket).
                        surface = false;
                        DropMessage(message, deadline);
                        deadline = null;
                    }
                    total += result.Count;
                    if (total > _inboundCap && !session.DiscardInbound)
                    {
                        // JC-11: stop buffering at once (nothing past the cap is kept) and close with 1009.
                        surface = false;
                        message.SetLength(0);
                        message.Capacity = 0;
                        StopDeadline(deadline);
                        deadline = null;
                        await CloseAndDiscardAsync(session, WebSocketCloseStatus.MessageTooBig, "payload too large", DiscardLine.Oversized).ConfigureAwait(false);
                    }
                    else if (!result.EndOfMessage && deadline is { HasExpired: true })
                    {
                        // A part that completed after the limit, with more of the message to come: late
                        // all the same (a peer whose parts are always waiting never leaves a receive outstanding).
                        surface = false;
                        await CloseTimedOutAsync(session, message, deadline).ConfigureAwait(false);
                        deadline = null;
                    }
                    if (surface)
                        message.Write(buffer, 0, result.Count);
                    // The message's time starts when its first part is read, unless S is discarding.
                    if (firstPart && !result.EndOfMessage && !session.DiscardInbound)
                        deadline = ReceiveDeadline.Start(_time, _messageReceiveTimeout);
                    firstPart = false;
                }
                while (!result.EndOfMessage);

                // Stopped before the consumer runs: the limit is on the peer's delivery, not on our handling.
                StopDeadline(deadline);
                deadline = null;

                if (!surface || session.DiscardInbound)
                {
                    Interlocked.Increment(ref _inboundMessagesDropped);
                    continue;
                }

                var payload = message.TryGetBuffer(out var written)
                    ? new ReadOnlyMemory<byte>(written.Array, written.Offset, written.Count)
                    : message.ToArray();
                try
                {
                    // JC-8: inline, so this socket's next message is read only after the consumer
                    // returned; other sockets' loops are unaffected.
                    await _consumer!.ConsumeAsync(new InboundFrame(session.Endpoint, session.LocalDid, session.PeerDid, payload), session.ConsumerToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (session.ConsumerToken.IsCancellationRequested)
                {
                    // Dispose cancelled the consumer: the loop goes on reading, and dropping, to the
                    // peer's close.
                }
                catch (Exception ex)
                {
                    // Any other fault, including a cancellation that is not ours, is the consumer's:
                    // logged by type name (rate-limited), and the loop goes on with the next message.
                    LogRateLimited(ref _consumerFaults,
                        "WebSocketDidCommTransport: the inbound consumer threw {ExceptionType}; the message is dropped and the session socket keeps reading. {Count} such faults.",
                        "WebSocketDidCommTransport: the inbound consumer threw {ExceptionType}; the message is dropped and the session socket keeps reading.",
                        ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            // The socket ended: a fault, a dispose, a retire or discard backstop, a send-failure stop,
            // or a clock that could not time the message (the socket ends rather than go unlimited).
            Log(LogLevel.Debug, "WebSocketDidCommTransport: a return-route session socket stopped reading ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            // A message cut short by the socket's end stops its timer here.
            StopDeadline(deadline);
            // Never returned while a receive may still write into the buffer: every exit above awaits
            // the receive it started, and should one not, the buffer is left to the GC instead.
            if (buffer is not null && receive is not { IsCompleted: false })
                ArrayPool<byte>.Shared.Return(buffer);
            await EndReceiveLoopAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The message being read on <paramref name="session"/> was not finished within
    /// <see cref="WebSocketTransportOptions.MessageReceiveTimeout"/> of its first part: drop what was
    /// kept of it, stop its timer, and close and discard the socket with 1008 (policy violation).
    /// Never throws.
    /// </summary>
    /// <param name="session">The session socket.</param>
    /// <param name="message">What was kept of the message.</param>
    /// <param name="deadline">The message's expired time limit.</param>
    private Task CloseTimedOutAsync(SessionSocket session, MemoryStream message, ReceiveDeadline deadline)
    {
        DropMessage(message, deadline);
        return CloseAndDiscardAsync(session, WebSocketCloseStatus.PolicyViolation, "message receive timeout", DiscardLine.ReceiveTimeout);
    }

    /// <summary>Drop what was kept of the message being read and stop its timer, if any. Never throws.</summary>
    /// <param name="message">What was kept of the message.</param>
    /// <param name="deadline">The message's time limit, or <c>null</c>.</param>
    private static void DropMessage(MemoryStream message, ReceiveDeadline? deadline)
    {
        message.SetLength(0);
        message.Capacity = 0;
        StopDeadline(deadline);
    }

    /// <summary>Stop a message's timer, if any. Never throws: the timer comes from the host's clock, and a timer left running only completes a task nothing waits on.</summary>
    /// <param name="deadline">The time limit, or <c>null</c>.</param>
    private static void StopDeadline(ReceiveDeadline? deadline)
    {
        try
        {
            deadline?.Dispose();
        }
        catch
        {
            // See the summary.
        }
    }

    /// <summary>Which log lines a close-and-discard writes (see <see cref="CloseAndDiscardAsync"/>).</summary>
    private enum DiscardLine
    {
        /// <summary>A pushed message exceeded the receive cap (JC-11).</summary>
        Oversized,

        /// <summary>A pushed message was not finished within <see cref="WebSocketTransportOptions.MessageReceiveTimeout"/>.</summary>
        ReceiveTimeout,
    }

    /// <summary>
    /// The session loop ends <paramref name="session"/>'s service after a message it will not take
    /// (one over the receive cap, JC-11, or one not finished in time). Stop routing to the socket,
    /// switch it to discarding, send a close frame with <paramref name="status"/> under its send gate
    /// (so the close never overlaps a send), arm the backstop and write <paramref name="line"/>'s
    /// rate-limited line. The loop keeps reading, and dropping, until the peer's close or the
    /// backstop. Never throws.
    /// </summary>
    /// <param name="session">The session socket.</param>
    /// <param name="status">The close status: 1009 for size, 1008 for time.</param>
    /// <param name="description">The close frame's reason text.</param>
    /// <param name="line">Which log lines (and counter) to write.</param>
    private async Task CloseAndDiscardAsync(SessionSocket session, WebSocketCloseStatus status, string description, DiscardLine line)
    {
        session.DiscardInbound = true;
        _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(session.Key, session));
        SemaphoreSlim? gate = null;
        var held = false;
        try
        {
            // A loop never waits on a gate once dispose began (dispose owns the gates then), and the
            // wait is bounded and ends when the loop's token is cancelled (dispose, backstop).
            if (!_disposed && _sendLocks.TryGetValue(session.Key, out gate))
            {
                held = await gate.WaitAsync(DrainBudget, session.LoopToken).ConfigureAwait(false);
                // Skipped in CloseSent: a retire already sent its close and armed the backstop.
                if (held && !_disposed && session.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(CloseGrace);
                    await session.Socket.CloseOutputAsync(status, description, closeCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug,
                line == DiscardLine.Oversized
                    ? "WebSocketDidCommTransport: closing an oversized return-route session socket failed ({ExceptionType})."
                    : "WebSocketDidCommTransport: closing a timed-out return-route session socket failed ({ExceptionType}).",
                ex.GetType().Name);
        }
        finally
        {
            if (held)
                gate!.Release();
        }
        session.ArmBackstop(CloseGrace);
        if (line == DiscardLine.Oversized)
        {
            LogRateLimited(ref _oversizedPushes,
                "WebSocketDidCommTransport: a message pushed on a return-route session socket exceeded {MaxBytes} bytes; the socket was closed with 1009 and the rest of its messages are discarded (FR-RR-07). {Count} such messages.",
                "WebSocketDidCommTransport: a message pushed on a return-route session socket exceeded {MaxBytes} bytes; the socket was closed with 1009 and the rest of its messages are discarded (FR-RR-07).",
                _inboundCap);
        }
        else
        {
            LogRateLimited(ref _timedOutPushes,
                "WebSocketDidCommTransport: a message pushed on a return-route session socket was not finished within {Timeout} of its first part; the socket was closed with 1008 and the rest of its messages are discarded (FR-RR-07). {Count} such messages.",
                "WebSocketDidCommTransport: a message pushed on a return-route session socket was not finished within {Timeout} of its first part; the socket was closed with 1008 and the rest of its messages are discarded (FR-RR-07).",
                _messageReceiveTimeout);
        }
    }

    /// <summary>
    /// The end of a receive loop (E8): stop routing to the socket, answer a close the peer started
    /// (under the send gate, bounded; skipped once dispose began), and mark the loop done. Before
    /// dispose began, the loop also ends the socket when nobody did first (<see cref="SessionSocket.TryEnd"/>):
    /// it disposes it and raises <c>Disconnected</c>. Once dispose has begun it ends nothing: the
    /// socket stays in <see cref="_liveSessionSockets"/>, and dispose, which then owns it, answers a
    /// close the peer started before it disposes it. Never throws.
    /// </summary>
    /// <param name="session">The session socket whose loop ended.</param>
    private async Task EndReceiveLoopAsync(SessionSocket session)
    {
        try
        {
            _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(session.Key, session));
            // The peer started the close (a receiver eviction, FR-RR-03; or its answer to nothing we
            // sent): complete the handshake. Under the gate, so the close never overlaps a send; no
            // gate holder ever waits for a loop, so this cannot deadlock. Once dispose has begun the
            // answer is dispose's (its close phase, or its sweep): the loop waits on no gate then.
            if (!_disposed
                && session.Socket.State == WebSocketState.CloseReceived
                && _sendLocks.TryGetValue(session.Key, out var gate))
            {
                var held = false;
                try
                {
                    held = await gate.WaitAsync(DrainBudget, session.LoopToken).ConfigureAwait(false);
                    if (held && !_disposed && session.Socket.State == WebSocketState.CloseReceived)
                    {
                        using var closeCts = new CancellationTokenSource(CloseGrace);
                        await session.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "return-route session closed", closeCts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Debug, "WebSocketDidCommTransport: answering a return-route session socket's close failed ({ExceptionType}).", ex.GetType().Name);
                }
                finally
                {
                    if (held)
                        gate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "WebSocketDidCommTransport: ending a return-route session socket failed ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            // Read once, after the answer attempt: before dispose the loop ends the socket unless
            // another party already did (a send that failed on it or found the connection dead);
            // once dispose has begun the socket is dispose's, so a close the peer started is answered
            // before the socket is disposed, by exactly one party (TryEnd).
            var disposing = _disposed;
            try
            {
                if (!disposing && session.TryEnd())
                {
                    session.DisposeSocket();
                    RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, session.Endpoint);
                }
            }
            catch
            {
                // RaiseLifecycle swallows handler faults, but its own logging is host code too.
            }
            finally
            {
                // Last: dispose's sweep finds the sockets still in _liveSessionSockets and awaits their
                // LoopDone; once dispose has begun, a loop that ends leaves its socket there for it.
                if (!disposing)
                    _liveSessionSockets.TryRemove(session, out _);
                session.MarkLoopDone();
            }
        }
    }

    /// <summary>
    /// Whether a <see cref="TransportRequest.LocalDid"/> / <see cref="TransportRequest.PeerDid"/> can
    /// be part of a session key: a DID (<c>did:</c> prefix) of at most 2048 characters (FR-API-09)
    /// without whitespace or a control character (the key's separator is a line feed).
    /// </summary>
    /// <param name="value">The value.</param>
    private static bool IsSessionDid(string value)
    {
        if (value.Length <= 4
            || value.Length > MaxSessionDidLength
            || !value.StartsWith("did:", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var c in value)
        {
            if (c <= ' ')
                return false;
        }
        return true;
    }

    /// <summary>The refusal of an identity that cannot key a session socket; the value is not echoed.</summary>
    /// <param name="name">The property name, for the message.</param>
    private static ArgumentException InvalidSessionIdentity(string name) => new(
        $"TransportRequest.{name} must be a DID ('did:' prefix) of at most {MaxSessionDidLength} characters without whitespace or control characters to open a return-route session socket.",
        "request");

    /// <summary>Write one log line; never throws (a logging provider is host code).</summary>
    /// <param name="level">The level.</param>
    /// <param name="message">The message template.</param>
    /// <param name="args">The template arguments: type, enum and count values only — never a DID, endpoint or body.</param>
    private void Log(LogLevel level, string message, params object?[] args)
    {
        try
        {
            _logger.Log(level, message, args);
        }
        catch
        {
            // Logging must never change what the transport does.
        }
    }

    /// <summary>Write one log line with the host's own exception; never throws (a logging provider is host code).</summary>
    /// <param name="level">The level.</param>
    /// <param name="exception">An exception host code threw (a lifecycle handler's), handed back to the host's logger as it is.</param>
    /// <param name="message">The message template.</param>
    /// <param name="args">The template arguments: type, enum and count values only — never a DID, endpoint or body.</param>
    private void Log(LogLevel level, Exception exception, string message, params object?[] args)
    {
        try
        {
            _logger.Log(level, exception, message, args);
        }
        catch
        {
            // Logging must never change what the transport does.
        }
    }

    /// <summary>
    /// Write a line a peer could make the transport repeat: the first and every 1000th at Warning
    /// (with the running count), every other one at Debug.
    /// </summary>
    /// <param name="counter">The line's counter.</param>
    /// <param name="warning">The Warning template; its last placeholder is the count.</param>
    /// <param name="debug">The Debug template.</param>
    /// <param name="arg">The one argument both templates share.</param>
    private void LogRateLimited(ref long counter, string warning, string debug, object arg)
    {
        var n = Interlocked.Increment(ref counter);
        if (n == 1 || n % WarnEvery == 0)
            Log(LogLevel.Warning, warning, arg, n);
        else
            Log(LogLevel.Debug, debug, arg);
    }

    /// <summary>
    /// Return the pooled connection for <paramref name="key"/>, opening (and handshaking) a new one
    /// when there is none. Called only from inside the endpoint's send gate.
    /// </summary>
    /// <param name="key">Pool key of the endpoint.</param>
    /// <param name="endpoint">Endpoint to connect to.</param>
    /// <param name="ct">Cancels the connect.</param>
    /// <returns>
    /// The socket, and whether this call opened it — the caller raises <c>Connected</c> after it
    /// releases the send gate rather than under it (see the note in <see cref="SendAsync"/>).
    /// </returns>
    private async Task<(System.Net.WebSockets.WebSocket Socket, bool Opened)> GetOrConnectAsync(string key, Uri endpoint, CancellationToken ct)
    {
        if (_pool.TryGetValue(key, out var existing) && existing.State == WebSocketState.Open)
            return (existing, false);

        var gate = _connectLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pool.TryGetValue(key, out existing) && existing.State == WebSocketState.Open)
                return (existing, false);

            // Connecting after a clean reset: take whatever's in the pool, dispose, and open
            // a new socket. This is the recovery path after SendFailed.
            if (existing is not null)
            {
                _pool.TryRemove(key, out _);
                existing.Dispose();
            }

            var factory = _options.WebSocketFactory ?? (() => new ClientWebSocket());
            var connect = _options.Connect ?? DefaultConnect;
            var socket = factory();
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_options.ConnectTimeout);
                await connect(socket, endpoint, connectCts.Token).ConfigureAwait(false);
                // FR-TRN-12: the STOMP session handshake is part of establishing the connection —
                // once per socket (a pooled socket is already CONNECTED; a reconnect re-runs this
                // path and re-handshakes), under the same connect timeout budget.
                if (_options.UseStomp)
                    await StompConnectAsync(socket, endpoint, connectCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // The nascent socket never entered the pool; dispose it so a failed (or timed-out)
                // connect doesn't leak — significant under the reconnect retry loop.
                socket.Dispose();
                throw;
            }
            _pool[key] = socket;
            if (_poolSealed)
            {
                // DisposeAsync finished draining while this connect was in flight — its budget
                // expired on our send gate, so it is no longer waiting for us. Take the entry back
                // (only if it is still ours) and fail the send: leaving it would hand a disposed
                // transport an Open, never-closed socket. Publishing BEFORE this read pairs with
                // dispose sealing BEFORE its final sweep, so at least one side always sees the other.
                _pool.TryRemove(new KeyValuePair<string, System.Net.WebSockets.WebSocket>(key, socket));
                socket.Dispose();
                throw new ObjectDisposedException(nameof(WebSocketDidCommTransport));
            }
            return (socket, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task DefaultConnect(System.Net.WebSockets.WebSocket socket, Uri endpoint, CancellationToken ct)
    {
        if (socket is ClientWebSocket cws)
        {
            // Drive the handshake through the SSRF-pinning invoker so the underlying TCP connection
            // can only reach a guard-vetted IP (or the named TrustedProxy). There is no path around it.
            return ConnectPinnedAsync(cws, endpoint, ct);
        }
        throw new InvalidOperationException(
            "Default Connect supports ClientWebSocket only. Provide WebSocketTransportOptions.Connect for custom socket types (used by tests against TestServer).");
    }

    /// <summary>
    /// Connect through the SSRF-pinning invoker and reclassify the guard's refusal as fatal.
    /// <see cref="ClientWebSocket"/> wraps every ConnectCallback failure in a retriable
    /// <see cref="WebSocketException"/> (via <see cref="HttpRequestException"/>), so without this
    /// unwrap an endpoint that resolves only to private addresses would burn the whole exponential
    /// backoff budget while holding the endpoint's send gate — the refusal is deterministic and
    /// must fail the send on the attempt it arrives (issue #72). Every other connect failure keeps
    /// its shape and classification.
    /// </summary>
    /// <param name="socket">The socket to connect.</param>
    /// <param name="endpoint">Endpoint to connect to.</param>
    /// <param name="ct">Cancels the connect.</param>
    private async Task ConnectPinnedAsync(ClientWebSocket socket, Uri endpoint, CancellationToken ct)
    {
        try
        {
            await socket.ConnectAsync(endpoint, _pinnedInvoker, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is GuardRefusalException refusal)
                    throw new FatalSendException(
                        refusal.Cause,
                        "every address the endpoint resolves to is private or reserved (SSRF defense)");
            }
            throw;
        }
    }

    /// <summary>Wrap the packed envelope in one STOMP SEND frame (FR-TRN-12).</summary>
    private ReadOnlyMemory<byte> BuildStompSendPayload(TransportRequest request)
    {
        var destination = _options.StompDestination ?? request.Endpoint.AbsolutePath;
        var frame = new StompFrame(
            "SEND",
            new[]
            {
                KeyValuePair.Create("destination", destination),
                KeyValuePair.Create("content-type", request.MediaType),
            },
            request.Payload);
        return StompFrameCodec.Encode(frame);
    }

    /// <summary>
    /// FR-TRN-12 session handshake: CONNECT (accept-version:1.2, host, heart-beat:0,0) and await
    /// the server's CONNECTED. No receipts / subscriptions / heart-beats are negotiated. Any
    /// non-CONNECTED answer (an ERROR frame, malformed bytes, or a close) is a transport failure.
    /// </summary>
    private async Task StompConnectAsync(System.Net.WebSockets.WebSocket socket, Uri endpoint, CancellationToken ct)
    {
        var connectFrame = new StompFrame(
            "CONNECT",
            new[]
            {
                KeyValuePair.Create("accept-version", "1.2"),
                KeyValuePair.Create("host", endpoint.Host),
                KeyValuePair.Create("heart-beat", "0,0"),
            },
            ReadOnlyMemory<byte>.Empty);
        await socket.SendAsync(StompFrameCodec.Encode(connectFrame), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);

        // The CONNECTED answer is a small control frame; 16 KiB is generous headroom.
        var answer = await ReceiveOneMessageAsync(socket, maxBytes: 16 * 1024, endpoint, ct).ConfigureAwait(false);
        StompFrame frame;
        try
        {
            frame = StompFrameCodec.Decode(answer);
        }
        catch (MalformedMessageException ex)
        {
            throw new TransportException(
                $"STOMP handshake with '{endpoint}' failed: the server's answer is not a valid STOMP frame (FR-TRN-12).",
                ex, httpStatusCode: null, scheme: endpoint.Scheme);
        }
        if (!string.Equals(frame.Command, "CONNECTED", StringComparison.Ordinal))
        {
            throw new TransportException(
                $"STOMP handshake with '{endpoint}' failed: expected CONNECTED, got '{frame.Command}' (FR-TRN-12).",
                httpStatusCode: null,
                scheme: endpoint.Scheme);
        }
        // STOMP 1.2 §CONNECTED: the server states the session's actual version. We offered
        // exactly 1.2, so an absent version or any other value means a session we don't speak —
        // fail the handshake rather than continue on mismatched framing rules. TryGetHeader
        // honors repeated-header-first-wins, so a duplicate version header can't smuggle a
        // second value past this check. Mirrors the server-side accept-version validation in
        // StompServerSession.
        if (!frame.TryGetHeader("version", out var version) || !string.Equals(version, "1.2", StringComparison.Ordinal))
        {
            // Permanently fatal, unlike the failures above: a server that states a version it will
            // speak is not having a bad moment, and reconnecting can only produce the same answer.
            // FatalSendException keeps it out of the retry budget (see SendAsync).
            throw new FatalSendException(
                new TransportException(
                    $"STOMP handshake with '{endpoint}' failed: expected version 1.2, got '{version ?? "(absent)"}' (FR-TRN-12).",
                    httpStatusCode: null,
                    scheme: endpoint.Scheme),
                "the peer's STOMP handshake is permanently incompatible");
        }
    }

    /// <summary>Reassemble one logical WebSocket message (FR-TRN-09 fragmentation rule), capped at <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]> ReceiveOneMessageAsync(
        System.Net.WebSockets.WebSocket socket, int maxBytes, Uri endpoint, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new TransportException(
                    $"STOMP handshake with '{endpoint}' failed: the server closed the connection before answering (FR-TRN-12).",
                    httpStatusCode: null,
                    scheme: endpoint.Scheme);
            }
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > maxBytes)
            {
                throw new TransportException(
                    $"STOMP handshake with '{endpoint}' failed: the answer exceeds {maxBytes} bytes (FR-TRN-12).",
                    httpStatusCode: null,
                    scheme: endpoint.Scheme);
            }
            if (result.EndOfMessage)
                return ms.ToArray();
        }
    }

    private void RaiseLifecycle(WebSocketLifecycleEventKind kind, Uri endpoint, Exception? exception = null)
    {
        if (Lifecycle is not null)
        {
            try { Lifecycle(this, new WebSocketLifecycleEventArgs(kind, endpoint, exception)); }
            catch (Exception ex)
            {
                // Lifecycle handlers must never break the transport. Swallow + log so the host's
                // observability defect can't cascade into a delivery failure. The host gets its own
                // handler's exception back, as before; the line goes through the guarded Log, since a
                // logging provider is host code too.
                Log(LogLevel.Warning, ex, "WebSocketDidCommTransport: a lifecycle handler threw {ExceptionType} ({Kind}).", ex.GetType().Name, kind);
            }
        }
    }

    // Scheme + authority + path + query: the query is part of the WebSocket handshake URL and
    // routinely carries routing/auth material, so endpoints differing only in query must not
    // share a socket whose handshake used the other's URL (issue #72). Fragments never reach
    // the wire and stay out of the key.
    private static string PoolKey(Uri endpoint) =>
        $"{endpoint.Scheme.ToLowerInvariant()}://{endpoint.Authority}{endpoint.PathAndQuery}";

    private ResiliencePipeline BuildReconnectPipeline(WebSocketTransportOptions options)
    {
        // FR-TRN-11 / DD-05: exponential backoff with jitter. MaxRetryAttempts == 0 disables
        // the retry strategy entirely (useful for tests + senders that want fail-fast).
        var builder = new ResiliencePipelineBuilder();
        if (options.MaxReconnectAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                // TransportException covers the TRANSIENT STOMP handshake failures (FR-TRN-12) —
                // a peer that closed mid-handshake may well answer the next attempt, so failing to
                // establish the session is a connection failure like any other and must consume the
                // same reconnect budget. Permanently fatal failures — the peer named a STOMP
                // version we do not speak, or the connect-time SSRF guard refused every address the
                // endpoint resolves to — arrive as FatalSendException, which is absent from this
                // list precisely so they are never retried. Invariant: the two non-retryable
                // TransportException sources — the scheme refusal and the pre-send SSRF check in
                // SendAsync — are thrown BEFORE the pipeline executes, so a pre-pipeline refusal is
                // never retried here either.
                // OperationCanceledException splits on WHO cancelled, mirroring the catch-filter in
                // SendAsync: the caller's token means the caller gave up — fatal, propagate — while
                // any other OCE is a per-attempt connect/send timeout firing on a half-dead
                // connection. A production ClientWebSocket surfaces that timeout as the PLAIN base
                // type (not the TaskCanceledException subclass Task.Delay-based fakes throw), so
                // matching the base type here is what makes a real half-dead connection retryable.
                // The !_disposed guard stops all retrying once DisposeAsync has begun: a disposed
                // transport must never reconnect (and ObjectDisposedException derives from
                // InvalidOperationException, so without the guard a post-dispose failure would
                // burn the whole backoff budget before surfacing).
                ShouldHandle = args => new ValueTask<bool>(
                    !_disposed
                    && args.Outcome.Exception switch
                    {
                        OperationCanceledException => !args.Context.CancellationToken.IsCancellationRequested,
                        WebSocketException or TimeoutException or InvalidOperationException or TransportException => true,
                        _ => false,
                    }),
                MaxRetryAttempts = options.MaxReconnectAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.ReconnectBaseDelay,
                MaxDelay = options.ReconnectMaxDelay,
                UseJitter = true,
            });
        }
        return builder.Build();
    }

    /// <summary>
    /// Close every connection and release every gate. What this guarantees, precisely:
    /// <list type="bullet">
    /// <item>No send that has not yet acquired its endpoint's (or session's) send gate will ever run —
    /// new sends fail fast at the <see cref="SendAsync"/> entry check, retries stop (the retry
    /// predicate tests the flag), and a send that wins a gate after this point re-checks it inside.</item>
    /// <item>Every send already past that check is drained — including one still inside its
    /// CONNECT phase, which holds its gate but has no pool (or session) entry yet. That is why the
    /// drain iterates the gates, not the pool, and reads the pool and the session sockets
    /// <i>inside</i> the gate: every pool and session write happens under the same gate.</item>
    /// <item>A send that outlives the drain budget is abandoned, not waited for: its socket is
    /// hard-disposed (or, if it has not been published yet, refused by the seal in
    /// <see cref="GetOrConnectAsync"/> / <see cref="OpenSessionAsync"/>), so no Open socket survives
    /// this call either way: pooled, return-route session, or retiring session socket.</item>
    /// <item>Every open pooled socket and active session socket is closed politely: its close frame
    /// is sent (after a STOMP <c>DISCONNECT</c> under <see cref="WebSocketTransportOptions.UseStomp"/>)
    /// and the peer's close frame is waited for, up to <see cref="CloseBudget"/>, every endpoint
    /// concurrently. A peer answers the close only after it has handled the messages it received
    /// before it, so a one-way message sent just before this call (a Message Pickup 4.0
    /// acknowledgement) has been handled when the call returns, unless the peer does not answer in
    /// time: the socket is then aborted. A session socket's own receive loop stays its only reader
    /// and reads the peer's close. With an infinite or non-positive
    /// <see cref="WebSocketTransportOptions.SendTimeout"/> the budget is zero and nothing is waited
    /// for. A retiring session socket has already sent its close: it is not closed again, only
    /// stopped and disposed (below).</item>
    /// <item>From the moment this call begins, nothing new reaches the inbound consumer: every
    /// message a peer pushes on a session socket, active or retiring, is read and dropped whole
    /// (the rest of one being read included). A consumer call already in progress keeps its token
    /// until every endpoint has drained (or used up its drain budget), so a reply it is sending, to
    /// any endpoint, is drained like any other send. Only then does every consumer call in
    /// progress, on active and retiring session sockets alike, have its token cancelled, before
    /// any socket is closed: it then no longer keeps its loop from the peer's close.</item>
    /// <item>A close the peer started on a session socket is answered exactly once, and the socket
    /// is disposed only after that answer was written, unless its budget ran out first (or a failed
    /// send had already ended the connection): by the receive loop before this call began, or by
    /// this call (its close phase for a socket it drained, its sweep for one the loop, or a send that
    /// found the socket closed, took out of routing). A send that finds the socket closed by the peer
    /// writes nothing on it: it only stops routing to it.</item>
    /// <item>Every session socket's receive loop is cancelled and awaited for at most
    /// <see cref="CloseBudget"/>. A loop held by an inbound consumer that ignores cancellation is
    /// abandoned; its socket is disposed when the budget runs out, and the loop touches nothing
    /// when it ends. No loop starts waiting on a send gate once this call has begun; one already
    /// waiting is released by the close phase or stopped by the sweep.</item>
    /// <item><c>Disconnected</c> is raised once per socket that was connected, naming its endpoint.</item>
    /// <item>The call is bounded: at most <see cref="DrainBudget"/> + 2 × <see cref="CloseBudget"/>
    /// in total, no matter how many endpoints are wedged (they all drain at once, then all close at
    /// once) or how unresponsive the peers are (every network write here, and every wait for a
    /// peer's close, is cancellation-bounded). Since every endpoint closes only once all have
    /// drained, a fast endpoint's close waits for the slowest drain.</item>
    /// <item>It never throws, and it always ends with the seal and the sweeps: a socket (a host's
    /// <see cref="WebSocketTransportOptions.WebSocketFactory"/>) or a logging provider that throws
    /// does not stop the teardown, and a gate an endpoint drained is always released.</item>
    /// <item>It is idempotent and concurrency-safe: the first caller tears down, the rest wait.</item>
    /// </list>
    /// </summary>
    /// <returns>A task that completes when teardown is done.</returns>
    public async ValueTask DisposeAsync()
    {
        // Set BEFORE any gate or socket is touched — the whole invariant above rests on this
        // ordering (SendAsync re-reads the flag inside the gate).
        _disposed = true;
        if (Interlocked.Exchange(ref _disposeClaimed, 1) != 0)
        {
            // A second caller must not run teardown again: it would race the first over the same
            // gates and sockets (and used to escape with ObjectDisposedException from an
            // already-disposed gate, leaking everything after that point). Wait for the winner.
            await _disposeCompleted.Task.ConfigureAwait(false);
            return;
        }

        EndpointTeardown[]? drained = null;
        try
        {
            await TearDownEndpointsAsync(teardowns => drained = teardowns).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The phases never throw by design (every per-endpoint step catches what it throws, host
            // sockets and logging included); should one, dispose itself still does not throw.
            Log(LogLevel.Debug, "WebSocketDidCommTransport: ignoring a teardown exception during dispose ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            try
            {
                // A gate an endpoint drained is released by its close; one whose close never ran (a
                // phase that threw) is released here, so no gate stays held.
                if (drained is not null)
                {
                    foreach (var teardown in drained)
                        teardown.ReleaseGate();
                }
                // Whatever the phases did: the seal and the sweeps run, so no socket is left behind.
                await SweepAsync().ConfigureAwait(false);
            }
            finally
            {
                _disposeCompleted.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Dispose's three phases over every endpoint: drain, cancel the consumers, close. Each
    /// per-endpoint step catches what it throws; <paramref name="onDrained"/> receives the drained
    /// endpoints as soon as phase 1 is over, so the caller can release any gate a later phase did not.
    /// </summary>
    /// <param name="onDrained">Receives phase 1's result.</param>
    private async Task TearDownEndpointsAsync(Action<EndpointTeardown[]> onDrained)
    {
        // Nothing new reaches the inbound consumer once dispose has begun: every session socket,
        // active or retiring, drops what it reads from here on. A consumer call in progress keeps
        // its token for now: a reply it is sending, to any endpoint, must be drained, not
        // cancelled (its token is linked into that send).
        foreach (var live in _liveSessionSockets.Keys)
            live.DiscardInbound = true;
        // Every endpoint with a gate (a send is, or was, running), a pooled socket or an active
        // session socket (session keys share the gate dictionary, JC-21).
        var keys = new HashSet<string>(_sendLocks.Keys, StringComparer.Ordinal);
        keys.UnionWith(_pool.Keys);
        keys.UnionWith(_sessions.Keys);
        // Phase 1, every endpoint at once (a serial drain made N unresponsive endpoints cost
        // N × budget): drain its in-flight send and take its socket. Each keeps its gate, once it
        // has it, until its socket's close. No send holds one gate while it waits for another,
        // and one that starts now fails at the entry check, so holding gates across the phases
        // cannot deadlock.
        var drained = await Task.WhenAll(keys.Select(DrainEndpointAsync)).ConfigureAwait(false);
        onDrained(drained);
        // Phase 2, once every drain has finished or used up its budget: cancel every consumer call
        // in progress, on active and retiring session sockets alike. Until now none was, so a
        // reply a consumer was sending, to whichever endpoint, has been drained. Its token's
        // callbacks are host code and the drained gates are held: on pool threads.
        foreach (var live in _liveSessionSockets.Keys)
            live.StopConsumerSoon();
        // Phase 3, every endpoint at once: close its socket and release its gate.
        var disconnected = await Task.WhenAll(drained.Select(CloseEndpointAsync)).ConfigureAwait(false);
        // Raised here, not inside the per-endpoint tasks, so handlers are never invoked
        // concurrently (or under a send gate) — the guarantee the serial loop gave for free.
        foreach (var endpoint in disconnected)
        {
            if (endpoint is not null)
                RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, endpoint);
        }
    }

    /// <summary>
    /// The end of dispose, whatever its phases did: seal, sweep the pool and the session sockets
    /// still left, and release the idle gates. Never throws.
    /// </summary>
    private async Task SweepAsync()
    {
        // Seal BEFORE the sweep: a send whose drain budget expired publishes its socket and
        // then reads this flag, so ordering the seal first makes it impossible for both the
        // sweep and the late writer to miss the entry (see GetOrConnectAsync).
        _poolSealed = true;
        foreach (var key in _pool.Keys)
        {
            if (_pool.TryRemove(key, out var leftover))
                DisposeQuietly(leftover);
        }

        // The sweep, after the seal like the pool's: every session socket still left. An active
        // one the drain could not reach; a retiring one (no longer in _sessions, still reading);
        // and one whose loop took it out of _sessions once dispose had begun (it reads the peer's
        // close and leaves the socket here, unanswered, for dispose). Each is stopped, its loop
        // awaited, a close the peer started answered under the key's send gate, and the socket
        // disposed, all within one CloseBudget: a loop held by a consumer that ignores
        // cancellation is abandoned, its socket disposed at the budget.
        var leftovers = new HashSet<SessionSocket>();
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryRemove(key, out var active))
                leftovers.Add(active);
        }
        foreach (var live in _liveSessionSockets.Keys)
            leftovers.Add(live);
        if (leftovers.Count > 0)
        {
            using var sweep = new CancellationTokenSource(CloseBudget);
            var ended = await Task.WhenAll(leftovers.Select(s => FinishSessionForDisposeAsync(s, sweep.Token))).ConfigureAwait(false);
            // Raised here, with no gate held and one at a time, as for the close phase.
            foreach (var endpoint in ended)
            {
                if (endpoint is not null)
                    RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, endpoint);
            }
            var abandoned = leftovers.Count(s => !s.LoopDone.IsCompleted);
            if (abandoned > 0)
                Log(LogLevel.Debug, "WebSocketDidCommTransport: {Count} return-route session receive loop(s) did not end within the close budget; abandoned.", abandoned);
        }

        try
        {
            DisposeIdleGates(_connectLocks);
            DisposeIdleGates(_sendLocks);
            _pinnedInvoker.Dispose();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "WebSocketDidCommTransport: ignoring a cleanup exception during dispose ({ExceptionType}).", ex.GetType().Name);
        }
    }

    /// <summary>Dispose a pooled socket. Never throws: a socket implementation (a host's <c>WebSocketFactory</c>) that throws on dispose is closed as far as we are concerned.</summary>
    /// <param name="socket">The socket.</param>
    private static void DisposeQuietly(System.Net.WebSockets.WebSocket socket)
    {
        try
        {
            socket.Dispose();
        }
        catch
        {
            // See the summary.
        }
    }

    /// <summary>
    /// One endpoint's (or session's) teardown, across dispose's phases: drained by
    /// <see cref="DrainEndpointAsync"/>, closed by <see cref="CloseEndpointAsync"/>.
    /// </summary>
    private sealed class EndpointTeardown(string key, SemaphoreSlim? gate)
    {
        /// <summary>Pool key of the endpoint, or session key of a session socket.</summary>
        public string Key { get; } = key;

        /// <summary>The key's send gate, if one was ever minted.</summary>
        public SemaphoreSlim? Gate { get; } = gate;

        /// <summary>Whether the drain acquired <see cref="Gate"/>: held from then until the socket's close.</summary>
        public bool GateHeld { get; set; }

        /// <summary>The pooled socket taken for this key, if any.</summary>
        public System.Net.WebSockets.WebSocket? Socket { get; set; }

        /// <summary>The active session socket taken for this key, if any.</summary>
        public SessionSocket? Session { get; set; }

        /// <summary>Whether this teardown claimed the session socket's end (and so reports it).</summary>
        public bool SessionEnded { get; set; }

        private int _gateReleased;

        /// <summary>Release <see cref="Gate"/> when the drain acquired it and it has not been released yet (idempotent).</summary>
        public void ReleaseGate()
        {
            if (GateHeld && Interlocked.Exchange(ref _gateReleased, 1) == 0)
                Gate!.Release();
        }
    }

    /// <summary>
    /// Phase 1 of dispose for one endpoint (or session): drain its in-flight send, then take its
    /// pooled (or active session) socket. The gate, once acquired, stays held until the socket's
    /// close (<see cref="CloseEndpointAsync"/>). Never throws: one bad endpoint must not abort the
    /// teardown of the others.
    /// </summary>
    /// <param name="key">Pool key of the endpoint, or session key of a session socket.</param>
    /// <returns>What the close needs.</returns>
    private async Task<EndpointTeardown> DrainEndpointAsync(string key)
    {
        // TryGetValue, never GetOrAdd: dispose must not mint gates.
        _sendLocks.TryGetValue(key, out var sendGate);
        var teardown = new EndpointTeardown(key, sendGate);
        try
        {
            // Bounded by the worst case for a HEALTHY hold — the gate spans connect + STOMP
            // handshake + send — so an in-flight send always finishes inside it. On expiry the
            // polite shutdown is skipped and the hard Dispose of the close phase unblocks the stuck
            // send with a socket-level error instead. Inside the try: the wait is part of the
            // guarded region, so nothing it could throw can strand the rest of this teardown.
            teardown.GateHeld = sendGate is not null && await sendGate.WaitAsync(DrainBudget).ConfigureAwait(false);
            // Read the pool INSIDE the gate: a send that was still connecting when dispose began
            // has published its socket by now, and every pool write happens under this same gate.
            _pool.TryRemove(key, out var socket);
            teardown.Socket = socket;
            // The same for the session sockets. A key names a pooled socket or a session socket,
            // never both, so at most one of these is non-null.
            _sessions.TryRemove(key, out var session);
            teardown.Session = session;
            if (session is not null)
            {
                // Nothing the peer pushes reaches the consumer: DisposeAsync marked every socket it
                // found as dropping, and OpenSessionAsync marks one a send opened since, as it is
                // published. Dispose, not the loop, reports the socket's end, after the gate is
                // released.
                teardown.SessionEnded = session.TryEnd();
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "WebSocketDidCommTransport: ignoring drain exception during dispose ({ExceptionType}).", ex.GetType().Name);
        }
        return teardown;
    }

    /// <summary>
    /// Phase 3 of dispose for one endpoint (or session): close its pooled (or active session)
    /// socket, when the drain got its gate and the socket is open, then dispose the socket and
    /// release the gate. Never throws: a close is best effort, and one bad endpoint must not abort
    /// the teardown of the others.
    /// </summary>
    /// <param name="teardown">What the drain took.</param>
    /// <returns>
    /// The endpoint to report as <c>Disconnected</c>, or <c>null</c> when there was no connection
    /// to close (a gate exists, but no pooled or active session socket), or when the session socket
    /// already reported its end.
    /// </returns>
    private async Task<Uri?> CloseEndpointAsync(EndpointTeardown teardown)
    {
        var socket = teardown.Socket;
        var session = teardown.Session;
        try
        {
            if (teardown.GateHeld && session is not null && session.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // The same bounded wait for the peer's close as the pooled socket's below, with the
                // session's own receive loop as the socket's only reader: send our close frame, then
                // wait for the loop to read the peer's close and end. The loop then answers nothing,
                // waits on no send gate and disposes nothing (dispose has begun), so it ends without
                // this gate. When the peer's close was read first (the socket is in CloseReceived),
                // this close frame is its answer. A consumer call in progress was cancelled once
                // every endpoint had drained (DisposeAsync), so it does not keep the loop from the
                // peer's close. Both steps share one CloseBudget; on expiry the finally disposes the
                // socket under the loop, which ends it.
                using var closeCts = new CancellationTokenSource(CloseBudget);
                await session.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "transport disposed", closeCts.Token).ConfigureAwait(false);
                await session.LoopDone.WaitAsync(closeCts.Token).ConfigureAwait(false);
            }
            if (teardown.GateHeld && socket is not null && socket.State == WebSocketState.Open)
            {
                // The shutdown writes and the wait for the peer's close share one budget: a peer that
                // stopped reading (TCP backpressure on the DISCONNECT) or never answers the close
                // would otherwise hang dispose forever. On expiry the cancellation aborts the socket
                // and we fall through to the hard Dispose below.
                using var closeCts = new CancellationTokenSource(CloseBudget);
                // FR-TRN-12: end the STOMP session politely before the WebSocket close. Best
                // effort — no RECEIPT is requested (receipts are out of the minimal subset).
                if (_options.UseStomp)
                {
                    var disconnect = StompFrameCodec.Encode(new StompFrame(
                        "DISCONNECT", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty));
                    await socket.SendAsync(disconnect, WebSocketMessageType.Binary, endOfMessage: true, closeCts.Token).ConfigureAwait(false);
                }
                // CloseAsync, not CloseOutputAsync (#100, OD-7): send our close frame and wait for
                // the peer's. A peer answers the close only after it has handled what it received
                // before it (the mediator handles one connection's messages in order), while it may
                // stop handling a message once the connection drops (the mediator does, on Kestrel).
                // Waiting is what keeps a one-way message sent just before dispose, such as a
                // Message Pickup 4.0 acknowledgement, from being lost. Still bounded: the wait runs
                // under the same CloseBudget, every endpoint closes concurrently (one budget in
                // total, not one per socket), and a peer that never answers is cut off when it
                // expires. Data frames that arrive before the peer's close are read and dropped.
                // CloseAsync is this socket's only reader: a pooled socket is otherwise read only by
                // the STOMP handshake in GetOrConnectAsync, before it is published, under the send
                // gate held here. (A session socket has a reader of its own: see above.)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "transport disposed", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Guarded and by type name only: a throwing logging provider must not strand the rest of
            // this endpoint's teardown, and the exception text is not ours to log.
            Log(LogLevel.Debug, "WebSocketDidCommTransport: ignoring close exception during dispose ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            if (socket is not null)
                DisposeQuietly(socket);
            session?.DisposeSocket();
            teardown.ReleaseGate();
            // After the release: stopping the loop runs the consumer's cancellation callbacks, which
            // never run under a send gate.
            session?.StopLoop();
        }
        if (session is not null)
            return teardown.SessionEnded ? session.Endpoint : null;
        // The pool key is a well-formed absolute URI string (see PoolKey); reconstruct it so
        // observers see which endpoint just disconnected on a clean close.
        return socket is not null && Uri.TryCreate(teardown.Key, UriKind.Absolute, out var endpoint) ? endpoint : null;
    }

    /// <summary>
    /// The sweep's teardown of one session socket: claim its end, stop its loop (before any gate:
    /// the consumer's cancellation callbacks are host code), wait for the loop, its only reader, to
    /// finish, answer a close the peer started when this call ended the socket (under the key's send
    /// gate, re-checking the state there: a send that outlived the drain may still hold it), and
    /// dispose the socket. Every wait and the answer share <paramref name="token"/> (one
    /// <see cref="CloseBudget"/> for the whole sweep). Never throws.
    /// </summary>
    /// <param name="session">The session socket.</param>
    /// <param name="token">The sweep's budget.</param>
    /// <returns>The endpoint to report as <c>Disconnected</c>, or <c>null</c> when another party ended the socket.</returns>
    private async Task<Uri?> FinishSessionForDisposeAsync(SessionSocket session, CancellationToken token)
    {
        var owns = session.TryEnd();
        session.StopLoop();
        try
        {
            await session.LoopDone.WaitAsync(token).ConfigureAwait(false);
            // Every session key's gate lives until DisposeIdleGates, after the sweep: a session key
            // always finds its gate here.
            if (owns && session.Socket.State == WebSocketState.CloseReceived && _sendLocks.TryGetValue(session.Key, out var gate))
            {
                var held = false;
                try
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    held = true;
                    if (session.Socket.State == WebSocketState.CloseReceived)
                        await session.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "transport disposed", token).ConfigureAwait(false);
                }
                finally
                {
                    if (held)
                        gate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "WebSocketDidCommTransport: ending a return-route session socket during dispose failed ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            session.DisposeSocket();
            _liveSessionSockets.TryRemove(session, out _);
        }
        return owns ? session.Endpoint : null;
    }

    /// <summary>
    /// Dispose every gate that is provably idle. A gate acquired with a zero wait has no holder,
    /// and — because <see cref="_disposed"/> is already set — no future sender will wait on it
    /// (sends fail fast before touching gates), so disposing it is safe. A gate still held by a
    /// send stuck past the drain timeout is deliberately left undisposed: disposing it would turn
    /// that sender's <c>Release()</c> in its <c>finally</c> into an
    /// <see cref="ObjectDisposedException"/>. An undisposed <see cref="SemaphoreSlim"/> holds no
    /// unmanaged state, so the skipped gate is simply left to the GC.
    /// </summary>
    /// <param name="gates">The gate dictionary to drain, dispose, and clear.</param>
    private static void DisposeIdleGates(ConcurrentDictionary<string, SemaphoreSlim> gates)
    {
        foreach (var (_, gate) in gates)
        {
            // Tolerate an already-disposed gate: the dispose-once latch makes a concurrent
            // disposer impossible, but a gate is cheap to skip and a throw here would strand
            // everything after it (that is exactly how the SocketsHttpHandler used to leak).
            try
            {
                if (gate.Wait(0))
                    gate.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        gates.Clear();
    }

    /// <summary>
    /// One return-route session socket S(E, L, P) (E11): the socket, the identities it was opened
    /// for, and its receive loop's cancellation and completion. Every member is safe to call from
    /// any thread, any number of times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop's <see cref="CancellationTokenSource"/> is cancelled only through
    /// <see cref="StopLoop"/>, and the consumer's (linked to it) only through it or
    /// <see cref="StopConsumerSoon"/>; both contain whatever the callbacks registered on the tokens
    /// throw. The consumer's token is the one the inbound consumer gets, so host code (the consumer,
    /// the dispatcher, every handler) can register callbacks on it; a callback that throws must not
    /// escape on a thread the transport does not own. That is why the backstop is a timer of its own
    /// whose callback calls <see cref="StopLoop"/>, and never <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>:
    /// the source's own timer rethrows a callback's exception on a thread-pool thread, which ends
    /// the process.
    /// </para>
    /// <para>
    /// The <see cref="CancellationTokenSource"/>s are never disposed: several parties may cancel them (a
    /// failed send, the backstop, dispose) at any time, and
    /// <see cref="CancellationTokenSource.Dispose()"/> must not race a cancel. It holds no unmanaged
    /// state. The backstop timer is disposed when the loop ends (<see cref="MarkLoopDone"/>), or
    /// right after it is armed when the loop has already ended.
    /// </para>
    /// </remarks>
    private sealed class SessionSocket
    {
        // The backstop's callback: a static lambda over the socket, so the timer roots nothing else.
        private static readonly TimerCallback s_backstop = static state => ((SessionSocket)state!).StopLoop();

        private readonly CancellationTokenSource _loop = new();
        // The consumer's token: cancelled with the loop's, or on its own by StopConsumerSoon (dispose), so
        // the loop can go on reading to the peer's close once the consumer was told to stop.
        private readonly CancellationTokenSource _consumer;
        private readonly TaskCompletionSource _loopDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ended;
        private volatile bool _discardInbound;
        // The armed backstop, or null. Set once (the first arm wins); disposed when the loop ends.
        private Timer? _backstop;

        /// <summary>Create the record of a connected session socket.</summary>
        /// <param name="key">The session key.</param>
        /// <param name="endpoint">The endpoint it was opened to.</param>
        /// <param name="localDid">The local DID of the request that opened it.</param>
        /// <param name="peerDid">The peer DID of the request that opened it.</param>
        /// <param name="socket">The connected socket.</param>
        public SessionSocket(string key, Uri endpoint, string localDid, string peerDid, System.Net.WebSockets.WebSocket socket)
        {
            Key = key;
            Endpoint = endpoint;
            LocalDid = localDid;
            PeerDid = peerDid;
            Socket = socket;
            LoopToken = _loop.Token;
            _consumer = CancellationTokenSource.CreateLinkedTokenSource(LoopToken);
            ConsumerToken = _consumer.Token;
        }

        /// <summary>The session key.</summary>
        public string Key { get; }

        /// <summary>The endpoint the socket was opened to (named by <c>Disconnected</c>).</summary>
        public Uri Endpoint { get; }

        /// <summary>The local DID of the request that opened the socket (the frames' attribution).</summary>
        public string LocalDid { get; }

        /// <summary>The peer DID of the request that opened the socket (the frames' attribution).</summary>
        public string PeerDid { get; }

        /// <summary>The socket.</summary>
        public System.Net.WebSockets.WebSocket Socket { get; }

        /// <summary>Cancels the receive loop: its receives, and with them the consumer (<see cref="ConsumerToken"/>).</summary>
        public CancellationToken LoopToken { get; }

        /// <summary>The token the inbound consumer gets: cancelled with <see cref="LoopToken"/>, or on its own by <see cref="StopConsumerSoon"/>.</summary>
        public CancellationToken ConsumerToken { get; }

        /// <summary>Completes when the receive loop has fully ended (or never started).</summary>
        public Task LoopDone => _loopDone.Task;

        /// <summary>Set once an oversized message (1009) or a message not finished in time (1008) closed the socket, or dispose began: every later message is dropped (after dispose began, the rest of the one being read too).</summary>
        public bool DiscardInbound
        {
            get => _discardInbound;
            set => _discardInbound = value;
        }

        /// <summary>
        /// Whether this call is the socket's first end: the one that disposes the socket, answers
        /// first a close the peer started (when it can still write: not after a failed send), and
        /// raises <c>Disconnected</c>. Exactly one party ends each socket.
        /// </summary>
        public bool TryEnd() => Interlocked.Exchange(ref _ended, 1) == 0;

        /// <summary>
        /// Cancel the consumer's token, but not the loop's (the loop goes on reading to the peer's
        /// close), on a thread-pool thread without the caller's <see cref="ExecutionContext"/>: dispose
        /// calls it while it holds the drained send gates, and the callbacks registered on that token
        /// are host code, which never runs under one. Never throws, and neither does the work item (see
        /// <see cref="StopLoop"/>).
        /// </summary>
        public void StopConsumerSoon()
            => ThreadPool.UnsafeQueueUserWorkItem(static socket => socket.StopConsumer(), this, preferLocal: false);

        private void StopConsumer()
        {
            try
            {
                _consumer.Cancel();
            }
            catch
            {
                // A host callback registered on the consumer's token threw; see StopLoop.
            }
        }

        /// <summary>
        /// Cancel the receive loop now. Never throws: the token is cancelled and every callback
        /// registered on it runs (on the calling thread) even when one of them throws, and what they
        /// throw is swallowed here. The only path that cancels the loop's token.
        /// </summary>
        public void StopLoop()
        {
            try
            {
                _loop.Cancel();
            }
            catch
            {
                // A host callback registered on the consumer's token threw (AggregateException); the
                // token is cancelled and every other callback ran regardless.
            }
        }

        /// <summary>
        /// Stop the receive loop after <paramref name="after"/> unless it ended first: a timer whose
        /// callback calls <see cref="StopLoop"/>, so what a host callback on the loop's token throws
        /// is contained on the timer thread instead of escaping it. Only the first arm counts: every
        /// caller passes the same grace, so the first deadline is the earliest. The timer does not
        /// capture the caller's <see cref="ExecutionContext"/>. Arming does not throw (a timer
        /// disposed by a concurrent loop end is tolerated), and the timer's callback does not throw.
        /// </summary>
        /// <param name="after">The backstop delay; a legal timer due time (finite, at most <see cref="int.MaxValue"/> ms).</param>
        public void ArmBackstop(TimeSpan after)
        {
            if (_loopDone.Task.IsCompleted || Volatile.Read(ref _backstop) is not null)
                return;
            Timer timer;
            // The callback runs host cancellation callbacks: never under the arming caller's async
            // locals (SuppressFlow throws when the flow is already suppressed, hence the check).
            if (ExecutionContext.IsFlowSuppressed())
            {
                timer = new Timer(s_backstop, this, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                    timer = new Timer(s_backstop, this, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
            }
            if (Interlocked.CompareExchange(ref _backstop, timer, null) is not null)
            {
                // A concurrent arm won; its deadline is the same or earlier.
                timer.Dispose();
                return;
            }
            try
            {
                timer.Change(after, System.Threading.Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // The loop ended and MarkLoopDone disposed the timer between the publish and here.
            }
            // Published before this read, and MarkLoopDone completes the loop before it reads the
            // timer: at least one side sees the other, so a timer armed as the loop ends is disposed.
            if (_loopDone.Task.IsCompleted)
                timer.Dispose();
        }

        /// <summary>Dispose the socket (idempotent). Never throws.</summary>
        public void DisposeSocket()
        {
            try
            {
                Socket.Dispose();
            }
            catch
            {
                // A socket implementation that throws on dispose is closed as far as we are concerned.
            }
        }

        /// <summary>Mark the receive loop as ended and release the backstop timer, if one was armed.</summary>
        public void MarkLoopDone()
        {
            _loopDone.TrySetResult();
            Volatile.Read(ref _backstop)?.Dispose();
        }
    }

    /// <summary>
    /// A send failure that no reconnect can fix — the peer named a STOMP version we do not speak,
    /// or the connect-time SSRF guard refused every address the endpoint resolves to. Deliberately
    /// NOT a <see cref="TransportException"/>: the retry predicate handles that type, and this one
    /// must never be retried. The classified failure travels as
    /// <see cref="Exception.InnerException"/> and is what the caller finally sees;
    /// <see cref="Reason"/> is the one-clause explanation the wrapping message leads with.
    /// </summary>
    private sealed class FatalSendException : Exception
    {
        /// <summary>Wrap the classified, permanently fatal failure.</summary>
        /// <param name="cause">The failure to hand back to the caller.</param>
        /// <param name="reason">One clause naming why no reconnect can help.</param>
        public FatalSendException(TransportException cause, string reason) : base(cause.Message, cause)
            => Reason = reason;

        /// <summary>One clause naming why no reconnect can help.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// Marks the connect-time SSRF refusal at its throw site, inside the
    /// <c>SocketsHttpHandler.ConnectCallback</c>. The callback's failures reach the retry
    /// predicate wrapped in a retriable <see cref="WebSocketException"/>, so the marker is what
    /// lets <see cref="ConnectPinnedAsync"/> find the refusal in the wrap chain and reclassify it
    /// as <see cref="FatalSendException"/> (issue #72). Everything else the callback can throw —
    /// DNS and socket errors are genuinely transient — passes through unmarked.
    /// </summary>
    private sealed class GuardRefusalException : Exception
    {
        /// <summary>Wrap the guard's refusal.</summary>
        /// <param name="cause">The guard's classified refusal.</param>
        public GuardRefusalException(TransportException cause) : base(cause.Message, cause)
            => Cause = cause;

        /// <summary>The guard's classified refusal, preserved for the caller.</summary>
        public TransportException Cause { get; }
    }
}
