using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// A stored message to push to its recipient: over the recipient's live connection, or straight to
/// its endpoint (FR-PICK-08/09). It names the record only: the payload that is sent is the one the
/// store holds when the push takes the record.
/// </summary>
/// <remarks>
/// A job outlives the forward that produced it (it can wait in the backlog long after the forward
/// handler gave back its byte reservation), and nothing counts the memory it holds but its number.
/// So it holds bounded values only: two bare DIDs of at most
/// <see cref="Consistency.IdentifierLimits.MaxDidLength"/> characters, the queue's own record id of
/// at most <see cref="PickupSizing.MaxMintedIdLength"/>, and the forward's id as the bounded log id
/// (<see cref="MediatorLog.LogId"/>, at most <see cref="DidComm.Diagnostics.LogValues.MaxIdLength"/>
/// characters), never the wire value, which the message model does not bound.
/// </remarks>
internal sealed record PushJob
{
    /// <summary>Create a job; the forward's id is kept in its bounded log form.</summary>
    /// <param name="grantPeerDid">The grant the message is held under: the peer whose live session gets a live push, the key the endpoint directory is asked with, and the store's key for the record. A bare DID.</param>
    /// <param name="recipientDid">The registered recipient DID the message is for; re-checked against the grant before the push. A bare DID.</param>
    /// <param name="messageId">The store's id of the record to take and send.</param>
    /// <param name="forwardId">The id of the forward that carried it, for log lines only; any length (it is bounded here).</param>
    /// <exception cref="ArgumentException">A DID is longer than <see cref="Consistency.IdentifierLimits.MaxDidLength"/>, or the record id longer than <see cref="PickupSizing.MaxMintedIdLength"/>.</exception>
    public PushJob(string grantPeerDid, string recipientDid, string messageId, string? forwardId)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ArgumentNullException.ThrowIfNull(recipientDid);
        ArgumentNullException.ThrowIfNull(messageId);
        if (grantPeerDid.Length > Consistency.IdentifierLimits.MaxDidLength || recipientDid.Length > Consistency.IdentifierLimits.MaxDidLength)
            throw new ArgumentException("A push job holds bare DIDs of at most 2048 characters.");
        if (messageId.Length > PickupSizing.MaxMintedIdLength)
            throw new ArgumentException("A push job holds the queue's own record id.", nameof(messageId));
        GrantPeerDid = grantPeerDid;
        RecipientDid = recipientDid;
        MessageId = messageId;
        ForwardId = MediatorLog.LogId(forwardId);
    }

    /// <summary>The grant the message is held under.</summary>
    public string GrantPeerDid { get; }

    /// <summary>The registered recipient DID the message is for.</summary>
    public string RecipientDid { get; }

    /// <summary>The store's id of the record.</summary>
    public string MessageId { get; }

    /// <summary>The forward's id in its bounded log form (<see cref="MediatorLog.LogId"/>).</summary>
    public string ForwardId { get; }

    /// <summary>The characters the job holds: the measure of what it retains (bounded by construction).</summary>
    internal int RetainedCharacters => GrantPeerDid.Length + RecipientDid.Length + MessageId.Length + ForwardId.Length;

    /// <summary>Nothing identifying: no DID, no queue id.</summary>
    public override string ToString() => nameof(PushJob);
}

