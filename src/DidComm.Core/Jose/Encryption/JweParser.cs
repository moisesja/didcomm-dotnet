using System.Text.Json;
using DidComm.Consistency;
using DidComm.Crypto;
using DidComm.Crypto.KeyAgreement;
using DidComm.Crypto.Kdf;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Secrets;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Jose.Encryption;

/// <summary>
/// Parses a DIDComm JWE (General JSON Serialization) and recovers the decrypted payload bytes.
/// Selects between anoncrypt (ECDH-ES+A256KW) and authcrypt (ECDH-1PU+A256KW) based on the
/// protected-header <c>alg</c>; runs FR-ENC-13 <c>apv</c> re-verification before any ECDH
/// happens so a tampered recipient list aborts early.
/// </summary>
internal static class JweParser
{
    /// <summary>Parse <paramref name="packed"/> and decrypt for the first recipient kid present in <paramref name="secretsLookup"/>.</summary>
    /// <param name="packed">JWE General JSON serialization (string).</param>
    /// <param name="secretsLookup">Internal lookup of recipient private keys (test-supplied in Phase 2).</param>
    /// <param name="senderLookup">Internal lookup of sender public keys (authcrypt only; pass <c>null</c> for anoncrypt-only paths).</param>
    /// <param name="cryptoProvider">DidComm crypto provider.</param>
    /// <exception cref="MalformedMessageException">When the JWE shape is invalid.</exception>
    /// <exception cref="CryptoException">When no recipient could be unwrapped or decryption failed.</exception>
    public static JweParseResult Parse(
        string packed,
        IInternalSecretsLookup secretsLookup,
        IInternalSenderKeyLookup? senderLookup,
        DidCommDefaultCryptoProvider cryptoProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        ArgumentNullException.ThrowIfNull(secretsLookup);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var jwe = ParseStructure(packed);
        var header = JweProtectedHeader.Decode(jwe.ProtectedB64u);

        // RFC 7516 §4.1.13: a 'crit' header naming extensions the recipient doesn't understand MUST be
        // rejected. This implementation understands no JWE crit extensions, so any 'crit' is fatal.
        if (header.AdditionalMembers is not null && header.AdditionalMembers.ContainsKey("crit"))
            throw new MalformedMessageException("JWE protected header marks an unsupported extension critical ('crit').");

        // FR-ENC-09 / content-encryption allow-list. Reject any 'enc' outside the supported set
        // before deriving a key, and pin authcrypt (ECDH-1PU) to A256CBC-HS512 — the only AEAD the
        // spec authorizes for authenticated encryption. Mirrors the send-side restriction; stops an
        // attacker steering the receiver into an unintended AEAD and avoids an uncaught
        // NotSupportedException surfacing from GetAead later.
        if (!JoseAlgorithms.IsSupportedContentEncryption(header.Enc))
            throw new CryptoException($"Unsupported JWE 'enc' '{header.Enc}'.");
        if (string.Equals(header.Alg, JoseAlgorithms.Ecdh1PuA256Kw, StringComparison.Ordinal) &&
            !string.Equals(header.Enc, JoseAlgorithms.A256CbcHs512, StringComparison.Ordinal))
            throw new CryptoException(
                $"Authcrypt (ECDH-1PU) requires enc=A256CBC-HS512 (FR-ENC-09); got '{header.Enc}'.");

        var apvRecomputed = ApvComputer.Compute(jwe.Recipients.Select(r => r.Kid));
        if (!string.Equals(apvRecomputed, header.Apv, StringComparison.Ordinal))
            throw new CryptoException(
                $"JWE 'apv' mismatch (FR-ENC-13). Header={header.Apv}, recomputed from recipient kids={apvRecomputed}.");

        var presentKids = secretsLookup.FindPresent(jwe.Recipients.Select(r => r.Kid));
        if (presentKids.Count == 0)
            throw new CryptoException("No recipient kid in the JWE matches a held private key.");

        var matchedRecipient = jwe.Recipients.First(r => presentKids.Contains(r.Kid));
        var privateJwk = secretsLookup.TryGet(matchedRecipient.Kid)
            ?? throw new CryptoException($"Secret lookup returned no key for kid '{matchedRecipient.Kid}'.");

        if (!string.Equals(privateJwk.Crv, header.Epk.Crv, StringComparison.Ordinal))
            throw new CryptoException(
                $"Recipient key curve ({privateJwk.Crv}) does not match JWE 'epk' curve ({header.Epk.Crv}).");

        var ephemeralPubBytes = ExtractEphemeralPublicKey(header.Epk);
        var recipientPrivBytes = Base64Url.Decode(privateJwk.D!);
        var aad = Encoding.ASCII.GetBytes(jwe.ProtectedB64u);
        byte[] kek;
        string senderKid;

        try
        {
            switch (header.Alg)
            {
                case JoseAlgorithms.EcdhEsA256Kw:
                {
                    senderKid = string.Empty;
                    var apvBytes = Base64Url.Decode(header.Apv);
                    kek = EcdhEsKdf.DeriveKeyForReceiver(
                        cryptoProvider.NetDidProvider,
                        KeyTypeMapper.FromCurveForKeyAgreement(privateJwk.Crv!),
                        recipientPrivBytes,
                        ephemeralPubBytes,
                        Encoding.ASCII.GetBytes(header.Alg),
                        apvBytes,
                        keyDataLen: 32);
                    break;
                }
                case JoseAlgorithms.Ecdh1PuA256Kw:
                {
                    if (senderLookup is null)
                        throw new CryptoException("Authcrypt unpack requires a sender-key lookup; none was supplied.");

                    // FR-ENC-14/17: resolve the sender key id from 'skid' when present, else from 'apu'
                    // (= base64url(utf8(skid)); the 1PU draft does not mandate 'skid'). When BOTH are
                    // present they MUST agree, so a peer cannot present one sender identity in 'skid' and
                    // a different one in 'apu'.
                    string skid;
                    if (!string.IsNullOrEmpty(header.Skid))
                    {
                        skid = header.Skid;
                        if (!string.IsNullOrEmpty(header.Apu) &&
                            !string.Equals(header.Apu, ApuComputer.Compute(skid), StringComparison.Ordinal))
                            throw new CryptoException("Authcrypt 'apu' does not match base64url(skid) (FR-ENC-14).");
                    }
                    else if (!string.IsNullOrEmpty(header.Apu))
                    {
                        skid = Encoding.UTF8.GetString(Base64Url.Decode(header.Apu));
                    }
                    else
                    {
                        throw new CryptoException("Authcrypt JWE is missing both 'skid' and 'apu' in the protected header (FR-ENC-17).");
                    }

                    var senderPublicJwk = senderLookup.TryGet(skid)
                        ?? throw new CryptoException($"Could not resolve sender public key for skid '{skid}'.");
                    if (!string.Equals(senderPublicJwk.Crv, privateJwk.Crv, StringComparison.Ordinal))
                        throw new CryptoException(
                            $"Authcrypt sender key curve ({senderPublicJwk.Crv}) does not match recipient curve ({privateJwk.Crv}).");

                    senderKid = skid;
                    var (_, senderPubBytes) = NetDidJwkConverter.ExtractPublicKey(Jose.JwkConversion.ToNetDidJwk(senderPublicJwk));
                    var apvBytes = Base64Url.Decode(header.Apv);
                    // 1PU draft-04 §2.3: PartyUInfo is the UTF-8 bytes of the sender skid (the decoded
                    // 'apu'); fall back to utf8(skid) when the peer omitted 'apu'.
                    var apuBytes = string.IsNullOrEmpty(header.Apu)
                        ? Encoding.UTF8.GetBytes(skid)
                        : Base64Url.Decode(header.Apu);
                    kek = Ecdh1PuKdf.DeriveKeyForReceiver(
                        cryptoProvider.NetDidProvider,
                        KeyTypeMapper.FromCurveForKeyAgreement(privateJwk.Crv!),
                        recipientPrivBytes,
                        ephemeralPubBytes,
                        senderPubBytes,
                        Encoding.ASCII.GetBytes(header.Alg),
                        apuBytes,
                        apvBytes,
                        jwe.Tag,
                        keyDataLen: 32);
                    break;
                }
                default:
                    throw new CryptoException($"Unsupported JWE 'alg' '{header.Alg}'.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recipientPrivBytes);
        }

        byte[] cek;
        try
        {
            cek = cryptoProvider.KeyUnwrap(JoseAlgorithms.A256Kw, kek, matchedRecipient.EncryptedKey);
        }
        catch (CryptographicException ex)
        {
            throw new CryptoException($"AES-KW unwrap failed for recipient kid '{matchedRecipient.Kid}'.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        byte[] plaintext;
        try
        {
            plaintext = cryptoProvider.AeadDecrypt(header.Enc, cek, jwe.Iv, aad, jwe.Ciphertext, jwe.Tag);
        }
        catch (CryptographicException ex)
        {
            throw new CryptoException($"AEAD decryption failed ('{header.Enc}').", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }

        // FR-CONSIST-02 lives one level up (the caller knows the inner plaintext's `to` once it
        // is parsed); the parser surfaces enough metadata for the higher layer to enforce it.
        return new JweParseResult(
            Plaintext: plaintext,
            Algorithm: header.Alg,
            ContentEncryption: header.Enc,
            RecipientKid: matchedRecipient.Kid,
            AllRecipientKids: jwe.Recipients.Select(r => r.Kid).ToArray(),
            SenderKid: senderKid,
            IsAuthenticated: !string.IsNullOrEmpty(senderKid));
    }

    /// <summary>
    /// Read just enough of a packed JWE to surface the recipient kid list and (for authcrypt)
    /// the <c>skid</c> — no crypto is performed, no exceptions are thrown for invalid bodies
    /// past the header. Used by the Phase 3 facade to pre-warm DID resolution and secret
    /// lookups before invoking the full <see cref="Parse"/>.
    /// </summary>
    /// <param name="packed">JWE General JSON serialization.</param>
    /// <returns>The structural metadata; never <c>null</c>. <c>Skid</c> is <c>null</c> for anoncrypt.</returns>
    /// <exception cref="MalformedMessageException">When the input is not a JWE-shaped JSON object.</exception>
    public static JwePeekResult PeekRecipients(string packed)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        var jwe = ParseStructure(packed);
        var header = JweProtectedHeader.Decode(jwe.ProtectedB64u);
        return new JwePeekResult(
            Algorithm: header.Alg,
            Skid: string.IsNullOrEmpty(header.Skid) ? null : header.Skid,
            RecipientKids: jwe.Recipients.Select(r => r.Kid).ToArray());
    }

    private static byte[] ExtractEphemeralPublicKey(Jwk epk)
    {
        try
        {
            var (_, bytes) = NetDidJwkConverter.ExtractPublicKey(Jose.JwkConversion.ToNetDidJwk(epk));
            return bytes;
        }
        catch (CryptographicException ex)
        {
            // Off-curve epk caught by net-did's EcPointValidator (FR-ENC-03).
            throw new CryptoException("JWE 'epk' is not on the asserted curve.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new CryptoException("JWE 'epk' JWK is malformed.", ex);
        }
    }

    private static ParsedJwe ParseStructure(string packed)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(packed, DidCommJson.StrictDocument);
        }
        catch (JsonException ex)
        {
            throw new MalformedMessageException("JWE is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MalformedMessageException("JWE root is not a JSON object.");

            // Every required member is read with an explicit type check so a missing or mistyped
            // member (e.g. a numeric 'iv') yields a MalformedMessageException — the parser's documented
            // failure — rather than a raw KeyNotFoundException / ArgumentNullException at the boundary.
            var protectedB64u = RequireString(root, "protected");
            var iv = DecodeB64u(RequireString(root, "iv"), "iv");
            var ciphertext = DecodeB64u(RequireString(root, "ciphertext"), "ciphertext");
            var tag = DecodeB64u(RequireString(root, "tag"), "tag");

            if (!root.TryGetProperty("recipients", out var recipientsEl) || recipientsEl.ValueKind != JsonValueKind.Array)
                throw new MalformedMessageException("JWE is missing the 'recipients' array.");

            var recipients = new List<ParsedRecipient>();
            foreach (var rec in recipientsEl.EnumerateArray())
            {
                if (rec.ValueKind != JsonValueKind.Object ||
                    !rec.TryGetProperty("header", out var hdr) || hdr.ValueKind != JsonValueKind.Object)
                    throw new MalformedMessageException("JWE recipient is missing its 'header' object.");
                var kid = RequireString(hdr, "kid");
                var encryptedKey = DecodeB64u(RequireString(rec, "encrypted_key"), "encrypted_key");
                recipients.Add(new ParsedRecipient(kid, encryptedKey));
            }

            if (recipients.Count == 0)
                throw new MalformedMessageException("JWE has zero recipients.");

            return new ParsedJwe(protectedB64u, recipients, iv, ciphertext, tag);
        }
    }

    private static string RequireString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new MalformedMessageException($"JWE is missing required string member '{name}'.");
        return el.GetString()!;
    }

    private static byte[] DecodeB64u(string value, string name)
    {
        try
        {
            return Base64Url.Decode(value);
        }
        catch (FormatException ex)
        {
            throw new MalformedMessageException($"JWE member '{name}' is not valid base64url.", ex);
        }
    }

    private sealed record ParsedRecipient(string Kid, byte[] EncryptedKey);
    private sealed record ParsedJwe(string ProtectedB64u, IReadOnlyList<ParsedRecipient> Recipients, byte[] Iv, byte[] Ciphertext, byte[] Tag);
}

/// <summary>Outcome of a successful JWE parse.</summary>
/// <param name="Plaintext">Decrypted bytes (may be a plaintext JWM or a nested JWS).</param>
/// <param name="Algorithm">JOSE <c>alg</c> (<c>ECDH-ES+A256KW</c> or <c>ECDH-1PU+A256KW</c>).</param>
/// <param name="ContentEncryption">JOSE <c>enc</c>.</param>
/// <param name="RecipientKid">The recipient kid whose private key actually unwrapped the CEK.</param>
/// <param name="AllRecipientKids">Every recipient kid carried in the envelope (FR-API-04 metadata).</param>
/// <param name="SenderKid">Sender <c>skid</c> for authcrypt; empty string for anoncrypt.</param>
/// <param name="IsAuthenticated">True for authcrypt; false for anoncrypt (anonymous sender, FR-API-04).</param>
internal sealed record JweParseResult(
    byte[] Plaintext,
    string Algorithm,
    string ContentEncryption,
    string RecipientKid,
    IReadOnlyList<string> AllRecipientKids,
    string SenderKid,
    bool IsAuthenticated);

/// <summary>Structural peek into a JWE — kids only, no decryption.</summary>
/// <param name="Algorithm">Protected-header <c>alg</c> (e.g. <c>ECDH-1PU+A256KW</c>).</param>
/// <param name="Skid">Sender key identifier for authcrypt; <c>null</c> for anoncrypt.</param>
/// <param name="RecipientKids">Recipient kids in declared order.</param>
internal sealed record JwePeekResult(string Algorithm, string? Skid, IReadOnlyList<string> RecipientKids);
