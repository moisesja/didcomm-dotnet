namespace DidComm.Exceptions;

/// <summary>
/// Thrown when a DIDComm plaintext message or attachment violates the structural rules in
/// PRD §4 (missing required header, invalid <c>id</c> characters, attachment <c>data</c> with
/// no member, etc.). Per FR-MSG-02 / FR-ATT-02 / FR-ATT-03 / FR-ATT-04 this is the canonical
/// rejection for shape problems detected before any cryptographic processing.
/// </summary>
public sealed class MalformedMessageException : DidCommException
{
    /// <summary>Initialize with a message.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    public MalformedMessageException(string message) : base(message) { }

    /// <summary>Initialize with a message and inner exception.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public MalformedMessageException(string message, Exception innerException) : base(message, innerException) { }
}
