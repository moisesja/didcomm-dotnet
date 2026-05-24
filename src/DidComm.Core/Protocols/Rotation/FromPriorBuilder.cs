using System.Text.Json;
using DidComm.Crypto;
using DidComm.Crypto.KeyAgreement;
using DidComm.Jose;

namespace DidComm.Protocols.Rotation;

/// <summary>
/// Constructs the JWT carried in a DIDComm <c>from_prior</c> header (FR-ROT-01). The JWT is
/// signed by a key authorized under the <strong>prior</strong> DID's <c>authentication</c>
/// relationship; the JWT's <c>kid</c> identifies that key.
/// </summary>
/// <remarks>
/// Compact JOSE serialization per RFC 7519 (JWT) and RFC 7515 (JWS Compact). The
/// <c>typ</c> header is set to <c>JWT</c>. Claims are emitted in lexicographic key order so
/// the same inputs produce byte-identical output across runs (matches the FR-MSG-09 / NFR-10
/// deterministic-bytes posture used for JWE signing input).
/// </remarks>
public static class FromPriorBuilder
{
    /// <summary>Build a from_prior JWT from validated claims using the signer's private JWK.</summary>
    /// <param name="claims">Sub / Iss / Iat triple.</param>
    /// <param name="signerPrivateJwk">Private JWK; <c>Kid</c> MUST identify a key authorized under <paramref name="claims"/>.Iss <c>authentication</c>.</param>
    public static string Build(FromPriorClaims claims, Jwk signerPrivateJwk)
        => Build(claims, signerPrivateJwk, new DefaultCryptoProvider());

    /// <summary>Test seam: build with an explicit crypto provider.</summary>
    /// <param name="claims">Sub / Iss / Iat triple.</param>
    /// <param name="signerPrivateJwk">Private JWK; <c>Kid</c> MUST identify a key authorized under <paramref name="claims"/>.Iss <c>authentication</c>.</param>
    /// <param name="cryptoProvider">Crypto provider for signing.</param>
    internal static string Build(FromPriorClaims claims, Jwk signerPrivateJwk, DefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(signerPrivateJwk);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        if (string.IsNullOrEmpty(signerPrivateJwk.Crv))
            throw new ArgumentException("Signer JWK is missing 'crv'.", nameof(signerPrivateJwk));
        if (string.IsNullOrEmpty(signerPrivateJwk.D))
            throw new ArgumentException("Signer JWK is missing 'd' (private key material).", nameof(signerPrivateJwk));
        if (string.IsNullOrEmpty(signerPrivateJwk.Kid))
            throw new ArgumentException("Signer JWK is missing 'kid'.", nameof(signerPrivateJwk));

        var alg = KeyTypeMapper.ToJwsAlgorithm(signerPrivateJwk.Crv);

        // Header — key order: alg, kid, typ (anonymous-object property order is preserved by
        // the serializer, matching the claims block below). RFC 7515 compact JWS signing input
        // is the ASCII bytes of "<b64u(header)>.<b64u(payload)>". Serialize rather than
        // string-interpolate so an unusual 'kid' (quote / control char) is escaped, not injected.
        var headerJson = JsonSerializer.Serialize(new
        {
            alg,
            kid = signerPrivateJwk.Kid,
            typ = "JWT",
        });
        var headerB64u = Base64Url.Encode(Encoding.UTF8.GetBytes(headerJson));

        // Claims — key order: iat, iss, sub (lexicographic).
        var claimsJson = JsonSerializer.Serialize(new
        {
            iat = claims.Iat,
            iss = claims.Iss,
            sub = claims.Sub,
        });
        var claimsB64u = Base64Url.Encode(Encoding.UTF8.GetBytes(claimsJson));

        var signingInput = Encoding.ASCII.GetBytes($"{headerB64u}.{claimsB64u}");
        var privateBytes = Base64Url.Decode(signerPrivateJwk.D);
        byte[] signature;
        try
        {
            signature = cryptoProvider.Sign(alg, privateBytes, signingInput);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
        }

        return $"{headerB64u}.{claimsB64u}.{Base64Url.Encode(signature)}";
    }
}
