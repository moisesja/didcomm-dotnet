namespace DidComm.Exceptions;

/// <summary>
/// Thrown when a Message Type URI (MTURI) is malformed (FR-PROTO-01) or otherwise
/// violates the protocol-identity rules in PRD §10.1.
/// </summary>
public sealed class ProtocolException : DidCommException
{
    /// <summary>Initialize with a message.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    public ProtocolException(string message) : base(message) { }

    /// <summary>Initialize with a message and inner exception.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
