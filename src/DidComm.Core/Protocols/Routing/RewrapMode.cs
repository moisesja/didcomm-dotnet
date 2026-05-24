namespace DidComm.Protocols.Routing;

/// <summary>
/// Selects whether <see cref="ForwardProcessor"/> ships the onward payload verbatim or
/// re-anoncrypts it inside a fresh outer <c>forward</c> envelope (FR-ROUTE-06 / spec
/// §Rewrapping).
/// </summary>
public enum RewrapMode
{
    /// <summary>
    /// Default behaviour: pass the inner attachment bytes through unchanged. The onion size
    /// shrinks by one layer at each mediator hop.
    /// </summary>
    PassThrough = 0,

    /// <summary>
    /// FR-ROUTE-06 rewrapping: the mediator re-anoncrypts the inner attachment into a new
    /// <c>forward</c> addressed to <c>body.next</c>, so the onion size stays constant. The
    /// outermost wrap is encrypted to the mediator's interpretation of <c>next</c>; the
    /// onward recipient sees a double-wrapped message.
    /// </summary>
    ReanoncryptToNext = 1,
}
