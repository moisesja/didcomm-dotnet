using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
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
/// packed DIDComm envelope as exactly one WebSocket binary message (FR-TRN-09); the receiver
/// MUST reassemble fragmented frames before processing. Connections are pooled by endpoint and
/// reconnect with exponential backoff (1s / 30s / 0.5 jitter — DD-05) on send failures.
/// Optionally wraps each envelope in minimal STOMP 1.2 framing when
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
/// <see cref="WebSocketTransportOptions.SendTimeout"/>).
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
/// The loop never sends anything but close frames. Pooled sockets are never read. Under
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
public sealed class WebSocketDidCommTransport : IDidCommTransport, IAsyncDisposable
{
    private readonly WebSocketTransportOptions _options;
    private readonly ILogger<WebSocketDidCommTransport> _logger;
    private readonly ResiliencePipeline _reconnectPipeline;
    private readonly OutboundEndpointGuard _guard;
    // Non-null on the default connect path: pins every ClientWebSocket connection to a guard-vetted
    // IP via SocketsHttpHandler.ConnectCallback (see constructor). Null when a custom Connect
    // delegate owns its own vetting.
    private readonly HttpMessageInvoker? _pinnedInvoker;
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
    // The ACTIVE session socket of each session key (PoolKey + "\n" + L + "\n" + P, see SendAsync).
    // Written only under that key's send gate; removed conditionally (by instance) by the socket's
    // own receive loop and by dispose. A key lives in exactly one of _pool and _sessions: a session
    // key contains line feeds, which neither a pool key nor a validated DID can.
    private readonly ConcurrentDictionary<string, SessionSocket> _sessions = new(StringComparer.Ordinal);
    // Every session socket whose receive loop has not finished: active ones and retiring ones (no
    // longer in _sessions, still reading the frames already sent). Dispose sweeps and awaits these.
    private readonly ConcurrentDictionary<SessionSocket, byte> _liveSessionSockets = new();
    // One-time notices (0 = not yet logged).
    private int _stompSessionNotice;
    private int _discardNotice;
    // Rate-limiter counters for the flood-shaped session log lines (first and every 1000th at Warning).
    private long _consumerFaults;
    private long _oversizedPushes;
    // Whole pushed messages read and dropped without reaching the consumer (ReceiveInbound off, or
    // the socket is discarding after a 1009). Diagnostics for tests only; never logged per message.
    private long _inboundMessagesDropped;
    private const long WarnEvery = 1000;
    // FR-API-09: the longest LocalDid / PeerDid a session key accepts.
    private const int MaxSessionDidLength = 2048;

    /// <summary>Fires when the transport opens, closes, or fails to send (FR-TRN-11).</summary>
    /// <remarks>
    /// Handlers run synchronously on transport code paths — including from inside
    /// <see cref="DisposeAsync"/> itself (the <c>Disconnected</c> events) and from the receive loop of a
    /// return-route session socket (its <c>Disconnected</c>, raised once per socket and naming the
    /// endpoint it was opened to). A handler must
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
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
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
        // owns its own vetting, so the invoker is built only for the default path.
        _pinnedInvoker = _options.Connect is null
            ? new HttpMessageInvoker(new SocketsHttpHandler
            {
                ConnectCallback = async (context, ct) =>
                {
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
                },
            })
            : null;
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
    /// How long the polite shutdown (STOMP DISCONNECT + close frame) may take. One per-send budget:
    /// these are ordinary socket writes, and the peer is under no obligation to read them.
    /// </summary>
    private TimeSpan CloseBudget => AsWaitBudget(_options.SendTimeout);

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

