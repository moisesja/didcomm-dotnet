using System.Text.Json;
using DidComm.Consistency;
using DidComm.Crypto;
using DidComm.Crypto.KeyAgreement;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Messages;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Jose.Signing;

/// <summary>
/// Parses and verifies a DIDComm signed envelope (JWS, JSON serialization) per FR-SIG-01..06.
/// Accepts both Flattened and General JSON serializations on receive (FR-SIG-02).
/// </summary>
/// <remarks>
/// Verification needs the signer's *public* key. Phase 2 takes it as a caller-supplied
/// lookup delegate (Phase 3's resolver will provide it through <c>IDidKeyService</c>). At
/// least one signature MUST verify for the parse to succeed; on success the parser also
/// runs the FR-CONSIST-03 check (signer kid ↔ plaintext from).
/// </remarks>
internal static class JwsParser
{
    /// <summary>
    /// Parse <paramref name="packed"/>, verify at least one signature, and return the inner message
    /// alongside the verified signer info.
    /// </summary>
    /// <param name="packed">Packed JWS JSON string (General or Flattened).</param>
    /// <param name="resolveSignerPublicJwk">
    /// Function from signer <c>kid</c> to the verifier's public JWK. Returns <c>null</c> when
    /// the kid is unknown; null is treated as "skip this signature, try the next".
    /// </param>
    /// <param name="cryptoProvider">Crypto provider used to verify (delegated to net-did).</param>
    /// <exception cref="MalformedMessageException">When the JWS structure is invalid.</exception>
    /// <exception cref="CryptoException">When no signature verifies.</exception>
    /// <exception cref="ConsistencyException">When FR-CONSIST-03 (signer kid ↔ from) fails.</exception>
    public static JwsParseResult Parse(
        string packed,
        Func<string, Jwk?> resolveSignerPublicJwk,
        ICryptoProvider cryptoProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        ArgumentNullException.ThrowIfNull(resolveSignerPublicJwk);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        using var doc = JsonDocument.Parse(packed, DidCommJson.StrictDocument);
        var root = doc.RootElement;

        if (!root.TryGetProperty("payload", out var payloadElement) || payloadElement.ValueKind != JsonValueKind.String)
            throw new MalformedMessageException("JWS is missing required 'payload' string.");

        var payloadB64u = payloadElement.GetString()!;
        var payloadBytes = Base64Url.Decode(payloadB64u);

        var signatures = ExtractSignatures(root).ToList();
        if (signatures.Count == 0)
            throw new MalformedMessageException("JWS contains no signatures.");

        Exception? lastFailure = null;
        foreach (var sig in signatures)
        {
            var publicJwk = resolveSignerPublicJwk(sig.Kid);
            if (publicJwk is null)
            {
                lastFailure = new CryptoException($"No verifier public JWK supplied for signer kid '{sig.Kid}'.");
                continue;
            }

            try
            {
                var header = JwsProtectedHeader.Decode(sig.ProtectedB64u);
                if (!string.Equals(header.Alg, KeyTypeMapper.ToJwsAlgorithm(publicJwk.Crv!), StringComparison.Ordinal))
                {
                    lastFailure = new CryptoException(
                        $"JWS protected 'alg' ({header.Alg}) does not match the public key's curve ({publicJwk.Crv}).");
                    continue;
                }

                // The JWS spec allows 'kid' in either the protected or the unprotected header.
                // Spec vectors typically carry it only in the unprotected header. When both
                // are present they MUST match; when only one is present, use that one.
                if (!string.IsNullOrEmpty(header.Kid)
                    && !string.IsNullOrEmpty(sig.Kid)
                    && !string.Equals(header.Kid, sig.Kid, StringComparison.Ordinal))
                {
                    lastFailure = new MalformedMessageException(
                        $"JWS protected 'kid' ({header.Kid}) does not match the unprotected header 'kid' ({sig.Kid}).");
                    continue;
                }

                var signingInput = Encoding.ASCII.GetBytes(sig.ProtectedB64u + "." + payloadB64u);
                var publicKeyBytes = ExtractPublicKeyBytes(publicJwk);
                if (!cryptoProvider.Verify(header.Alg, publicKeyBytes, signingInput, sig.Signature))
                {
                    lastFailure = new CryptoException($"JWS signature did not verify for kid '{sig.Kid}'.");
                    continue;
                }

                var innerMessage = ParseInnerMessage(payloadBytes);

                if (innerMessage.From is not null)
                    AddressingConsistency.CheckSignedFromMatchesSignerKid(innerMessage.From, sig.Kid);

                return new JwsParseResult(innerMessage, header.Alg, sig.Kid, payloadBytes);
            }
            catch (Exception ex) when (ex is CryptoException or MalformedMessageException or ConsistencyException)
            {
                lastFailure = ex;
            }
        }

        if (lastFailure is null)
            throw new CryptoException("No JWS signature verified.");
        throw lastFailure;
    }

