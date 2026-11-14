using System.Runtime.CompilerServices;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// What the pickup role remembers about one live (WebSocket) return-route session: whether the
/// recipient turned live delivery on, how to frame a live delivery for it, and the token its
/// leases carry.
/// </summary>
internal sealed class PickupSessionState
{
    private readonly PickupSessionTracker _tracker;
    private int _hooked;
    private int _closed;
    private int _leasing;
    private volatile LiveMode? _live;

    public PickupSessionState(PickupSessionTracker tracker) => _tracker = tracker;

    /// <summary>
    /// The owner token of every lease taken over this session. Random, never on the wire, never
    /// logged: the store uses it only to end those leases when the session closes.
    /// </summary>
    public string OwnerToken { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The session has ended. Once <c>true</c> it stays <c>true</c>, and live delivery is off.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    // A full fence (Interlocked), not a volatile write: this write and the caller's later read of
    // IsClosed must not be reordered (a store followed by a load is the one pair release/acquire
    // semantics allow to swap), or a close running at the same time could miss this lease.
    /// <summary>
    /// Say that a lease is about to be taken under <see cref="OwnerToken"/>. Called before the
    /// store call that records the lease; the caller looks at <see cref="IsClosed"/> again once the
    /// lease is in use. A session that never took a lease has nothing to clean up when it closes,
    /// so a peer that only opens connections and toggles live mode costs the store nothing.
    /// </summary>
    public void MarkLeasing() => Interlocked.Exchange(ref _leasing, 1);

    /// <summary>
    /// Live delivery as the recipient turned it on: how to build a live <c>delivery</c> (from, to,
    /// message) in its protocol version, and since when (on <c>DidCommOptions.Clock</c>);
    /// <c>null</c> while live delivery is off or the session closed. Only messages stored at or
    /// after <see cref="LiveMode.Since"/> are pushed: what was queued before is picked up (FR-PICK-06).
    /// </summary>
    public LiveMode? Live => IsClosed ? null : _live;

    /// <summary>
    /// Turn live delivery on (a framing) or off (<c>null</c>). Turned on while already on, it keeps
    /// its first instant. Returns whether it is on afterwards: never on a closed session.
    /// </summary>
    /// <param name="liveDelivery">The framing, or <c>null</c> to turn live delivery off.</param>
    /// <param name="now">The instant live delivery goes on.</param>
    public bool SetLive(Func<string, string, DeliveredMessage, Message>? liveDelivery, DateTimeOffset now)
    {
        _live = liveDelivery is null ? null : new LiveMode(liveDelivery, _live?.Since ?? now);
        // Closed after the write above: either this read sees it, or OnClosed runs after the write
        // and clears it. Live delivery can never stay on for a session that has ended.
        if (IsClosed)
            _live = null;
        return Live is not null;
    }

    /// <summary>Hook the session's end signal, once.</summary>
    public void EnsureHooked(CancellationToken closed)
    {
        if (Interlocked.CompareExchange(ref _hooked, 1, 0) != 0)
            return;
        // UnsafeRegister: no ExecutionContext is captured, and a token that is already cancelled
        // runs the callback right here. The callback never throws and never runs store code on the
        // cancelling thread.
        closed.UnsafeRegister(static state => ((PickupSessionState)state!).OnClosed(), this);
    }

    private void OnClosed()
    {
        // Closed is written before the flag is read, and a taker writes the flag before it reads
        // Closed: one of the two always sees the other, so no lease is left to its timeout by a race.
        // Both writes are full fences (Interlocked), so neither side's write can be reordered after
        // its read.
        Interlocked.Exchange(ref _closed, 1);
        _live = null;
        if (Volatile.Read(ref _leasing) == 1)
            _tracker.ReleaseLeasesInBackground(OwnerToken);
    }
}

/// <summary>Live delivery on a session: the framing of a live <c>delivery</c> and the instant it went on.</summary>
/// <param name="Build">Builds a live <c>delivery</c> (from, to, message) in the protocol version live delivery was turned on with.</param>
/// <param name="Since">When live delivery went on, on <c>DidCommOptions.Clock</c>: only messages stored at or after it are pushed.</param>
internal sealed record LiveMode(Func<string, string, DeliveredMessage, Message> Build, DateTimeOffset Since);

/// <summary>
/// The per-session state of the pickup role (FR-PICK-06): one <see cref="PickupSessionState"/> per
/// live return-route session, created the first time the session is used for pickup and reset by
/// the session's own <see cref="IReturnRouteSession.Closed"/> signal — not by the session
/// registry's notifications, which are best-effort.
/// </summary>
/// <remarks>
/// State hangs off the session object itself (a weak table), so it needs no bound of its own and
/// cannot outlive the session: what exists is limited by the sessions the transport keeps alive,
/// which the registry bounds. HTTP sessions are never tracked — they cannot do live delivery and
/// their leases end by time only.
/// </remarks>
internal sealed class PickupSessionTracker
{
    private readonly ConditionalWeakTable<IReturnRouteSession, PickupSessionState> _states = new();
    /// <summary>The most owner tokens that wait for, or are in, their clean-up at any time.</summary>
    internal const int MaxPendingReleases = 1024;

