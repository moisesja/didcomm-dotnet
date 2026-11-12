namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// What <see cref="IReturnRouteSession.TrySendAsync(DidComm.Messages.Message, int, CancellationToken)"/>
/// — the size-bounded send — did with a message. Only <see cref="Sent"/> means the session now
/// carries it; after <see cref="NotSent"/> and <see cref="TooLarge"/> nothing was sent or queued.
/// </summary>
public enum ReturnRouteSendResult
{
    /// <summary>
    /// The message was not accepted, for any of the reasons
    /// <see cref="IReturnRouteSession.TrySendAsync(DidComm.Messages.Message, CancellationToken)"/>
    /// answers <c>false</c> for: the mode is not <see cref="ReturnRouteMode.All"/>, the session is
    /// closed, an HTTP session already holds a message, a WebSocket session has no free push slot,
    /// the message is not addressed from the session's local DID to exactly its peer, or the
    /// snapshot or the pack failed. Most of these are decided before the message is packed, so
    /// this value says nothing about the message's size.
    /// </summary>
    NotSent = 0,

    /// <summary>
    /// The message was accepted for transmission, exactly as a <c>true</c> from
    /// <see cref="IReturnRouteSession.TrySendAsync(DidComm.Messages.Message, CancellationToken)"/>:
    /// held as the HTTP <c>200</c> body or queued to the WebSocket writer. Queued, not delivered.
    /// Its packed envelope was no larger than the caller's bound.
    /// </summary>
    Sent = 1,

    /// <summary>
    /// The message was packed and its packed envelope was larger than the caller's bound. Nothing
    /// was sent or queued, and the call left the session as it found it: the HTTP slot it claimed,
    /// or the WebSocket push slot it took, was given back. A smaller message can be sent on the
    /// same session right after (as long as nothing else closed or downgraded it meanwhile).
    /// </summary>
    TooLarge = 2,
}
