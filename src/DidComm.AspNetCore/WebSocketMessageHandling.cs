using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// #100 OD-8: the cancellation token under which the registry-aware WebSocket receive loop handles the
/// messages of one connection: each message's STOMP frame, unpack, admission, dispatch and reply, and
/// whatever a handler starts with the token and leaves running. One per connection, created when the
/// loop starts. While the connection lives nothing cancels it, so a handler has no time limit of its
/// own (as before). It is cancelled at the first of:
/// <list type="bullet">
/// <item>the end of the loop (<see cref="Dispose"/>, the first step of its <c>finally</c>): whatever
/// handler work is still running is cancelled when the connection's work is over, however it ended
/// (a normal close too);</item>
/// <item>when the connection ended with a message in hand (received in full, not yet handled to the
/// end): the bound after that end (the loop passes the close grace,
/// <see cref="WebSocketReturnRouteSession.DefaultCloseGrace"/>, 5 s), timed on the container's clock,
/// or the host's shutdown, whichever comes first. The message is still handled meanwhile.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The connection ends when the loop's connection token is cancelled: the peer dropped the connection
/// (the request's abort), or the connection's return-route session ended it (its writer found the
/// socket broken, or an evicted peer did not answer the close frame within the close grace, JC-21).
/// The message in hand was received whole before any of these, and none of them is the peer taking it
/// back. A connection that ends with no message in hand needs no bound: the loop ends at once.
/// </para>
/// <para>
/// The loop reads with the connection token and stops once it is cancelled, so after the end it reads
/// nothing more: a connection has at most one finish (the bound is armed at most once), and it lasts
/// at most the bound.
/// </para>
/// <para>
/// Nothing is timed while the connection lives: the bound's timer is created, and the host's stopping
/// token watched, only once the connection ended with a message in hand. A clock that cannot create
/// the timer ends the handling at once, as before OD-8, rather than leaving it unbounded. The
/// cancellation is requested with <see cref="CancellationTokenSource.CancelAsync"/>, so the callbacks
/// handler code registered on the token run on the thread pool, never on the timer's thread, the
/// host's stopping thread or the loop's; a fault in one is observed and logged by type. The source is
/// disposed only once those callbacks have run, so none is ever skipped. The lines carry no wire value
/// (D7).
/// </para>
/// </remarks>
internal sealed class WebSocketMessageHandling : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _bound;
    private readonly CancellationToken _stopping;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private CancellationTokenRegistration _connectionEnded;
    // Under _gate:
    private bool _inHand;          // a message received in full is being handled
    private bool _ended;           // the connection token was cancelled
    private bool _armed;           // the bound and the shutdown watch were armed (at most once)
    private bool _over;            // Dispose ran: the loop ended
    private Task? _cancellation;   // the cancellation, once requested (at most once)
    private ITimer? _timer;
    private CancellationTokenRegistration _hostStopping;

    private WebSocketMessageHandling(TimeProvider time, TimeSpan bound, CancellationToken stopping, ILogger? logger)
    {
        _time = time;
        _bound = bound;
        _stopping = stopping;
        _logger = logger;
        Token = _source.Token;
    }

    /// <summary>The token the connection's messages are handled under.</summary>
    internal CancellationToken Token { get; }

    /// <summary>Start the handling token of a connection, when its receive loop starts.</summary>
    /// <param name="connection">The loop's connection token; its cancellation with a message in hand starts the bound.</param>
    /// <param name="time">The container's clock, which times the bound.</param>
    /// <param name="bound">How long a message in hand may still be handled once the connection ended.</param>
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

    /// <summary>
    /// The loop has received a message in full and starts handling it; dispose the result once
    /// everything done for the message in the loop is over. A connection that ends from now on (or had
    /// already ended) starts the bound.
    /// </summary>
    internal MessageInHand BeginMessage()
    {
        bool arm;
        lock (_gate)
        {
            _inHand = true;
            arm = _ended && !_armed && !_over;
            _armed |= arm;
        }
        if (arm)
            ArmFinish();
        return new MessageInHand(this);
    }

    /// <summary>
    /// The loop ended: cancel the token (unless the bound or the shutdown did already) and release the
    /// timer and the registrations; the source itself is disposed once the cancellation's callbacks
    /// have run. Idempotent.
    /// </summary>
    public void Dispose()
    {
        ITimer? timer;
        CancellationTokenRegistration stopping;
        Task cancellation;
        lock (_gate)
        {
            if (_over)
                return;
            _over = true;
            timer = _timer;
            stopping = _hostStopping;
            _timer = null;
            _hostStopping = default;
            cancellation = _cancellation ??= _source.CancelAsync();
        }
        // Waits for OnConnectionEnded when it is running on another thread; from here it never runs.
        _connectionEnded.Dispose();
        stopping.Dispose();
        timer?.Dispose();
        _ = DisposeSourceAsync(cancellation);
    }

    private void EndMessage()
    {
        lock (_gate)
            _inHand = false;
    }

    private void OnConnectionEnded()
    {
        bool arm;
        lock (_gate)
        {
            _ended = true;
            arm = _inHand && !_armed && !_over;
            _armed |= arm;
        }
        if (arm)
            ArmFinish();
    }

    private void ArmFinish()
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
            if (!_over)
            {
                _timer = timer;
                _hostStopping = stopping;
                return;
            }
        }
        // The loop ended meanwhile and cancelled the token itself.
        stopping.Dispose();
        timer.Dispose();
    }

    private void End(Reason reason, string? exceptionType)
    {
        lock (_gate)
        {
            if (_over || _cancellation is not null)
                return;
            _cancellation = _source.CancelAsync();
        }
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
    }

    // The source goes only once its cancellation callbacks have run: a source disposed before then
    // would never run them.
    private async Task DisposeSourceAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog.Write(_logger, LogLevel.Warning, "MapDidCommWebSocket: a cancellation callback of a message's handling threw {ExceptionType}; isolated.", ex.GetType().Name);
        }
        _source.Dispose();
    }

    /// <summary>The message in hand; disposing it ends the message (not the token).</summary>
    internal readonly struct MessageInHand(WebSocketMessageHandling owner) : IDisposable
    {
        public void Dispose() => owner?.EndMessage();
    }

    private enum Reason
    {
        Bound,
        HostStopping,
        ClockFault,
    }
}