    private readonly IMessageStore _store;
    private readonly ILogger? _logger;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pending = new();
    private int _pendingCount;
    private int _pumping;
    private long _dropped;

    public PickupSessionTracker(IMessageStore store, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// The state of <paramref name="session"/>, created and hooked to its end signal on first use;
    /// <c>null</c> when there is no session or it is not a live one.
    /// </summary>
    public PickupSessionState? Track(IReturnRouteSession? session)
    {
        if (session is null || !session.IsLive)
            return null;
        var state = _states.GetValue(session, _ => new PickupSessionState(this));
        state.EnsureHooked(session.Closed);
        return state;
    }

    /// <summary>The state of <paramref name="session"/> if it was ever tracked; never creates one.</summary>
    public PickupSessionState? Find(IReturnRouteSession session)
        => _states.TryGetValue(session, out var state) ? state : null;

    /// <summary>
    /// End every lease taken over a session that has closed (FR-PICK-04/08): what it was sent but
    /// did not acknowledge becomes deliverable again. Never blocks and never runs store code on the
    /// calling thread: the owner token joins a backlog that one worker drains, one store call at a
    /// time. Both are bounded — at most <see cref="MaxPendingReleases"/> tokens wait or are being
    /// released, however many sessions close and however slow the store is. A token that finds the
    /// backlog full is dropped (counted, logged): those leases then end by their timeout, as do the
    /// leases of a release that fails.
    /// </summary>
    public void ReleaseLeasesInBackground(string ownerToken)
    {
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingReleases)
        {
            Interlocked.Decrement(ref _pendingCount);
            var level = MediatorLog.RateLimited(ref _dropped, out var n);
            MediatorLog.Write(_logger, level, "Message pickup: the lease clean-up backlog is full ({MaxPendingReleases}); the leases of a closed session end by timeout. {Count} such sessions.", MaxPendingReleases, n);
            return;
        }

        _pending.Enqueue(ownerToken);
        StartPumpIfIdle();
    }

    /// <summary>Releases dropped because the backlog was full; their leases ended by timeout.</summary>
    internal long DroppedReleases => Interlocked.Read(ref _dropped);

    /// <summary>Owner tokens waiting for, or in, their release call.</summary>
    internal int PendingReleases => Volatile.Read(ref _pendingCount);

    private void StartPumpIfIdle()
    {
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
            return;
        // Unsafe queue: the closing thread's ExecutionContext (its AsyncLocals, an ambient request)
        // does not flow into store code that runs after that thread has moved on.
        ThreadPool.UnsafeQueueUserWorkItem(static tracker => _ = tracker.PumpAsync(), this, preferLocal: false);
    }

    // The one worker: drains the backlog in order, one release at a time, then stops. A release
    // that never completes stops the clean-up; the backlog then fills and later closes fall back
    // to lease expiry — bounded either way.
    private async Task PumpAsync()
    {
        while (true)
        {
            while (_pending.TryDequeue(out var ownerToken))
            {
                try
                {
                    await _store.ReleaseLeasesAsync(ownerToken, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    MediatorLog.Write(_logger, LogLevel.Warning, "Message pickup: the leases of a closed session could not be released ({ExceptionType}); they end by timeout.", ex.GetType().Name);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                }
            }

            Volatile.Write(ref _pumping, 0);
            // A token enqueued after the last TryDequeue and before the reset has no pump: take it up.
            if (_pending.IsEmpty || Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
                return;
        }
    }
}
