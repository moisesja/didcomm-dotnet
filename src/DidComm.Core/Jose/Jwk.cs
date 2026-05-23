using System.Text.Json;
using System.Text.Json.Serialization;

namespace DidComm.Jose;

/// <summary>
/// DIDComm-shaped JSON Web Key. Holds the JOSE-standard members directly and preserves any
/// unknown headers in <see cref="AdditionalData"/> so an unpack→repack round-trip is lossless
/// (FR-MSG-15 forward-compatibility requirement).
/// </summary>
/// <remarks>
/// <para>
/// Phase 0 keeps this model deliberately minimal: only the members DIDComm v2.1 actually touches.
/// Conversion to and from <c>NetDid.Core.Jwk</c>'s
/// <see cref="Microsoft.IdentityModel.Tokens.JsonWebKey"/> happens in
/// <see cref="JwkConversion"/>. The <c>Microsoft.IdentityModel.Tokens.JsonWebKey</c> type is
/// strongly opinionated about which members exist; we keep our own shape to avoid leaking that
/// opinion into the rest of DIDComm.
/// </para>
/// <para>
/// Members use base64url-no-pad encoding per RFC 7518 §6 for binary fields (<c>x</c>, <c>y</c>,
/// <c>d</c>). Callers that need raw bytes should round-trip through
/// <see cref="JwkConversion.ExtractPublicKey(Jwk)"/>.
/// </para>
/// </remarks>
public sealed class Jwk
{
    /// <summary>Key type. <c>"OKP"</c> for Ed25519/X25519; <c>"EC"</c> for P-256/P-384/P-521/secp256k1.</summary>
    [JsonPropertyName("kty")]
    public string Kty { get; set; } = string.Empty;

    /// <summary>Curve. One of <c>"Ed25519"</c>, <c>"X25519"</c>, <c>"P-256"</c>, <c>"P-384"</c>, <c>"P-521"</c>, <c>"secp256k1"</c>.</summary>
    [JsonPropertyName("crv")]
    public string? Crv { get; set; }

    /// <summary>Public key X-coordinate (EC) or raw public key (OKP), base64url-no-pad.</summary>
    [JsonPropertyName("x")]
    public string? X { get; set; }

    /// <summary>Public key Y-coordinate. Only present for <c>kty="EC"</c>.</summary>
    [JsonPropertyName("y")]
    public string? Y { get; set; }

    /// <summary>Private key. Base64url-no-pad. Present only on private JWKs.</summary>
    [JsonPropertyName("d")]
    public string? D { get; set; }

    /// <summary>Key identifier. For DIDComm this is a DID URL with a fragment (FR-ENC-16, FR-SIG-03).</summary>
    [JsonPropertyName("kid")]
    public string? Kid { get; set; }

    /// <summary>Intended JWS/JWE algorithm hint (e.g. <c>"EdDSA"</c>, <c>"ECDH-1PU+A256KW"</c>).</summary>
    [JsonPropertyName("alg")]
    public string? Alg { get; set; }

    /// <summary>Public-key use hint (<c>"sig"</c> or <c>"enc"</c>).</summary>
    [JsonPropertyName("use")]
    public string? Use { get; set; }

    /// <summary>
    /// Unknown / extension members preserved verbatim across deserialize→serialize so a JWK
    /// carrying a member DIDComm does not recognize survives an unpack→repack round-trip
    /// (FR-MSG-15). Populated by <see cref="System.Text.Json"/>'s
    /// <see cref="JsonExtensionDataAttribute"/>.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}
