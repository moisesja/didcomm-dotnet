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
/// Pushes stored messages to their recipients on a few background workers — over the recipient's
/// live connection, or straight to its own endpoint — after the forward pump has stored them, never
/// on the request or on the pump (FR-PICK-08/09, D6, JC-4, JC-5). It runs whenever
/// <c>AddMediator()</c> is used: live pushes need it whether or not
/// <see cref="MediatorOptions.DirectPush"/> is on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The store decides what is pushed (D-1).</strong> The dispatcher keeps no per-message
/// state. The forward sink stores a message and wakes its grant (<see cref="Wake"/>); an operation
/// for that grant then takes, one at a time, the next message the store hands out at that moment
/// (<see cref="PickupCoordinator.TakeNextAsync"/>: due, unexpired, unleased, not passed over, oldest
/// first, its recipient DID still registered under the grant) and pushes it. A message that expired,
/// was acknowledged, purged or picked up is therefore never pushed and never occupies anything.
/// </para>
/// <para>
/// <strong>In the delivery order (D6).</strong> First, while the grant's peer has a live session
/// with live delivery on, the messages that arrived after it turned live delivery on go over that
/// session (<see cref="PickupCoordinator.PushNextLiveAsync"/>, <c>Pushed-Live</c>): each stays leased
/// to the session until <c>messages-received</c>, its close or the lease timeout; one too large for
/// the session's envelope is passed over, as a pickup does; one the session does not take is given
/// back and ends the operation. "Arrived after" is arrival order, not time: the store numbers every
/// record as it stores it (<see cref="QueuedMessage.Sequence"/>), and the session's mark is the
/// highest number in the status that answered its turning live delivery on — so a message is either
/// counted in that status, and the recipient's to pick up (FR-PICK-06), or pushed, whatever the
/// clocks do. Turning live delivery on wakes the grant
/// (<see cref="PickupCoordinator.LiveDeliveryStarted"/>), for a message that arrived after that
/// status but before the session was live. Then, when direct push is on and the host's
/// <see cref="IRecipientEndpointDirectory"/> names an absolute <c>http</c> or <c>https</c> endpoint
/// (a WebSocket write gives no receipt), the grant's due messages that its direct push has not
/// attempted yet go to it, oldest first: each is taken under a lease of its own
/// (<see cref="MediatorOptions.PushTimeout"/> + <see cref="LeaseMargin"/>) and sent as a single
/// attempt through
/// <see cref="DidCommClient.SendPackedAsync(ReadOnlyMemory{byte}, Uri, string, SendPackedOptions, CancellationToken)"/>
/// (<see cref="SendPackedOptions.SingleAttempt"/>: one POST, no retry, no redirect followed, outside
/// the HTTP transport's circuit breaker, the outbound endpoint guard applied); an HTTP 2xx removes it
/// (<c>Pushed</c>), anything else gives it back and ends the operation. Otherwise a message waits
/// for pickup.
/// </para>
/// <para>
/// <strong>One direct attempt per message; retries.</strong> Each grant has a direct-push cursor on
/// the same arrival sequence: the direct step takes only records after it
/// (<see cref="MessageLeaseRequest.AfterSequence"/>) and moves it past every record it sends,
/// whatever the outcome. So each message gets one direct attempt; one the endpoint refused (or that
/// reached the deadline) stays queued for pickup, and neither blocks the grant's later messages nor
/// is sent again with them. A message that was not yet due when a later one of its grant was
/// attempted is behind the cursor and waits for pickup too. The cursors are kept apart from the
/// per-grant entries below (pruning an idle grant keeps its cursor), at most
/// <see cref="CoordinateMediationOptions.MaxGrants"/> of them; past that the least recently used is
/// dropped, and its grant's remaining records get at most one more direct attempt each. A live push
/// the session did not take (<c>NotSent</c>: no free push slot, downgraded or closing) — or that
/// faulted, or reached its deadline and was then given back — is tried again at the grant's next
/// wake-up: that is at most one live attempt per wake-up (the operation ends there, and a deadline
/// also pauses the grant), a wake-up is a stored message or a session turning live delivery on, and
/// nothing is written to the connection unless the session takes it — so it cannot multiply the
/// traffic of what was forwarded. One too large for the session is passed over, for a period that
/// doubles each time. Pickup always works. Delivery is at-least-once.
/// </para>
/// <para>
/// <strong>A push names what it took (D-5).</strong> Every send is preceded by a lease on exactly
/// that record and followed, once, by its acknowledgement (a direct 2xx), its release, its pass-over
/// or — for a live push — the session's lease running on.
/// </para>
/// <para>
/// <strong>Wake-ups, one operation per grant (D-2, D-3).</strong> <see cref="Wake"/> is O(1) and
/// never blocks; wake-ups coalesce into one flag per grant. The flag is consumed before the
/// operation reads the store, so a wake-up that arrives during an operation is served by another one
/// right after it. A grant has at most one operation from its start until the work it started has
/// actually ended — not until its deadline — keyed by grant, so a new session does not reset it.
/// Grants are served in turn; an operation pushes at most
/// <see cref="MessagePickupOptions.MaxDeliveryBatch"/> messages, then yields its worker and its grant
/// takes its turn again, so a busy grant cannot monopolise a worker.
/// </para>
/// <para>
/// <strong>Deadline and cooldown.</strong> Each push has a deadline,
/// <see cref="MediatorOptions.PushTimeout"/>, on a timer whose callback cancels inside a
/// <c>try</c> (never <c>CancelAfter</c>). At the deadline the worker stops waiting and serves other
/// grants; the push counts as <c>Push-Failed</c>, and its grant is paused for
/// <see cref="CooldownPushTimeouts"/> × <see cref="MediatorOptions.PushTimeout"/> (one minute at the
/// defaults), measured on the monotonic clock (<see cref="TimeProvider.GetTimestamp"/>), so a step of
/// the wall clock neither lengthens nor shortens it: wake-ups meanwhile are skipped
/// (<c>Push-Skipped-Cooldown</c>), and their messages wait for pickup. The abandoned push still ends
/// its lease exactly once when it ends, and its grant is not served again before that. A session
/// that ignores cancellation and later reports the message sent keeps it leased, as for any live
/// push; a direct push whose 2xx arrives late removes it (at-least-once). When a stalled push does
/// end at its deadline, a peer that stalls on purpose holds a worker at most once per cooldown: with
/// K grants that stall every push this way and W workers, a healthy recipient's push can wait up to
/// about ceil(K / W) × <see cref="MediatorOptions.PushTimeout"/>, and once K reaches 3 × W that wait
/// recurs; nothing is lost, pickup always works, and a mediation policy that enrols only known peers
/// closes it. A push that does not end at its deadline is the next paragraph's case.
/// </para>
/// <para>
/// <strong>Bounded (D-4).</strong> <see cref="MediatorOptions.PushConcurrency"/> workers; at most
/// <see cref="UnsettledPerWorker"/> × <see cref="MediatorOptions.PushConcurrency"/> operations
/// between their start and the end of the work they started (abandoned ones included). When that
/// many exist, a wake-up starts nothing (<c>Push-Skipped-Saturated</c>) and its messages wait for
/// pickup or the grant's next wake-up. A host collaborator — a return-route session, the DID
/// resolver, a transport, the endpoint directory or the store — that never completes a call after
/// it was cancelled keeps that call's operation, and its grant, unsettled until it does complete;
/// <see cref="UnsettledPerWorker"/> × <see cref="MediatorOptions.PushConcurrency"/> such calls hold
/// every operation there is, and then no push starts, for any recipient, until one of them
/// completes. Pickup keeps working throughout. Each operation holds at most one record at a time. The
/// dispatcher holds one small entry per grant that is woken, busy or cooling down (its DID, at most
/// 2048 characters), dropped when it is none of these, and at most
/// <see cref="CoordinateMediationOptions.MaxGrants"/> direct-push cursors (a DID and a number each).
/// </para>
/// <para>
/// <strong>Dependencies (L-017).</strong> The pickup queue, the message store, the counter and the
/// settings. The directory and <see cref="DidCommClient"/> are resolved on the first direct push.
/// </para>
/// <para>
/// <strong>Logging.</strong> Outcome names, counts and exception type names; never a DID, a queue id,
/// the endpoint or an exception message. Failures are rate-limited per reason.
/// </para>
/// <para>
/// <strong>Shutdown.</strong> Disposing stops admission, cancels the running pushes (their leases are
/// given back) and drops the waiting wake-ups (<c>Push-Skipped-Shutdown</c>); every message stays
/// queued. <see cref="DisposeAsync"/> also waits up to five seconds for the workers.
/// </para>
/// </remarks>
internal sealed class PushDispatcher : IAsyncDisposable, IDisposable
{
    /// <summary>How much longer than the push deadline a direct push's lease on its record runs.</summary>
    internal static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    /// <summary>How many push timeouts a grant whose push reached the deadline is paused for.</summary>
    internal const int CooldownPushTimeouts = 2;

