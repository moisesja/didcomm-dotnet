using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// Pushes stored messages to their recipients on a few background workers, after the forward pump
/// has stored them, never on the request or on the pump (FR-PICK-08/09, D6, JC-4): over the
/// recipient's live connection when it has one, and otherwise, with
/// <see cref="MediatorOptions.DirectPush"/> on, to the endpoint the host registered for it. It runs
/// whenever <c>AddMediator()</c> is used.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The store decides what is pushed, live or direct (D-1).</strong> The dispatcher keeps no
/// per-message state. The forward sink stores a message and wakes its grant (<see cref="Wake"/>); an
/// operation for that grant then takes, one at a time, the next message the store hands out at that
/// moment (<see cref="PickupCoordinator.TakeNextAsync"/>: due, unexpired, unleased, not passed over,
/// not already taken by an earlier push, oldest first, its recipient DID still registered under the
/// grant) and pushes it. A message that expired, was acknowledged, purged or picked up is therefore
/// never pushed and never occupies anything.
/// </para>
/// <para>
/// <strong>In the delivery order (D6): live first.</strong> Every push looks for the grant's live
/// session first. While the grant's peer has a live session with live delivery on, the messages
/// that arrived after it turned live delivery on go over that session
/// (<see cref="PickupCoordinator.PushNextLiveAsync"/>, <c>Pushed-Live</c>): each stays leased to the
/// session until <c>messages-received</c>, its close or the lease timeout — after a close or a
/// timeout it waits in the queue for a <c>delivery-request</c> and is never pushed again (owner
/// decision on #100; Message Pickup 4.0 L304, L308; <see cref="QueuedMessage.PushTaken"/>);
/// one too large for the session's envelope is passed over, as a pickup does; one the session does
/// not take is given back and ends the operation. "Arrived after" is arrival order, not time: the store numbers every record
/// as it stores it (<see cref="QueuedMessage.Sequence"/>), and the session's mark is the highest
/// number in the status that answered its turning live delivery on — so a message is either counted
/// in that status, and the recipient's to pick up (FR-PICK-06), or pushed, whatever the clocks do.
/// Turning live delivery on wakes the grant (<see cref="PickupCoordinator.LiveDeliveryStarted"/>),
/// for a message that arrived after that status but before the session was live.
/// </para>
/// <para>
/// <strong>Then, with direct push on, the recipient's own endpoint (D6 step 3).</strong> Only a push
/// that finds no live session goes on, under the same deadline, to the endpoint the host's
/// <see cref="IRecipientEndpointDirectory"/> names for the grant
/// (<see cref="PickupCoordinator.PushNextDirectAsync"/>). It takes the grant's next message, whenever
/// it arrived, and sends it once: a 2xx removes it (<c>Pushed-Direct</c>, and the operation goes on to
/// the next message). A direct push that does not deliver leaves its message queued for the
/// recipient's next <c>delivery-request</c> (<c>Push-Failed</c>), never to be pushed again; what
/// happens next depends on the answer. A 4xx other than 408 and 429 — the endpoint's definite "not
/// this message" — moves on to the next message with no pause, so messages anyone stored ahead of a
/// real one cannot hold it back (ADV-110-1a-4); an operation that met one and delivered nothing
/// ends at its <see cref="MessagePickupOptions.MaxDeliveryBatch"/> bound without the yield, so a real
/// message waits behind refused ones only when a whole run of them delivers nothing (more than
/// <see cref="MessagePickupOptions.MaxDeliveryBatch"/> in a row): then until the next wake-up, or
/// for pickup. Anything else —
/// a 5xx, 408 or 429 (which say "later"), a 3xx that is not followed, a 307 chain past its bound, no
/// answer, a fault of the send (the transport's or the client's own timeout included), a fault of
/// the step, or the push deadline — ends the operation, with no yield, and pauses that recipient's
/// direct pushes for <see cref="CooldownPushTimeouts"/> × <see cref="MediatorOptions.PushTimeout"/>
/// on the monotonic clock. While the pause runs, a wake-up still runs the live step, so a live
/// session that appears meanwhile gets its pushes; only the direct step is skipped
/// (<c>Push-Skipped-Cooldown</c>, once per operation). So a message that failed this way never
/// holds up later ones beyond the pause and is never sent again: the messages behind it go at the
/// first wake-up after the pause, until the next push that fails (one run per pause: under an
/// endpoint that keeps failing, part of them go only by pickup).
/// A grant the directory has no usable endpoint for ends the operation
/// with nothing taken (<c>Push-Skipped-NoEndpoint</c>, counted on an operation's first push only).
/// A message that expired between its take and its send is given back unsent and never sent, and
/// ends the operation (no pause). A
/// session that turns live delivery on while the directory answers wins: the direct push takes
/// nothing and the next push goes live; one that turns it on after the direct push's take finds that
/// push in flight (at most a duplicate delivery). A
/// session that closes during an operation hands the rest of it to direct push; what was pushed live
/// is not pushed again. With direct push off, a message for a recipient without a live session waits
/// for pickup (<c>Push-Skipped-NoLiveSession</c>).
/// </para>
/// <para>
/// <strong>At most one push attempt per message, kept on the record (D-6).</strong> The store marks a
/// record as taken by a push, live or direct, in the same step as the take; the attempt is spent the
/// moment the mediator hands the message to the session or the transport, and only a definite
/// "not sent" clears the mark. A marked record is for a <c>delivery-request</c> only. The progress is
/// on the record, so it survives a lease's end, a host store's own caching, any number of grants and
/// a restart; nothing here remembers it. A store that loses the mark loses that guarantee; what
/// still bounds it is the operation's bound and the direct pause above: a record the endpoint
/// refuses with a 4xx is sent up to 2 × <see cref="MessagePickupOptions.MaxDeliveryBatch"/> − 1 times
/// per wake-up (a run that delivered something ahead of it yields once more; the run after delivers
/// nothing, so it does not), one that fails otherwise at most once per pause, and a store that judges
/// expiry by a clock of its own costs at most one take per wake-up.
/// </para>
/// <para>
/// <strong>Where and how a direct push goes (D-7).</strong> Only to the endpoint the directory returns
/// for the grant, read before the take; never to an address a message carries. Only absolute
/// <c>http</c> or <c>https</c>, accepted by the HTTP transport and passed by the outbound endpoint
/// guard before the take and on every connection. One send on the mediator's own instance of the
/// HTTP transport: no retry, no circuit breaker shared with anyone, a 307 followed up to the
/// transport's <c>MaxRedirectHops</c> with every hop checked, and no other redirect followed (owner
/// decision OD-1 on #110). On a host whose own connection code replaced the library's (a connect
/// callback that checks nothing, or a primary handler of its own), the check before the take is a
/// name check only: the name is looked up again at connect, so the host's connection code must check
/// addresses at connect itself (owner decisions OD-5 and OD-7 on #110; residual 5).
/// </para>
/// <para>
/// <strong>Retries.</strong> A live push the session answered "not sent" for (<c>NotSent</c>: no
/// free push slot, downgraded or closing; or too large, then passed over), or that never got to the
/// session (the deadline, a closed session or a fault before the send), is given back as unsent and
/// tried again live at the grant's next wake-up. One that may have reached the connection — the
/// session took it, or the send faulted, was cancelled or reached its deadline once the session had
/// it — counts as pushed and is not pushed again: it waits for the recipient's next
/// <c>delivery-request</c> (owner decision 2026-11-21 on #100, OD-5). That is at most one live attempt per
/// wake-up (the operation ends there, and a deadline also pauses the grant), a wake-up is a stored message or a
/// session turning live delivery on, and nothing is written to the connection unless the session
/// takes it — so it cannot multiply the traffic of what was forwarded. One too large for the session
/// is passed over, for a period that doubles each time. A direct push is never retried: a message
/// whose send was attempted and not delivered is delivered by pickup (owner decision OD-2 on #110),
/// and one whose send never started (no endpoint, the deadline or a fault before the send) is given
/// back as unsent. Pickup always works. Delivery is at-least-once.
/// </para>
/// <para>
/// <strong>A push names what it took (D-5).</strong> Every send is preceded by a lease on exactly
/// that record and followed, once, by its release, its pass-over, its removal (a direct push
/// answered 2xx) or the session's lease running on.
/// </para>
/// <para>
/// <strong>Wake-ups, one operation per grant (D-2, D-3).</strong> <see cref="Wake"/> is O(1) and
/// never blocks; wake-ups coalesce into one flag per grant, and carry nothing but the grant's DID. The flag is consumed before the
/// operation reads the store, so a wake-up that arrives during an operation is served by another one
/// right after it. A grant has at most one operation from its start until the work it started has
/// actually ended — not until its deadline — keyed by grant, so a new session does not reset it, and
/// at most one send of the grant (a live push or a direct POST) is in flight at a time.
/// Grants are served in turn; an operation pushes at most
/// <see cref="MessagePickupOptions.MaxDeliveryBatch"/> messages, then yields its worker and its grant
/// takes its turn again, so a busy grant cannot monopolise a worker.
/// </para>
/// <para>
/// <strong>Deadline and cooldown.</strong> Each push, live and direct together, has a deadline,
/// <see cref="MediatorOptions.PushTimeout"/>, on a timer whose callback cancels inside a
/// <c>try</c> (never <c>CancelAfter</c>). At the deadline the worker stops waiting and serves other
/// grants; the push counts as <c>Push-Failed</c>. A direct push's deadline starts the direct pause
/// above (its live pushes go on). A live push's deadline pauses the whole grant for
/// <see cref="CooldownPushTimeouts"/> × <see cref="MediatorOptions.PushTimeout"/> (one minute at the
/// defaults), measured on the monotonic clock (<see cref="TimeProvider.GetTimestamp"/>), so a step of
/// the wall clock neither lengthens nor shortens it: wake-ups meanwhile are skipped
/// (<c>Push-Skipped-Cooldown</c>), and their messages wait for pickup. The abandoned push still ends
/// its lease exactly once when it ends, and its grant is not served again before that. A session
/// that ignores cancellation and later reports the message sent keeps it leased, as for any live
/// push (at-least-once). When a stalled push does end at its deadline (or a direct one at the
/// transport's or the client's own timeout), a peer that stalls on purpose
/// (a session, or an endpoint that never answers) holds a worker at most once per pause: with K grants that stall every push this way and W
/// workers, a healthy recipient's push can wait up to about ceil(K / W) ×
/// <see cref="MediatorOptions.PushTimeout"/>, and once K reaches 3 × W that wait recurs; nothing is
/// lost, pickup always works, and a mediation policy that enrols only known peers closes it. A push
/// that does not end at its deadline is the next paragraph's case.
/// </para>
/// <para>
/// <strong>Bounded (D-4).</strong> <see cref="MediatorOptions.PushConcurrency"/> workers; at most
/// <see cref="UnsettledPerWorker"/> × <see cref="MediatorOptions.PushConcurrency"/> operations
/// between their start and the end of the work they started (abandoned ones included). When that
/// many exist, a wake-up starts nothing (<c>Push-Skipped-Saturated</c>) and its messages wait for
/// pickup or the grant's next wake-up. A host collaborator — a return-route session, the DID
/// resolver, the store or the endpoint directory — that never completes a call after it was cancelled keeps that call's
/// operation, and its grant, unsettled until it does complete;
/// <see cref="UnsettledPerWorker"/> × <see cref="MediatorOptions.PushConcurrency"/> such calls hold
/// every operation there is, and then no push starts, for any recipient, until one of them
/// completes. Pickup keeps working throughout. A name lookup the endpoint check started is not
/// counted there: at the push's deadline the check stops waiting for it, the operation settles and
/// the recipient's direct pushes pause, while the lookup may run on until the system's resolver gives
/// up. Since each worker starts at most
/// one push per <see cref="MediatorOptions.PushTimeout"/>, at most about
/// <see cref="MediatorOptions.PushConcurrency"/> × ceil(resolver timeout ÷
/// <see cref="MediatorOptions.PushTimeout"/>) such lookups run at once (ADV-110-3a-2). Each operation holds at most one record at a time. The
/// dispatcher holds one small entry per grant that is woken, busy, cooling down or whose direct
/// pushes are paused: its DID (at most 2048 characters), never anything of a message. An entry is
/// dropped when its grant is none of these and is next looked at: when its operation ends, when
/// the grant is woken again, when a live push's deadline starts a cooldown (which looks at every
/// entry), or — for an entry whose direct pause has run out, such as one a revoked grant left
/// behind, which is never woken again — at the next direct pause that starts or the next worker
/// that finishes, which look again at the pauses that have run out, oldest first. So the entries
/// are bounded by the grants woken, busy or cooling now plus the recipients whose direct pushes
/// were paused within the last pause length, and nothing is left of a pause once it has run out
/// and the dispatcher next does any work.
/// </para>
/// <para>
/// <strong>Dependencies (L-017).</strong> The pickup queue, the counter and the settings; with direct
/// push on, the host's endpoint directory and the direct-push sender, which is built on the first
/// direct push (in a generic host, at start-up). A sender that cannot be built fails that push
/// (<c>Push-Failed</c>), never a worker.
/// </para>
/// <para>
/// <strong>Logging.</strong> Outcome names, counts and exception type names; never a DID, a queue id,
/// an endpoint or an exception message. Failures are rate-limited. A failed live push writes one
/// line (the dispatcher's). A direct push the endpoint refused with a 4xx (other than 408 and 429)
/// writes one: the push step's (nothing is paused). One that failed otherwise at the endpoint, or
/// whose send failed, writes two on purpose: the push step's, which says why it did not deliver,
/// and the dispatcher's, which says what it paused. A direct push that reached its deadline, or that a fault the step let through
/// ended (a store fault, an endpoint check that threw, a sender that could not be built), writes
/// one: the dispatcher's, with the reason (<c>Timeout</c>, or the fault's exception type) and the
/// pause.
/// </para>
/// <para>
/// <strong>Shutdown.</strong> Disposing stops admission, cancels the running pushes (their leases are
/// given back) and drops the waiting wake-ups (<c>Push-Skipped-Shutdown</c>); every message stays
/// queued. <see cref="DisposeAsync"/> also waits up to five seconds for the workers.
/// </para>
/// </remarks>
internal sealed class PushDispatcher : IAsyncDisposable, IDisposable
{
    /// <summary>How many push timeouts a grant whose live push reached the deadline is paused for, and a recipient's direct pushes after one did not deliver.</summary>
    internal const int CooldownPushTimeouts = 2;

