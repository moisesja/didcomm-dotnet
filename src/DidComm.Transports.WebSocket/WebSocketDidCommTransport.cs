using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using DidComm.Exceptions;
using DidComm.Transports;
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

    /// <summary>Fires when the transport opens, closes, or fails to send (FR-TRN-11).</summary>
    public event EventHandler<WebSocketLifecycleEventArgs>? Lifecycle;

    /// <summary>Initialize the transport with bound options.</summary>
    /// <param name="options">Bound <see cref="WebSocketTransportOptions"/>.</param>
    /// <param name="logger">Optional logger; pass <see cref="NullLogger{T}.Instance"/> outside DI.</param>
    /// <param name="coreOptions">
    /// Optional core <see cref="DidComm.Facade.DidCommOptions"/>; its <c>OutboundEndpointPolicy</c> is
    /// inherited as the single source of truth when this transport's policy is left unset (#27).
    /// </param>
    public WebSocketDidCommTransport(
        IOptions<WebSocketTransportOptions> options,
        ILogger<WebSocketDidCommTransport>? logger = null,
        IOptions<DidComm.Facade.DidCommOptions>? coreOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? NullLogger<WebSocketDidCommTransport>.Instance;
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
                    var socket = await _guard.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    Stream stream = new NetworkStream(socket, ownsSocket: true);
                    return stream;
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
    public async Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Endpoint);
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

        try
        {
            await _reconnectPipeline.ExecuteAsync(async token =>
            {
                // The reconnect pipeline runs attempts sequentially, so a plain counter is safe.
                // attempt 0 is the first try; > 0 means this attempt is a recovery after a failure.
                var isReconnect = attempt++ > 0;
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
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation is not a transport failure — let it propagate as-is.
            throw;
        }
        catch (FatalHandshakeException ex)
        {
            // Permanently fatal (see FatalHandshakeException): retrying would burn the whole
            // reconnect budget — every attempt a full connect holding this endpoint's send gate,
            // head-of-line-blocking every other sender — on a peer that can never succeed. Say so
            // instead of claiming an exhausted budget, and hand back the classified cause.
            throw new TransportException(
                $"WebSocket send to '{request.Endpoint}' failed: the peer's STOMP handshake is permanently incompatible, so no reconnect was attempted.",
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
                ? cws.ConnectAsync(endpoint, _pinnedInvoker, ct)
                : cws.ConnectAsync(endpoint, ct);
        }
        throw new InvalidOperationException(
            "Default Connect supports ClientWebSocket only. Provide WebSocketTransportOptions.Connect for custom socket types (used by tests against TestServer).");
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
            // FatalHandshakeException keeps it out of the retry budget (see SendAsync).
            throw new FatalHandshakeException(new TransportException(
                $"STOMP handshake with '{endpoint}' failed: expected version 1.2, got '{version ?? "(absent)"}' (FR-TRN-12).",
                httpStatusCode: null,
                scheme: endpoint.Scheme));
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

    private static string PoolKey(Uri endpoint) =>
        $"{endpoint.Scheme.ToLowerInvariant()}://{endpoint.Authority}{endpoint.AbsolutePath}";

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
                // same reconnect budget. A permanently fatal handshake (the peer named a STOMP
                // version we do not speak) arrives as FatalHandshakeException, which is absent from
                // this list precisely so it is never retried. Invariant: the two non-retryable
                // TransportException sources — the scheme refusal and the SSRF guard in SendAsync —
                // are thrown BEFORE the pipeline executes, so a pre-pipeline refusal is never
                // retried here either.
                // The !_disposed guard stops all retrying once DisposeAsync has begun: a disposed
                // transport must never reconnect (and ObjectDisposedException derives from
                // InvalidOperationException, so without the guard a post-dispose failure would
                // burn the whole backoff budget before surfacing).
                ShouldHandle = args => new ValueTask<bool>(
                    !_disposed
                    && args.Outcome.Exception
                        is WebSocketException
                        or TimeoutException
                        or TaskCanceledException
                        or InvalidOperationException
                        or TransportException),
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
    /// <item>No send that has not yet acquired its endpoint's send gate will ever run — new sends
    /// fail fast at the <see cref="SendAsync"/> entry check, retries stop (the retry predicate
    /// tests the flag), and a send that wins a gate after this point re-checks it inside.</item>
    /// <item>Every send already past that check is drained — including one still inside its
    /// CONNECT phase, which holds its gate but has no pool entry yet. That is why the drain
    /// iterates the gates, not the pool, and reads the pool <i>inside</i> the gate: every pool
    /// write happens under the same gate.</item>
    /// <item>A send that outlives the drain budget is abandoned, not waited for: its socket is
    /// hard-disposed (or, if it has not been published yet, refused by the seal in
    /// <see cref="GetOrConnectAsync"/>), so no Open socket survives this call either way.</item>
    /// <item>The call is bounded: at most <see cref="DrainBudget"/> + <see cref="CloseBudget"/>
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
            // Every endpoint with a gate (a send is, or was, running) or a pooled socket.
            var keys = new HashSet<string>(_sendLocks.Keys, StringComparer.Ordinal);
            keys.UnionWith(_pool.Keys);
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
    /// Drain one endpoint's in-flight send and close its pooled socket. Never throws: a close is
    /// best effort, and one bad endpoint must not abort the teardown of the others.
    /// </summary>
    /// <param name="key">Pool key of the endpoint to close.</param>
    /// <returns>
    /// The endpoint to report as <c>Disconnected</c>, or <c>null</c> when there was no connection
    /// to close (a gate exists, but the endpoint has no pooled socket).
    /// </returns>
    private async Task<Uri?> CloseEndpointAsync(string key)
    {
        // TryGetValue, never GetOrAdd: dispose must not mint gates.
        _sendLocks.TryGetValue(key, out var sendGate);
        var gateHeld = false;
        System.Net.WebSockets.WebSocket? socket = null;
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
            _logger.LogDebug(ex, "WebSocketDidCommTransport: ignoring close exception during dispose");
        }
        finally
        {
            socket?.Dispose();
            if (gateHeld)
                sendGate!.Release();
        }
        // The pool key is a well-formed absolute URI string (see PoolKey); reconstruct it so
        // observers see which endpoint just disconnected on a clean close.
        return socket is not null && Uri.TryCreate(key, UriKind.Absolute, out var endpoint) ? endpoint : null;
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
    /// A handshake failure that no reconnect can fix. Deliberately NOT a
    /// <see cref="TransportException"/>: the retry predicate handles that type, and this one must
    /// never be retried. The classified failure travels as <see cref="Exception.InnerException"/>
    /// and is what the caller finally sees.
    /// </summary>
    private sealed class FatalHandshakeException : Exception
    {
        /// <summary>Wrap the classified, permanently fatal failure.</summary>
        /// <param name="cause">The failure to hand back to the caller.</param>
        public FatalHandshakeException(TransportException cause) : base(cause.Message, cause) { }
    }
}
