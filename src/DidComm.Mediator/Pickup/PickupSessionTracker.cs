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
    private volatile Func<string, string, DeliveredMessage, Message>? _liveDelivery;

    public PickupSessionState(PickupSessionTracker tracker) => _tracker = tracker;

    /// <summary>
    /// The owner token of every lease taken over this session. Random, never on the wire, never
    /// logged: the store uses it only to end those leases when the session closes.
    /// </summary>
    public string OwnerToken { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The session has ended. Once <c>true</c> it stays <c>true</c>, and live delivery is off.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>
    /// How to build a live <c>delivery</c> (from, to, message) in the protocol version the recipient
    /// turned live delivery on with; <c>null</c> while live delivery is off or the session closed.
    /// </summary>
    public Func<string, string, DeliveredMessage, Message>? LiveDelivery => IsClosed ? null : _liveDelivery;

    /// <summary>Turn live delivery on (a framing) or off (<c>null</c>). Returns whether it is on afterwards: never on a closed session.</summary>
    public bool SetLive(Func<string, string, DeliveredMessage, Message>? liveDelivery)
    {
        _liveDelivery = liveDelivery;
        // Closed after the write above: either this read sees it, or OnClosed runs after the write
        // and clears it. Live delivery can never stay on for a session that has ended.
        if (IsClosed)
            _liveDelivery = null;
        return LiveDelivery is not null;
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
        Volatile.Write(ref _closed, 1);
        _liveDelivery = null;
        _tracker.ReleaseLeasesInBackground(OwnerToken);
    }
}

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
    private readonly IMessageStore _store;
    private readonly ILogger? _logger;

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
    /// did not acknowledge becomes deliverable again. Runs on the thread pool so the thread that
    /// closed the session never executes store code; a failure is logged and the leases then end by
    /// their timeout.
    /// </summary>
    public void ReleaseLeasesInBackground(string ownerToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _store.ReleaseLeasesAsync(ownerToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MediatorLog.Write(_logger, LogLevel.Warning, "Message pickup: the leases of a closed session could not be released ({ExceptionType}); they end by timeout.", ex.GetType().Name);
            }
        });
    }
}
