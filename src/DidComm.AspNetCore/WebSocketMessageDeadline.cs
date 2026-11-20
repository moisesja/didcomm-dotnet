using System.Buffers;
using System.Net.WebSockets;
using DidComm.Protocols.Routing;
using DidComm.Transports;
using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// The receive time limit of one accepted WebSocket connection
/// (<see cref="DidCommReceiveOptions.MessageReceiveTimeout"/>), shared by both receive loops of
/// <c>MapDidCommWebSocket</c>: the first message is timed from the accept, every later one from its
/// first part to its last frame, and the wait for a later message's first part is never timed, so an
/// idle session stays open. One timer per message, never per read.
/// </summary>
internal sealed class WebSocketMessageDeadline : IDisposable
{
    /// <summary>The close status sent when the time runs out: 1008, policy violation (RFC 6455 §7.4.1).</summary>
    internal const WebSocketCloseStatus CloseStatus = WebSocketCloseStatus.PolicyViolation;

    /// <summary>The close reason sent with <see cref="CloseStatus"/>.</summary>
    internal const string CloseDescription = "message receive timeout";

    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly MessageReceiveTimeoutLog _log;
    private ReceiveDeadline? _running;

    private WebSocketMessageDeadline(TimeProvider time, TimeSpan timeout, MessageReceiveTimeoutLog log)
    {
        _time = time;
        _timeout = timeout;
        _log = log;
        _running = ReceiveDeadline.Start(time, timeout);
    }

    /// <summary>
    /// Start the time of a connection's first message, at the moment the connection was accepted: a
    /// connection that stays silent is closed too. A clock that cannot create the timer throws, and the
    /// connection then ends rather than running without a limit.
    /// </summary>
    /// <param name="time">The container's clock.</param>
    /// <param name="timeout">The validated <see cref="DidCommReceiveOptions.MessageReceiveTimeout"/>.</param>
    /// <param name="log">The mapped endpoint's expiry line.</param>
    internal static WebSocketMessageDeadline StartAtAccept(TimeProvider time, TimeSpan timeout, MessageReceiveTimeoutLog log)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        return new WebSocketMessageDeadline(time, timeout, log);
    }

    /// <summary>
    /// Wait until <paramref name="receive"/> completes or the running message's time is up. Returns
    /// <c>true</c> when the receive completed (it also wins when both are done), <c>false</c> when the
    /// time ran out with the receive still outstanding. Between messages nothing is timed and it
    /// returns <c>true</c> at once: the caller awaits the receive itself.
    /// </summary>
    /// <param name="receive">The outstanding receive.</param>
    internal ValueTask<bool> WaitAsync(Task receive)
        => _running is null ? new ValueTask<bool>(true) : ReceiveDeadline.WaitAsync(receive, _running);

    /// <summary>
    /// A part of a message other than its last was read. When no time is running this is the
    /// message's first part, and its time starts now; otherwise returns <c>false</c> when that time is
    /// up (a part that arrived after it ran out does not save the message).
    /// </summary>
    internal bool PartReceived()
    {
        if (_running is null)
        {
            _running = ReceiveDeadline.Start(_time, _timeout);
            return true;
        }
        return !_running.HasExpired;
    }

    /// <summary>The message's last frame was read: its time stops, and the wait for the next message is untimed.</summary>
    internal void MessageReceived()
    {
        var running = _running;
        _running = null;
        running?.Dispose();
    }

    /// <summary>Write the mapped endpoint's (rate-limited) expiry line.</summary>
    /// <param name="logger">Optional logger.</param>
    internal void LogExpired(ILogger? logger) => _log.Expired(logger, _timeout);

    /// <summary>Stop the running timer, if any (idempotent).</summary>
    public void Dispose() => MessageReceived();
}

/// <summary>
/// The receive-timeout line of one mapped WebSocket endpoint. Any peer can let a message run out of
/// time, so the line is rate-limited: the first and every 1000th at Information, the others at Debug.
/// It carries no wire value.
/// </summary>
internal sealed class MessageReceiveTimeoutLog
{
    private long _expired;

