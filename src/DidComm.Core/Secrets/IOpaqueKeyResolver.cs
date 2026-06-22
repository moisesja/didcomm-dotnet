using DataProofsDotnet.Jose.Encryption;
using NetCrypto;

namespace DidComm.Secrets;

/// <summary>
/// Optional capability an <see cref="ISecretsResolver"/> MAY also implement to perform DIDComm's two
/// private-key operations — JWS signing and ECDH key agreement — WITHOUT ever exposing private key
/// material (FR-SEC-06). This is the seam that lets keys held in a non-extractable boundary — HSM,
/// cloud KMS, OS keychain, MPC, or a <c>NetCrypto.IKeyStore</c> — sign and decrypt DIDComm v2
/// envelopes while the private scalar never leaves custody.
/// </summary>
/// <remarks>
/// <para>
/// When the resolver registered as the <see cref="ISecretsResolver"/> singleton also implements this
/// interface, the facade routes the signature step (signed envelopes, the inner JWS of
/// sign-then-encrypt, and the <c>from_prior</c> JWT) and the ECDH step (authcrypt send,
/// authcrypt / anoncrypt receive) through these <em>operation handles</em> instead of decoding a
/// private <see cref="Jwk"/>. Both methods return <c>null</c> when the <c>kid</c> is not held
/// opaquely, so the facade falls back to the extractable <see cref="ISecretsResolver.FindAsync"/>
/// path for that key. A single wallet may therefore mix opaque (keystore-held) and extractable
/// (in-memory) keys.
/// </para>
/// <para>
/// Selection — which recipient / sender / signer kids the agent holds — still flows through
/// <see cref="ISecretsResolver.FindPresentAsync"/>; a keystore-backed resolver answers it from its
/// alias list, so it need not expose private material to be a sufficient sole resolver. The handles
/// here cover only the secret operation itself; everything downstream (Concat-KDF, A256KW key wrap,
/// AEAD, header assembly, signature normalization) is public-data math the JOSE layer already owns.
/// </para>
/// </remarks>
public interface IOpaqueKeyResolver
{
    /// <summary>
    /// Return an opaque JWS signer for <paramref name="kid"/> (signs inside the secure boundary), or
    /// <c>null</c> when the key is not held opaquely. The returned <see cref="ISigner"/> MAY emit a
    /// raw EdDSA / compact-or-DER ECDSA signature; the JWS layer normalizes it to the JOSE wire form.
    /// </summary>
    /// <param name="kid">Key identifier — typically a DID URL with a fragment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ISigner?> ResolveSignerAsync(string kid, CancellationToken ct = default);

    /// <summary>
    /// Return an opaque ECDH key-agreement handle for <paramref name="kid"/>, or <c>null</c> when the
    /// key is not held opaquely. The handle is self-describing (it carries its own
    /// <c>crv</c> — <c>X25519</c> / <c>P-256</c> / <c>P-384</c> / <c>P-521</c>) and derives the raw
    /// shared secret <c>Z</c> inside the secure boundary; the JWE layer performs all subsequent
    /// (public-data) KDF / key-wrap / AEAD. Implementations SHOULD resolve held-ness and the curve in a
    /// single backing lookup (the facade does not pre-fetch either), so the held receive path stays as
    /// cheap as possible.
    /// </summary>
    /// <param name="kid">Key identifier — typically a DID URL with a fragment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEcdhKey?> ResolveKeyAgreementAsync(string kid, CancellationToken ct = default);
}
