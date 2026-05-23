using Microsoft.IdentityModel.Tokens;
using NetDid.Core.Crypto;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Jose;

/// <summary>
/// Thin shim that converts between DIDComm's <see cref="Jwk"/> model and net-did's
/// <see cref="JsonWebKey"/>. Off-curve point validation (<c>FR-ENC-03</c>) happens automatically
/// inside <see cref="NetDidJwkConverter.ExtractPublicKey"/> — callers that pass a malformed EC
/// JWK through <see cref="ExtractPublicKey(Jwk)"/> receive a <see cref="CryptographicException"/>
/// before any key bytes are returned.
/// </summary>
internal static class JwkConversion
{
    /// <summary>Convert a DIDComm <see cref="Jwk"/> to the net-did representation for use with its converter.</summary>
    public static JsonWebKey ToNetDidJwk(Jwk source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new JsonWebKey
        {
            Kty = source.Kty,
            Crv = source.Crv,
            X = source.X,
            Y = source.Y,
            D = source.D,
            Kid = source.Kid,
            Alg = source.Alg,
            Use = source.Use,
        };
    }

    /// <summary>
    /// Extract the (key type, raw public key bytes) pair from a DIDComm JWK. Delegates to
    /// <see cref="NetDidJwkConverter.ExtractPublicKey"/> so the invalid-curve defense
    /// (RFC 7518 §6.2.2) runs before any bytes are returned.
    /// </summary>
    /// <exception cref="CryptographicException">When the JWK contains an off-curve EC point.</exception>
    /// <exception cref="ArgumentException">When the JWK <c>kty</c>/<c>crv</c> combination is unsupported.</exception>
    public static (KeyType KeyType, byte[] PublicKey) ExtractPublicKey(Jwk jwk)
        => NetDidJwkConverter.ExtractPublicKey(ToNetDidJwk(jwk));

    /// <summary>Build a public-only DIDComm JWK from a raw public key and key type.</summary>
    public static Jwk ToPublicJwk(KeyType keyType, byte[] publicKey, string? kid = null)
    {
        var netDid = NetDidJwkConverter.ToPublicJwk(keyType, publicKey);
        return new Jwk
        {
            Kty = netDid.Kty,
            Crv = netDid.Crv,
            X = netDid.X,
            Y = netDid.Y,
            Kid = kid,
        };
    }
}