    /// <summary>Count one expiry and write its line.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeout">The limit that ran out.</param>
    internal void Expired(ILogger? logger, TimeSpan timeout)
    {
        var level = RateLimitedLog.Level(ref _expired, LogLevel.Information, out var count);
        SafeLog.Write(logger, level,
            "MapDidCommWebSocket: a message was not received in full within the receive timeout ({TimeoutSeconds} s); closing the connection with 1008 (policy violation); {Count} so far on this endpoint.",
            Math.Round(timeout.TotalSeconds, 3), count);
    }
}

/// <summary>The last step of both WebSocket receive loops: the close handshake, and the receive buffer.</summary>
internal static class WebSocketClosing
{
    /// <summary>
    /// End a connection whose receive loop has stopped. Send the close frame the loop asked for
    /// (<paramref name="status"/>; none when <c>null</c>) if the socket can still send one, then read
    /// and drop whatever the peer still sends until its own close frame arrives — all within
    /// <paramref name="grace"/>. A peer that does not finish the handshake in time, or a socket that
    /// fails, is aborted. Finally <paramref name="buffer"/> goes back to <paramref name="pool"/>, but
    /// only when no receive into it can still be running; otherwise it is left to the garbage
    /// collector. Never throws.
    /// </summary>
    /// <remarks>
    /// It never calls <see cref="WebSocket.CloseAsync"/>, which starts a receive of its own: the loop
    /// may have left one outstanding (<paramref name="outstanding"/>, after its time ran out), and that
    /// one is awaited here first. <see cref="WebSocket.CloseOutputAsync"/> only sends, so it may run
    /// while that receive is outstanding.
    /// </remarks>
    /// <param name="socket">The connection.</param>
    /// <param name="status">The close status to send, or <c>null</c> to send none.</param>
    /// <param name="description">The close reason sent with <paramref name="status"/>.</param>
    /// <param name="outstanding">A receive into <paramref name="buffer"/> the loop left running, or <c>null</c>.</param>
    /// <param name="buffer">The loop's receive buffer, rented from <paramref name="pool"/>.</param>
    /// <param name="pool">The pool <paramref name="buffer"/> came from.</param>
    /// <param name="grace">How long the handshake may take, in real time.</param>
    /// <param name="aborted">The request's abort token: the handshake ends (and the socket is aborted) when it fires.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>Whether <paramref name="buffer"/> went back to <paramref name="pool"/>.</returns>
    internal static async Task<bool> EndAsync(
        WebSocket socket,
        WebSocketCloseStatus? status,
        string? description,
        Task<WebSocketReceiveResult>? outstanding,
        byte[] buffer,
        ArrayPool<byte> pool,
        TimeSpan grace,
        CancellationToken aborted,
        ILogger? logger)
    {
        var receive = outstanding;
        if (status is not null || receive is not null)
        {
            using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            graceCts.CancelAfter(grace);
            try
            {
                if (status is { } closeStatus && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(closeStatus, description, graceCts.Token).ConfigureAwait(false);

                // Waiting for the peer's close frame: what it sent before it saw ours is read into the
                // same buffer and dropped. The receives carry no token (a cancelled receive aborts the
                // socket); the grace bounds the wait instead.
                while (receive is not null || socket.State == WebSocketState.CloseSent)
                {
                    receive ??= socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    var result = await receive.WaitAsync(graceCts.Token).ConfigureAwait(false);
                    receive = null;
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch (Exception ex)
            {
                // The grace ran out, the request was aborted, or the socket failed: nothing more to
                // read or write. Type name only.
                SafeLog.Write(logger, LogLevel.Debug, "MapDidCommWebSocket: close failed with {ExceptionType}; aborting the connection.", ex.GetType().Name);
                socket.Abort();
            }
        }

        if (receive is not null)
        {
            ObserveAbandoned(receive);
            if (!receive.IsCompleted)
                return false; // still writing into the buffer: never give it back to the pool
        }
        pool.Return(buffer);
        return true;
    }

    /// <summary>Observe a receive nobody awaits any more, so its fault is never an unobserved task exception.</summary>
    private static void ObserveAbandoned(Task receive)
        => receive.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
