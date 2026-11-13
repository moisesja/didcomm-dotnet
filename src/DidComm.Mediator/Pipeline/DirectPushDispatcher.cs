using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// A stored message to push straight to its recipient's endpoint (FR-PICK-09). It names the record
/// only: the payload that is sent is the one the store holds when the push takes the record.
/// </summary>
/// <param name="GrantPeerDid">The grant the message is held under: the key the endpoint directory is asked with, and the store's key for the record.</param>
/// <param name="RecipientDid">The registered recipient DID the message is for; re-checked against the grant before the push.</param>
/// <param name="MessageId">The store's id of the record to take, send and remove.</param>
/// <param name="ForwardId">The id of the forward that carried it, for log lines only.</param>
internal sealed record PushJob(string GrantPeerDid, string RecipientDid, string MessageId, string ForwardId)
{
    /// <summary>Nothing identifying: no DID, no queue id.</summary>
    public override string ToString() => nameof(PushJob);
}

/// <summary>
/// Pushes stored messages straight to their recipients' own endpoints when
/// <see cref="MediatorOptions.DirectPush"/> is on (FR-PICK-09, JC-4, JC-5), on a few background
/// workers, after the message has been stored and the forward acknowledged — never on the request
/// or on the forward pump.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bounded.</strong> At most <see cref="MediatorOptions.DirectPushConcurrency"/> pushes run
/// at once; at most <see cref="MaxBacklog"/> jobs wait or run; and one grant may hold at most
/// <see cref="MaxPerGrant"/> of them, so one recipient cannot take the whole backlog. A job names a
/// stored record and pins no payload. <see cref="TryEnqueue"/> never blocks: a job beyond either
/// bound is refused (<c>Push-Skipped-Saturated</c>) and its message simply stays in the queue for
/// pickup. Every job is a message already admitted under the queue's quota, so the work is bounded
/// by what the mediator stored, 1:1.
/// </para>
/// <para>
/// <strong>Fair.</strong> Each grant's jobs wait in their own first-in, first-out line, and the
/// grants that have work are served in turn, with at most one push in flight per grant. A recipient
/// whose endpoint is slow or hangs therefore holds one worker at a time (for at most
/// <see cref="MediatorOptions.DirectPushTimeout"/> per push), never all of them: the other grants'
/// pushes go on with the remaining workers. A grant's own pushes go out one after another, in the
/// order they were handed over.
/// </para>
/// <para>
/// <strong>Per push: it takes exactly the record it sends (L-057).</strong> The endpoint comes only
/// from the host's <see cref="IRecipientEndpointDirectory"/> (absent, <c>null</c> or a throw:
/// skipped), and only an absolute <c>http</c> or <c>https</c> endpoint is pushed to: a WebSocket
/// write gives no receipt. The registration is checked again (a recipient removed or a grant revoked
/// since the message was stored is not pushed to). Then the push takes the record itself, by id,
/// under a lease of its own (<see cref="IMessageStore.LeaseByIdAsync"/>, judged on
/// <c>DidCommOptions.Clock</c>): a record the store no longer holds, or holds expired, aged out, not
/// yet due, leased to a pickup or passed over, is not pushed (<c>Push-Skipped-NotHeld</c>), so an
/// expired or aged-out message is never pushed (FR-ROUTE-09, FR-PICK-10), one a recipient is fetching
/// or has acknowledged is not sent twice, and one a purge removed is not sent after the peer is
/// granted again. The stored payload goes out through <see cref="DidCommClient.SendPackedAsync"/>,
/// which always applies the outbound endpoint guard. Only an accepted answer with an HTTP 2xx status
/// counts as delivered; the record is then removed. Anything else gives the lease back at once, and
/// the message waits for pickup: one attempt per message. The lease runs
/// <see cref="MediatorOptions.DirectPushTimeout"/> plus <see cref="LeaseMargin"/>, so a record whose
/// push never finishes goes back to pickup after that.
/// </para>
/// <para>
/// <strong>Deadline.</strong> One per push, <see cref="MediatorOptions.DirectPushTimeout"/>, over the
/// re-check, the take, the send and the removal (the directory lookup is the host's own synchronous
/// code and is not bounded: a slow directory delays only pushes). It is a timer whose callback
/// cancels inside a <c>try</c>, never <c>CancelAfter</c>: a callback host code registered on the
/// token must not take the process down. Giving a lease back is not cancelled by shutdown; it has a
/// bound of its own of the same length.
/// </para>
/// <para>
/// <strong>Dependencies (L-017).</strong> The directory and <see cref="DidCommClient"/> are resolved
/// from the container on the first push and kept, never at construction, so nothing here can close
/// a cycle through the facade, the transports or the dispatcher, and nothing is built when direct
/// push is off.
/// </para>
/// <para>
/// <strong>Logging.</strong> Lines carry the forward's id (shortened when long) and exception type
/// names, never a DID, a queue id, the endpoint or an exception message (a transport error's text
/// contains the URL). Failures are rate-limited per reason: the first and every 1000th at Warning,
/// the rest at Debug.
/// </para>
/// <para>
/// <strong>Shutdown.</strong> Disposing stops admission, cancels the running pushes (their leases are
/// given back) and discards the waiting jobs (<c>Push-Skipped-Shutdown</c>); their messages stay
/// queued, so nothing is lost. <see cref="DisposeAsync"/> also waits up to five seconds for the
/// workers.
/// </para>
/// </remarks>
internal sealed class DirectPushDispatcher : IAsyncDisposable, IDisposable
{
    /// <summary>Jobs waiting or running, at most (the forward handler's own backlog bound).</summary>
    internal const int MaxBacklog = ForwardHandler.DefaultCapacity;

