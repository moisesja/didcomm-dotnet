using System.Collections.Concurrent;
using System.Net.WebSockets;
using DidComm.Exceptions;
using DidComm.Transports;
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
/// </summary>
public sealed class WebSocketDidCommTransport : IDidCommTransport, IAsyncDisposable
{
    private readonly WebSocketTransportOptions _options;
    private readonly ILogger<WebSocketDidCommTransport> _logger;
    private readonly ResiliencePipeline _reconnectPipeline;
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _pool = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    /// <summary>Fires when the transport opens, closes, or fails to send (FR-TRN-11).</summary>
    public event EventHandler<WebSocketLifecycleEventArgs>? Lifecycle;

    /// <summary>Initialize the transport with bound options.</summary>
    /// <param name="options">Bound <see cref="WebSocketTransportOptions"/>.</param>
    /// <param name="logger">Optional logger; pass <see cref="NullLogger{T}.Instance"/> outside DI.</param>
    public WebSocketDidCommTransport(
        IOptions<WebSocketTransportOptions> options,
        ILogger<WebSocketDidCommTransport>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? NullLogger<WebSocketDidCommTransport>.Instance;
        _reconnectPipeline = BuildReconnectPipeline(_options);
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

        var key = PoolKey(request.Endpoint);

        await _reconnectPipeline.ExecuteAsync(async token =>
        {
            var socket = await GetOrConnectAsync(key, request.Endpoint, token).ConfigureAwait(false);
            try
            {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                sendCts.CancelAfter(_options.SendTimeout);
                // FR-TRN-09: one logical WebSocket message per packed envelope. We always send
                // the full buffer with EndOfMessage = true; receivers MUST loop until they see
                // EndOfMessage to handle fragmentation at the wire layer.
                await socket.SendAsync(request.Payload, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Drop the broken socket so the next attempt opens a fresh connection.
                _pool.TryRemove(key, out _);
                socket.Dispose();
                RaiseLifecycle(WebSocketLifecycleEventKind.SendFailed, request.Endpoint, ex);
                throw;
            }
        }, ct).ConfigureAwait(false);

        return new TransportResult(Accepted: true, HttpStatusCode: null);
    }

    private async Task<System.Net.WebSockets.WebSocket> GetOrConnectAsync(string key, Uri endpoint, CancellationToken ct)
    {
        if (_pool.TryGetValue(key, out var existing) && existing.State == WebSocketState.Open)
            return existing;

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
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
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            await connect(socket, endpoint, connectCts.Token).ConfigureAwait(false);
            _pool[key] = socket;
            RaiseLifecycle(WebSocketLifecycleEventKind.Connected, endpoint);
            return socket;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static Task DefaultConnect(System.Net.WebSockets.WebSocket socket, Uri endpoint, CancellationToken ct)
    {
        if (socket is ClientWebSocket cws)
            return cws.ConnectAsync(endpoint, ct);
        throw new InvalidOperationException(
            "Default Connect supports ClientWebSocket only. Provide WebSocketTransportOptions.Connect for custom socket types (used by tests against TestServer).");
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
        foreach (var (_, socket) in _pool)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "transport disposed", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocketDidCommTransport: ignoring close exception during dispose");
            }
            finally
            {
                socket.Dispose();
            }
        }
        _pool.Clear();
        _connectLock.Dispose();
    }
}
