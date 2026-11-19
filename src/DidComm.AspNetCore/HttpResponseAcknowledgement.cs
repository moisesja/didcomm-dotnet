using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// The transport acknowledgement of one request on the registry-aware HTTP endpoint
/// (<see cref="ProtocolContext.Acknowledged"/>), and the deadline by which its response must be
/// committed when the endpoint sets one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Acknowledged"/> completes from <c>Response.OnCompleted</c> (the response has been committed to
/// the connection) and is cancelled when the request aborts first (<c>RequestAborted</c>), when the
/// endpoint turns the response into its uniform 400 (<see cref="Cancel"/>), or when the deadline
/// passes first. The first of these wins; the rest change nothing.
/// </para>
/// <para>
/// <strong>The deadline.</strong> <see cref="ArmDeadline"/> starts one one-shot timer on the
/// container's <see cref="TimeProvider"/>. If the response has not been committed when it fires, the
/// acknowledgement is cancelled first — so a handler waiting on it (the forward handler) drops its
/// deferred work and releases what it reserved, whatever the server then does with the abort — and
/// the request is aborted (<see cref="HttpContext.Abort"/>), so the client never receives that
/// response. Completion wins: the commit and the deadline are decided under one lock and the abort is
/// made under it, so nothing is aborted once <c>OnCompleted</c> has run (after that the server may
/// reuse the context for the connection's next request). The timer is disposed by whichever ends the
/// request first — the commit, a client abort, or its own firing — so none outlives its request by
/// more than its due time. The callback never throws: a timer callback that throws would end the
/// process.
/// </para>
/// </remarks>
internal sealed class HttpResponseAcknowledgement
{
    private readonly HttpContext _context;
    private readonly TaskCompletionSource _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private CancellationTokenRegistration _abortRegistration;
    private ITimer? _deadline;
    private TimeSpan _dueTime;
    private ResponseDeadlineLog? _log;
    private ILogger? _logger;
    // The request is over for the deadline: the response completed, the request aborted, or the deadline fired.
    private bool _settled;

    private HttpResponseAcknowledgement(HttpContext context) => _context = context;

    /// <summary>Completes once the response has been committed; cancelled when it never will be.</summary>
    internal Task Acknowledged => _acknowledged.Task;

    /// <summary>
    /// Create the acknowledgement of <paramref name="context"/>'s request. Its abort registration is
    /// kept until the response completes, not disposed when the endpoint returns: the response is
    /// written after the endpoint returns.
    /// </summary>
    /// <param name="context">The request.</param>
    internal static HttpResponseAcknowledgement Start(HttpContext context)
    {
        var acknowledgement = new HttpResponseAcknowledgement(context);
        acknowledgement._abortRegistration = context.RequestAborted.Register(
            static state => ((HttpResponseAcknowledgement)state!).OnRequestAborted(), acknowledgement);
        context.Response.OnCompleted(static state =>
        {
            ((HttpResponseAcknowledgement)state).OnResponseCompleted();
            return Task.CompletedTask;
        }, acknowledgement);
        return acknowledgement;
    }

    /// <summary>The response will not acknowledge the inbound (a client abort, or the uniform 400 after the handler ran).</summary>
    internal void Cancel() => _acknowledged.TrySetCanceled();

    /// <summary>
    /// Abort the request when its response is not committed within <paramref name="dueTime"/> from
    /// now (at once when it is zero or less). Call once, after the handler ran; a second call, or a
    /// call once the request is over, does nothing. A <paramref name="time"/> that cannot create the
    /// timer leaves the request without a deadline.
    /// </summary>
    /// <param name="time">The container's clock.</param>
    /// <param name="dueTime">How long the response has to be committed.</param>
    /// <param name="log">The mapped endpoint's rate-limited abort line.</param>
    /// <param name="logger">Optional logger.</param>
    internal void ArmDeadline(TimeProvider time, TimeSpan dueTime, ResponseDeadlineLog log, ILogger? logger)
    {
        lock (_gate)
        {
            if (_settled || _deadline is not null)
                return;
            _dueTime = dueTime;
            _log = log;
            _logger = logger;
            try
            {
                _deadline = time.CreateTimer(
                    static state => ((HttpResponseAcknowledgement)state!).OnDeadline(),
                    this,
                    dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);
            }
            catch (Exception)
            {
                // A host TimeProvider that cannot create the timer: the response goes out without a
                // deadline, as it would without this endpoint's bound.
            }
        }
    }

    private void OnResponseCompleted()
    {
        ITimer? deadline;
        lock (_gate)
        {
            _settled = true;
            deadline = _deadline;
            _deadline = null;
        }
        deadline?.Dispose();
        // Dispose the abort registration before completing: a concurrent abort has either run (and
        // cancelled, so the result below changes nothing) or never will.
        _abortRegistration.Dispose();
        _acknowledged.TrySetResult();
    }

    private void OnRequestAborted()
    {
        ITimer? deadline;
        lock (_gate)
        {
            _settled = true;
            deadline = _deadline;
            _deadline = null;
        }
        deadline?.Dispose();
        _acknowledged.TrySetCanceled();
    }

    private void OnDeadline()
    {
        ITimer? deadline;
        var aborted = false;
        lock (_gate)
        {
            deadline = _deadline;
            _deadline = null;
            if (!_settled)
            {
                _settled = true;
                // Cancel before aborting: whatever the server does with the abort, the inbound is
                // not acknowledged, and the commit can no longer win.
                _acknowledged.TrySetCanceled();
                try
                {
                    _context.Abort();
                    aborted = true;
                }
                catch (Exception)
                {
                    // The server refused the abort; the acknowledgement is cancelled all the same.
                }
            }
        }
        deadline?.Dispose();
        if (aborted)
            _log?.Aborted(_logger, _dueTime);
    }
}

/// <summary>
/// The response-deadline line of one mapped endpoint. Any client can make a response miss its
/// deadline (by not reading it), so the line is rate-limited: the first and every 1000th at Warning,
/// the others at Debug. It carries no wire value.
/// </summary>
internal sealed class ResponseDeadlineLog
{
    private long _aborts;

    /// <summary>Count one abort and write its line.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="dueTime">The deadline the response missed.</param>
    internal void Aborted(ILogger? logger, TimeSpan dueTime)
    {
        var level = RateLimitedLog.Level(ref _aborts, LogLevel.Warning, out var count);
        SafeLog.Write(logger, level,
            "DidComm receive: a response was not committed within its deadline ({DeadlineSeconds} s) and its request was aborted; {Count} so far.",
            Math.Round(dueTime.TotalSeconds, 3), count);
    }
}
