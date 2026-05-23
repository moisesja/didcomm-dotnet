using NetDid.Core.Crypto;
using NetDidConcatKdf = NetDid.Core.Crypto.Kdf.ConcatKdf;
using NetDidICryptoProvider = NetDid.Core.ICryptoProvider;

namespace DidComm.Crypto.Kdf;

/// <summary>
/// ECDH-1PU key derivation for JOSE authcrypt (<c>draft-madden-jose-ecdh-1pu-04 §2</c>).
/// Composes <c>Z = Ze ‖ Zs</c> from two raw ECDH computations (ephemeral × recipient,
/// then sender-static × recipient), then runs net-did's Concat KDF with the AEAD
/// authentication tag bound into <c>SuppPubInfo</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the DIDComm-specific wiring that satisfies <c>FR-ENC-15</c> (encrypt the payload
/// FIRST, then use the resulting AEAD tag in the KEK derivation when wrapping the CEK). The
/// underlying primitives — raw ECDH and the Concat KDF itself — live in net-did 1.3.0; this
/// class only owns the 1PU-specific orchestration.
/// </para>
/// <para>
/// <c>SuppPubInfo</c> layout per draft-madden §2.3:
/// </para>
/// <code>
/// SuppPubInfo = BE32(keyDataLen * 8) ‖ AEAD_tag
/// </code>
/// <para>
/// For anoncrypt (ECDH-ES), call <see cref="NetDidConcatKdf.DeriveKey"/> directly with
/// <c>sharedSecret = Ze</c> and <c>suppPubInfo = BE32(keyDataLen * 8)</c> only.
/// </para>
/// </remarks>
internal static class Ecdh1PuKdf
{
    /// <summary>
    /// Derive the wrapping key for ECDH-1PU+A256KW.
    /// </summary>
    /// <param name="cryptoProvider">The net-did <see cref="NetDidICryptoProvider"/> supplying raw ECDH.</param>
    /// <param name="curve">Curve for both ECDH operations (must match for sender, ephemeral, recipient).</param>
    /// <param name="senderPrivateKey">The authcrypt sender's static private key (matches <c>skid</c>).</param>
    /// <param name="ephemeralPrivateKey">The per-message ephemeral private key (matches <c>epk</c>).</param>
    /// <param name="recipientPublicKey">The recipient's public key (matches recipient <c>kid</c>).</param>
    /// <param name="algorithmId">UTF-8 of the JOSE <c>alg</c> for which the wrapping key is derived
    ///   (e.g. <c>"ECDH-1PU+A256KW"</c>). Length-prefixed inside the Concat KDF.</param>
    /// <param name="apu">PartyUInfo (sender info). Length-prefixed inside the Concat KDF.</param>
    /// <param name="apv">PartyVInfo (recipient info). Length-prefixed inside the Concat KDF.</param>
    /// <param name="aeadTag">The AEAD authentication tag from the just-completed content encryption.
    ///   Pass <see cref="ReadOnlySpan{T}.Empty"/> for the per-recipient outer derivation that does
    ///   not bind a tag (anoncrypt and the no-1PU paths).</param>
    /// <param name="keyDataLen">Wrapping-key length in bytes (32 for A256KW).</param>
    /// <returns><paramref name="keyDataLen"/> bytes of derived keying material.</returns>
    public static byte[] DeriveKey(
        NetDidICryptoProvider cryptoProvider,
        KeyType curve,
        ReadOnlySpan<byte> senderPrivateKey,
        ReadOnlySpan<byte> ephemeralPrivateKey,
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> algorithmId,
        ReadOnlySpan<byte> apu,
        ReadOnlySpan<byte> apv,
        ReadOnlySpan<byte> aeadTag,
        int keyDataLen)
    {
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var ze = cryptoProvider.DeriveSharedSecret(curve, ephemeralPrivateKey, recipientPublicKey);
        var zs = cryptoProvider.DeriveSharedSecret(curve, senderPrivateKey, recipientPublicKey);

        var z = new byte[ze.Length + zs.Length];
        ze.AsSpan().CopyTo(z);
        zs.AsSpan().CopyTo(z.AsSpan(ze.Length));
        CryptographicOperations.ZeroMemory(ze);
        CryptographicOperations.ZeroMemory(zs);

        var suppPubInfo = new byte[4 + aeadTag.Length];
        BinaryPrimitives.WriteUInt32BigEndian(suppPubInfo, checked((uint)keyDataLen * 8U));
        aeadTag.CopyTo(suppPubInfo.AsSpan(4));

        try
        {
            return NetDidConcatKdf.DeriveKey(
                sharedSecret: z,
                algorithmId: algorithmId,
                partyUInfo: apu,
                partyVInfo: apv,
                suppPubInfo: suppPubInfo,
                suppPrivInfo: ReadOnlySpan<byte>.Empty,
                keyDataLen: keyDataLen);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(z);
        }
    }
}
