using System.Text.Json;
using DidComm.Consistency;
using DidComm.Crypto;
using DidComm.Crypto.KeyAgreement;
using DidComm.Crypto.Kdf;
using DidComm.Exceptions;
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
                    if (string.IsNullOrEmpty(header.Skid))
                        throw new CryptoException("Authcrypt JWE is missing 'skid' in the protected header.");
                    if (senderLookup is null)
                        throw new CryptoException("Authcrypt unpack requires a sender-key lookup; none was supplied.");
                    var senderPublicJwk = senderLookup.TryGet(header.Skid)
                        ?? throw new CryptoException($"Could not resolve sender public key for skid '{header.Skid}'.");
                    if (!string.Equals(senderPublicJwk.Crv, privateJwk.Crv, StringComparison.Ordinal))
                        throw new CryptoException(
                            $"Authcrypt sender key curve ({senderPublicJwk.Crv}) does not match recipient curve ({privateJwk.Crv}).");

                    senderKid = header.Skid;
                    var (_, senderPubBytes) = NetDidJwkConverter.ExtractPublicKey(Jose.JwkConversion.ToNetDidJwk(senderPublicJwk));
                    var apvBytes = Base64Url.Decode(header.Apv);
                    // 1PU draft-04 §2.3: PartyUInfo is the base64url-DECODED apu (the original
                    // UTF-8 bytes of the sender skid), not the b64u string itself.
                    var apuBytes = string.IsNullOrEmpty(header.Apu) ? Array.Empty<byte>() : Base64Url.Decode(header.Apu);
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
        using var doc = JsonDocument.Parse(packed);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new MalformedMessageException("JWE root is not a JSON object.");

        var protectedB64u = root.GetProperty("protected").GetString()
            ?? throw new MalformedMessageException("JWE is missing 'protected'.");
        var iv = Base64Url.Decode(root.GetProperty("iv").GetString()!);
        var ciphertext = Base64Url.Decode(root.GetProperty("ciphertext").GetString()!);
        var tag = Base64Url.Decode(root.GetProperty("tag").GetString()!);

        var recipients = new List<ParsedRecipient>();
        foreach (var rec in root.GetProperty("recipients").EnumerateArray())
        {
            var kid = rec.GetProperty("header").GetProperty("kid").GetString()
                ?? throw new MalformedMessageException("JWE recipient is missing 'header.kid'.");
            var encryptedKey = Base64Url.Decode(rec.GetProperty("encrypted_key").GetString()!);
            recipients.Add(new ParsedRecipient(kid, encryptedKey));
        }

        if (recipients.Count == 0)
            throw new MalformedMessageException("JWE has zero recipients.");

        return new ParsedJwe(protectedB64u, recipients, iv, ciphertext, tag);
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
