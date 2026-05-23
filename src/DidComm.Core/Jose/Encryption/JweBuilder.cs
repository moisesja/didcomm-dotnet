using System.Text.Json;
using DidComm.Crypto;
using DidComm.Crypto.KeyAgreement;
using DidComm.Crypto.Kdf;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Messages;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Jose.Encryption;

/// <summary>
/// Builds a DIDComm encrypted envelope (JWE, General JSON Serialization) per
/// FR-ENV-06 / FR-ENC-09..19. One <see cref="JweBuilder.PackAnoncrypt"/> or
/// <see cref="JweBuilder.PackAuthcrypt"/> call produces one JWE per (DID, curve) combination —
/// splitting multi-curve recipients is the higher layer's job (Phase 3 facade, FR-ENC-04).
/// </summary>
internal static class JweBuilder
{
    /// <summary>
    /// Pack <paramref name="plaintextBytes"/> as a multi-recipient anoncrypt JWE
    /// (ECDH-ES+A256KW + <paramref name="contentEncryption"/>).
    /// </summary>
    /// <param name="plaintextBytes">Bytes to encrypt (the inner JWM or a nested JWS).</param>
    /// <param name="recipients">Recipient public JWKs; ALL must share the same curve (FR-ENC-04).</param>
    /// <param name="contentEncryption">JWE <c>enc</c> algorithm (<c>A256CBC-HS512</c>, <c>A256GCM</c>, or <c>XC20P</c>).</param>
    /// <param name="cryptoProvider">The DidComm crypto provider (must be <see cref="DidCommDefaultCryptoProvider"/> so the receiver can reuse the underlying net-did ECDH).</param>
    public static string PackAnoncrypt(
        ReadOnlySpan<byte> plaintextBytes,
        IReadOnlyList<Jwk> recipients,
        string contentEncryption,
        DidCommDefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        if (recipients.Count == 0)
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));

        EnsureRecipientsShareCurve(recipients, out var curve);

        var ephemeral = EphemeralKeyPair.Generate(curve);
        try
        {
            var apvBytes = ApvComputer.ComputeBytes(recipients.Select(r => r.Kid ?? throw new MalformedMessageException("Recipient JWK is missing 'kid'.")));
            var apvB64u = Base64Url.Encode(apvBytes);

            var header = new JweProtectedHeader
            {
                Typ = MediaTypes.Encrypted,
                Alg = JoseAlgorithms.EcdhEsA256Kw,
                Enc = contentEncryption,
                Epk = ephemeral.ToPublicEpkJwk(),
                Apv = apvB64u,
            };

            return EncryptAndAssemble(
                plaintextBytes, header, contentEncryption, cryptoProvider,
                wrapPerRecipient: (recipientPubBytes, _) =>
                {
                    var kek = EcdhEsKdf.DeriveKey(
                        cryptoProvider.NetDidProvider,
                        KeyTypeMapper.FromCurveForKeyAgreement(curve),
                        ephemeral.PrivateKey,
                        recipientPubBytes,
                        Encoding.ASCII.GetBytes(header.Alg),
                        apvBytes,
                        keyDataLen: 32);
                    return kek;
                },
                recipients);
        }
        finally
        {
            ephemeral.Clear();
        }
    }

    /// <summary>
    /// Pack <paramref name="plaintextBytes"/> as a multi-recipient authcrypt JWE
    /// (ECDH-1PU+A256KW + A256CBC-HS512, enforcing FR-ENC-09's authcrypt-only restriction).
    /// </summary>
    /// <param name="plaintextBytes">Bytes to encrypt (the inner JWM or nested JWS).</param>
    /// <param name="recipients">Recipient public JWKs; ALL must share the same curve and match the sender's curve.</param>
    /// <param name="senderPrivateJwk">Sender's static private JWK (matches <c>skid</c>).</param>
    /// <param name="skid">Sender key identifier (DID URL with fragment).</param>
    /// <param name="contentEncryption">JWE <c>enc</c> — MUST be <c>A256CBC-HS512</c> (FR-ENC-09).</param>
    /// <param name="cryptoProvider">DidComm crypto provider.</param>
    /// <exception cref="ArgumentException">When <paramref name="contentEncryption"/> is not <c>A256CBC-HS512</c> (FR-ENC-09).</exception>
    public static string PackAuthcrypt(
        ReadOnlySpan<byte> plaintextBytes,
        IReadOnlyList<Jwk> recipients,
        Jwk senderPrivateJwk,
        string skid,
        string contentEncryption,
        DidCommDefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(senderPrivateJwk);
        ArgumentException.ThrowIfNullOrEmpty(skid);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        if (recipients.Count == 0)
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));
        if (contentEncryption != JoseAlgorithms.A256CbcHs512)
            throw new ArgumentException(
                $"Authcrypt MUST use A256CBC-HS512 content encryption (FR-ENC-09). Got '{contentEncryption}'.",
                nameof(contentEncryption));
        if (string.IsNullOrEmpty(senderPrivateJwk.Crv) || string.IsNullOrEmpty(senderPrivateJwk.D))
            throw new MalformedMessageException("Sender JWK is missing 'crv' or 'd'.");

        EnsureRecipientsShareCurve(recipients, out var curve);
        if (!string.Equals(curve, senderPrivateJwk.Crv, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Authcrypt requires the sender key and all recipients on the same curve. Sender='{senderPrivateJwk.Crv}', recipients='{curve}'.",
                nameof(senderPrivateJwk));

        var ephemeral = EphemeralKeyPair.Generate(curve);
        var senderPrivBytes = Base64Url.Decode(senderPrivateJwk.D!);
        try
        {
            var apvBytes = ApvComputer.ComputeBytes(recipients.Select(r => r.Kid ?? throw new MalformedMessageException("Recipient JWK is missing 'kid'.")));
            var apvB64u = Base64Url.Encode(apvBytes);
            var apuB64u = ApuComputer.Compute(skid);
            // 1PU draft-04 §2.3: PartyUInfo passed to ConcatKDF is the base64url-DECODED apu,
            // i.e. the raw UTF-8 bytes of the skid string — NOT the ASCII bytes of the b64u form.
            var apuBytes = Encoding.UTF8.GetBytes(skid);

            var header = new JweProtectedHeader
            {
                Typ = MediaTypes.Encrypted,
                Alg = JoseAlgorithms.Ecdh1PuA256Kw,
                Enc = contentEncryption,
                Epk = ephemeral.ToPublicEpkJwk(),
                Apv = apvB64u,
                Apu = apuB64u,
                Skid = skid,
            };

            return EncryptAndAssemble(
                plaintextBytes, header, contentEncryption, cryptoProvider,
                wrapPerRecipient: (recipientPubBytes, tag) =>
                {
                    var kek = Ecdh1PuKdf.DeriveKey(
                        cryptoProvider.NetDidProvider,
                        KeyTypeMapper.FromCurveForKeyAgreement(curve),
                        senderPrivBytes,
                        ephemeral.PrivateKey,
                        recipientPubBytes,
                        Encoding.ASCII.GetBytes(header.Alg),
                        apuBytes,
                        apvBytes,
                        tag,
                        keyDataLen: 32);
                    return kek;
                },
                recipients);
        }
        finally
        {
            ephemeral.Clear();
            CryptographicOperations.ZeroMemory(senderPrivBytes);
        }
    }

    private static string EncryptAndAssemble(
        ReadOnlySpan<byte> plaintextBytes,
        JweProtectedHeader header,
        string contentEncryption,
        DidCommDefaultCryptoProvider cryptoProvider,
        Func<byte[], byte[], byte[]> wrapPerRecipient,
        IReadOnlyList<Jwk> recipients)
    {
        var cekLen = KeyTypeMapper.ContentEncryptionKeySizeBytes(contentEncryption);
        var ivLen = KeyTypeMapper.IvSizeBytes(contentEncryption);
        var cek = new byte[cekLen];
        var iv = new byte[ivLen];
        cryptoProvider.Fill(cek);
        cryptoProvider.Fill(iv);

        try
        {
            var protectedB64u = header.EncodeBase64Url();
            var aad = Encoding.ASCII.GetBytes(protectedB64u);

            var (ciphertext, tag) = cryptoProvider.AeadEncrypt(contentEncryption, cek, iv, aad, plaintextBytes);

            var wraps = new List<RecipientWrap>(recipients.Count);
            foreach (var recipient in recipients)
            {
                var (_, recipientPubBytes) = NetDidJwkConverter.ExtractPublicKey(Jose.JwkConversion.ToNetDidJwk(recipient));
                var kek = wrapPerRecipient(recipientPubBytes, tag);
                try
                {
                    var wrapped = cryptoProvider.KeyWrap(JoseAlgorithms.A256Kw, kek, cek);
                    wraps.Add(new RecipientWrap(recipient.Kid!, wrapped));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(kek);
                }
            }

            return RenderJwe(protectedB64u, wraps, iv, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    private static string RenderJwe(string protectedB64u, IReadOnlyList<RecipientWrap> wraps, byte[] iv, byte[] ciphertext, byte[] tag)
    {
        var recipientArr = wraps.Select(w => new
        {
            header = new { kid = w.Kid },
            encrypted_key = Base64Url.Encode(w.EncryptedKey),
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            @protected = protectedB64u,
            recipients = recipientArr,
            iv = Base64Url.Encode(iv),
            ciphertext = Base64Url.Encode(ciphertext),
            tag = Base64Url.Encode(tag),
        });
    }

    private static void EnsureRecipientsShareCurve(IReadOnlyList<Jwk> recipients, out string curve)
    {
        curve = recipients[0].Crv ?? throw new MalformedMessageException("Recipient JWK is missing 'crv'.");
        for (var i = 1; i < recipients.Count; i++)
        {
            var c = recipients[i].Crv;
            if (!string.Equals(curve, c, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"All recipients of a single JWE MUST share a curve (FR-ENC-04 / FR-ENC-11). Got {curve} and {c}.",
                    nameof(recipients));
        }
    }
}