    /// <summary>Jobs one grant may have waiting or running, at most.</summary>
    internal const int MaxPerGrant = 16;

    /// <summary>How much longer than the push deadline a push's lease on its record runs.</summary>
    internal static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly IMessageStore _store;
    private readonly IMediationStore _mediation;
    private readonly DidCommOptions _didComm;
    private readonly MediatorMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly bool _enabled;
    private readonly TimeSpan _timeout;
    private readonly int _capacity;
    private readonly int _perGrant;
    private readonly object _gate = new();
    // Under _gate: each grant's waiting jobs in arrival order and whether one of its pushes runs;
    // the grants that have a waiting job and none running, in the order they are to be served.
    private readonly Dictionary<string, GrantLine> _grants = new(StringComparer.Ordinal);
    private readonly Queue<GrantLine> _ready = new();
    // One count per entry put on _ready; a worker takes one, then a grant off _ready.
    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Task[] _workers;
    private TaskCompletionSource _idle = NewCompletedSignal();
    private int _outstanding;
    private int _stopped;
    private DidCommClient? _client;
    private IRecipientEndpointDirectory? _directory;
    private bool _directoryResolved;
    private long _saturatedLines;
    private long _failedLines;
    private long _schemeLines;
    private long _directoryFaultLines;
    private long _storeFaultLines;

    /// <summary>Create the dispatcher; its workers start here when direct push is on.</summary>
    /// <param name="services">The container, for the directory and the facade, resolved on the first push.</param>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="store">The message store: the push takes, removes or gives back the record it sends.</param>
    /// <param name="mediation">The grant store, to check the registration again before a push.</param>
    /// <param name="didCommOptions">Supplies the clock the store judges the record by.</param>
    /// <param name="metrics">The direct-push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the per-push deadline.</param>
    public DirectPushDispatcher(
        IServiceProvider services,
        IOptions<MediatorOptions> options,
        IMessageStore store,
        IMediationStore mediation,
        IOptions<DidCommOptions> didCommOptions,
        MediatorMetrics metrics,
        ILogger<DirectPushDispatcher>? logger,
        TimeProvider timeProvider)
        : this(services, options, store, mediation, didCommOptions, metrics, logger, timeProvider, MaxBacklog, MaxPerGrant)
    {
    }

