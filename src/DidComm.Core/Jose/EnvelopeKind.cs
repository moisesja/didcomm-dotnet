namespace DidComm.Jose;

/// <summary>
/// One of the legal DIDComm envelope shapes (per FR-ENV-02). <see cref="EnvelopeDetector"/>
/// classifies an incoming packed message into one of these so the unpack pipeline can
/// dispatch to the right decoder.
/// </summary>
internal enum EnvelopeKind
{
    /// <summary>An unprotected DIDComm plaintext JWM (<c>application/didcomm-plain+json</c>).</summary>
    Plaintext,

    /// <summary>A JWS-signed DIDComm message (<c>application/didcomm-signed+json</c>).</summary>
    Signed,

    /// <summary>A JWE encrypted DIDComm message; anoncrypt and authcrypt share one media type per FR-ENV-01. The KDF/sender role is decided by the protected-header <c>alg</c>.</summary>
    Encrypted,
}
