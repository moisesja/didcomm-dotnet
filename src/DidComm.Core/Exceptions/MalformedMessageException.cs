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

    /// <summary>
    /// Set when the message was refused because its own <c>expires_time</c> has passed (FR-API-05),
    /// so an internal caller can count the refusal as an expiry rather than a malformed message (the
    /// mediator opening a forward addressed to itself, FR-ROUTE-06). Not part of the public contract.
    /// </summary>
    internal bool Expired { get; init; }

    /// <summary>
    /// Set when the restricted unpack of a layer addressed to this agent (an anoncrypt envelope around
    /// a plaintext message only, FR-ROUTE-06) refused the layer for its shape — plaintext, signed,
    /// authcrypt, another envelope inside, or <c>from_prior</c> — rather than because it does not
    /// decrypt or parse. An internal caller that may then unpack the same layer in full, as the message
    /// for this agent, does so only for this refusal. Not part of the public contract.
    /// </summary>
    internal bool RefusedShape { get; init; }
}