    private static IEnumerable<RawSignature> ExtractSignatures(JsonElement root)
    {
        // Flattened: payload + protected + (optional header) + signature at the top level.
        if (root.TryGetProperty("signature", out var sigEl)
            && root.TryGetProperty("protected", out var protEl))
        {
            var kid = root.TryGetProperty("header", out var hdr) && hdr.ValueKind == JsonValueKind.Object
                ? hdr.TryGetProperty("kid", out var kEl) ? kEl.GetString() ?? string.Empty : string.Empty
                : string.Empty;

            var protB64u = protEl.GetString()!;
            if (string.IsNullOrEmpty(kid))
            {
                // Kid MAY live only in protected; pull it from there as a fallback.
                kid = JwsProtectedHeader.Decode(protB64u).Kid;
            }

            yield return new RawSignature(protB64u, kid, Base64Url.Decode(sigEl.GetString()!));
            yield break;
        }

        // General: payload + signatures: [ { protected, header, signature }, … ]
        if (root.TryGetProperty("signatures", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in arr.EnumerateArray())
            {
                var protB64u = entry.GetProperty("protected").GetString()!;
                var kid = entry.TryGetProperty("header", out var hdr2) && hdr2.ValueKind == JsonValueKind.Object
                    ? hdr2.TryGetProperty("kid", out var kEl) ? kEl.GetString() ?? string.Empty : string.Empty
                    : string.Empty;
                if (string.IsNullOrEmpty(kid))
                    kid = JwsProtectedHeader.Decode(protB64u).Kid;
                var sig = Base64Url.Decode(entry.GetProperty("signature").GetString()!);
                yield return new RawSignature(protB64u, kid, sig);
            }
        }
    }

    private static byte[] ExtractPublicKeyBytes(Jwk publicJwk)
    {
        var (_, bytes) = NetDidJwkConverter.ExtractPublicKey(Jose.JwkConversion.ToNetDidJwk(publicJwk));
        return bytes;
    }

    private static Message ParseInnerMessage(ReadOnlySpan<byte> payloadBytes)
    {
        Message? msg;
        try
        {
            msg = JsonSerializer.Deserialize<Message>(payloadBytes, DidCommJson.Default);
        }
        catch (JsonException ex)
        {
            throw new MalformedMessageException("Signed JWS payload is not a valid DIDComm plaintext message.", ex);
        }
        return msg ?? throw new MalformedMessageException("Signed JWS payload deserialized to null.");
    }

    private readonly record struct RawSignature(string ProtectedB64u, string Kid, byte[] Signature);
}

/// <summary>Outcome of a successful JWS parse: inner message plus signer metadata.</summary>
/// <param name="Message">The recovered inner plaintext message.</param>
/// <param name="SignatureAlgorithm">JOSE <c>alg</c> of the verified signature (e.g. <c>"EdDSA"</c>).</param>
/// <param name="SignerKid">The verified signer key identifier.</param>
/// <param name="PayloadBytes">Raw decoded payload bytes (the canonical JSON of the inner message).</param>
internal sealed record JwsParseResult(Message Message, string SignatureAlgorithm, string SignerKid, byte[] PayloadBytes);
