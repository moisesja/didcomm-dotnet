using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DidComm.AspNetCore;

/// <summary>
/// The transport acknowledgement of one request on the registry-aware HTTP endpoint
/// (<see cref="ProtocolContext.Acknowledged"/>), and, when the endpoint sets one, the deadline by
/// which its response must be handed to the transport.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Without a deadline</strong> (every response but the two below) <see cref="Acknowledged"/>
/// completes from <c>Response.OnCompleted</c> and is cancelled when the request aborts first
/// (<c>RequestAborted</c>) or when the endpoint turns the response into its uniform 400
/// (<see cref="Cancel"/>): the behaviour of every registry-aware endpoint since the acknowledgement
/// exists.
/// </para>
/// <para>
/// <strong>With a deadline</strong> (<see cref="WithDeadline"/>: the forward handler's 202, and the
/// mediator endpoint's returned 200) the endpoint completes the response itself
/// (<c>Response.CompleteAsync()</c>, inside its result), and the acknowledgement is decided there:
/// once the response has been handed to the transport it completes, and nothing after that — a
/// completion callback of other middleware, however slow, or the client closing its connection
/// while such callbacks run — can cancel it. Kestrel runs the completion callbacks only after the
/// response was sent, one at a time and the last registered first, so <c>OnCompleted</c> says
/// nothing about how long ago the response left. If the deadline (one one-shot timer on the
/// container's <see cref="TimeProvider"/>) passes while the response is still being written — a
/// client that does not read, so the write cannot finish — the acknowledgement is cancelled first,
/// so a handler waiting on it (the forward handler) drops its deferred work and releases what it
/// reserved whatever the server then does, and the request is aborted
/// (<see cref="HttpContext.Abort"/>), so the client never receives that response. The handing-over
/// and the deadline are decided under one lock and the abort is made under it; a write that finished
/// a moment before the deadline fired wins even if its continuation has not run yet. The timer is
/// disposed by whichever comes first — the handing-over, a client abort, or its own firing — so none
/// outlives its request by more than its due time. The timer callback never throws: one that throws
/// would end the process.
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
    // The response's completion (Response.CompleteAsync), once the result has started it.
    private Task? _writing;
    // The deadline has no say any more: the response was handed to the transport, the request was
    // aborted, the response completed, or the deadline fired.
    private bool _deadlineOver;

    private HttpResponseAcknowledgement(HttpContext context) => _context = context;

    /// <summary>Completes once the response has been handed to the transport; cancelled when it never will be.</summary>
    internal Task Acknowledged => _acknowledged.Task;

    /// <summary>
    /// Create the acknowledgement of <paramref name="context"/>'s request. Its abort registration is
    /// kept until the response is handed over or completes, not disposed when the endpoint returns:
    /// the response is written after the endpoint returns.
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
    /// Answer with <paramref name="response"/> under a deadline: the returned result writes it,
    /// completes the response and then completes the acknowledgement; if the response has not been
    /// handed to the transport within <paramref name="dueTime"/> from now (at once when it is zero or
    /// less), the acknowledgement is cancelled and the request aborted. Call once, after the handler
    /// ran. A <paramref name="time"/> that cannot create the timer leaves the response without a
    /// deadline; it is still completed here.
    /// </summary>
    /// <param name="response">The response the endpoint would have returned.</param>
    /// <param name="time">The container's clock.</param>
    /// <param name="dueTime">How long the response has to be handed to the transport.</param>
    /// <param name="log">The mapped endpoint's rate-limited abort line.</param>
    /// <param name="logger">Optional logger.</param>
    internal IResult WithDeadline(IResult response, TimeProvider time, TimeSpan dueTime, ResponseDeadlineLog log, ILogger? logger)
    {
        lock (_gate)
        {
            if (!_deadlineOver && _deadline is null)
            {
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
        return new HandedOverResult(this, response);
    }

    /// <summary>The response's completion has started; a deadline that finds it finished does not abort.</summary>
    internal void Writing(Task completion)
    {
        lock (_gate)
            _writing = completion;
    }

    /// <summary>The response has been handed to the transport: the deadline is over and the inbound is acknowledged.</summary>
    internal void HandedOver()
    {
        ITimer? deadline;
        lock (_gate)
        {
            if (_deadlineOver)
                return; // the deadline aborted the request, or the request was aborted: already cancelled
            _deadlineOver = true;
            deadline = _deadline;
            _deadline = null;
        }
        deadline?.Dispose();
        // From here no abort can take the acknowledgement back. Disposing the registration waits for
        // an abort callback already running; one that never ran is checked for on the token itself.
        _abortRegistration.Dispose();
        if (_context.RequestAborted.IsCancellationRequested)
            _acknowledged.TrySetCanceled();
        else
            _acknowledged.TrySetResult();
    }

    private void OnResponseCompleted()
    {
        ITimer? deadline;
        lock (_gate)
        {
            _deadlineOver = true;
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
            _deadlineOver = true;
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
            // A write that has finished wins, even when its continuation has not run HandedOver yet.
            if (!_deadlineOver && _writing is not { IsCompletedSuccessfully: true })
            {
                _deadlineOver = true;
                // Cancel before aborting: whatever the server does with the abort, the inbound is
                // not acknowledged, and the handing-over can no longer win.
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

    /// <summary>
    /// Writes the endpoint's response, starts and completes it, and then marks it handed over. A
    /// response without a body is sent with <c>Content-Length: 0</c>, as the server would have sent
    /// it. Starting it runs the <c>OnStarting</c> callbacks (on a server whose <c>CompleteAsync</c>
    /// does not, such as the test server, too); completing it waits until the server has taken the
    /// whole response. A failure while writing cancels the acknowledgement; when the request was
    /// aborted (by the deadline or the client) it is the end of that request, not an error, and is
    /// not rethrown.
    /// </summary>
    private sealed class HandedOverResult(HttpResponseAcknowledgement owner, IResult response) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                await response.ExecuteAsync(httpContext).ConfigureAwait(false);
                if (!httpContext.Response.HasStarted && httpContext.Response.ContentLength is null)
                    httpContext.Response.ContentLength = 0;
                await httpContext.Response.StartAsync().ConfigureAwait(false);
                var completion = httpContext.Response.CompleteAsync();
                owner.Writing(completion);
                await completion.ConfigureAwait(false);
            }
            catch (Exception) when (owner.Acknowledged.IsCanceled || httpContext.RequestAborted.IsCancellationRequested)
            {
                owner.Cancel();
                return;
            }
            catch (Exception)
            {
                owner.Cancel();
                throw;
            }
            owner.HandedOver();
        }
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
