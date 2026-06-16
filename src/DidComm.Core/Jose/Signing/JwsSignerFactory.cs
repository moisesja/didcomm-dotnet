using DataProofsDotnet.Jose.Signing;
using DidComm.Exceptions;
using NetCrypto;

namespace DidComm.Jose.Signing;

/// <summary>
/// Materializes a DataProofsDotnet.Jose <see cref="JwsSigner"/> from a private signer JWK.
/// </summary>
/// <remarks>
/// DataProofs' JWS layer signs through NetCrypto's <see cref="ISigner"/> (never raw key bytes —
/// the AC-8 key-store posture), whereas DIDComm holds signer keys as private JWKs surfaced by the
/// consumer's <c>ISecretsResolver</c>. This adapter bridges the two by building a one-shot
/// <see cref="KeyPairSigner"/> over the JWK's private scalar. It is the single place
/// <c>DidComm.Core</c> references NetCrypto directly; everything else flows through
/// DataProofsDotnet.Jose. ES512 / P-521 signing is out of DataProofs' v1 scope, so a P-521 signer
/// JWK surfaces as <see cref="NotSupportedException"/> from the <see cref="JwsSigner"/> constructor.
/// </remarks>
internal static class JwsSignerFactory
{
    private static readonly IKeyGenerator KeyGen = new DefaultKeyGenerator();
    private static readonly ICryptoProvider Crypto = new DefaultCryptoProvider();

    /// <summary>Build a <see cref="JwsSigner"/> from a private signer JWK (requires <c>crv</c>, <c>d</c>, <c>kid</c>).</summary>
    /// <param name="signerPrivateJwk">The signer's private JWK.</param>
    /// <exception cref="MalformedMessageException">When the JWK is missing required members or carries an unsupported curve.</exception>
    /// <exception cref="NotSupportedException">When the curve has no JWS algorithm in DataProofs' v1 scope (e.g. P-521 / ES512).</exception>
    public static JwsSigner FromPrivateJwk(Jwk signerPrivateJwk)
    {
        ArgumentNullException.ThrowIfNull(signerPrivateJwk);
        if (string.IsNullOrEmpty(signerPrivateJwk.Crv))
            throw new MalformedMessageException("Signer JWK is missing 'crv'.");
        if (string.IsNullOrEmpty(signerPrivateJwk.D))
            throw new MalformedMessageException("Signer JWK is missing private-key material ('d').");
        if (string.IsNullOrEmpty(signerPrivateJwk.Kid))
            throw new MalformedMessageException("Signer JWK is missing 'kid'.");

        var keyType = ToKeyType(signerPrivateJwk.Crv);
        var priv = Base64Url.Decode(signerPrivateJwk.D);
        try
        {
            var keyPair = KeyGen.FromPrivateKey(keyType, priv);
            return new JwsSigner(new KeyPairSigner(keyPair, Crypto), signerPrivateJwk.Kid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv);
        }
    }

    private static KeyType ToKeyType(string crv) => crv switch
    {
        JoseAlgorithms.CrvEd25519 => KeyType.Ed25519,
        JoseAlgorithms.CrvP256 => KeyType.P256,
        JoseAlgorithms.CrvP384 => KeyType.P384,
        JoseAlgorithms.CrvP521 => KeyType.P521,
        JoseAlgorithms.CrvSecp256k1 => KeyType.Secp256k1,
        _ => throw new MalformedMessageException($"Signer JWK curve '{crv}' is not a supported signing curve."),
    };
}
