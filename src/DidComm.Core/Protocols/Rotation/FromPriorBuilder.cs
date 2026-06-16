using System.Text.Json;
using DidComm.Jose.Signing;
using DpSig = DataProofsDotnet.Jose.Signing;

namespace DidComm.Protocols.Rotation;

/// <summary>
/// Constructs the JWT carried in a DIDComm <c>from_prior</c> header (FR-ROT-01). The JWT is
/// signed by a key authorized under the <strong>prior</strong> DID's <c>authentication</c>
/// relationship; the JWT's <c>kid</c> identifies that key.
/// </summary>
/// <remarks>
/// Compact JOSE serialization per RFC 7519 (JWT) and RFC 7515 (JWS Compact), built on
/// DataProofsDotnet.Jose's <see cref="DpSig.JwsBuilder"/>. The <c>typ</c> header is set to
/// <c>JWT</c>. Claims are emitted in lexicographic key order so the same inputs produce
/// byte-identical payloads across runs.
/// </remarks>
public static class FromPriorBuilder
{
    /// <summary>Build a from_prior JWT from validated claims using the signer's private JWK.</summary>
    /// <param name="claims">Sub / Iss / Iat triple.</param>
    /// <param name="signerPrivateJwk">Private JWK; <c>Kid</c> MUST identify a key authorized under <paramref name="claims"/>.Iss <c>authentication</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> BuildAsync(FromPriorClaims claims, Jwk signerPrivateJwk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(signerPrivateJwk);

        // JwsSignerFactory validates crv/d/kid and adapts the JWK into a NetCrypto-backed signer.
        var signer = JwsSignerFactory.FromPrivateJwk(signerPrivateJwk);

        // Claims — key order: iat, iss, sub (lexicographic). Serialize rather than interpolate so
        // an unusual value is JSON-escaped, not injected.
        var claimsJson = JsonSerializer.Serialize(new
        {
            iat = claims.Iat,
            iss = claims.Iss,
            sub = claims.Sub,
        });

        // Compact JWS with typ=JWT. DataProofs builds the {alg,kid,typ} protected header from the
        // signer and signs ASCII(b64u(header) "." b64u(payload)); the payload is these claim bytes.
        return await DpSig.JwsBuilder
            .BuildCompactAsync(Encoding.UTF8.GetBytes(claimsJson), signer, typ: "JWT", detachedPayload: false, ct)
            .ConfigureAwait(false);
    }
}
