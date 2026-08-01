using System.Text.Json;
using DidComm.Consistency;
using DidComm.Crypto.KeyAgreement;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Resolution;
using DpJwkConversion = DataProofsDotnet.Jose.JwkConversion;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Protocols.Rotation;

/// <summary>
/// Verifies a DIDComm <c>from_prior</c> JWT against the prior DID's <c>authentication</c>
/// relationship and extracts the claims (FR-ROT-01..02). A JWT that omits <c>sub</c> is the
/// relationship-termination form (FR-ROT-06) and is only valid on a message without
/// <c>from</c>. Out-of-order pre-rotation rejection (FR-ROT-05) is the application's
/// responsibility — the validator surfaces the iat / iss pair so a higher layer can compare
/// against its known-active state.
/// </summary>
/// <remarks>
/// When the supplied key service implements <see cref="IDidKeyBindingService"/>, authority and
/// verifying key are taken from a single resolution (FR-CONSIST-07 / #56) so a rotation can never
/// be accepted by combining two document versions. Legacy key services keep the pre-1.4.0
/// two-resolution behavior.
/// </remarks>
public static class FromPriorValidator
{
    /// <summary>Validate a from_prior JWT against <paramref name="currentSenderDid"/> and return its claims.</summary>
    /// <param name="jwt">Compact-serialized JWT.</param>
    /// <param name="currentSenderDid">The message <c>from</c> DID (the post-rotation identity), or <c>null</c> when the message carries no <c>from</c> — required for the relationship-termination form (FR-ROT-06), whose JWT omits <c>sub</c>.</param>
    /// <param name="keyService">DID resolver used to authorize the JWT signer key under <c>iss</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ProtocolException">When the JWT is malformed.</exception>
    /// <exception cref="ConsistencyException">When the signature is invalid, the kid is not authorized in the iss DID, sub does not match <paramref name="currentSenderDid"/> (FR-ROT-02), or the sub/from presence combination is invalid (a termination JWT on a message with <c>from</c>, or a rotation JWT on a message without it — FR-ROT-06/FR-ROT-02).</exception>
    /// <example>
    /// <code>
    /// // Rotation: sub == message.from (FR-ROT-02).
    /// var rotation = await FromPriorValidator.ValidateAsync(jwt, message.From!, keyService);
    /// // Termination: the JWT omits sub and the message has no from (FR-ROT-06).
    /// var termination = await FromPriorValidator.ValidateAsync(jwt, currentSenderDid: null, keyService);
    /// // termination.IsTermination == true
    /// </code>
    /// </example>
    public static Task<FromPriorClaims> ValidateAsync(
        string jwt,
        string? currentSenderDid,
        IDidKeyService keyService,
        CancellationToken ct = default)
        => ValidateAsync(jwt, currentSenderDid, keyService, new JoseCryptoProvider(), ct);