/// <summary>
/// Pushes stored messages to their recipients on a few background workers, after the message has
/// been stored and the forward acknowledged — never on the request or on the forward pump
/// (FR-PICK-08/09, D6, JC-4, JC-5). It runs whenever <c>AddMediator()</c> is used: live pushes need
/// it whether or not <see cref="MediatorOptions.DirectPush"/> is on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>In the delivery order (D6).</strong> A job first checks that the recipient DID is still
/// registered under the grant (<c>Push-Skipped-Unregistered</c> otherwise), then tries, in turn:
/// (1) a <strong>live push</strong> over the peer's live connection
/// (<see cref="PickupCoordinator.TryPushLiveAsync"/>: the newest live session of the peer with live
/// delivery on takes the record by id under a lease of its own and sends it as a one-message
/// <c>delivery</c>; taken by the session, it stays leased until <c>messages-received</c>, the
/// session's close or the lease timeout — <c>Pushed-Live</c>); (2) when the peer has no such
/// session, or the session did not take it, and direct push is on, a <strong>direct push</strong>
/// to the recipient's own endpoint (below); (3) otherwise the message simply stays queued for
/// pickup (<c>Push-Failed</c> when a live session did not take it, <c>Push-Skipped-NoLiveSession</c>
/// when there was none). A record the queue no longer holds for delivery — a <c>delivery-request</c>
/// took it first, it was acknowledged, expired, aged out or purged — is not pushed by either path
/// (<c>Push-Skipped-NotHeld</c>): no message is sent twice and none is lost. The live push packs a
/// <c>delivery</c> for the peer, which resolves the peer's DID; the peer controls how long that
/// takes, which is why it runs here, bounded, and not on the forward pump: one recipient that is
/// slow to resolve never holds up the storing of other recipients' messages.
/// </para>
/// <para>
/// <strong>Bounded by the queue, so every stored message gets its push.</strong> At most
/// <see cref="MediatorOptions.PushConcurrency"/> pushes run at once. A job exists only for a message
/// the queue has stored, and the queue's own quotas bound those: so the backlog is sized from the
/// pickup settings in effect, at most <see cref="MessagePickupOptions.MaxQueueMessages"/> jobs
/// waiting or running for one grant and <see cref="MessagePickupOptions.MaxStoreMessages"/> in all
/// (<see cref="PerGrant"/>, <see cref="Capacity"/>). No stored, due message is refused a job while
/// the store holds it, however long a recipient's first push takes. A job names a stored record,
/// pins no payload and holds bounded values only (<see cref="PushJob"/>): about 200 bytes besides
/// the ids the stored record already holds. <see cref="TryEnqueue"/> never blocks. A job beyond
/// either bound is refused (<c>Push-Skipped-Saturated</c>) and its message simply stays in the
/// queue for pickup; that happens only when a grant has more jobs than its quota — a host
/// <see cref="IMessageStore"/> that admits more than the pickup settings say, or jobs whose messages
/// a pickup already took and removed still waiting their turn.
/// </para>
/// <para>
/// <strong>What a live recipient can rely on.</strong> Every message stored while it has a live
/// session with live delivery on is pushed to that session, in the order it was stored, one at a
/// time — unless a push fails or reaches the deadline, in which case that message stays queued for
/// pickup, and so do the ones stored during the cooldown that a deadline starts (below). Delivery is
/// at-least-once: a
/// pushed message stays leased to the session until <c>messages-received</c>, and goes back to the
/// queue if the session closes or the lease times out first.
/// </para>
/// <para>
/// <strong>Fair.</strong> Each grant's jobs wait in their own first-in, first-out line, and the
/// grants that have work are served in turn, with at most one push in flight per grant. A recipient
/// that is slow to take a push (a slow endpoint, a DID that is slow to resolve) therefore holds one
/// worker at a time (for at most <see cref="MediatorOptions.PushTimeout"/> per push), never all of
/// them: the other grants' pushes go on with the remaining workers. A grant's own pushes go out one
/// after another, in the order they were handed over.
/// </para>
/// <para>
/// <strong>Cooldown.</strong> The workers are shared, and a peer controls how long its own pushes
/// take (its DID's resolution, its endpoint). So a grant whose push — live or direct — reaches the
/// deadline is paused for <see cref="CooldownPushTimeouts"/> × <see cref="MediatorOptions.PushTimeout"/>
/// (one minute at the defaults): its waiting jobs, and those handed over meanwhile, end at once with
/// <c>Push-Skipped-Cooldown</c> and never take a worker, and their messages stay queued for pickup.
/// A peer that stalls on purpose therefore holds a worker at most once every three push timeouts,
/// not continuously. Two timeouts, not more: an honest recipient whose resolution was slow once (a
/// cold cache) gets its live pushes back within a minute at the defaults, the same order as the
/// lease timeout after which an unacknowledged push is delivered again anyway. The residual: with K
/// grants that stall every push and W workers, a healthy recipient's push can still wait up to about
/// ceil(K / W) × <see cref="MediatorOptions.PushTimeout"/> for a worker, and once K reaches 3 × W the
/// stalled grants, each back after its cooldown, can keep every worker busy, so that wait recurs.
/// Nothing is lost and pickup always works; a mediation policy that enrols only known peers closes
/// it (grants are free under the default policy). A deadline reached while re-checking the
/// registration (the host's own store) starts no cooldown.
/// </para>
/// <para>
/// <strong>Direct push: it takes exactly the record it sends (L-057).</strong> The endpoint comes
/// only from the host's <see cref="IRecipientEndpointDirectory"/> (absent, <c>null</c> or a throw:
/// skipped), and only an absolute <c>http</c> or <c>https</c> endpoint is pushed to: a WebSocket
/// write gives no receipt. The push takes the record itself, by id, under a lease of its own
/// (<see cref="IMessageStore.LeaseByIdAsync"/>, judged on <c>DidCommOptions.Clock</c>), so an
/// expired or aged-out message is never pushed (FR-ROUTE-09, FR-PICK-10), one a recipient is
/// fetching or has acknowledged is not sent twice, and one a purge removed is not sent after the
/// peer is granted again. The stored payload goes out through <see cref="DidCommClient.SendPackedAsync(ReadOnlyMemory{byte}, Uri, string, SendPackedOptions, CancellationToken)"/>,
/// which always applies the outbound endpoint guard, as a single attempt
/// (<see cref="SendPackedOptions.SingleAttempt"/>): exactly one POST to exactly the directory's
/// endpoint, with no retry, no redirect followed and outside the HTTP transport's circuit breaker,
/// so one recipient's failing endpoint cannot stop pushes to the others or the host's other HTTP
/// sends. Only an accepted answer with an HTTP 2xx status counts as delivered (a redirect is a
/// failure); the record is then removed. Anything else gives the lease back at once, and the
/// message waits for pickup: one attempt per message. The lease runs
/// <see cref="MediatorOptions.PushTimeout"/> plus <see cref="LeaseMargin"/>, so a record whose
/// push never finishes goes back to pickup after that.
/// </para>
/// <para>
/// <strong>Deadline.</strong> One per job, <see cref="MediatorOptions.PushTimeout"/>, over the
/// re-check, the live push and the direct push (the directory lookup is the host's own synchronous
/// code: it cannot be interrupted, but its time counts). It is a timer whose callback cancels inside
/// a <c>try</c>, never <c>CancelAfter</c>: a callback host code registered on the token must not take
/// the process down. The deadline ends the job whether or not the session, the transport or the
/// store honours the cancellation: a call still running then is abandoned (its result ignored, its
/// fault observed) and the job counts as failed. What happens to the record then depends on who
/// holds its lease. A direct push gives its own lease back at once. A live push's lease is the
/// session's: it is given back when the abandoned send ends without the message going out — at
/// once when the session honours the cancellation (the library's WebSocket session does, and
/// queues nothing once the token is cancelled before the frame reaches its writer). A host session
/// that ignores the cancellation and later reports the message sent keeps it: the record stays
/// leased to that session, as for any live
/// push, until <c>messages-received</c>, the session's close or the lease timeout. So an abandoned
/// send may still reach the recipient, and the recipient may see the message twice (delivery is
/// at-least-once); an abandoned take keeps the record until its lease ends. Giving a lease back is
/// not cancelled by shutdown; the direct push's has a bound of its own of the same length.
/// </para>
/// <para>
/// <strong>Dependencies (L-017).</strong> The pickup queue (for the live push), the stores, the
/// counter and the settings. The directory and <see cref="DidCommClient"/> are resolved from the
/// container on the first direct push and kept, never at construction, so nothing here can close a
/// cycle through the facade, the transports or the dispatcher, and nothing is built when direct
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
internal sealed class PushDispatcher : IAsyncDisposable, IDisposable
{
    /// <summary>How much longer than the push deadline a direct push's lease on its record runs.</summary>
    internal static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    /// <summary>How many push timeouts a grant whose push reached the deadline is paused for.</summary>
    internal const int CooldownPushTimeouts = 2;

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    // One request to exactly the directory's endpoint: no retry, no redirect, no circuit breaker
    // (one recipient's failing endpoint must not stop pushes to the others, AD-6).
    private static readonly SendPackedOptions SingleAttempt = new() { SingleAttempt = true };