    /// <summary>Operations allowed between start and settlement, per worker.</summary>
    internal const int UnsettledPerWorker = 2;

    private static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly PickupCoordinator _pickup;
    private readonly MediatorMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cooldown;
    private readonly int _perRun;
    private readonly int _maxUnsettled;
    private readonly TimeSpan _shutdownGrace;
    private readonly DirectPushRoute? _direct;
    private readonly object _gate = new();
    // Under _gate: one entry per grant that is woken, busy or cooling down; the woken grants waiting
    // for a worker, in the order they are to be served.
    private readonly Dictionary<string, GrantState> _grants = new(StringComparer.Ordinal);
    private readonly Queue<GrantState> _ready = new();
    // Under _gate: every direct pause started, oldest first, with the timestamp it started at. It
    // decides only WHEN an entry is looked at again once its pause has run out (a revoked grant is
    // never woken again, so its entry would otherwise stay for the life of the process); whether a
    // recipient is paused is always read from its own entry (IsDirectPausedLocked). Each start is
    // queued once and dropped once: amortized O(1) per pause start and per worker finish (QA4-3).
    private readonly Queue<(GrantState Grant, long Since)> _pauseStarts = new();
    // One count per entry put on _ready; a worker takes one, then a grant off _ready.
    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Task[] _workers;
    private TaskCompletionSource _idle = NewCompletedSignal();
    private int _pending;
    private int _unsettled;
    private int _stopped;
    private long _saturatedLines;
    private long _failedLines;
    private long _directFailedLines;
    private long _cooldownLines;

