namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The live return-route sessions of this host, keyed by peer DID subject (PRD §8a, FR-RR-03).
/// Transports register a session when a WebSocket connection becomes a return-route session and
/// unregister it when the connection ends; handlers (message-pickup live delivery) look sessions up
/// with <see cref="Find"/> and subscribe to the lifecycle events.
/// </summary>
/// <remarks>
/// <para>
/// Bounds: at most <see cref="ReturnRouteSessionOptions.MaxSessionsPerPeer"/> sessions per peer —
/// one more evicts that peer's oldest (also when the registry is full, since the total does not
/// grow) — and <see cref="ReturnRouteSessionOptions.MaxSessions"/> in total — at the bound a
/// registration that would grow the total is refused and nothing is evicted, so a flood of fresh
/// DIDs can never displace an existing peer. The registry forgets an evicted session and reports
/// it in <see cref="SessionRegistration.Evicted"/>; closing its transport is the registrant's job,
/// because the registry has no handle on sockets.
/// </para>
/// <para>
/// Events are raised outside any lock, after the state change, each subscriber in its own
/// <c>try</c>/<c>catch</c> (the exception type name is logged, never rethrown), so a subscriber can
/// neither corrupt the registry nor affect the registrant.
/// </para>
/// <para>
/// Dependency rule: an implementation must not take <c>ProtocolHandlerRegistry</c>,
/// <c>ProtocolDispatcher</c>, <c>DidCommClient</c> or an <c>IProtocolHandler</c> in its constructor —
/// the registry is a dependency of handlers and of the endpoints, and the handler registry's
/// factory walks every handler, so such a dependency would cycle the DI graph. The in-memory
/// default depends on options only.
/// </para>
/// </remarks>
public interface IReturnRouteSessionRegistry
{
    /// <summary>
    /// Register <paramref name="session"/> under its <see cref="IReturnRouteSession.PeerDid"/>.
    /// Never throws for a bound: the decision is in the result. Raises
    /// <see cref="SessionClosed"/> with <see cref="SessionCloseReason.Evicted"/> for an evicted
    /// session and then <see cref="SessionOpened"/> for this one.
    /// </summary>
    /// <param name="session">The session to register; its <see cref="IReturnRouteSession.PeerDid"/> must be a bare DID subject.</param>
    SessionRegistration Register(IReturnRouteSession session);

    /// <summary>
    /// Forget <paramref name="session"/>. Returns <c>true</c> when it was registered, raising
    /// <see cref="SessionClosed"/> with <see cref="SessionCloseReason.Unregistered"/>; <c>false</c>
    /// (and no event) when it was not — including when the registry already evicted it.
    /// </summary>
    /// <param name="session">The session to forget.</param>
    bool Unregister(IReturnRouteSession session);

    /// <summary>
    /// The sessions registered for the DID subject of <paramref name="did"/>, oldest first, as a
    /// snapshot that later registrations do not change. Empty for a value that is not a DID or
    /// DID URL.
    /// </summary>
    /// <param name="did">A DID or DID URL; compared by DID subject.</param>
    IReadOnlyList<IReturnRouteSession> Find(string did);

    /// <summary>The number of registered sessions.</summary>
    int Count { get; }

    /// <summary>Raised after a session was registered; <see cref="ReturnRouteSessionEventArgs.Reason"/> is <see cref="SessionCloseReason.None"/>.</summary>
    event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened;

    /// <summary>Raised after a session left the registry, with why.</summary>
    event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed;
}
