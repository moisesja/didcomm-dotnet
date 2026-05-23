using DidComm.Jose;
using NetDid.Core;
using NetDid.Core.Crypto;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Crypto.KeyAgreement;

/// <summary>
/// A freshly-generated per-envelope ephemeral key pair for ECDH. JWE protected headers carry
/// the public half as <c>epk</c> (FR-ENC-11); the private half is held in memory for the
/// duration of the pack call, then released. Wraps net-did's <see cref="DefaultKeyGenerator"/>
/// so this layer does not duplicate per-curve generation logic.
/// </summary>
internal sealed class EphemeralKeyPair
{
    private static readonly IKeyGenerator _generator = new DefaultKeyGenerator();

    /// <summary>The JOSE curve this pair was generated on.</summary>
    public string Curve { get; }

    /// <summary>Raw public-key bytes in the shape net-did expects (OKP raw / EC SEC1 uncompressed).</summary>
    public byte[] PublicKey { get; }

    /// <summary>Raw private-key bytes (scalar). Caller is responsible for releasing after use.</summary>
    public byte[] PrivateKey { get; }

    private EphemeralKeyPair(string curve, byte[] publicKey, byte[] privateKey)
    {
        Curve = curve;
        PublicKey = publicKey;
        PrivateKey = privateKey;
    }

    /// <summary>Generate a new key pair for the requested JOSE curve.</summary>
    /// <param name="crv">JWK <c>crv</c> value — one of <c>X25519</c>, <c>P-256</c>, <c>P-384</c>, <c>P-521</c>.</param>
    public static EphemeralKeyPair Generate(string crv)
    {
        ArgumentException.ThrowIfNullOrEmpty(crv);
        var keyType = KeyTypeMapper.FromCurveForKeyAgreement(crv);
        var pair = _generator.Generate(keyType);
        return new EphemeralKeyPair(crv, pair.PublicKey, pair.PrivateKey);
    }

    /// <summary>
    /// Build a JWK <c>epk</c> object for the public half of this pair. Used to populate the JWE
    /// protected header at pack time.
    /// </summary>
    public Jwk ToPublicEpkJwk()
    {
        var keyType = KeyTypeMapper.FromCurveForKeyAgreement(Curve);
        var netDidJwk = NetDidJwkConverter.ToPublicJwk(keyType, PublicKey);
        return new Jwk
        {
            Kty = netDidJwk.Kty,
            Crv = netDidJwk.Crv,
            X = netDidJwk.X,
            Y = netDidJwk.Y,
        };
    }

    /// <summary>Release private-key material as best as the platform allows.</summary>
    public void Clear() => CryptographicOperations.ZeroMemory(PrivateKey);
}
