using NetDid.Core.Crypto;
using NetDidConcatKdf = NetDid.Core.Crypto.Kdf.ConcatKdf;
using NetDidICryptoProvider = NetDid.Core.ICryptoProvider;

namespace DidComm.Crypto.Kdf;

/// <summary>
/// ECDH-ES key derivation for JOSE anoncrypt (RFC 7518 §4.6). Performs a single raw ECDH
/// (ephemeral × recipient) and feeds the result into net-did's Concat KDF with a
/// tag-free <c>SuppPubInfo = BE32(keyDataLen * 8)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Mirror of <see cref="Ecdh1PuKdf"/> for the anoncrypt path — no sender static key, no
/// AEAD tag bound into <c>SuppPubInfo</c>. Owning both wrappers in this folder keeps the
/// difference between ECDH-ES and ECDH-1PU obvious at the call site:
/// </para>
/// <list type="bullet">
///   <item>Anoncrypt → <see cref="EcdhEsKdf.DeriveKey"/> (Ze only, no tag).</item>
///   <item>Authcrypt → <see cref="Ecdh1PuKdf.DeriveKey"/> (Ze ‖ Zs, tag in SuppPubInfo per FR-ENC-15).</item>
/// </list>
/// </remarks>
internal static class EcdhEsKdf
{
    /// <summary>Derive the wrapping key for ECDH-ES+A256KW.</summary>
    /// <param name="cryptoProvider">The net-did <see cref="NetDidICryptoProvider"/> supplying raw ECDH.</param>
    /// <param name="curve">Curve for the ECDH (must match ephemeral, recipient).</param>
    /// <param name="ephemeralPrivateKey">Per-message ephemeral private key (matches <c>epk</c>).</param>
    /// <param name="recipientPublicKey">Recipient's public key (matches recipient <c>kid</c>).</param>
    /// <param name="algorithmId">UTF-8 of the JOSE <c>alg</c> (<c>"ECDH-ES+A256KW"</c>).</param>
    /// <param name="apv">PartyVInfo (recipient info — base64url-no-pad bytes of FR-ENC-13).</param>
    /// <param name="keyDataLen">Wrapping-key length in bytes (32 for A256KW).</param>
    /// <returns><paramref name="keyDataLen"/> bytes of derived keying material.</returns>
    public static byte[] DeriveKey(
        NetDidICryptoProvider cryptoProvider,
        KeyType curve,
        ReadOnlySpan<byte> ephemeralPrivateKey,
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> algorithmId,
        ReadOnlySpan<byte> apv,
        int keyDataLen)
        => DeriveKeyCore(cryptoProvider, curve, ephemeralPrivateKey, recipientPublicKey, algorithmId, apv, keyDataLen);

    /// <summary>
    /// Receive-side variant. By DH commutativity the recipient computes the same KEK using
    /// <c>ECDH(recipient_priv, ephemeral_pub)</c> instead of <c>ECDH(ephemeral_priv, recipient_pub)</c>.
    /// </summary>
    /// <param name="cryptoProvider">The net-did <see cref="NetDidICryptoProvider"/> supplying raw ECDH.</param>
    /// <param name="curve">Curve for the ECDH.</param>
    /// <param name="recipientPrivateKey">Recipient's own private key.</param>
    /// <param name="ephemeralPublicKey">Ephemeral public key from the JWE protected header <c>epk</c>.</param>
    /// <param name="algorithmId">UTF-8 of the JOSE <c>alg</c> (<c>"ECDH-ES+A256KW"</c>).</param>
    /// <param name="apv">PartyVInfo (FR-ENC-13).</param>
    /// <param name="keyDataLen">Wrapping-key length in bytes (32 for A256KW).</param>
    public static byte[] DeriveKeyForReceiver(
        NetDidICryptoProvider cryptoProvider,
        KeyType curve,
        ReadOnlySpan<byte> recipientPrivateKey,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte> algorithmId,
        ReadOnlySpan<byte> apv,
        int keyDataLen)
        => DeriveKeyCore(cryptoProvider, curve, recipientPrivateKey, ephemeralPublicKey, algorithmId, apv, keyDataLen);

    private static byte[] DeriveKeyCore(
        NetDidICryptoProvider cryptoProvider,
        KeyType curve,
        ReadOnlySpan<byte> ownPrivateKey,
        ReadOnlySpan<byte> peerPublicKey,
        ReadOnlySpan<byte> algorithmId,
        ReadOnlySpan<byte> apv,
        int keyDataLen)
    {
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var z = cryptoProvider.DeriveSharedSecret(curve, ownPrivateKey, peerPublicKey);

        var suppPubInfo = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(suppPubInfo, checked((uint)keyDataLen * 8U));

        try
        {
            return NetDidConcatKdf.DeriveKey(
                sharedSecret: z,
                algorithmId: algorithmId,
                partyUInfo: ReadOnlySpan<byte>.Empty,
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