    /// <summary>Operations allowed between start and settlement, per worker.</summary>
    internal const int UnsettledPerWorker = 2;

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    // One request to exactly the directory's endpoint: no retry, no redirect, no circuit breaker
    // (one recipient's failing endpoint must not stop pushes to the others, AD-6).
    private static readonly SendPackedOptions SingleAttempt = new() { SingleAttempt = true };

    private readonly IServiceProvider _services;
    private readonly PickupCoordinator _pickup;
    private readonly IMessageStore _store;
    private readonly DidCommOptions _didComm;
    private readonly MediatorMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly bool _directPush;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cooldown;
    private readonly int _perRun;
    private readonly int _maxUnsettled;
    private readonly object _gate = new();
    // Under _gate: one entry per grant that is woken, busy or cooling down; the woken grants waiting
    // for a worker, in the order they are to be served.
    private readonly Dictionary<string, GrantState> _grants = new(StringComparer.Ordinal);
    private readonly Queue<GrantState> _ready = new();
    // Under _gate: each grant's direct-push cursor, most recently used first, at most _maxCursors.
    private readonly Dictionary<string, LinkedListNode<DirectCursor>> _cursors = new(StringComparer.Ordinal);
    private readonly LinkedList<DirectCursor> _cursorOrder = new();
    private readonly int _maxCursors;
    // One count per entry put on _ready; a worker takes one, then a grant off _ready.
    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Task[] _workers;
    private TaskCompletionSource _idle = NewCompletedSignal();
    private int _pending;
    private int _unsettled;
    private int _stopped;
    private DidCommClient? _client;
    private IRecipientEndpointDirectory? _directory;
    private bool _directoryResolved;
    private long _saturatedLines;
    private long _failedLines;
    private long _endpointLines;
    private long _storeFaultLines;
    private long _cooldownLines;

