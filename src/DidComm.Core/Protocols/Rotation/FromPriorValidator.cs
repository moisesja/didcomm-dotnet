using System.Text.Json;
using DidComm.Crypto;
using DidComm.Crypto.KeyAgreement;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Resolution;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Protocols.Rotation;

/// <summary>
/// Verifies a DIDComm <c>from_prior</c> JWT against the prior DID's <c>authentication</c>
/// relationship and extracts the claims (FR-ROT-01..02). Out-of-order pre-rotation rejection
/// (FR-ROT-05) is the application's responsibility — the validator surfaces the iat / iss
/// pair so a higher layer can compare against its known-active state.
/// </summary>
public static class FromPriorValidator
{
    /// <summary>Validate a from_prior JWT against <paramref name="currentSenderDid"/> and return its claims.</summary>
    /// <param name="jwt">Compact-serialized JWT.</param>
    /// <param name="currentSenderDid">The message <c>from</c> DID (the post-rotation identity).</param>
    /// <param name="keyService">DID resolver used to authorize the JWT signer key under <c>iss</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ProtocolException">When the JWT is malformed.</exception>
    /// <exception cref="ConsistencyException">When the signature is invalid, the kid is not authorized in the iss DID, or sub does not match <paramref name="currentSenderDid"/> (FR-ROT-02).</exception>
    public static Task<FromPriorClaims> ValidateAsync(
        string jwt,
        string currentSenderDid,
        IDidKeyService keyService,
        CancellationToken ct = default)
        => ValidateAsync(jwt, currentSenderDid, keyService, new DefaultCryptoProvider(), ct);

    /// <summary>Test seam: validate with an explicit crypto provider.</summary>
    /// <param name="jwt">Compact-serialized JWT.</param>
    /// <param name="currentSenderDid">The message <c>from</c> DID (the post-rotation identity).</param>
    /// <param name="keyService">DID resolver used to authorize the JWT signer key under <c>iss</c>.</param>
    /// <param name="cryptoProvider">Crypto provider for signature verification.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<FromPriorClaims> ValidateAsync(
        string jwt,
        string currentSenderDid,
        IDidKeyService keyService,
        DefaultCryptoProvider cryptoProvider,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jwt);
        ArgumentException.ThrowIfNullOrEmpty(currentSenderDid);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new ProtocolException("from_prior JWT must have three dot-separated segments (compact JWS).");

        var headerJson = Encoding.UTF8.GetString(Base64Url.Decode(parts[0]));
        var claimsJson = Encoding.UTF8.GetString(Base64Url.Decode(parts[1]));
        var signature = Base64Url.Decode(parts[2]);

        string? alg, kid;
        try
        {
            using var headerDoc = JsonDocument.Parse(headerJson, DidCommJson.StrictDocument);
            alg = headerDoc.RootElement.GetProperty("alg").GetString();
            kid = headerDoc.RootElement.GetProperty("kid").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            throw new ProtocolException("from_prior JWT header is malformed.", ex);
        }
        if (string.IsNullOrEmpty(alg) || string.IsNullOrEmpty(kid))
            throw new ProtocolException("from_prior JWT header is missing 'alg' or 'kid'.");

        FromPriorClaims claims;
        try
        {
            using var claimsDoc = JsonDocument.Parse(claimsJson, DidCommJson.StrictDocument);
            var iss = claimsDoc.RootElement.GetProperty("iss").GetString()
                ?? throw new ProtocolException("from_prior JWT 'iss' is missing or null.");
            var sub = claimsDoc.RootElement.GetProperty("sub").GetString()
                ?? throw new ProtocolException("from_prior JWT 'sub' is missing or null.");
            var iat = claimsDoc.RootElement.GetProperty("iat").GetInt64();
            long? exp = claimsDoc.RootElement.TryGetProperty("exp", out var expEl) && expEl.ValueKind == JsonValueKind.Number
                ? expEl.GetInt64() : null;
            long? nbf = claimsDoc.RootElement.TryGetProperty("nbf", out var nbfEl) && nbfEl.ValueKind == JsonValueKind.Number
                ? nbfEl.GetInt64() : null;
            claims = new FromPriorClaims(Sub: sub, Iss: iss, Iat: iat, Exp: exp, Nbf: nbf);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            throw new ProtocolException("from_prior JWT claims are malformed.", ex);
        }

        // FR-ROT-02 — sub MUST equal the message 'from' DID.
        if (!string.Equals(claims.Sub, currentSenderDid, StringComparison.Ordinal))
        {
            throw new ConsistencyException(
                $"from_prior 'sub' ({claims.Sub}) does not match message 'from' ({currentSenderDid}) (FR-ROT-02).");
        }

        // FR-ROT-01 — the JWT MUST be signed by a key authorized in the prior DID's authentication relationship.
        var authorized = await keyService.IsKeyAuthorizedAsync(
            claims.Iss, kid, VerificationRelationship.Authentication, ct).ConfigureAwait(false);
        if (!authorized)
        {
            throw new ConsistencyException(
                $"from_prior signer kid '{kid}' is not authorized under '{claims.Iss}' authentication (FR-ROT-01).");
        }

        var signerPubs = await keyService.GetVerificationMethodsAsync(
            claims.Iss, VerificationRelationship.Authentication, ct).ConfigureAwait(false);
        var signerJwk = signerPubs.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal))
            ?? throw new ConsistencyException($"from_prior signer kid '{kid}' not present in resolved keys (FR-ROT-01).");

        var (_, publicBytes) = NetDidJwkConverter.ExtractPublicKey(JwkConversion.ToNetDidJwk(signerJwk));
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

        // Cross-check: the header alg MUST match the JWK's curve. Prevents a downgrade where
        // an attacker swaps the alg to one a different relationship key happens to satisfy.
        var expectedAlg = KeyTypeMapper.ToJwsAlgorithm(signerJwk.Crv!);
        if (!string.Equals(expectedAlg, alg, StringComparison.Ordinal))
        {
            throw new ConsistencyException(
                $"from_prior JWT 'alg' ({alg}) does not match the resolved signer key's curve algorithm ({expectedAlg}).");
        }

        if (!cryptoProvider.Verify(alg, publicBytes, signingInput, signature))
        {
            throw new ConsistencyException("from_prior JWT signature did not verify (FR-ROT-01).");
        }

        return claims;
    }
}