    /// <summary>Test seam: the dispatcher with explicit bounds.</summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The mediator settings.</param>
    /// <param name="store">The message store.</param>
    /// <param name="mediation">The grant store.</param>
    /// <param name="didCommOptions">Supplies the clock the store judges the record by.</param>
    /// <param name="metrics">The direct-push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the per-push deadline.</param>
    /// <param name="capacity">Jobs waiting or running, at most.</param>
    /// <param name="perGrant">Jobs one grant may have waiting or running, at most.</param>
    internal DirectPushDispatcher(
        IServiceProvider services,
        IOptions<MediatorOptions> options,
        IMessageStore store,
        IMediationStore mediation,
        IOptions<DidCommOptions> didCommOptions,
        MediatorMetrics metrics,
        ILogger<DirectPushDispatcher>? logger,
        TimeProvider timeProvider,
        int capacity,
        int perGrant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mediation);
        ArgumentNullException.ThrowIfNull(didCommOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perGrant, 1);

        // Read once, after validation: a host that changes the options object later changes nothing here.
        var settings = options.Value;
        settings.ValidatePipelineRanges();
        _enabled = settings.DirectPush;
        _timeout = settings.DirectPushTimeout;
        var concurrency = settings.DirectPushConcurrency;

        _services = services;
        _store = store;
        _mediation = mediation;
        _didComm = didCommOptions.Value;
        _metrics = metrics;
        _logger = logger;
        _time = timeProvider;
        _capacity = capacity;
        _perGrant = perGrant;
        _shutdownToken = _shutdown.Token;

        if (!_enabled)
        {
            _workers = Array.Empty<Task>();
            return;
        }

