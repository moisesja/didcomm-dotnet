namespace DidComm.Exceptions;

/// <summary>
/// Thrown when the message-layer addressing-consistency rules in PRD §4.3
/// (FR-CONSIST-01..06) are violated — e.g. an authcrypt <c>skid</c> whose DID subject does
/// not match the plaintext <c>from</c>, a recipient <c>kid</c> not present in <c>to</c>, or a
/// signer <c>kid</c> not authorized in the asserted <c>from</c>'s DID document.
/// </summary>
public sealed class ConsistencyException : DidCommException
{
    /// <summary>Initialize with a message.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    public ConsistencyException(string message) : base(message) { }

    /// <summary>Initialize with a message and inner exception.</summary>
    /// <param name="message">Human-readable failure reason.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public ConsistencyException(string message, Exception innerException) : base(message, innerException) { }
}
