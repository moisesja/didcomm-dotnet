namespace DidComm.Protocols.Empty;

/// <summary>Empty 1.0 protocol identifiers (FR-PROTO-06) — header-only messages used as ACKs.</summary>
public static class EmptyProtocol
{
    /// <summary>Protocol identifier URI for Empty 1.0.</summary>
    public const string ProtocolUri = "https://didcomm.org/empty/1.0";

    /// <summary>The single message type URI of the protocol — <c>…/empty/1.0/empty</c>.</summary>
    public const string MessageType = "https://didcomm.org/empty/1.0/empty";
}