        // The workers must not carry the context of whoever happened to build this singleton (a
        // request's activity, its async-locals): start them with the flow suppressed.
        _workers = new Task[concurrency];
        var suppressed = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
        try
        {
            for (var i = 0; i < _workers.Length; i++)
                _workers[i] = Task.Run(WorkerAsync);
        }
        finally
        {
            suppressed?.Undo();
        }
    }

    /// <summary>Whether direct push is on (workers running).</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Hand a stored message to the push workers. Never blocks and never throws: when direct push
    /// is off it returns <c>false</c> and records nothing; when a bound is reached, or the
    /// dispatcher is shutting down, the job is refused and counted, and the message stays queued.
    /// </summary>
    /// <param name="job">The stored message.</param>
    /// <returns><c>true</c> when a worker will push it.</returns>
    public bool TryEnqueue(PushJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!_enabled)
            return false;

        string refusal;
        var signal = false;
        lock (_gate)
        {
            if (_stopped != 0)
            {
                refusal = PushOutcomes.SkippedShutdown;
            }
            else
            {
                _grants.TryGetValue(job.GrantPeerDid, out var line);
                if (_outstanding >= _capacity || (line?.Count ?? 0) >= _perGrant)
                {
                    refusal = PushOutcomes.SkippedSaturated;
                }
                else
                {
                    if (line is null)
                        _grants.Add(job.GrantPeerDid, line = new GrantLine(job.GrantPeerDid));
                    line.Waiting.Enqueue(job);
                    if (_outstanding++ == 0)
                        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    // A grant joins the turn only while none of its pushes runs; a running one
                    // re-joins it when that push ends.
                    if (!line.InFlight && !line.OnReady)
                    {
                        line.OnReady = true;
                        _ready.Enqueue(line);
                        signal = true;
                    }
                    refusal = string.Empty;
                }
            }
        }

        if (refusal.Length == 0)
        {
            if (signal)
                _readySignal.Release();
            return true;
        }

        _metrics.RecordPush(refusal);
        if (refusal == PushOutcomes.SkippedSaturated)
        {
            var level = MediatorLog.RateLimited(ref _saturatedLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} skipped: the push backlog is full; the message stays queued for pickup. {Count} such skips.", MediatorLog.LogId(job.ForwardId), n);
        }
        else
        {
            MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: the mediator is shutting down; the message stays queued for pickup.", MediatorLog.LogId(job.ForwardId));
        }
        return false;
    }

    /// <summary>Test seam: complete when no job is waiting or running.</summary>
    /// <param name="timeout">How long to wait.</param>
    internal Task WaitForIdleAsync(TimeSpan timeout)
    {
        Task idle;
        lock (_gate)
            idle = _idle.Task;
        return idle.WaitAsync(timeout);
    }

    /// <summary>Jobs waiting or running.</summary>
    internal int Outstanding
    {
        get
        {
            lock (_gate)
                return _outstanding;
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _readySignal.WaitAsync(_shutdownToken).ConfigureAwait(false);

                // Serve the next grant in turn: its oldest waiting job; the grant leaves the turn
                // until this push ends, so it never has two pushes running.
                GrantLine line;
                PushJob job;
                lock (_gate)
                {
                    if (!_ready.TryDequeue(out var next))
                        continue; // the waiting jobs were discarded by a shutdown
                    line = next;
                    line.OnReady = false;
                    job = line.Waiting.Dequeue();
                    line.InFlight = true;
                }

                try
                {
                    if (_shutdownToken.IsCancellationRequested)
                        _metrics.RecordPush(PushOutcomes.SkippedShutdown);
                    else
                        await PushOneAsync(job).ConfigureAwait(false);
                }
                finally
                {
                    Finish(line);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            MediatorLog.Write(_logger, LogLevel.Error, "Direct push worker stopped with {ExceptionType}.", ex.GetType().Name);
        }
    }

    private async Task PushOneAsync(PushJob job)
    {
        var forwardId = MediatorLog.LogId(job.ForwardId);

        // Where to: only the host's directory, never anything the message says (JC-5).
        Uri? endpoint;
        try
        {
            endpoint = Directory()?.Find(job.GrantPeerDid);
        }
        catch (Exception ex)
        {
            _metrics.RecordPush(PushOutcomes.SkippedNoEndpoint);
            var level = MediatorLog.RateLimited(ref _directoryFaultLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} skipped: the endpoint directory failed with {ExceptionType}; the message stays queued for pickup. {Count} such faults.", forwardId, ex.GetType().Name, n);
            return;
        }

        if (endpoint is null)
        {
            _metrics.RecordPush(PushOutcomes.SkippedNoEndpoint);
            MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: no endpoint for the recipient; the message stays queued for pickup.", forwardId);
            return;
        }

        if (!endpoint.IsAbsoluteUri
            || !(string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            _metrics.RecordPush(PushOutcomes.SkippedScheme);
            var level = MediatorLog.RateLimited(ref _schemeLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} skipped: the directory's endpoint is not an absolute http or https URI (a WebSocket write gives no receipt); the message stays queued for pickup. {Count} such skips.", forwardId, n);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        using var timer = _time.CreateTimer(static state => CancelQuietly((CancellationTokenSource)state!), deadline, _timeout, Timeout.InfiniteTimeSpan);
        MessageLease? lease = null;
        try
        {
            // The registration may have ended since the message was stored (a remove, a revoke).
            var grant = await _mediation.FindGrantByRecipientDidAsync(job.RecipientDid, deadline.Token).ConfigureAwait(false);
            if (!string.Equals(grant?.PeerDid, job.GrantPeerDid, StringComparison.Ordinal))
            {
                _metrics.RecordPush(PushOutcomes.SkippedUnregistered);
                MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: the recipient is no longer registered under the grant.", forwardId);
                return;
            }

            // Take exactly the record this job names, as the store holds it now (L-057): never an
            // expired, aged-out, not-yet-due, leased, passed-over, acknowledged or purged one.
            var now = _didComm.Now();
            var taking = new MessageLease(Owner: null, ExpiresAt: SaturatingAdd(now, _timeout + LeaseMargin));
            var record = await _store.LeaseByIdAsync(job.GrantPeerDid, job.MessageId, taking, now, deadline.Token).ConfigureAwait(false);
            if (record is null)
            {
                _metrics.RecordPush(PushOutcomes.SkippedNotHeld);
                MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: the queue no longer holds the message for delivery (expired, aged out, picked up or being picked up, or removed).", forwardId);
                return;
            }
            lease = taking;

            var result = await Client().SendPackedAsync(record.Payload, endpoint, MediaTypes.Encrypted, deadline.Token).ConfigureAwait(false);
            if (result is { Accepted: true, HttpStatusCode: >= 200 and <= 299 })
            {
                // Delivered: counted as pushed whatever the removal says.
                _metrics.RecordPush(PushOutcomes.Pushed);
                lease = null; // never given back: the message was delivered
                var removed = await RemoveQuietlyAsync(job, forwardId, deadline.Token).ConfigureAwait(false);
                MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId}: delivered (HTTP {StatusCode}); queued copy removed: {Removed}.", forwardId, result.HttpStatusCode, removed);
                return;
            }

            _metrics.RecordPush(PushOutcomes.Failed);
            var level = MediatorLog.RateLimited(ref _failedLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} not delivered: the endpoint did not accept it (HTTP {StatusCode}); the message stays queued for pickup. {Count} such failures.", forwardId, result.HttpStatusCode, n);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            _metrics.RecordPush(PushOutcomes.SkippedShutdown);
            MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} abandoned: the mediator is shutting down; the message stays queued for pickup.", forwardId);
        }
        catch (Exception ex)
        {
            // A transport error, a non-2xx answer the transport turned into an exception, the
            // deadline, a store or facade fault. Type name only: a transport error names the URL.
            var reason = deadline.IsCancellationRequested ? "Timeout" : ex.GetType().Name;
            _metrics.RecordPush(PushOutcomes.Failed);
            var level = MediatorLog.RateLimited(ref _failedLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} failed ({ExceptionType}); the message stays queued for pickup. {Count} such failures.", forwardId, reason, n);
        }
        finally
        {
            // Not delivered: give the record back at once so a pickup gets it now, not when the lease ends.
            if (lease is not null)
                await ReleaseQuietlyAsync(job, lease, forwardId).ConfigureAwait(false);
        }
    }

    // After a delivered push: remove exactly the record that was sent. A failure leaves it under the
    // push's lease until that ends; then a pickup may deliver it again (delivery is at-least-once).
    private async Task<bool> RemoveQuietlyAsync(PushJob job, string forwardId, CancellationToken ct)
    {
        try
        {
            return await _store.AcknowledgeAsync(job.GrantPeerDid, new[] { job.MessageId }, ct).ConfigureAwait(false) > 0;
        }
        catch (Exception ex)
        {
            var level = MediatorLog.RateLimited(ref _storeFaultLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId}: delivered, but removing the queued copy failed with {ExceptionType}; it may be delivered again once the push's lease ends. {Count} such faults.", forwardId, ex.GetType().Name, n);
            return false;
        }
    }

    // Not cancelled by shutdown (a shutdown is exactly when the record should go back to pickup),
    // but bounded: a store that hangs here must not hold the worker. A lease not given back ends by
    // itself at its expiry.
    private async Task ReleaseQuietlyAsync(PushJob job, MessageLease lease, string forwardId)
    {
        using var bound = new CancellationTokenSource();
        using var timer = _time.CreateTimer(static state => CancelQuietly((CancellationTokenSource)state!), bound, _timeout, Timeout.InfiniteTimeSpan);
        try
        {
            await _store.ReleaseAsync(job.GrantPeerDid, new[] { job.MessageId }, lease.Id, bound.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var level = MediatorLog.RateLimited(ref _storeFaultLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId}: giving the queued message back failed with {ExceptionType}; it returns to pickup when the push's lease ends. {Count} such faults.", forwardId, ex.GetType().Name, n);
        }
    }

    private static DateTimeOffset SaturatingAdd(DateTimeOffset instant, TimeSpan span)
        => span.Ticks >= (DateTimeOffset.MaxValue - instant).Ticks ? DateTimeOffset.MaxValue : instant + span;

    // Resolved on the first push and kept (L-017): never at construction. A directory that is not
    // registered is remembered as absent.
    private IRecipientEndpointDirectory? Directory()
    {
        if (Volatile.Read(ref _directoryResolved))
            return _directory;
        var directory = _services.GetService<IRecipientEndpointDirectory>();
        _directory = directory;
        Volatile.Write(ref _directoryResolved, true);
        return directory;
    }

    private DidCommClient Client() => _client ??= _services.GetRequiredService<DidCommClient>();

    // A push of this grant ended: it gives back its slot and, when the grant has more waiting, the
    // grant takes its turn again at the back of the line.
    private void Finish(GrantLine line)
    {
        TaskCompletionSource? idle = null;
        var signal = false;
        lock (_gate)
        {
            line.InFlight = false;
            if (line.Waiting.Count > 0 && _stopped == 0)
            {
                line.OnReady = true;
                _ready.Enqueue(line);
                signal = true;
            }
            else if (line.Waiting.Count == 0)
            {
                _grants.Remove(line.GrantPeerDid);
            }
            if (--_outstanding == 0)
                idle = _idle;
        }
        if (signal)
            _readySignal.Release();
        idle?.TrySetResult();
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
            // A callback host code registered on the token threw, or the source is already
            // disposed: neither may escape onto a timer thread.
        }
    }

    private static TaskCompletionSource NewCompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
        return signal;
    }

    // Stop admission, cancel the running pushes, discard what waits. Idempotent.
    private void Stop()
    {
        var discarded = 0;
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            if (_stopped != 0)
                return;
            _stopped = 1;

            // Every waiting job ends here; a running push ends on its own, through Finish.
            _ready.Clear();
            foreach (var line in _grants.Values.ToArray())
            {
                discarded += line.Waiting.Count;
                line.Waiting.Clear();
                line.OnReady = false;
                if (!line.InFlight)
                    _grants.Remove(line.GrantPeerDid);
            }
            _outstanding -= discarded;
            if (discarded > 0 && _outstanding == 0)
                idle = _idle;
        }

        CancelQuietly(_shutdown);

        for (var i = 0; i < discarded; i++)
            _metrics.RecordPush(PushOutcomes.SkippedShutdown);
        idle?.TrySetResult();
        if (discarded > 0)
            MediatorLog.Write(_logger, LogLevel.Information, "Direct push stopped: {Discarded} waiting pushes discarded; their messages stay queued for pickup.", discarded);
    }

    // One grant's line, touched only under _gate.
    private sealed class GrantLine(string grantPeerDid)
    {
        public string GrantPeerDid { get; } = grantPeerDid;

        /// <summary>The grant's waiting jobs, oldest first.</summary>
        public Queue<PushJob> Waiting { get; } = new();

        /// <summary>Whether one of the grant's pushes is running.</summary>
        public bool InFlight { get; set; }

        /// <summary>Whether the grant is on the ready line.</summary>
        public bool OnReady { get; set; }

        /// <summary>The grant's jobs waiting or running, against <see cref="MaxPerGrant"/>.</summary>
        public int Count => Waiting.Count + (InFlight ? 1 : 0);
    }

    /// <summary>Stop admission, cancel the running pushes and discard the waiting ones; does not wait.</summary>
    public void Dispose() => Stop();

    /// <summary>Stop as <see cref="Dispose"/> does, then wait up to five seconds for the workers to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_workers.Length == 0)
            return;
        try
        {
            await Task.WhenAll(_workers).WaitAsync(ShutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            MediatorLog.Write(_logger, LogLevel.Warning, "Direct push workers did not stop within {Grace}; their messages stay queued for pickup.", ShutdownGrace);
        }
        catch
        {
            // The workers never fault (they log and stop); nothing to add here.
        }
    }
}
