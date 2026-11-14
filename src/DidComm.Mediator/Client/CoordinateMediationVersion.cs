namespace DidComm.Mediator.Client;

/// <summary>
/// Which version of the Coordinate Mediation protocol <see cref="MediationClient"/> speaks to a
/// mediator (FR-MED-09, FR-MED-10). Set it with
/// <see cref="MediatorClientOptions.CoordinateMediationVersion"/>.
/// </summary>
public enum CoordinateMediationVersion
{
    /// <summary>
    /// Ask the mediator first, with a Discover Features query, and use 3.0 when it supports it in
    /// the mediator role, 2.0 otherwise. The answer is remembered per mediator for an hour. A
    /// mediator that answers neither version fails the call with
    /// <see cref="MediationNotSupportedException"/>; one that does not answer the query at all fails
    /// it with <see cref="TimeoutException"/> — pin a version for such a mediator.
    /// </summary>
    Negotiate = 0,

    /// <summary>Coordinate Mediation 3.0 (<c>recipient-update</c>, <c>recipient-query</c>), without asking first.</summary>
    V3 = 1,

    /// <summary>Coordinate Mediation 2.0 (<c>keylist-update</c>, <c>keylist-query</c>), without asking first.</summary>
    V2 = 2,
}
