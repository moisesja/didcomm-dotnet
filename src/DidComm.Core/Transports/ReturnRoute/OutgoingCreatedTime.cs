using DidComm.Messages;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The FR-RR-09 sender rule: every outgoing message that falls under the receiver's FR-RR-08 replay
/// scope carries <c>created_time</c>, so the receiver's admission (which refuses an in-scope message
/// without one) accepts it. Applied by <c>DidCommClient.SendAsync</c> to its own copy of the
/// caller's message, and by the AspNetCore return-route sessions to the replies and pushes they
/// pack.
/// </summary>
/// <remarks>
/// The sender cannot know how the receiver will authenticate the envelope, so the scope here is the
/// receiver's scope without its envelope clause: a message whose <c>return_route</c> header is
/// <c>all</c> or <c>thread</c>, or whose type belongs to a mediator-service protocol
/// (<see cref="MediatorServiceProtocols.IsMediatorServiceType"/>). A malformed header counts as
/// <c>none</c>, as the receiver reads it (FR-RR-01).
/// </remarks>
internal static class OutgoingCreatedTime
{
    /// <summary>Whether <paramref name="message"/> falls under the FR-RR-09 stamping scope.</summary>
    /// <param name="message">The outgoing message.</param>
    internal static bool IsInScope(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ReturnRouteHeader.Read(message).Mode is ReturnRouteMode.All or ReturnRouteMode.Thread
            || MediatorServiceProtocols.IsMediatorServiceType(message.Type);
    }

    /// <summary>
    /// Set <paramref name="message"/>'s <c>created_time</c> to <paramref name="now"/> (Unix seconds)
    /// when it has none and is in scope. A value the message already carries is never replaced or
    /// refreshed, whatever its age (FR-RR-09): the receiver judges it as sent.
    /// </summary>
    /// <param name="message">The outgoing message; the caller owns it (a library snapshot, never the application's instance).</param>
    /// <param name="now">The current time from the facade's clock.</param>
    internal static void StampIfInScope(Message message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.CreatedTime is null && IsInScope(message))
            message.CreatedTime = now.ToUnixTimeSeconds();
    }
}
