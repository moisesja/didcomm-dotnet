namespace DidComm.Resolution;

/// <summary>
/// Verification-relationship selector used by <see cref="IDidKeyService"/> to extract the
/// right subset of keys from a resolved DID Document. DIDComm uses two of the W3C DID Core
/// relationships: <c>keyAgreement</c> for encryption recipients / authcrypt senders, and
/// <c>authentication</c> for JWS signers (FR-DID-03).
/// </summary>
public enum VerificationRelationship
{
    /// <summary>The <c>keyAgreement</c> relationship — keys used for ECDH (anoncrypt recipient, authcrypt sender + recipient).</summary>
    KeyAgreement,

    /// <summary>The <c>authentication</c> relationship — keys used for JWS signing / verifying.</summary>
    Authentication,
}
