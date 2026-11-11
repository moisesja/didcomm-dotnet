namespace DidComm.Transports.ReturnRoute;

/// <summary>Why a session left the registry, carried by <see cref="IReturnRouteSessionRegistry.SessionClosed"/>.</summary>
public enum SessionCloseReason
{
    /// <summary>Not a close: the value carried by <see cref="IReturnRouteSessionRegistry.SessionOpened"/>.</summary>
    None,

    /// <summary>The registrant unregistered the session (the connection ended).</summary>
    Unregistered,

    /// <summary>The registry evicted the session because its peer reached <see cref="ReturnRouteSessionOptions.MaxSessionsPerPeer"/>.</summary>
    Evicted,
}

/// <summary>Arguments of the registry's <c>SessionOpened</c> / <c>SessionClosed</c> events.</summary>
public sealed class ReturnRouteSessionEventArgs : EventArgs
{
    /// <summary>Create the arguments.</summary>
    /// <param name="session">The session the event is about.</param>
    /// <param name="reason">Why it closed; <see cref="SessionCloseReason.None"/> for an open event.</param>
    public ReturnRouteSessionEventArgs(IReturnRouteSession session, SessionCloseReason reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Reason = reason;
    }

    /// <summary>The session the event is about.</summary>
    public IReturnRouteSession Session { get; }

    /// <summary>Why the session closed; <see cref="SessionCloseReason.None"/> on <c>SessionOpened</c>.</summary>
    public SessionCloseReason Reason { get; }
}
