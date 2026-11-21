using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// #100 OD-8: the cancellation token under which the registry-aware WebSocket receive loop handles one
/// message it has received in full: its STOMP frame, its unpack, admission, dispatch and reply. While
/// the connection lives nothing cancels it, so a handler has no time limit of its own (as before).
/// When the connection ends with the message in hand, the message is still handled: from that moment
/// its handling ends when the bound runs out (the loop passes the close grace,
/// <see cref="WebSocketReturnRouteSession.DefaultCloseGrace"/>, 5 s), timed on the container's clock,
/// or when the host starts to stop, whichever comes first.
/// </summary>
/// <remarks>
/// <para>
/// The connection ends when the loop's connection token is cancelled: the peer dropped the connection
/// (the request's abort), or the connection's return-route session ended it (its writer found the
/// socket broken, or an evicted peer did not answer the close frame within the close grace, JC-21).
/// The message was received whole before any of these, and none of them is the peer taking it back.
/// </para>
/// <para>
/// The loop reads with the connection token and stops once it is cancelled, so after the end it reads
/// nothing more: a connection has at most one such finish, and it lasts at most the bound.
/// </para>
/// <para>
/// Nothing is timed while the connection lives: the bound's timer is created, and the host's stopping
/// token watched, only once the connection ended with this message in hand (at once when it had
/// already ended when the message was complete). A clock that cannot create the timer ends the
/// handling at once, as before OD-8, rather than leaving it unbounded. The cancellation is requested
/// with <see cref="CancellationTokenSource.CancelAsync"/>, so the callbacks handler code registered on
/// the token run on the thread pool, never on the timer's thread or the host's stopping thread, and a
/// fault in one is observed and logged by type. The lines carry no wire value (D7).
/// </para>
/// </remarks>
internal sealed class WebSocketMessageHandling : IDisposable
{
    private const int Running = 0;
    private const int Ended = 1;
    private const int Done = 2;

    private readonly CancellationTokenSource _source = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _bound;
    private readonly CancellationToken _stopping;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private CancellationTokenRegistration _connectionEnded;
    private CancellationTokenRegistration _hostStopping; // under _gate
    private ITimer? _timer; // under _gate
    private int _state = Running;

    private WebSocketMessageHandling(TimeProvider time, TimeSpan bound, CancellationToken stopping, ILogger? logger)
    {
        _time = time;
        _bound = bound;
        _stopping = stopping;
        _logger = logger;
        Token = _source.Token;
    }

    /// <summary>The token the message is handled under: cancelled only after the connection ended, by the bound or the host's shutdown.</summary>
    internal CancellationToken Token { get; }

    /// <summary>
    /// Start the handling of a message the loop has just received in full. Call it right after the
    /// message's last frame was read, and dispose it once everything done for the message is over.
    /// </summary>
    /// <param name="connection">The loop's connection token; its cancellation starts the bound.</param>
    /// <param name="time">The container's clock, which times the bound.</param>
    /// <param name="bound">How long the handling may go on once the connection ended.</param>
    /// <param name="stopping">The host's stopping token (<c>IHostApplicationLifetime.ApplicationStopping</c>), or none.</param>
    /// <param name="logger">Optional logger.</param>
    internal static WebSocketMessageHandling Start(CancellationToken connection, TimeProvider time, TimeSpan bound, CancellationToken stopping, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        var handling = new WebSocketMessageHandling(time, bound, stopping, logger);
        // Runs at once, on this thread, when the connection had already ended.
        handling._connectionEnded = connection.UnsafeRegister(static state => ((WebSocketMessageHandling)state!).OnConnectionEnded(), handling);
        return handling;
    }

    /// <summary>The handling is over: no end is requested or reported from now on, and the timer and registrations are released.</summary>
    public void Dispose()
    {
        Interlocked.CompareExchange(ref _state, Done, Running);
        // Waits for OnConnectionEnded when it is running on another thread; from here it never runs.
        _connectionEnded.Dispose();
        ITimer? timer;
        CancellationTokenRegistration stopping;
        lock (_gate)
        {
            timer = _timer;
            stopping = _hostStopping;
        }
        stopping.Dispose();
        // A firing already under way may still reach End, which then finds the handling done.
        timer?.Dispose();
        _source.Dispose();
    }

    private void OnConnectionEnded()
    {
        ITimer timer;
        try
        {
            timer = _time.CreateTimer(static state => ((WebSocketMessageHandling)state!).End(Reason.Bound, null), this, _bound, Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            End(Reason.ClockFault, ex.GetType().Name);
            return;
        }
        // Runs End at once, on this thread, when the host is already stopping.
        var stopping = _stopping.UnsafeRegister(static state => ((WebSocketMessageHandling)state!).End(Reason.HostStopping, null), this);
        lock (_gate)
        {
            _timer = timer;
            _hostStopping = stopping;
        }
    }

    private void End(Reason reason, string? exceptionType)
    {
        if (Interlocked.CompareExchange(ref _state, Ended, Running) != Running)
            return;
        switch (reason)
        {
            case Reason.Bound:
                SafeLog.Write(_logger, LogLevel.Debug,
                    "MapDidCommWebSocket: the connection ended while a message it had received in full was being handled, and the handling did not finish within {GraceSeconds} s; it is cancelled.",
                    _bound.TotalSeconds);
                break;
            case Reason.HostStopping:
                SafeLog.Write(_logger, LogLevel.Debug,
                    "MapDidCommWebSocket: the connection ended while a message it had received in full was being handled, and the host is stopping; the handling is cancelled.");
                break;
            default:
                SafeLog.Write(_logger, LogLevel.Debug,
                    "MapDidCommWebSocket: the connection ended while a message it had received in full was being handled, and the clock could not time the rest ({ExceptionType}); the handling is cancelled.",
                    exceptionType);
                break;
        }

        Task cancelled;
        try
        {
            cancelled = _source.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return; // the handling ended meanwhile
        }
        if (!cancelled.IsCompletedSuccessfully)
            _ = ObserveAsync(cancelled, _logger);
    }

    private static async Task ObserveAsync(Task cancellation, ILogger? logger)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog.Write(logger, LogLevel.Warning, "MapDidCommWebSocket: a cancellation callback of a message's handling threw {ExceptionType}; isolated.", ex.GetType().Name);
        }
    }

    private enum Reason
    {
        Bound,
        HostStopping,
        ClockFault,
    }
}
