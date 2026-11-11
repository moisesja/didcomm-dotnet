namespace DidComm.Transports.ReturnRoute;

/// <summary>What <see cref="IReturnRouteSessionRegistry.Register"/> decided.</summary>
public enum SessionRegistrationOutcome
{
    /// <summary>The session is now registered (possibly after evicting the peer's oldest, see <see cref="SessionRegistration.Evicted"/>).</summary>
    Registered,

    /// <summary>The registry is at <see cref="ReturnRouteSessionOptions.MaxSessions"/>; nothing was evicted and the session is not registered.</summary>
    RefusedTotalBound,

    /// <summary>The same session instance was already registered; nothing changed.</summary>
    AlreadyRegistered,
}

/// <summary>
/// The result of <see cref="IReturnRouteSessionRegistry.Register"/>: the decision and, when the
/// per-peer bound evicted that peer's oldest session to make room, the evicted session. The
/// registry has already forgotten an evicted session; closing its transport is the registrant's
/// job.
/// </summary>
/// <param name="Outcome">The decision.</param>
/// <param name="Evicted">The session evicted to make room, or <c>null</c>. Non-null only with <see cref="SessionRegistrationOutcome.Registered"/>.</param>
public readonly record struct SessionRegistration(SessionRegistrationOutcome Outcome, IReturnRouteSession? Evicted)
{
    /// <summary><c>true</c> when <see cref="Outcome"/> is <see cref="SessionRegistrationOutcome.Registered"/>.</summary>
    public bool Registered => Outcome == SessionRegistrationOutcome.Registered;
}