    /// <summary>Create the dispatcher; its workers start here.</summary>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="queue">The pickup queue: its store view, live sessions and leases are the ones a push uses.</param>
    /// <param name="metrics">The push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the deadlines and the cooldown (its monotonic timestamps).</param>
    public PushDispatcher(
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        MediatorMetrics metrics,
        ILogger<PushDispatcher>? logger,
        TimeProvider timeProvider)
        : this(options, queue, metrics, logger, timeProvider, shutdownGrace: null) { }

    /// <summary>Test seam: a dispatcher with an explicit shutdown grace.</summary>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="queue">The pickup queue: its store view, live sessions and leases are the ones a push uses.</param>
    /// <param name="metrics">The push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the deadlines and the cooldown (its monotonic timestamps).</param>
    /// <param name="shutdownGrace">How long <see cref="DisposeAsync"/> waits for the workers; <c>null</c> for 5 seconds, <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shutdownGrace"/> is zero or negative (and not <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    internal PushDispatcher(
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        MediatorMetrics metrics,
        ILogger<PushDispatcher>? logger,
        TimeProvider timeProvider,
        TimeSpan? shutdownGrace)
        : this(options, queue, metrics, logger, timeProvider, shutdownGrace, direct: null) { }

    /// <summary>
    /// A dispatcher that, with <paramref name="direct"/> given, pushes a grant that has no live
    /// session to the endpoint the host's directory names for it (direct push, D6 step 3).
    /// <c>AddMediator()</c> builds it this way when <see cref="MediatorOptions.DirectPush"/> is on.
    /// </summary>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="queue">The pickup queue: its store view, live sessions and leases are the ones a push uses.</param>
    /// <param name="metrics">The push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the deadlines and the cooldown (its monotonic timestamps).</param>
    /// <param name="shutdownGrace">How long <see cref="DisposeAsync"/> waits for the workers; <c>null</c> for 5 seconds, <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
    /// <param name="direct">The host's endpoint directory and the direct-push sender; <c>null</c> for live pushes only.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shutdownGrace"/> is zero or negative (and not <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    /// <exception cref="ArgumentException">A pipeline setting is out of range.</exception>
    internal PushDispatcher(
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        MediatorMetrics metrics,
        ILogger<PushDispatcher>? logger,
        TimeProvider timeProvider,
        TimeSpan? shutdownGrace,
        DirectPushRoute? direct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _shutdownGrace = shutdownGrace ?? DefaultShutdownGrace;
        if (_shutdownGrace <= TimeSpan.Zero && _shutdownGrace != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(shutdownGrace));

        // Read once, after validation: a host that changes the options object later changes nothing here.
        var settings = options.Value;
        settings.ValidatePipelineRanges();
        _timeout = settings.PushTimeout;
        _cooldown = TimeSpan.FromTicks(settings.PushTimeout.Ticks * CooldownPushTimeouts);
        _maxUnsettled = UnsettledPerWorker * settings.PushConcurrency;

        _pickup = queue.Coordinator;
        _perRun = _pickup.MaxDeliveryBatch;
        _metrics = metrics;
        _logger = logger;
        _time = timeProvider;
        _direct = direct;
        _shutdownToken = _shutdown.Token;
        _pickup.LiveDeliveryStarted += OnLiveDeliveryStarted;

        // The workers must not carry the context of whoever happened to build this singleton (a
        // request's activity, its async-locals): start them with the flow suppressed.
        _workers = new Task[settings.PushConcurrency];
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

    /// <summary>Operations allowed between start and settlement.</summary>
    internal int MaxUnsettled => _maxUnsettled;

    /// <summary>How long <see cref="DisposeAsync"/> waits for the workers.</summary>
    internal TimeSpan ShutdownGrace => _shutdownGrace;

    /// <summary>Pushes one operation makes before it yields its worker.</summary>
    internal int PerRun => _perRun;

    /// <summary>
    /// Whether a grant with no live session is pushed too (direct push is on): the forward sink then
    /// wakes a due message's grant whether or not it has a live session.
    /// </summary>
    internal bool PushesWithoutLiveSession => _direct is not null;

    /// <summary>
    /// Wake <paramref name="grantPeerDid"/>'s grant: something may be due for it. Never blocks and
    /// never throws for a valid DID; wake-ups coalesce, and one that arrives during the grant's
    /// operation is served right after it.
    /// </summary>
    /// <param name="grantPeerDid">The grant's peer DID: a bare DID of at most 2048 characters.</param>
    /// <returns><c>true</c> when the wake-up will be served; <c>false</c> when it was skipped (cooldown, shutdown) and counted.</returns>
    /// <exception cref="ArgumentException"><paramref name="grantPeerDid"/> is longer than 2048 characters.</exception>
    public bool Wake(string grantPeerDid)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        if (grantPeerDid.Length > Consistency.IdentifierLimits.MaxDidLength)
            throw new ArgumentException("A grant is named by a bare DID of at most 2048 characters.", nameof(grantPeerDid));

        string? skipped = null;
        var signal = false;
        lock (_gate)
        {
            if (_stopped != 0)
            {
                skipped = PushOutcomes.SkippedShutdown;
            }
            else
            {
                if (!_grants.TryGetValue(grantPeerDid, out var grant))
                    _grants.Add(grantPeerDid, grant = new GrantState(grantPeerDid));
                if (IsCoolingLocked(grant))
                {
                    skipped = PushOutcomes.SkippedCooldown;
                }
                else
                {
                    grant.Woken = true;
                    signal = EnqueueLocked(grant);
                }
            }
        }

        if (signal)
            _readySignal.Release();
        if (skipped is null)
            return true;
        _metrics.RecordPush(skipped);
        return false;
    }

    // A session turned live delivery on: a message that arrived after the status it was answered
    // with, while no session was live, is pushed now (a wake-up like any other).
    private void OnLiveDeliveryStarted(string grantPeerDid) => Wake(grantPeerDid);

    /// <summary>Test seam: complete when no wake-up waits and no worker serves a grant (abandoned work may still run).</summary>
    /// <param name="timeout">How long to wait.</param>
    internal Task WaitForIdleAsync(TimeSpan timeout)
    {
        Task idle;
        lock (_gate)
            idle = _idle.Task;
        return idle.WaitAsync(timeout);
    }

    /// <summary>Operations started and not yet settled, abandoned ones included.</summary>
    internal int Unsettled
    {
        get
        {
            lock (_gate)
                return _unsettled;
        }
    }

    /// <summary>Test seam: the characters the dispatcher's per-grant entries hold (their DIDs).</summary>
    internal long RetainedCharacters
    {
        get
        {
            lock (_gate)
                return _grants.Keys.Sum(key => (long)key.Length);
        }
    }

    /// <summary>Test seam: the direct pause starts not yet looked at again.</summary>
    internal int PauseStarts
    {
        get
        {
            lock (_gate)
                return _pauseStarts.Count;
        }
    }

    /// <summary>Test seam: the per-grant entries held (woken, busy or cooling).</summary>
    internal int GrantEntries
    {
        get
        {
            lock (_gate)
                return _grants.Count;
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _readySignal.WaitAsync(_shutdownToken).ConfigureAwait(false);

                GrantState grant;
                var saturated = false;
                lock (_gate)
                {
                    if (!_ready.TryDequeue(out var next))
                        continue; // the waiting wake-ups were dropped by a shutdown
                    grant = next;
                    grant.Queued = false;
                    // Consumed before the store is read: a wake-up from here on is served after this.
                    grant.Woken = false;
                    if (_unsettled >= _maxUnsettled)
                    {
                        saturated = true;
                    }
                    else
                    {
                        grant.Busy = true;
                        _unsettled++;
                    }
                }

                if (saturated)
                {
                    _metrics.RecordPush(PushOutcomes.SkippedSaturated);
                    var level = MediatorLog.RateLimited(ref _saturatedLines, out var n);
                    MediatorLog.Write(_logger, level, "Push skipped: {Unsettled} push operations have not ended yet; the messages stay queued for pickup and for the recipient's next wake-up. {Count} such skips.", _maxUnsettled, n);
                    Served(grant);
                    continue;
                }

                try
                {
                    await ServeAsync(grant).ConfigureAwait(false);
                }
                finally
                {
                    Served(grant);
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

    // One operation: pushes, one record at a time, each under its own deadline, at most _perRun of
    // them. Each push goes live first, and only when the grant has no live session, direct (with a
    // route). The grant settles here, or — when a push is abandoned at its deadline — when that
    // push's work actually ends.
    private async Task ServeAsync(GrantState grant)
    {
        var attempted = false;
        var more = false;
        // A run that met a refusal and delivered nothing ends at its bound without the yield: a store
        // that drops the push mark hands back the same refused record (nothing is delivered after it),
        // so it costs at most 2 × _perRun − 1 refused sends per wake-up (a run that delivered ahead of
        // it yields once more), never a loop (ADV-110-3a-3). A run that delivered
        // something yields as usual, so one refused message cannot hold a backlog back (ADV-110-2b-1,
        // the coordinator's call under the churn rule).
        var failed = false;
        var deliveredAny = false;
        Task? abandoned = null;
        try
        {
            for (var pushes = 0; ; )
            {
                if (pushes >= _perRun)
                {
                    more = !failed || deliveredAny; // yield the worker; the grant takes its turn again
                    return;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                using var timer = _time.CreateTimer(static state => CancelQuietly((CancellationTokenSource)state!), deadline, _timeout, Timeout.InfiniteTimeSpan);
                Task work = Task.CompletedTask;
                var direct = false;
                var directPaused = false;
                var live = LiveStep.Empty;
                var directStep = DirectStep.Empty;
                try
                {
                    var liveWork = _pickup.PushNextLiveAsync(grant.GrantPeerDid, deadline.Token).AsTask();
                    work = liveWork;
                    live = await liveWork.WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (live == LiveStep.NoSession && _direct is { } route)
                    {
                        // D6 step 3: no live session, so the recipient's own endpoint, under the same
                        // deadline — unless its direct pushes are paused (only those: the live step
                        // above ran as always).
                        directPaused = IsDirectPaused(grant);
                        if (!directPaused)
                        {
                            direct = true;
                            var directWork = PushDirectAsync(route, grant.GrantPeerDid, deadline.Token);
                            work = directWork;
                            directStep = await directWork.WaitAsync(deadline.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    if (!work.IsCompleted)
                        abandoned = work;
                    if (_shutdownToken.IsCancellationRequested)
                    {
                        _metrics.RecordPush(PushOutcomes.SkippedShutdown);
                        return;
                    }
                    _metrics.RecordPush(PushOutcomes.Failed);
                    // A direct push's deadline pauses its recipient's direct pushes only; a live
                    // push's pauses the grant.
                    if (direct)
                    {
                        StartDirectPause(grant, "Timeout");
                    }
                    else
                    {
                        Failed("Timeout");
                        StartCooldown(grant);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    _metrics.RecordPush(PushOutcomes.Failed);
                    if (direct)
                        StartDirectPause(grant, ex.GetType().Name);
                    else
                        Failed(ex.GetType().Name);
                    return;
                }

                if (directPaused)
                {
                    // Once per operation: the operation ends here.
                    _metrics.RecordPush(PushOutcomes.SkippedCooldown);
                    return;
                }
                failed |= direct && directStep == DirectStep.Refused;
                deliveredAny |= direct && directStep == DirectStep.Delivered;
                if (!(direct ? RecordDirect(grant, directStep, attempted) : RecordLive(live, attempted)))
                    return;
                attempted = true;
                pushes++;
            }
        }
        finally
        {
            if (abandoned is null)
                Settle(grant, more);
            else
                _ = abandoned.ContinueWith(static (t, state) => { _ = t.Exception; ((Action)state!)(); }, (Action)(() => Settle(grant, more: false)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // The sender is built on the first direct push (in a generic host, at start-up): one that cannot
    // be built (no HTTP transport, a bad trusted proxy) faults this push, inside the task, never the worker.
    private async Task<DirectStep> PushDirectAsync(DirectPushRoute route, string grantPeerDid, CancellationToken ct)
        => await _pickup.PushNextDirectAsync(grantPeerDid, route.Directory, route.Sender.Value, ct).ConfigureAwait(false);

    // Count a live step; true when the operation goes on to the next record.
    private bool RecordLive(LiveStep step, bool attempted)
    {
        switch (step)
        {
            case LiveStep.Sent:
                _metrics.RecordPush(PushOutcomes.PushedLive);
                return true;
            case LiveStep.Removed:
                _metrics.RecordPush(PushOutcomes.SkippedUnregistered);
                return true;
            case LiveStep.PassedOver:
                _metrics.RecordPush(PushOutcomes.Failed);
                return true;
            case LiveStep.NotSent:
                _metrics.RecordPush(PushOutcomes.Failed);
                Failed("NotDelivered");
                return false;
            case LiveStep.NoSession:
                // Only with direct push off: with it on, a grant without a live session goes direct.
                if (!attempted)
                    _metrics.RecordPush(PushOutcomes.SkippedNoLiveSession);
                return false;
            default: // Empty: nothing left to push, which is no outcome (a coalesced wake-up
                     // often finds its message already pushed, or taken by a pickup).
                return false;
        }
    }

    // Count a direct step; true when the operation goes on to the next record.
    private bool RecordDirect(GrantState grant, DirectStep step, bool attempted)
    {
        switch (step)
        {
            case DirectStep.Delivered:
                _metrics.RecordPush(PushOutcomes.PushedDirect);
                return true;
            case DirectStep.NotDelivered:
                // The attempt is spent and kept on the record. The run ends, with no yield, and the
                // recipient's direct pushes pause: the messages behind it go at the first wake-up
                // after the pause (or by pickup), and a store that lost the mark gets the refused one
                // sent at most once per pause. The step logged why (the refusal or the send's fault).
                _metrics.RecordPush(PushOutcomes.Failed);
                StartDirectPause(grant, reason: null);
                return false;
            case DirectStep.Refused:
                // The endpoint's definite "not this message" (a 4xx other than 408 and 429): nothing
                // says it is unwell, so no pause, and the next record goes on at once — so messages a
                // third party stored ahead of a real one cannot hold it back (ADV-110-1a-4). The
                // attempt is spent and kept on the record. The step logged it.
                _metrics.RecordPush(PushOutcomes.Failed);
                return true;
            case DirectStep.Removed:
                _metrics.RecordPush(PushOutcomes.SkippedUnregistered);
                return true;
            case DirectStep.Expired:
                // Given back unsent, never sent. The run ends (no pause: nothing was sent), so a store
                // that judges expiry by a clock of its own costs at most one take per wake-up. No outcome.
                return false;
            case DirectStep.EndpointRefused:
                // The outbound address check refused where the endpoint points: no POST and nothing
                // taken, but as for a failed send the run ends and the recipient's direct pushes
                // pause, so an endpoint that is refused (or whose name lookup stalls) costs at most
                // one check per pause. The step logged the reason.
                _metrics.RecordPush(PushOutcomes.Failed);
                StartDirectPause(grant, reason: null);
                return false;
            case DirectStep.NoEndpoint:
                if (!attempted)
                    _metrics.RecordPush(PushOutcomes.SkippedNoEndpoint);
                return false;
            case DirectStep.LiveSession:
                // A live session showed up while the directory answered (one turned live delivery on,
                // or a downgraded one resumed, which wakes nothing): nothing was taken, and the next
                // push goes live, in this operation (D6). No outcome; it counts as one of the run's pushes.
                return true;
            default: // Empty: as for a live push, no outcome.
                return false;
        }
    }

    // One line per failed live push. A failed direct push is logged with the pause it starts.
    private void Failed(string reason)
    {
        var level = MediatorLog.RateLimited(ref _failedLines, out var n);
        MediatorLog.Write(_logger, level, "Live push not delivered ({Reason}); the message stays queued for pickup, and is pushed again at the recipient's next wake-up unless it may have reached the connection. {Count} such failures.", reason, n);
    }

    // Under _gate. On the monotonic clock: a step of the wall clock does not move it.
    private bool IsCoolingLocked(GrantState grant)
        => grant.CoolingSince is { } since && _time.GetElapsedTime(since) < _cooldown;

    // Under _gate. The same clock and length, for the recipient's direct pushes only.
    private bool IsDirectPausedLocked(GrantState grant)
        => grant.DirectPausedSince is { } since && _time.GetElapsedTime(since) < _cooldown;

    private bool IsDirectPaused(GrantState grant)
    {
        lock (_gate)
            return IsDirectPausedLocked(grant);
    }

    // Under _gate: put a woken, idle grant in line; false when it is already in line or busy (it is
    // served after its operation settles).
    private bool EnqueueLocked(GrantState grant)
    {
        if (grant.Queued || grant.Busy)
            return false;
        grant.Queued = true;
        _ready.Enqueue(grant);
        if (_pending++ == 0)
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    // Under _gate: drop the entry of a grant that is neither woken, in line, busy nor cooling — that
    // very entry only. A worker may still hold an entry that was dropped and replaced meanwhile (a
    // saturated skip that has not finished yet); removing by key would drop the replacement, which
    // may be busy, and let a second operation of the grant start (D-3).
    private void PruneLocked(GrantState grant)
    {
        if (!grant.Woken && !grant.Queued && !grant.Busy && !IsCoolingLocked(grant) && !IsDirectPausedLocked(grant)
            && _grants.TryGetValue(grant.GrantPeerDid, out var held) && ReferenceEquals(held, grant))
        {
            _grants.Remove(grant.GrantPeerDid);
        }
    }

    // A worker is done with the grant it took off the line; it also looks again at the direct
    // pauses that have run out.
    private void Served(GrantState grant)
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            PruneLocked(grant);
            if (_pauseStarts.Count > 0)
                DropSpentPausesLocked(_time.GetTimestamp());
            if (--_pending == 0)
                idle = _idle;
        }
        idle?.TrySetResult();
    }

    // The work an operation started has ended: the grant may be served again, at once when it was
    // woken meanwhile (or yielded with more to do) and is not cooling down.
    private void Settle(GrantState grant, bool more)
    {
        var signal = false;
        string? skipped = null;
        lock (_gate)
        {
            grant.Busy = false;
            _unsettled--;
            // A wake-up held during the operation is served now, or skipped like any other.
            if (grant.Woken && (_stopped != 0 || IsCoolingLocked(grant)))
            {
                grant.Woken = false;
                skipped = _stopped != 0 ? PushOutcomes.SkippedShutdown : PushOutcomes.SkippedCooldown;
            }
            // The rest of a yielded operation is not a wake-up: nothing to count when it cannot go on.
            grant.Woken |= more && _stopped == 0 && !IsCoolingLocked(grant);
            if (grant.Woken)
                signal = EnqueueLocked(grant);
            else
                PruneLocked(grant);
        }
        if (skipped is not null)
            _metrics.RecordPush(skipped);
        if (signal)
            _readySignal.Release();
    }

    private void StartCooldown(GrantState grant)
    {
        lock (_gate)
        {
            foreach (var other in _grants.Values.ToArray())
                PruneLocked(other);
            grant.CoolingSince = _time.GetTimestamp();
        }
        var level = MediatorLog.RateLimited(ref _cooldownLines, out var n);
        MediatorLog.Write(_logger, level, "Pushes to a recipient paused for {Cooldown}: its push reached the deadline. Its messages stay queued for pickup meanwhile. {Count} such pauses.", _cooldown, n);
    }

    // A direct push did not deliver (refused, faulted, or its deadline): that recipient's direct
    // pushes pause; its live pushes do not. Its entry is kept while the pause runs (PruneLocked).
    // Amortized O(1) under the lock, which every wake-up takes: it walks no entries, only the pauses
    // that have run out, each once, so a burst of refusals (an endpoint host many recipients share
    // going down) costs work linear in their number (QA4-3). The pause start is queued
    // (_pauseStarts); once it has run out, its entry is looked at again by the next pause start or
    // worker finish (DropSpentPausesLocked), if its grant's next wake-up (Served, Settle) or a live
    // cooldown's scan (StartCooldown) has not dropped it already (ADV-110-1b-1).
    // reason: why, when the push step did not log it (the deadline, a fault it let through); null
    // when the step logged the refusal or the send's fault itself.
    private void StartDirectPause(GrantState grant, string? reason)
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            grant.DirectPausedSince = now;
            _pauseStarts.Enqueue((grant, now));
            DropSpentPausesLocked(now);
        }
        var level = MediatorLog.RateLimited(ref _directFailedLines, out var n);
        if (reason is null)
            MediatorLog.Write(_logger, level, "Direct pushes to a recipient paused for {Pause}: one did not deliver. Its messages stay queued: the one whose send was attempted is not pushed again (the recipient's next delivery-request gets it), and the ones behind it go at the first wake-up after the pause, or by pickup. {Count} such pauses.", _cooldown, n);
        else
            MediatorLog.Write(_logger, level, "Direct push not delivered ({Reason}); direct pushes to this recipient are paused for {Pause}. Its messages stay queued: a message whose send was attempted is not pushed again (the recipient's next delivery-request gets it), and the ones behind it go at the first wake-up after the pause, or by pickup. {Count} such pauses.", reason, _cooldown, n);
    }

    // Under _gate: look again at the entries whose direct pause has run out by now, oldest first,
    // and drop each that is nothing else any more (PruneLocked, which reads the entry's own state).
    // Stops at the first pause still running: every pause has the same length, so none behind it has
    // run out either. An item is acted on only for that very pause of that very entry: a newer pause
    // of the recipient has its own item further back, and an entry dropped meanwhile (and perhaps
    // made again) is not in the map as this instance.
    private void DropSpentPausesLocked(long now)
    {
        while (_pauseStarts.TryPeek(out var start) && _time.GetElapsedTime(start.Since, now) >= _cooldown)
        {
            _pauseStarts.Dequeue();
            if (start.Grant.DirectPausedSince == start.Since)
                PruneLocked(start.Grant);
        }
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

    // Stop admission, cancel the running pushes, drop the waiting wake-ups. Idempotent.
    private void Stop()
    {
        var dropped = 0;
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            if (_stopped != 0)
                return;
            _stopped = 1;
            _pickup.LiveDeliveryStarted -= OnLiveDeliveryStarted;
            while (_ready.TryDequeue(out var grant))
            {
                grant.Queued = false;
                grant.Woken = false;
                PruneLocked(grant);
                dropped++;
            }
            _pending -= dropped;
            if (dropped > 0 && _pending == 0)
                idle = _idle;
        }

        CancelQuietly(_shutdown);
        for (var i = 0; i < dropped; i++)
            _metrics.RecordPush(PushOutcomes.SkippedShutdown);
        idle?.TrySetResult();
    }

    // One grant's state, touched only under _gate.
    private sealed class GrantState(string grantPeerDid)
    {
        public string GrantPeerDid { get; } = grantPeerDid;

        /// <summary>A wake-up not yet served.</summary>
        public bool Woken { get; set; }

        /// <summary>On the line for a worker.</summary>
        public bool Queued { get; set; }

        /// <summary>An operation started and its work has not ended.</summary>
        public bool Busy { get; set; }

        /// <summary>When its last cooldown started (<see cref="TimeProvider.GetTimestamp"/>); <c>null</c> if it never had one.</summary>
        public long? CoolingSince { get; set; }

        /// <summary>When its direct pushes were last paused (<see cref="TimeProvider.GetTimestamp"/>); <c>null</c> if they never were.</summary>
        public long? DirectPausedSince { get; set; }
    }

    /// <summary>Stop admission, cancel the running pushes and drop the waiting wake-ups; does not wait.</summary>
    public void Dispose() => Stop();

    /// <summary>Stop as <see cref="Dispose"/> does, then wait up to five seconds (the default grace) for the workers to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(_shutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            MediatorLog.Write(_logger, LogLevel.Warning, "Push workers did not stop within {Grace}; their messages stay queued for pickup.", _shutdownGrace);
        }
        catch
        {
            // The workers never fault (they log and stop); nothing to add here.
        }
    }
}
