using NetDid.Core.Crypto;

namespace DidComm.Crypto.KeyAgreement;

/// <summary>
/// Routing between JOSE-world identifiers (JWK <c>crv</c> / <c>kty</c>, JWE <c>alg</c>) and
/// the <c>NetDid.Core.Crypto.KeyType</c> enum. One source of truth so the JWE/JWS builders
/// never hard-code curve-to-keytype matches.
/// </summary>
internal static class KeyTypeMapper
{
    /// <summary>
    /// Map a JWK <c>crv</c> value to the <see cref="KeyType"/> used by net-did's ECDH /
    /// sign / verify primitives. Throws when the curve is not supported for key agreement.
    /// </summary>
    /// <param name="crv">JWK <c>crv</c> value.</param>
    /// <exception cref="NotSupportedException">When <paramref name="crv"/> is not one of the four FR-ENC-01/02 curves.</exception>
    public static KeyType FromCurveForKeyAgreement(string crv) => crv switch
    {
        Jose.JoseAlgorithms.CrvX25519 => KeyType.X25519,
        Jose.JoseAlgorithms.CrvP256 => KeyType.P256,
        Jose.JoseAlgorithms.CrvP384 => KeyType.P384,
        Jose.JoseAlgorithms.CrvP521 => KeyType.P521,
        _ => throw new NotSupportedException($"Curve '{crv}' is not supported for key agreement."),
    };

    /// <summary>Map a <see cref="KeyType"/> to its JWK <c>crv</c> value.</summary>
    /// <param name="keyType">The net-did key type.</param>
    public static string ToCurve(KeyType keyType) => keyType switch
    {
        KeyType.Ed25519 => Jose.JoseAlgorithms.CrvEd25519,
        KeyType.X25519 => Jose.JoseAlgorithms.CrvX25519,
        KeyType.P256 => Jose.JoseAlgorithms.CrvP256,
        KeyType.P384 => Jose.JoseAlgorithms.CrvP384,
        KeyType.P521 => Jose.JoseAlgorithms.CrvP521,
        KeyType.Secp256k1 => Jose.JoseAlgorithms.CrvSecp256k1,
        _ => throw new NotSupportedException($"KeyType '{keyType}' has no JWK curve mapping in this layer."),
    };

    /// <summary>Map a JWK <c>crv</c> to the <see cref="KeyType"/> used for signing (includes secp256k1 and Ed25519).</summary>
    /// <param name="crv">JWK <c>crv</c> value.</param>
    public static KeyType FromCurveForSigning(string crv) => crv switch
    {
        Jose.JoseAlgorithms.CrvEd25519 => KeyType.Ed25519,
        Jose.JoseAlgorithms.CrvP256 => KeyType.P256,
        Jose.JoseAlgorithms.CrvP384 => KeyType.P384,
        Jose.JoseAlgorithms.CrvP521 => KeyType.P521,
        Jose.JoseAlgorithms.CrvSecp256k1 => KeyType.Secp256k1,
        _ => throw new NotSupportedException($"Curve '{crv}' is not supported for signing."),
    };

    /// <summary>The JOSE signing algorithm matched to a JWK curve.</summary>
    /// <param name="crv">JWK <c>crv</c> value (Ed25519, P-256, P-384, P-521, secp256k1).</param>
    public static string ToJwsAlgorithm(string crv) => crv switch
    {
        Jose.JoseAlgorithms.CrvEd25519 => Jose.JoseAlgorithms.EdDSA,
        Jose.JoseAlgorithms.CrvP256 => Jose.JoseAlgorithms.ES256,
        Jose.JoseAlgorithms.CrvP384 => Jose.JoseAlgorithms.ES384,
        Jose.JoseAlgorithms.CrvP521 => Jose.JoseAlgorithms.ES512,
        Jose.JoseAlgorithms.CrvSecp256k1 => Jose.JoseAlgorithms.ES256K,
        _ => throw new NotSupportedException($"Curve '{crv}' has no JWS algorithm mapping."),
    };

    /// <summary>The CEK / KEK length in bytes for a given content-encryption / key-wrap pair.</summary>
    /// <param name="contentEncryption">JWE <c>enc</c> value.</param>
    public static int ContentEncryptionKeySizeBytes(string contentEncryption) => contentEncryption switch
    {
        Jose.JoseAlgorithms.A256CbcHs512 => 64, // 32 MAC + 32 ENC (RFC 7518 §5.2.5)
        Jose.JoseAlgorithms.A256Gcm => 32,
        Jose.JoseAlgorithms.XC20P => 32,
        _ => throw new NotSupportedException($"Content-encryption algorithm '{contentEncryption}' is not supported."),
    };

    /// <summary>The IV / nonce length in bytes for a given content-encryption algorithm.</summary>
    /// <param name="contentEncryption">JWE <c>enc</c> value.</param>
    public static int IvSizeBytes(string contentEncryption) => contentEncryption switch
    {
        Jose.JoseAlgorithms.A256CbcHs512 => 16,
        Jose.JoseAlgorithms.A256Gcm => 12,
        Jose.JoseAlgorithms.XC20P => 24,
        _ => throw new NotSupportedException($"Content-encryption algorithm '{contentEncryption}' is not supported."),
    };
}
