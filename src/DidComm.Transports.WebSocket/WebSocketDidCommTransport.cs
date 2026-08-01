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
                System.Net.WebSockets.WebSocket socket;
                try
                {
                    socket = await GetOrConnectAsync(key, request.Endpoint, token).ConfigureAwait(false);
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
                        RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, request.Endpoint, ex);
                    }
                    RaiseLifecycle(WebSocketLifecycleEventKind.SendFailed, request.Endpoint, ex);
                    throw;
                }

                if (isReconnect)
                    RaiseLifecycle(WebSocketLifecycleEventKind.Reconnected, request.Endpoint);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation is not a transport failure — let it propagate as-is.
            throw;
        }
        catch (TransportException)
        {
            // Already classified (e.g. the scheme refusal above can't reach here, but a future
            // inner throw might) — don't double-wrap.
            throw;
        }
        catch (Exception ex)
        {
            // FR-TRN-11 / FR-API-07: surface an exhausted reconnect budget (or any other transport
            // failure) as TransportException so callers pattern-match the category without depending
            // on WebSocketException / TimeoutException specifics.
            throw new TransportException(
                $"WebSocket send to '{request.Endpoint}' failed after exhausting the reconnect budget ({_options.MaxReconnectAttempts} attempt(s)).",
                ex,
                httpStatusCode: null,
                scheme: request.Endpoint.Scheme);
        }

        return new TransportResult(Accepted: true, HttpStatusCode: null);
    }

    private async Task<System.Net.WebSockets.WebSocket> GetOrConnectAsync(string key, Uri endpoint, CancellationToken ct)
    {
        if (_pool.TryGetValue(key, out var existing) && existing.State == WebSocketState.Open)
            return existing;

        var gate = _connectLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pool.TryGetValue(key, out existing) && existing.State == WebSocketState.Open)
                return existing;

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
            RaiseLifecycle(WebSocketLifecycleEventKind.Connected, endpoint);
            return socket;
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

    private static ResiliencePipeline BuildReconnectPipeline(WebSocketTransportOptions options)
    {
        // FR-TRN-11 / DD-05: exponential backoff with jitter. MaxRetryAttempts == 0 disables
        // the retry strategy entirely (useful for tests + senders that want fail-fast).
        var builder = new ResiliencePipelineBuilder();
        if (options.MaxReconnectAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<WebSocketException>()
                    .Handle<TimeoutException>()
                    .Handle<TaskCanceledException>()
                    .Handle<InvalidOperationException>(),
                MaxRetryAttempts = options.MaxReconnectAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.ReconnectBaseDelay,
                MaxDelay = options.ReconnectMaxDelay,
                UseJitter = true,
            });
        }
        return builder.Build();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var (key, socket) in _pool)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    // FR-TRN-12: end the STOMP session politely before the WebSocket close. Best
                    // effort — no RECEIPT is requested (receipts are out of the minimal subset).
                    if (_options.UseStomp)
                    {
                        var disconnect = StompFrameCodec.Encode(new StompFrame(
                            "DISCONNECT", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty));
                        await socket.SendAsync(disconnect, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
                    }
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "transport disposed", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocketDidCommTransport: ignoring close exception during dispose");
            }
            finally
            {
                socket.Dispose();
                // The pool key is a well-formed absolute URI string (see PoolKey); reconstruct it so
                // observers see which endpoint just disconnected on a clean close.
                if (Uri.TryCreate(key, UriKind.Absolute, out var endpoint))
                    RaiseLifecycle(WebSocketLifecycleEventKind.Disconnected, endpoint);
            }
        }
        _pool.Clear();
        foreach (var (_, gate) in _connectLocks)
            gate.Dispose();
        _connectLocks.Clear();
        _pinnedInvoker?.Dispose();
    }
}