    /// <summary>Whole pushed messages read and dropped without reaching the consumer (diagnostics for tests).</summary>
    internal long InboundMessagesDropped => Interlocked.Read(ref _inboundMessagesDropped);

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
    /// When <see cref="TransportRequest.LocalDid"/> or <see cref="TransportRequest.PeerDid"/> is set
    /// but is not a DID (<c>did:</c> prefix) of at most 2048 characters without whitespace or control
    /// characters. The value is not repeated in the message.
    /// </exception>
    public async Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Endpoint);
        // The session key is built from these values (JC-21), so they are shape-checked before any
        // state is touched: a line feed in either would let two identities share a key.
        ValidateSessionDid(request.LocalDid, nameof(TransportRequest.LocalDid));
        ValidateSessionDid(request.PeerDid, nameof(TransportRequest.PeerDid));
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

        // SSRF defense, layer 1: reject obvious private / loopback / metadata hosts up front with a
        // clear error. The authoritative defense is the connect-time IP pinning in _pinnedInvoker
        // (see constructor), which additionally covers DNS rebinding and ResolveDnsNames = false. A
        // custom Connect delegate (e.g. tests against an in-process TestServer) owns its own vetting,
        // so both layers are skipped there.
        if (_options.Connect is null)
            _guard.Validate(request.Endpoint);

        var key = PoolKey(request.Endpoint);
        var attempt = 0;

        // FR-TRN-12 (opt-in): wrap the packed envelope in one STOMP SEND frame — destination +
        // content-type (the request's DIDComm media type, application/didcomm-encrypted+json on
        // the SendAsync path) + codec-computed content-length, one packed message per SEND body.
        // Built once; identical on every reconnect attempt. Default OFF: the payload bytes go on
        // the wire untouched, exactly as before.
        var payload = _options.UseStomp ? BuildStompSendPayload(request) : request.Payload;

        // FR-RR-07 (JC-10, JC-21): a request that names both identities may use the return-route
        // session socket S(E, L, P). `opens`: this send asks for a return route and opens S when there
        // is none; `retires`: an explicit `none` ends S after it was sent over it. Under STOMP there
        // are no session sockets (a spec-shaped push needs broker semantics outside the minimal subset).
        var opens = request.ReturnRoute is ReturnRouteMode.All or ReturnRouteMode.Thread;
        var retires = request.ReturnRoute == ReturnRouteMode.None;
        string? sessionKey = null;
        if (request.LocalDid is not null && request.PeerDid is not null)
        {
            if (!_options.UseStomp)
                sessionKey = key + "\n" + request.LocalDid + "\n" + request.PeerDid;
            else if (opens && Interlocked.Exchange(ref _stompSessionNotice, 1) == 0)
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
                // FR-TRN-09: one logical WebSocket message per packed envelope. We always send
                // the full buffer with EndOfMessage = true; receivers MUST loop until they see
                // EndOfMessage to handle fragmentation at the wire layer.
                await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token).ConfigureAwait(false);
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
                    else
                    {
                        // Closed under us (the peer's close frame, e.g. a receiver eviction, not yet
                        // handled by the loop; or a dead connection): never send on it again.
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
                // FR-TRN-09: one logical WebSocket message per packed envelope.
                await session.Socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // E9: a failed send drops S, so the retry re-routes (all/thread: a fresh S; header-less
                // or none: the pooled socket). Disconnected is raised once per socket: by whoever
                // ends it first, this path or its receive loop.
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
    /// (FR-RR-05), still under its send gate: stop routing to it, send the close frame (bounded), and
    /// arm the backstop that ends its loop if the peer never answers. The loop keeps surfacing the
    /// messages already on their way until the peer's close frame. Never throws.
    /// </summary>
    /// <param name="sessionKey">The session key.</param>
    /// <param name="session">The session socket the <c>none</c> was sent over.</param>
    private async Task RetireAsync(string sessionKey, SessionSocket session)
    {
        _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(sessionKey, session));
        try
        {
            using var closeCts = new CancellationTokenSource(CloseBudget);
            await session.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "return-route session ended", closeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "WebSocketDidCommTransport: closing a retired return-route session socket failed ({ExceptionType}).", ex.GetType().Name);
        }
        session.ArmBackstop(CloseBudget);
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
    /// close the socket with 1009 when one exceeds the receive cap (JC-11), and hand each whole
    /// message to the inbound consumer, one at a time (JC-8), when <c>ReceiveInbound</c> is on. Never
    /// sends anything but close frames, never throws (L-043), and always ends through
    /// <see cref="EndReceiveLoopAsync"/>.
    /// </summary>
    /// <param name="session">The session socket to read.</param>
    private async Task ReceiveLoopAsync(SessionSocket session)
    {
        byte[]? buffer = null;
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
                long total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await session.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    total += result.Count;
                    if (total > _inboundCap && !session.DiscardInbound)
                    {
                        // JC-11: stop buffering at once (nothing past the cap is kept) and close with 1009.
                        surface = false;
                        message.SetLength(0);
                        message.Capacity = 0;
                        await CloseOversizedAsync(session).ConfigureAwait(false);
                    }
                    if (surface)
                        message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

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
                    await _consumer!.ConsumeAsync(new InboundFrame(session.Endpoint, session.LocalDid, session.PeerDid, payload), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
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
            // The socket ended: a fault, a dispose, a retire backstop or a send-failure stop.
            Log(LogLevel.Debug, "WebSocketDidCommTransport: a return-route session socket stopped reading ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            await EndReceiveLoopAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// JC-11: a pushed message exceeded the receive cap. Stop routing to <paramref name="session"/>,
    /// switch it to discarding, send a 1009 close frame under its send gate (so the close never
    /// overlaps a send), arm the backstop and log once (rate-limited). Never throws.
    /// </summary>
    /// <param name="session">The session socket that received the oversized message.</param>
    private async Task CloseOversizedAsync(SessionSocket session)
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
                if (held && !_disposed && session.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(CloseBudget);
                    await session.Socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "payload too large", closeCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "WebSocketDidCommTransport: closing an oversized return-route session socket failed ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            if (held)
                gate!.Release();
        }
        session.ArmBackstop(CloseBudget);
        LogRateLimited(ref _oversizedPushes,
            "WebSocketDidCommTransport: a message pushed on a return-route session socket exceeded {MaxBytes} bytes; the socket was closed with 1009 and the rest of its messages are discarded (FR-RR-07). {Count} such messages.",
            "WebSocketDidCommTransport: a message pushed on a return-route session socket exceeded {MaxBytes} bytes; the socket was closed with 1009 and the rest of its messages are discarded (FR-RR-07).",
            _inboundCap);
    }

    /// <summary>
    /// The end of a receive loop (E8): stop routing to the socket, answer a close the peer started
    /// (under the send gate, bounded; skipped once dispose began), dispose the socket, raise
    /// <c>Disconnected</c> when this is the first end of the socket, and mark the loop done. Never throws.
    /// </summary>
    /// <param name="session">The session socket whose loop ended.</param>
    private async Task EndReceiveLoopAsync(SessionSocket session)
    {
        try
        {
            _sessions.TryRemove(new KeyValuePair<string, SessionSocket>(session.Key, session));
            // The peer started the close (a receiver eviction, FR-RR-03; or its answer to nothing we
            // sent): complete the handshake. Under the gate, so the close never overlaps a send; no
            // gate holder ever waits for a loop, so this cannot deadlock.
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
                        using var closeCts = new CancellationTokenSource(CloseBudget);
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
            try
            {
                session.DisposeSocket();
                if (session.TryEnd())
                    RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, session.Endpoint);
            }
            catch
            {
                // RaiseLifecycle swallows handler faults, but its own logging is host code too.
            }
            finally
            {
                // Last: dispose awaits LoopDone for every socket still in _liveSessionSockets.
                _liveSessionSockets.TryRemove(session, out _);
                session.MarkLoopDone();
            }
        }
    }

    /// <summary>
    /// Refuse a <see cref="TransportRequest.LocalDid"/> / <see cref="TransportRequest.PeerDid"/> that
    /// cannot be part of a session key: not a DID, longer than 2048 characters (FR-API-09), or holding
    /// whitespace or a control character (the key's separator is a line feed). The value is not echoed.
    /// </summary>
    /// <param name="value">The value; <c>null</c> passes.</param>
    /// <param name="name">The property name, for the message.</param>
    private static void ValidateSessionDid(string? value, string name)
    {
        if (value is null)
            return;
        var ok = value.Length > 4
                 && value.Length <= MaxSessionDidLength
                 && value.StartsWith("did:", StringComparison.Ordinal);
        if (ok)
        {
            foreach (var c in value)
            {
                if (c <= ' ')
                {
                    ok = false;
                    break;
                }
            }
        }
        if (!ok)
        {
            throw new ArgumentException(
                $"TransportRequest.{name} must be a DID ('did:' prefix) of at most {MaxSessionDidLength} characters without whitespace or control characters.",
                "request");
        }
    }

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
            // can only reach a guard-vetted IP. _pinnedInvoker is non-null whenever this default path
            // is in use (no custom Connect delegate overriding it).
            return _pinnedInvoker is not null
                ? ConnectPinnedAsync(cws, endpoint, ct)
                : cws.ConnectAsync(endpoint, ct);
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
            await socket.ConnectAsync(endpoint, _pinnedInvoker!, ct).ConfigureAwait(false);
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
                // observability defect can't cascade into a delivery failure.
                _logger.LogWarning(ex, "WebSocketDidCommTransport: lifecycle handler threw {Kind}", kind);
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
    /// <item>Every session socket's receive loop is cancelled and awaited for at most
    /// <see cref="CloseBudget"/>. A loop held by an inbound consumer that ignores cancellation is
    /// abandoned; its socket is already disposed, and the loop raises nothing more when it ends.
    /// A loop never waits on a send gate once this call has begun.</item>
    /// <item><c>Disconnected</c> is raised once per socket that was connected, naming its endpoint.</item>
    /// <item>The call is bounded: at most <see cref="DrainBudget"/> + 2 × <see cref="CloseBudget"/>
    /// in total, no matter how many endpoints are wedged (they drain concurrently) or how
    /// unresponsive the peers are (every network write here is cancellation-bounded).</item>
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

        try
        {
            // Every endpoint with a gate (a send is, or was, running), a pooled socket or an active
            // session socket (session keys share the gate dictionary, JC-21).
            var keys = new HashSet<string>(_sendLocks.Keys, StringComparer.Ordinal);
            keys.UnionWith(_pool.Keys);
            keys.UnionWith(_sessions.Keys);
            // Concurrently: the drain was serial, so N unresponsive endpoints cost N × budget.
            var disconnected = await Task.WhenAll(keys.Select(CloseEndpointAsync)).ConfigureAwait(false);
            // Raised here, not inside the per-endpoint tasks, so handlers are never invoked
            // concurrently (or under a send gate) — the guarantee the serial loop gave for free.
            foreach (var endpoint in disconnected)
            {
                if (endpoint is not null)
                    RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, endpoint);
            }

            // Seal BEFORE the sweep: a send whose drain budget expired publishes its socket and
            // then reads this flag, so ordering the seal first makes it impossible for both the
            // sweep and the late writer to miss the entry (see GetOrConnectAsync).
            _poolSealed = true;
            foreach (var key in _pool.Keys)
            {
                if (_pool.TryRemove(key, out var leftover))
                    leftover.Dispose();
            }

            // Session sockets: an active one the drain could not reach, and every retiring one (no
            // longer in _sessions, still reading). Each is stopped and disposed here — after the seal,
            // like the pool — and its loop is awaited, bounded: a loop never throws, and one held by a
            // consumer that ignores cancellation is abandoned with its socket already disposed.
            var ended = new List<Uri>();
            foreach (var key in _sessions.Keys)
            {
                if (_sessions.TryRemove(key, out var active))
                    StopSessionForDispose(active, ended);
            }
            var loops = new List<Task>();
            foreach (var live in _liveSessionSockets.Keys)
            {
                StopSessionForDispose(live, ended);
                loops.Add(live.LoopDone);
            }
            foreach (var endpoint in ended)
                RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, endpoint);
            if (loops.Count > 0)
            {
                try
                {
                    await Task.WhenAll(loops).WaitAsync(CloseBudget).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Log(LogLevel.Debug, "WebSocketDidCommTransport: {Count} return-route session receive loop(s) did not end within the close budget; abandoned.", loops.Count(t => !t.IsCompleted));
                }
            }

            DisposeIdleGates(_connectLocks);
            DisposeIdleGates(_sendLocks);
            _pinnedInvoker?.Dispose();
        }
        finally
        {
            _disposeCompleted.TrySetResult();
        }
    }

    /// <summary>
    /// Drain one endpoint's (or session's) in-flight send and close its pooled (or active session)
    /// socket. Never throws: a close is best effort, and one bad endpoint must not abort the
    /// teardown of the others.
    /// </summary>
    /// <param name="key">Pool key of the endpoint, or session key of a session socket, to close.</param>
    /// <returns>
    /// The endpoint to report as <c>Disconnected</c>, or <c>null</c> when there was no connection
    /// to close (a gate exists, but no pooled or active session socket), or when the session socket
    /// already reported its end.
    /// </returns>
    private async Task<Uri?> CloseEndpointAsync(string key)
    {
        // TryGetValue, never GetOrAdd: dispose must not mint gates.
        _sendLocks.TryGetValue(key, out var sendGate);
        var gateHeld = false;
        System.Net.WebSockets.WebSocket? socket = null;
        SessionSocket? session = null;
        try
        {
            // Bounded by the worst case for a HEALTHY hold — the gate spans connect + STOMP
            // handshake + send — so an in-flight send always finishes inside it. On expiry the
            // polite shutdown is skipped and the hard Dispose below unblocks the stuck send with a
            // socket-level error instead. Inside the try: the wait is part of the guarded region,
            // so nothing it could throw can strand the rest of this endpoint's cleanup.
            gateHeld = sendGate is not null && await sendGate.WaitAsync(DrainBudget).ConfigureAwait(false);
            // Read the pool INSIDE the gate: a send that was still connecting when dispose began
            // has published its socket by now, and every pool write happens under this same gate.
            _pool.TryRemove(key, out socket);
            // The same for the session sockets. A key names a pooled socket or a session socket,
            // never both, so at most one of these is non-null.
            _sessions.TryRemove(key, out session);
            if (gateHeld && session is not null && session.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(CloseBudget);
                await session.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "transport disposed", closeCts.Token).ConfigureAwait(false);
            }
            if (gateHeld && socket is not null && socket.State == WebSocketState.Open)
            {
                // Both shutdown writes are cancellation-bounded: a peer that stopped reading (TCP
                // backpressure on the DISCONNECT) or never answers the close would otherwise hang
                // dispose forever. On expiry we fall through to the hard Dispose below.
                using var closeCts = new CancellationTokenSource(CloseBudget);
                // FR-TRN-12: end the STOMP session politely before the WebSocket close. Best
                // effort — no RECEIPT is requested (receipts are out of the minimal subset).
                if (_options.UseStomp)
                {
                    var disconnect = StompFrameCodec.Encode(new StompFrame(
                        "DISCONNECT", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty));
                    await socket.SendAsync(disconnect, WebSocketMessageType.Binary, endOfMessage: true, closeCts.Token).ConfigureAwait(false);
                }
                // CloseOutputAsync, not CloseAsync: CloseAsync additionally waits for the peer's
                // close frame, which a hostile or wedged peer simply never sends. We send our close
                // frame and drop the connection; the peer sees a clean NormalClosure either way.
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "transport disposed", closeCts.Token).ConfigureAwait(false);
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
            socket?.Dispose();
            session?.DisposeSocket();
            if (gateHeld)
                sendGate!.Release();
            // After the release: stopping the loop runs the consumer's cancellation callbacks, which
            // never run under a send gate.
            session?.StopLoop();
        }
        if (session is not null)
            return session.TryEnd() ? session.Endpoint : null;
        // The pool key is a well-formed absolute URI string (see PoolKey); reconstruct it so
        // observers see which endpoint just disconnected on a clean close.
        return socket is not null && Uri.TryCreate(key, UriKind.Absolute, out var endpoint) ? endpoint : null;
    }

    /// <summary>
    /// Dispose-time teardown of one session socket: stop its loop, dispose it, and collect its
    /// endpoint for <c>Disconnected</c> when this is the socket's first end.
    /// </summary>
    /// <param name="session">The session socket.</param>
    /// <param name="ended">Collects the endpoints to report.</param>
    private static void StopSessionForDispose(SessionSocket session, List<Uri> ended)
    {
        session.StopLoop();
        session.DisposeSocket();
        if (session.TryEnd())
            ended.Add(session.Endpoint);
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
    /// The loop's <see cref="CancellationTokenSource"/> is never disposed: several parties may cancel
    /// it (a failed send, a retire backstop, dispose) at any time, and
    /// <see cref="CancellationTokenSource.Dispose()"/> must not race a cancel. It holds no unmanaged
    /// state; an armed backstop timer releases itself when it fires.
    /// </remarks>
    private sealed class SessionSocket
    {
        private readonly CancellationTokenSource _loop = new();
        private readonly TaskCompletionSource _loopDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ended;
        private volatile bool _discardInbound;

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

        /// <summary>Cancels the receive loop, and is the token the inbound consumer gets.</summary>
        public CancellationToken LoopToken { get; }

        /// <summary>Completes when the receive loop has fully ended (or never started).</summary>
        public Task LoopDone => _loopDone.Task;

        /// <summary>Set once an oversized message closed the socket with 1009: every later message is dropped.</summary>
        public bool DiscardInbound
        {
            get => _discardInbound;
            set => _discardInbound = value;
        }

        /// <summary>Whether this call is the socket's first end — the one that raises <c>Disconnected</c>.</summary>
        public bool TryEnd() => Interlocked.Exchange(ref _ended, 1) == 0;

        /// <summary>Cancel the receive loop now. Never throws.</summary>
        public void StopLoop()
        {
            try
            {
                _loop.Cancel();
            }
            catch
            {
                // A consumer's cancellation callback threw; the token is cancelled regardless.
            }
        }

        /// <summary>Cancel the receive loop after <paramref name="after"/> unless it ended first. Never throws.</summary>
        /// <param name="after">The backstop delay.</param>
        public void ArmBackstop(TimeSpan after)
        {
            try
            {
                _loop.CancelAfter(after);
            }
            catch
            {
                // Already cancelled: nothing to arm.
            }
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

        /// <summary>Mark the receive loop as ended.</summary>
        public void MarkLoopDone() => _loopDone.TrySetResult();
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