    private readonly IServiceProvider _services;
    private readonly PickupCoordinator _pickup;
    private readonly IMessageStore _store;
    private readonly IMediationStore _mediation;
    private readonly DidCommOptions _didComm;
    private readonly MediatorMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly bool _directPush;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cooldown;
    private readonly int _capacity;
    private readonly int _perGrant;
    private readonly object _gate = new();
    // Under _gate: each grant's waiting jobs in arrival order and its running one; the grants that
    // have a waiting job and none running, in the order they are to be served.
    private readonly Dictionary<string, GrantLine> _grants = new(StringComparer.Ordinal);
    private readonly Queue<GrantLine> _ready = new();
    // Under _gate: the grants paused after a deadline, until when (on _time). Entries are added only
    // by a push that held a worker for a whole deadline, at most PushConcurrency per PushTimeout, and
    // expired ones are dropped on each addition, so at most about (CooldownPushTimeouts + 1) x
    // PushConcurrency are ever held.
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);
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
    private long _liveFailedLines;
    private long _schemeLines;
    private long _directoryFaultLines;
    private long _storeFaultLines;
    private long _cooldownLines;

    /// <summary>Create the dispatcher; its workers start here.</summary>
    /// <param name="services">The container, for the directory and the facade, resolved on the first direct push.</param>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="queue">The pickup queue: its live sessions and leases are the ones a live push uses.</param>
    /// <param name="store">The message store: a direct push takes, removes or gives back the record it sends.</param>
    /// <param name="mediation">The grant store, to check the registration again before a push.</param>
    /// <param name="didCommOptions">Supplies the clock the store judges the record by.</param>
    /// <param name="metrics">The push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the per-push deadline.</param>
    public PushDispatcher(
        IServiceProvider services,
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        IMessageStore store,
        IMediationStore mediation,
        IOptions<DidCommOptions> didCommOptions,
        MediatorMetrics metrics,
        ILogger<PushDispatcher>? logger,
        TimeProvider timeProvider)
        : this(services, options, queue, store, mediation, didCommOptions, metrics, logger, timeProvider, capacity: null, perGrant: null)
    {
    }

    /// <summary>Test seam: the dispatcher with explicit bounds.</summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The mediator settings.</param>
    /// <param name="queue">The pickup queue.</param>
    /// <param name="store">The message store.</param>
    /// <param name="mediation">The grant store.</param>
    /// <param name="didCommOptions">Supplies the clock the store judges the record by.</param>
    /// <param name="metrics">The push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the per-push deadline.</param>
    /// <param name="capacity">Jobs waiting or running, at most; <c>null</c> for the queue's store-wide message quota.</param>
    /// <param name="perGrant">Jobs one grant may have waiting or running, at most; <c>null</c> for the queue's per-grant message quota.</param>
    internal PushDispatcher(
        IServiceProvider services,
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        IMessageStore store,
        IMediationStore mediation,
        IOptions<DidCommOptions> didCommOptions,
        MediatorMetrics metrics,
        ILogger<PushDispatcher>? logger,
        TimeProvider timeProvider,
        int? capacity,
        int? perGrant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mediation);
        ArgumentNullException.ThrowIfNull(didCommOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        // Sized from the queue (D1 of ADV-4): a job exists only for a stored message, so the
        // queue's quotas already bound the jobs, and none is refused while the store holds it.
        capacity ??= queue.Coordinator.MaxStoreMessages;
        perGrant ??= queue.Coordinator.MaxQueueMessages;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity.Value, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perGrant.Value, 1);

        // Read once, after validation: a host that changes the options object later changes nothing here.
        var settings = options.Value;
        settings.ValidatePipelineRanges();
        _directPush = settings.DirectPush;
        _timeout = settings.PushTimeout;
        _cooldown = TimeSpan.FromTicks(settings.PushTimeout.Ticks * CooldownPushTimeouts);
        var concurrency = settings.PushConcurrency;

        _services = services;
        _pickup = queue.Coordinator;
        _store = store;
        _mediation = mediation;
        _didComm = didCommOptions.Value;
        _metrics = metrics;
        _logger = logger;
        _time = timeProvider;
        _capacity = capacity.Value;
        _perGrant = perGrant.Value;
        _shutdownToken = _shutdown.Token;

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

    /// <summary>Whether direct push is on: a job not pushed live then goes to the recipient's endpoint.</summary>
    public bool DirectPushEnabled => _directPush;

    /// <summary>Jobs waiting or running, at most: the queue's store-wide message quota unless a test set it.</summary>
    internal int Capacity => _capacity;

    /// <summary>Jobs one grant may have waiting or running, at most: the queue's per-grant message quota unless a test set it.</summary>
    internal int PerGrant => _perGrant;

    /// <summary>
    /// Hand a stored message to the push workers. Never blocks and never throws: when a bound is
    /// reached, or the dispatcher is shutting down, the job is refused and counted, and the message
    /// stays queued.
    /// </summary>
    /// <param name="job">The stored message.</param>
    /// <returns><c>true</c> when a worker will push it.</returns>
    public bool TryEnqueue(PushJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        string refusal;
        var signal = false;
        lock (_gate)
        {
            if (_stopped != 0)
            {
                refusal = PushOutcomes.SkippedShutdown;
            }
            else if (IsCoolingLocked(job.GrantPeerDid))
            {
                refusal = PushOutcomes.SkippedCooldown;
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
                    if (line.Running is null && !line.OnReady)
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
            MediatorLog.Write(_logger, level, "Push of forward {ForwardId} skipped: the push backlog is full; the message stays queued for pickup. {Count} such skips.", job.ForwardId, n);
        }
        else if (refusal == PushOutcomes.SkippedCooldown)
        {
            MediatorLog.Write(_logger, LogLevel.Debug, "Push of forward {ForwardId} skipped: the recipient's pushes are paused after one reached the deadline; the message stays queued for pickup.", job.ForwardId);
        }
        else
        {
            MediatorLog.Write(_logger, LogLevel.Debug, "Push of forward {ForwardId} skipped: the mediator is shutting down; the message stays queued for pickup.", job.ForwardId);
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

    /// <summary>Test seam: the grants held in the cooldown table (expired ones included until the next addition).</summary>
    internal int CooldownEntries
    {
        get
        {
            lock (_gate)
                return _cooldowns.Count;
        }
    }

    /// <summary>
    /// Test seam: the characters held by the jobs waiting or running, the measure of what the
    /// backlog retains after the forwards that produced the jobs gave back their reservations.
    /// </summary>
    internal long RetainedCharacters
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (var line in _grants.Values)
                {
                    foreach (var job in line.Waiting)
                        total += job.RetainedCharacters;
                    total += line.Running?.RetainedCharacters ?? 0;
                }
                return total;
            }
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
                    line.Running = job;
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
            MediatorLog.Write(_logger, LogLevel.Error, "Push worker stopped with {ExceptionType}.", ex.GetType().Name);
        }
    }

    // One job, one outcome: the re-check, then the live push, then the direct push when it is on.
    private async Task PushOneAsync(PushJob job)
    {
        var forwardId = job.ForwardId;
        var stage = Stage.Check;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        using var timer = _time.CreateTimer(static state => CancelQuietly((CancellationTokenSource)state!), deadline, _timeout, Timeout.InfiniteTimeSpan);
        MessageLease? lease = null;
        try
        {
            // The registration may have ended since the message was stored (a remove, a revoke).
            var grant = await BoundedAsync(_mediation.FindGrantByRecipientDidAsync(job.RecipientDid, deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false);
            if (!string.Equals(grant?.PeerDid, job.GrantPeerDid, StringComparison.Ordinal))
            {
                _metrics.RecordPush(PushOutcomes.SkippedUnregistered);
                MediatorLog.Write(_logger, LogLevel.Debug, "Push of forward {ForwardId} skipped: the recipient is no longer registered under the grant.", forwardId);
                return;
            }

            // (1) Live: the peer's live session takes exactly this record, under its own lease.
            stage = Stage.Live;
            var live = await BoundedAsync(_pickup.TryPushLiveAsync(job.GrantPeerDid, job.MessageId, deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false);
            switch (live)
            {
                case LivePushResult.Sent:
                    _metrics.RecordPush(PushOutcomes.PushedLive);
                    MediatorLog.Write(_logger, LogLevel.Debug, "Push of forward {ForwardId}: delivered over the recipient's live connection.", forwardId);
                    return;
                case LivePushResult.NotHeld:
                    _metrics.RecordPush(PushOutcomes.SkippedNotHeld);
                    MediatorLog.Write(_logger, LogLevel.Debug, "Push of forward {ForwardId} skipped: the queue no longer holds the message for delivery (expired, aged out, picked up or being picked up, or removed).", forwardId);
                    return;
            }

            // (2) Direct, when it is on; (3) otherwise the message stays queued for pickup.
            if (!_directPush)
            {
                if (live == LivePushResult.NotSent)
                {
                    _metrics.RecordPush(PushOutcomes.Failed);
                    var level = MediatorLog.RateLimited(ref _liveFailedLines, out var n);
                    MediatorLog.Write(_logger, level, "Live push of forward {ForwardId} not delivered: the recipient's live connection did not take it; the message stays queued for pickup. {Count} such failures.", forwardId, n);
                }
                else
                {
                    _metrics.RecordPush(PushOutcomes.SkippedNoLiveSession);
                    MediatorLog.Write(_logger, LogLevel.Debug, "Push of forward {ForwardId} skipped: the recipient no longer has a live connection with live delivery on; the message stays queued for pickup.", forwardId);
                }
                return;
            }

            // Where to: only the host's directory, never anything the message says (JC-5).
            stage = Stage.Direct;
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

            // Take exactly the record this job names, as the store holds it now (L-057): never an
            // expired, aged-out, not-yet-due, leased, passed-over, acknowledged or purged one.
            var now = _didComm.Now();
            var taking = new MessageLease(Owner: null, ExpiresAt: SaturatingAdd(now, _timeout + LeaseMargin));
            var record = await BoundedAsync(_store.LeaseByIdAsync(job.GrantPeerDid, job.MessageId, taking, now, deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false);
            if (record is null)
            {
                _metrics.RecordPush(PushOutcomes.SkippedNotHeld);
                MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: the queue no longer holds the message for delivery (expired, aged out, picked up or being picked up, or removed).", forwardId);
                return;
            }
            lease = taking;

            var result = await BoundedAsync(Client().SendPackedAsync(record.Payload, endpoint, MediaTypes.Encrypted, SingleAttempt, deadline.Token), deadline.Token).ConfigureAwait(false);
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
            var failedLevel = MediatorLog.RateLimited(ref _failedLines, out var failures);
            MediatorLog.Write(_logger, failedLevel, "Direct push of forward {ForwardId} not delivered: the endpoint did not accept it (HTTP {StatusCode}); the message stays queued for pickup. {Count} such failures.", forwardId, result.HttpStatusCode, failures);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            _metrics.RecordPush(PushOutcomes.SkippedShutdown);
            MediatorLog.Write(_logger, LogLevel.Debug, "Push of forward {ForwardId} abandoned: the mediator is shutting down; the message stays queued for pickup.", forwardId);
        }
        catch (Exception ex)
        {
            // A session, transport or store fault, a non-2xx answer the transport turned into an
            // exception, the deadline. Type name only: a transport error names the URL.
            var reason = deadline.IsCancellationRequested ? "Timeout" : ex.GetType().Name;
            // The peer may control this delay (its resolution, its endpoint): pause the grant so it
            // cannot hold a shared worker push after push. Not for the registration check: that is
            // the host's own store, the same for every grant.
            if (deadline.IsCancellationRequested && !_shutdownToken.IsCancellationRequested && stage != Stage.Check)
                StartCooldown(job, forwardId);
            _metrics.RecordPush(PushOutcomes.Failed);
            var level = MediatorLog.RateLimited(ref _failedLines, out var n);
            switch (stage)
            {
                case Stage.Live:
                    MediatorLog.Write(_logger, level, "Live push of forward {ForwardId} failed ({ExceptionType}); the message stays queued for pickup. {Count} such failures.", forwardId, reason, n);
                    break;
                case Stage.Direct:
                    MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} failed ({ExceptionType}); the message stays queued for pickup. {Count} such failures.", forwardId, reason, n);
                    break;
                default:
                    MediatorLog.Write(_logger, level, "Push of forward {ForwardId} failed ({ExceptionType}) checking the registration; the message stays queued for pickup. {Count} such failures.", forwardId, reason, n);
                    break;
            }
        }
        finally
        {
            // A direct push that did not deliver: give the record back at once so a pickup gets it
            // now, not when the lease ends. (A live push gives its own lease back.)
            if (lease is not null)
                await ReleaseQuietlyAsync(job, lease, forwardId).ConfigureAwait(false);
        }
    }

    // Under _gate: whether the grant is paused now; an entry that has run out is dropped.
    private bool IsCoolingLocked(string grantPeerDid)
    {
        if (!_cooldowns.TryGetValue(grantPeerDid, out var until))
            return false;
        if (_time.GetUtcNow() < until)
            return true;
        _cooldowns.Remove(grantPeerDid);
        return false;
    }

    // A push of this grant reached the deadline: pause the grant and end its waiting jobs now, so none
    // of them takes a worker before the cooldown ends. The running job is the caller's; it ends
    // through Finish as usual.
    private void StartCooldown(PushJob job, string forwardId)
    {
        var discarded = 0;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var (grant, until) in _cooldowns.ToArray())
            {
                if (until <= now)
                    _cooldowns.Remove(grant);
            }
            _cooldowns[job.GrantPeerDid] = SaturatingAdd(now, _cooldown);
            if (_grants.TryGetValue(job.GrantPeerDid, out var line))
            {
                discarded = line.Waiting.Count;
                line.Waiting.Clear();
                _outstanding -= discarded;
            }
        }

        for (var i = 0; i < discarded; i++)
            _metrics.RecordPush(PushOutcomes.SkippedCooldown);
        var level = MediatorLog.RateLimited(ref _cooldownLines, out var n);
        MediatorLog.Write(_logger, level, "Pushes paused for {Cooldown} for the recipient of forward {ForwardId}: its push reached the deadline. {Discarded} waiting pushes skipped; those messages, and any stored during the pause, stay queued for pickup. {Count} such pauses.", _cooldown, forwardId, discarded, n);
    }

    // Which step of a job was running, for its failure line.
    private enum Stage
    {
        Check,
        Live,
        Direct,
    }

    // After a delivered push: remove exactly the record that was sent. A failure leaves it under the
    // push's lease until that ends; then a pickup may deliver it again (delivery is at-least-once).
    private async Task<bool> RemoveQuietlyAsync(PushJob job, string forwardId, CancellationToken ct)
    {
        try
        {
            return await BoundedAsync(_store.AcknowledgeAsync(job.GrantPeerDid, new[] { job.MessageId }, ct).AsTask(), ct).ConfigureAwait(false) > 0;
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
            await BoundedAsync(_store.ReleaseAsync(job.GrantPeerDid, new[] { job.MessageId }, lease.Id, bound.Token).AsTask(), bound.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var level = MediatorLog.RateLimited(ref _storeFaultLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId}: giving the queued message back failed with {ExceptionType}; it returns to pickup when the push's lease ends. {Count} such faults.", forwardId, ex.GetType().Name, n);
        }
    }

    // The deadline ends the wait whether or not the callee honours its token (L-059): a session, a
    // transport or a store that ignores cancellation must not hold a worker, and its grant's turn,
    // past the deadline. The abandoned call goes on by itself; its result is ignored and its fault
    // observed.
    private static async Task<T> BoundedAsync<T>(Task<T> work, CancellationToken token)
    {
        try
        {
            return await work.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private static DateTimeOffset SaturatingAdd(DateTimeOffset instant, TimeSpan span)
        => span.Ticks >= (DateTimeOffset.MaxValue - instant).Ticks ? DateTimeOffset.MaxValue : instant + span;

    // Resolved on the first direct push and kept (L-017): never at construction. A directory that
    // is not registered is remembered as absent.
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
            line.Running = null;
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
                if (line.Running is null)
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
            MediatorLog.Write(_logger, LogLevel.Information, "Pushes stopped: {Discarded} waiting pushes discarded; their messages stay queued for pickup.", discarded);
    }

    // One grant's line, touched only under _gate.
    private sealed class GrantLine(string grantPeerDid)
    {
        public string GrantPeerDid { get; } = grantPeerDid;

        /// <summary>The grant's waiting jobs, oldest first.</summary>
        public Queue<PushJob> Waiting { get; } = new();

        /// <summary>The grant's push that is running, if any.</summary>
        public PushJob? Running { get; set; }

        /// <summary>Whether the grant is on the ready line.</summary>
        public bool OnReady { get; set; }

        /// <summary>The grant's jobs waiting or running, against <see cref="PerGrant"/>.</summary>
        public int Count => Waiting.Count + (Running is null ? 0 : 1);
    }

    /// <summary>Stop admission, cancel the running pushes and discard the waiting ones; does not wait.</summary>
    public void Dispose() => Stop();

    /// <summary>Stop as <see cref="Dispose"/> does, then wait up to five seconds for the workers to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(ShutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            MediatorLog.Write(_logger, LogLevel.Warning, "Push workers did not stop within {Grace}; their messages stay queued for pickup.", ShutdownGrace);
        }
        catch
        {
            // The workers never fault (they log and stop); nothing to add here.
        }
    }
}
