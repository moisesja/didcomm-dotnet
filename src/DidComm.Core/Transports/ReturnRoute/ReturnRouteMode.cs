namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The value of the <c>return_route</c> header extension (PRD §8a, FR-RR-01): what the sender asks
/// the receiving transport to do with messages addressed back to it. Parsed by
/// <see cref="ReturnRouteHeader.TryRead"/> and written by
/// <see cref="Messages.MessageBuilder.WithReturnRoute"/>.
/// </summary>
public enum ReturnRouteMode
{
    /// <summary>No messages should be returned over this connection. The default when the header is absent or malformed.</summary>
    None = 0,

    /// <summary>Send all messages for this DID over the connection.</summary>
    All = 1,

    /// <summary>Send all messages matching the DID and thread specified in <c>return_route_thread</c>.</summary>
    Thread = 2,
}
