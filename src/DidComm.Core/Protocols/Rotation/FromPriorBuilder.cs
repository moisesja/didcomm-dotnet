using System.Text.Json.Nodes;
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

        // Claims in lexicographic key order (exp, iat, iss, nbf, sub) so identical inputs produce
        // byte-identical payloads across runs. exp/nbf are emitted only when present (RFC 7519
        // §4.1.4/§4.1.5) — a from_prior without them is non-expiring, so callers SHOULD set Exp to
        // bound replay (FR-ROT-05); the lifetime overload below does that in one call. Built via
        // JsonObject (insertion-ordered) rather than interpolation so values are JSON-escaped.
        var claimsObj = new JsonObject();
        if (claims.Exp is long exp) claimsObj["exp"] = exp;
        claimsObj["iat"] = claims.Iat;
        claimsObj["iss"] = claims.Iss;
        if (claims.Nbf is long nbf) claimsObj["nbf"] = nbf;
        claimsObj["sub"] = claims.Sub;
        var claimsJson = claimsObj.ToJsonString();

        // Compact JWS with typ=JWT. DataProofs builds the {alg,kid,typ} protected header from the
        // signer and signs ASCII(b64u(header) "." b64u(payload)); the payload is these claim bytes.
        return await DpSig.JwsBuilder
            .BuildCompactAsync(Encoding.UTF8.GetBytes(claimsJson), signer, typ: "JWT", detachedPayload: false, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Build a freshness-bounded from_prior JWT: sets <c>exp = <paramref name="claims"/>.Iat +
    /// <paramref name="lifetime"/></c> (overriding any <see cref="FromPriorClaims.Exp"/>) so the
    /// rotation token cannot be replayed past the window (FR-ROT-05). A short lifetime is recommended —
    /// a from_prior only needs to ride until a message reaches the new DID (FR-ROT-04).
    /// </summary>
    /// <param name="claims">Sub / Iss / Iat (and optional Nbf); Exp is computed from <paramref name="lifetime"/>.</param>
    /// <param name="signerPrivateJwk">Private JWK; <c>Kid</c> MUST identify a key authorized under <paramref name="claims"/>.Iss <c>authentication</c>.</param>
    /// <param name="lifetime">Positive validity window added to <c>Iat</c> to form <c>exp</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not positive.</exception>
    public static Task<string> BuildAsync(FromPriorClaims claims, Jwk signerPrivateJwk, TimeSpan lifetime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        // exp is second-granular, so a sub-second lifetime would floor to exp == iat — a token already
        // expired at issue. Require at least one whole second (red-team: avoid the silent zero-window).
        if (lifetime < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime,
                "from_prior lifetime must be at least 1 second (exp is second-granular).");

        var bounded = claims with { Exp = claims.Iat + (long)lifetime.TotalSeconds };
        return BuildAsync(bounded, signerPrivateJwk, ct);
    }
}
