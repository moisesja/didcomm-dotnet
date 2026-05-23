using DidComm.Jose;

namespace DidComm.Secrets;

/// <summary>
/// Minimal internal contract that the envelope layer uses to fetch the *public* key of a
/// remote sender — needed for authcrypt unpack (Zs = ECDH(local_priv, sender_pub)). Phase 3
/// merges this into the resolver-backed <c>IDidKeyService</c> documented in FR-DID-02..05;
/// Phase 2 keeps it minimal and test-substitutable.
/// </summary>
internal interface IInternalSenderKeyLookup
{
    /// <summary>
    /// Look up the public JWK for the authcrypt sender identified by <paramref name="skid"/>.
    /// Returns <c>null</c> when the kid is unknown; the unpack pipeline turns null into a
    /// <c>CryptoException</c> stating the sender key could not be resolved.
    /// </summary>
    /// <param name="skid">Sender key identifier (DID URL with fragment) as carried in the protected header.</param>
    Jwk? TryGet(string skid);
}