    /// <summary>Test seam: validate with an explicit crypto provider.</summary>
    /// <param name="jwt">Compact-serialized JWT.</param>
    /// <param name="currentSenderDid">The message <c>from</c> DID (the post-rotation identity); <c>null</c> for the FR-ROT-06 termination form.</param>
    /// <param name="keyService">DID resolver used to authorize the JWT signer key under <c>iss</c>.</param>
    /// <param name="cryptoProvider">Crypto provider for signature verification.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<FromPriorClaims> ValidateAsync(
        string jwt,
        string? currentSenderDid,
        IDidKeyService keyService,
        JoseCryptoProvider cryptoProvider,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jwt);
        // null means "the message has no 'from'" (FR-ROT-06 termination); empty is a caller bug.
        if (currentSenderDid is not null)
            ArgumentException.ThrowIfNullOrEmpty(currentSenderDid);
        ArgumentNullException.ThrowIfNull(keyService);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new ProtocolException("from_prior JWT must have three dot-separated segments (compact JWS).");

        byte[] signature;
        string? alg, kid;
        FromPriorClaims claims;
        try
        {
            // Everything in this block is a pure function of the attacker-controlled JWT string:
            // base64url-decode (FormatException on bad chars, ArgumentException on an empty segment),
            // JSON parse (JsonException), member access (KeyNotFoundException), and typed reads
            // (InvalidOperationException on a wrong JSON value kind, FormatException on an out-of-range
            // number). All of these mean "the JWT is malformed" and MUST surface as the typed
            // ProtocolException rather than escaping UnpackAsync as a raw runtime exception (issue #19,
            // FR-API-07). The resolver/crypto calls below stay OUTSIDE this block so a genuine
            // ConsistencyException/CryptoException is never masked.
            var headerJson = Encoding.UTF8.GetString(Base64Url.Decode(parts[0]));
            var claimsJson = Encoding.UTF8.GetString(Base64Url.Decode(parts[1]));
            signature = Base64Url.Decode(parts[2]);

            using var headerDoc = JsonDocument.Parse(headerJson, DidCommJson.StrictDocument);
            alg = headerDoc.RootElement.GetProperty("alg").GetString();
            kid = headerDoc.RootElement.GetProperty("kid").GetString();

            // RFC 7515 §4.1.11: fail closed on a 'crit' header — the library understands no from_prior
            // crit extensions, so any are by definition unsupported. Mirrors JwsParser/JweParser crit
            // rejection (#26). 'crit' is covered by the signature but was previously read and ignored.
            // The check runs before signature verification, so a crit envelope is rejected as malformed.
            if (headerDoc.RootElement.TryGetProperty("crit", out _))
                throw new ProtocolException("from_prior JWT marks an unsupported extension critical ('crit').");

            using var claimsDoc = JsonDocument.Parse(claimsJson, DidCommJson.StrictDocument);
            var iss = claimsDoc.RootElement.GetProperty("iss").GetString()
                ?? throw new ProtocolException("from_prior JWT 'iss' is missing or null.");
            // 'sub' ABSENT is the relationship-termination form (FR-ROT-06); an explicit JSON null
            // is neither a rotation nor the omit-sub wire shape, so it stays malformed.
            string? sub = null;
            if (claimsDoc.RootElement.TryGetProperty("sub", out var subEl))
            {
                sub = subEl.GetString()
                    ?? throw new ProtocolException("from_prior JWT 'sub' is null (omit it entirely for termination, FR-ROT-06).");
            }
            var iat = claimsDoc.RootElement.GetProperty("iat").GetInt64();
            long? exp = claimsDoc.RootElement.TryGetProperty("exp", out var expEl) && expEl.ValueKind == JsonValueKind.Number
                ? expEl.GetInt64() : null;
            long? nbf = claimsDoc.RootElement.TryGetProperty("nbf", out var nbfEl) && nbfEl.ValueKind == JsonValueKind.Number
                ? nbfEl.GetInt64() : null;
            claims = new FromPriorClaims(Sub: sub, Iss: iss, Iat: iat, Exp: exp, Nbf: nbf);
        }
        catch (Exception ex)
            when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException)
        {
            // Generic message: do not echo the offending alg/kid/segment (no parse-failure oracle).
            throw new ProtocolException("from_prior JWT is malformed.", ex);
        }

        if (string.IsNullOrEmpty(alg) || string.IsNullOrEmpty(kid))
            throw new ProtocolException("from_prior JWT header is missing 'alg' or 'kid'.");

        if (claims.Sub is null)
        {
            // FR-ROT-06 — termination form: the JWT omits 'sub' and the carrying message MUST have
            // no 'from'. A termination JWT on a message that names a sender is contradictory (it
            // asserts both "no successor identity" and a concrete sender) and is rejected.
            if (currentSenderDid is not null)
            {
                throw new ConsistencyException(
                    $"from_prior omits 'sub' (relationship termination, FR-ROT-06) but the message carries 'from' ({currentSenderDid}). Drop.");
            }
        }
        else if (currentSenderDid is null)
        {
            // A rotation JWT (sub present) on a message without 'from' has nothing to bind sub to.
            throw new ConsistencyException(
                $"from_prior 'sub' ({claims.Sub}) is present but the message has no 'from' to match (FR-ROT-02).");
        }
        else if (!string.Equals(claims.Sub, currentSenderDid, StringComparison.Ordinal))
        {
            // FR-ROT-02 — sub MUST equal the message 'from' DID.
            throw new ConsistencyException(
                $"from_prior 'sub' ({claims.Sub}) does not match message 'from' ({currentSenderDid}) (FR-ROT-02).");
        }

        // 'iss' MUST be a bare DID, not a decorated DID URL. Authorization below compares DID
        // subjects, so 'did:x', 'did:x?v=1', and 'did:x/p' would all authorize identically while
        // remaining distinct strings — and 'iss' is exactly what an application keys its rotation
        // replay / already-rotated state on (FR-ROT-05 is delegated to that layer). Whoever holds the
        // prior DID's key could otherwise mint unlimited equivalent-but-distinct 'iss' values, each
        // with a valid signature, and slip past such a check.
        if (!string.Equals(DidSubject.DidSubjectOf(claims.Iss), claims.Iss, StringComparison.Ordinal))
        {
            throw new ConsistencyException(
                $"from_prior 'iss' ({claims.Iss}) must be a bare DID, not a DID URL (FR-ROT-01).");
        }

        // FR-ROT-01 — the JWT MUST be signed by a key authorized in the prior DID's authentication
        // relationship. FR-CONSIST-07 (#56): the authority evidence and the key that verifies the
        // signature MUST come from the SAME resolved document. The pre-1.4.0 shape authorized the kid
        // in one resolution and then fetched the verifying key in a second, so a resolver whose
        // document changed between the calls could authorize a victim's key while the JWT was verified
        // with a replacement key under the same kid — forging an accepted rotation that hands the
        // attacker the prior DID's relationships. Resolve once and use that binding for both.
        Jwk signerJwk;
        if (keyService is IDidKeyBindingService bindingService)
        {
            // The binding is looked up by kid, so the document resolved is the KID's subject rather
            // than 'iss' as in the legacy shape. Bind the two before resolving: a kid naming some
            // other DID can never authorize this rotation anyway, and rejecting it up front means an
            // attacker-chosen kid cannot make us resolve an arbitrary DID first (FR-ROT-01).
            if (!DidSubject.SameDidSubject(kid, claims.Iss))
            {
                throw new ConsistencyException(
                    $"from_prior signer kid '{kid}' is not under the prior DID '{claims.Iss}' (FR-ROT-01).");
            }

            var binding = await bindingService.ResolveKeyBindingAsync(
                kid, VerificationRelationship.Authentication, ct).ConfigureAwait(false)
                ?? throw new ConsistencyException(
                    $"from_prior signer kid '{kid}' is not authorized under '{claims.Iss}' authentication (FR-ROT-01).");

            // The binding proves which document the key came from; the controller rule proves that
            // document's subject really authorizes it for 'iss'.
            try
            {
                AddressingConsistency.CheckCapturedBindingAuthorized(claims.Iss, binding);
            }
            catch (ConsistencyException ex)
            {
                throw new ConsistencyException(
                    $"from_prior signer kid '{kid}' is not authorized under '{claims.Iss}' authentication (FR-ROT-01). {ex.Message}", ex);
            }

            signerJwk = binding.PublicJwk;
        }
        else
        {
            // Legacy key services (no binding capability) keep the pre-1.4.0 two-resolution shape.
            var authorized = await keyService.IsKeyAuthorizedAsync(
                claims.Iss, kid, VerificationRelationship.Authentication, ct).ConfigureAwait(false);
            if (!authorized)
            {
                throw new ConsistencyException(
                    $"from_prior signer kid '{kid}' is not authorized under '{claims.Iss}' authentication (FR-ROT-01).");
            }

            var signerPubs = await keyService.GetVerificationMethodsAsync(
                claims.Iss, VerificationRelationship.Authentication, ct).ConfigureAwait(false);
            signerJwk = signerPubs.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal))
                ?? throw new ConsistencyException($"from_prior signer kid '{kid}' not present in resolved keys (FR-ROT-01).");
        }

        var (_, publicBytes) = DpJwkConversion.ExtractPublicKey(signerJwk);
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