    /// <summary>Create the dispatcher; its workers start here.</summary>
    /// <param name="services">The container, for the directory and the facade, resolved on the first direct push.</param>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="queue">The pickup queue: its store view, live sessions and leases are the ones a push uses.</param>
    /// <param name="store">The message store: a direct push removes or gives back the record it sent.</param>
    /// <param name="didCommOptions">Supplies the clock the store judges records by.</param>
    /// <param name="metrics">The push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the deadlines and the cooldown (its monotonic timestamps).</param>
    public PushDispatcher(
        IServiceProvider services,
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        IMessageStore store,
        IOptions<DidCommOptions> didCommOptions,
        MediatorMetrics metrics,
        ILogger<PushDispatcher>? logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(didCommOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Read once, after validation: a host that changes the options object later changes nothing here.
        var settings = options.Value;
        settings.ValidatePipelineRanges();
        _directPush = settings.DirectPush;
        _timeout = settings.PushTimeout;
        _cooldown = TimeSpan.FromTicks(settings.PushTimeout.Ticks * CooldownPushTimeouts);
        _maxUnsettled = UnsettledPerWorker * settings.PushConcurrency;
        _maxCursors = settings.CoordinateMediation.MaxGrants;

        _services = services;
        _pickup = queue.Coordinator;
        _perRun = _pickup.MaxDeliveryBatch;
        _store = store;
        _didComm = didCommOptions.Value;
        _metrics = metrics;
        _logger = logger;
        _time = timeProvider;
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

    /// <summary>Whether direct push is on.</summary>
    public bool DirectPushEnabled => _directPush;

    /// <summary>Operations allowed between start and settlement.</summary>
    internal int MaxUnsettled => _maxUnsettled;

    /// <summary>Pushes one operation makes before it yields its worker.</summary>
    internal int PerRun => _perRun;

    /// <summary>The most direct-push cursors kept.</summary>
    internal int MaxDirectCursors => _maxCursors;

    /// <summary>Test seam: the direct-push cursors kept.</summary>
    internal int DirectCursors
    {
        get
        {
            lock (_gate)
                return _cursors.Count;
        }
    }

    /// <summary>Test seam: the arrival sequence of the last record <paramref name="grantPeerDid"/>'s direct push attempted, if its cursor is kept; does not count as a use.</summary>
    /// <param name="grantPeerDid">The grant.</param>
    internal long? DirectCursorOf(string grantPeerDid)
    {
        lock (_gate)
            return _cursors.TryGetValue(grantPeerDid, out var node) ? node.Value.Sequence : null;
    }

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

    // One operation: live pushes, then direct ones, one record at a time, each under its own
    // deadline, at most _perRun of them. The grant settles here, or — when a push is abandoned at
    // its deadline — when that push's work actually ends.
    private async Task ServeAsync(GrantState grant)
    {
        var direct = false;
        var attempted = false;
        var more = false;
        Task? abandoned = null;
        try
        {
            for (var pushes = 0; ; )
            {
                if (pushes >= _perRun)
                {
                    more = true; // yield the worker; the grant takes its turn again
                    return;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                using var timer = _time.CreateTimer(static state => CancelQuietly((CancellationTokenSource)state!), deadline, _timeout, Timeout.InfiniteTimeSpan);
                var work = direct ? PushNextDirectAsync(grant.GrantPeerDid, deadline.Token) : PushNextLiveAsync(grant.GrantPeerDid, deadline.Token);
                Step step;
                try
                {
                    step = await work.WaitAsync(deadline.Token).ConfigureAwait(false);
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
                    Failed(direct, "Timeout");
                    StartCooldown(grant);
                    return;
                }
                catch (Exception ex)
                {
                    _metrics.RecordPush(PushOutcomes.Failed);
                    Failed(direct, ex.GetType().Name);
                    return;
                }

                switch (step)
                {
                    case Step.Pushed:
                        _metrics.RecordPush(direct ? PushOutcomes.Pushed : PushOutcomes.PushedLive);
                        break;
                    case Step.Removed:
                        _metrics.RecordPush(PushOutcomes.SkippedUnregistered);
                        break;
                    case Step.PassedOver:
                        _metrics.RecordPush(PushOutcomes.Failed);
                        break;
                    case Step.NotDelivered:
                        _metrics.RecordPush(PushOutcomes.Failed);
                        Failed(direct, "NotDelivered");
                        return;
                    case Step.NoEndpoint or Step.Scheme:
                        if (!attempted)
                            _metrics.RecordPush(step == Step.Scheme ? PushOutcomes.SkippedScheme : PushOutcomes.SkippedNoEndpoint);
                        return;
                    case Step.NoSession when _directPush && !direct:
                    case Step.Empty when _directPush && !direct:
                        direct = true;
                        continue;
                    case Step.NoSession:
                        if (!attempted)
                            _metrics.RecordPush(PushOutcomes.SkippedNoLiveSession);
                        return;
                    default: // Empty: nothing left to push, which is no outcome (a coalesced wake-up
                             // often finds its message already pushed, or taken by a pickup).
                        return;
                }
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

    // What one push did.
    private enum Step
    {
        Pushed,
        Removed,
        PassedOver,
        NotDelivered,
        NoEndpoint,
        Scheme,
        NoSession,
        Empty,
    }

    private async Task<Step> PushNextLiveAsync(string grantPeerDid, CancellationToken ct)
        => await _pickup.PushNextLiveAsync(grantPeerDid, ct).ConfigureAwait(false) switch
        {
            LiveStep.Sent => Step.Pushed,
            LiveStep.Removed => Step.Removed,
            LiveStep.PassedOver => Step.PassedOver,
            LiveStep.NotSent => Step.NotDelivered,
            LiveStep.NoSession => Step.NoSession,
            _ => Step.Empty,
        };

    // Take the grant's next due record after its cursor under a lease of this push's own, then find
    // where it goes, move the cursor past it, send it once, and remove it (2xx) or give it back
    // (anything else, a fault included). Neither is cancelled: they run when the send has ended,
    // however long that takes.
    private async Task<Step> PushNextDirectAsync(string grantPeerDid, CancellationToken ct)
    {
        var now = _didComm.Now();
        var lease = new MessageLease(Owner: null, ExpiresAt: SaturatingAdd(now, _timeout + LeaseMargin));
        var (taken, record) = await _pickup.TakeNextAsync(grantPeerDid, lease, CursorOf(grantPeerDid), now, ct).ConfigureAwait(false);
        if (record is null)
            return taken == TakeResult.Removed ? Step.Removed : Step.Empty;

        var delivered = false;
        try
        {
            // A take that came back after the deadline sends nothing: the record goes straight back.
            ct.ThrowIfCancellationRequested();
            var (endpoint, skipped) = Endpoint(grantPeerDid);
            if (endpoint is null)
                return skipped;
            // Attempted from here on, whatever the outcome: it gets no other direct attempt.
            Advance(grantPeerDid, record.Sequence);
            var result = await Client().SendPackedAsync(record.Payload, endpoint, MediaTypes.Encrypted, SingleAttempt, ct).ConfigureAwait(false);
            delivered = result is { Accepted: true, HttpStatusCode: >= 200 and <= 299 };
        }
        finally
        {
            if (!delivered)
                await QuietlyAsync(_store.ReleaseAsync(grantPeerDid, new[] { record.Id }, lease.Id, CancellationToken.None), "giving the message back").ConfigureAwait(false);
        }
        if (!delivered)
            return Step.NotDelivered;
        // Delivered: removed. Should the removal fail, the record goes back to pickup when the lease
        // ends and may be delivered again (at-least-once).
        await QuietlyAsync(_store.AcknowledgeAsync(grantPeerDid, new[] { record.Id }, CancellationToken.None), "removing the delivered message").ConfigureAwait(false);
        return Step.Pushed;
    }

    // Where a direct push goes: only the host's directory, never anything the message says (JC-5),
    // and only an absolute http(s) endpoint. Asked only when there is a message to push.
    private (Uri? Endpoint, Step Skipped) Endpoint(string grantPeerDid)
    {
        Uri? endpoint;
        string reason;
        try
        {
            endpoint = Directory()?.Find(grantPeerDid);
            reason = "no endpoint for the recipient";
        }
        catch (Exception ex)
        {
            endpoint = null;
            reason = "the endpoint directory failed with " + ex.GetType().Name;
        }

        var skipped = Step.NoEndpoint;
        if (endpoint is not null)
        {
            if (endpoint.IsAbsoluteUri
                && (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return (endpoint, skipped);
            }
            skipped = Step.Scheme;
            reason = "the directory's endpoint is not an absolute http or https URI (a WebSocket write gives no receipt)";
        }
        var level = MediatorLog.RateLimited(ref _endpointLines, out var n);
        MediatorLog.Write(_logger, level, "Direct push skipped: {Reason}; the messages stay queued for pickup. {Count} such skips.", reason, n);
        return (null, skipped);
    }

    private void Failed(bool direct, string reason)
    {
        var level = MediatorLog.RateLimited(ref _failedLines, out var n);
        if (direct)
            MediatorLog.Write(_logger, level, "Direct push not delivered ({Reason}); the message stays queued for pickup. {Count} such failures.", reason, n);
        else
            MediatorLog.Write(_logger, level, "Live push not delivered ({Reason}); the message stays queued for pickup and is tried again at the recipient's next wake-up. {Count} such failures.", reason, n);
    }

    // The arrival sequence after which the grant's direct push takes its next record: the last one
    // it attempted, or none (every record) when it attempted none or its cursor was dropped.
    private long? CursorOf(string grantPeerDid)
    {
        lock (_gate)
        {
            if (!_cursors.TryGetValue(grantPeerDid, out var node))
                return null;
            _cursorOrder.Remove(node);
            _cursorOrder.AddFirst(node);
            return node.Value.Sequence;
        }
    }

    // A direct push is about to send this record: move the grant's cursor past it. A new cursor
    // beyond the bound drops the least recently used one (its grant may attempt its remaining
    // records once more).
    private void Advance(string grantPeerDid, long sequence)
    {
        lock (_gate)
        {
            if (_cursors.TryGetValue(grantPeerDid, out var node))
            {
                node.Value.Sequence = Math.Max(node.Value.Sequence, sequence);
                _cursorOrder.Remove(node);
                _cursorOrder.AddFirst(node);
                return;
            }
            if (_cursors.Count >= _maxCursors && _cursorOrder.Last is { } oldest)
            {
                _cursorOrder.RemoveLast();
                _cursors.Remove(oldest.Value.GrantPeerDid);
            }
            _cursors.Add(grantPeerDid, _cursorOrder.AddFirst(new DirectCursor(grantPeerDid, sequence)));
        }
    }

    private async Task QuietlyAsync(ValueTask<int> call, string what)
    {
        try
        {
            await call.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var level = MediatorLog.RateLimited(ref _storeFaultLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push: {What} failed with {ExceptionType}; the message returns to pickup when the push's lease ends. {Count} such faults.", what, ex.GetType().Name, n);
        }
    }

    // Under _gate. On the monotonic clock: a step of the wall clock does not move it.
    private bool IsCoolingLocked(GrantState grant)
        => grant.CoolingSince is { } since && _time.GetElapsedTime(since) < _cooldown;

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
        if (!grant.Woken && !grant.Queued && !grant.Busy && !IsCoolingLocked(grant)
            && _grants.TryGetValue(grant.GrantPeerDid, out var held) && ReferenceEquals(held, grant))
        {
            _grants.Remove(grant.GrantPeerDid);
        }
    }

    // A worker is done with the grant it took off the line.
    private void Served(GrantState grant)
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            PruneLocked(grant);
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

    // One grant's direct-push cursor, touched only under _gate.
    private sealed class DirectCursor(string grantPeerDid, long sequence)
    {
        public string GrantPeerDid { get; } = grantPeerDid;

        /// <summary>The arrival sequence of the last record attempted.</summary>
        public long Sequence { get; set; } = sequence;
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
    }

    /// <summary>Stop admission, cancel the running pushes and drop the waiting wake-ups; does not wait.</summary>
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
