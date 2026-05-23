namespace DidComm.Crypto.Aead;

/// <summary>
/// Common surface for the JOSE content-encryption algorithms DIDComm v2.1 supports
/// (<c>A256CBC-HS512</c>, <c>A256GCM</c>, <c>XC20P</c>). All three are authenticated
/// encryption with associated data.
/// </summary>
/// <remarks>
/// Implementations MUST be stateless and thread-safe — DIDComm pack/unpack runs concurrently
/// (NFR-03). Decryption MUST use a constant-time comparison when checking the authentication
/// tag (NFR-09); BCL primitives (<see cref="AesGcm"/>, NSec's <c>XChaCha20Poly1305</c>) handle
/// this internally, but the AES-CBC-HMAC composition must do it explicitly.
/// </remarks>
internal interface IAead
{
    /// <summary>JOSE algorithm name (e.g. <c>"A256CBC-HS512"</c>). Used only for diagnostic messages.</summary>
    string Name { get; }

    /// <summary>Required content-encryption key (CEK) length in bytes.</summary>
    int KeySizeBytes { get; }

    /// <summary>Required IV / nonce length in bytes.</summary>
    int IvSizeBytes { get; }

    /// <summary>Authentication tag length in bytes.</summary>
    int TagSizeBytes { get; }

    /// <summary>Authenticate and encrypt <paramref name="plaintext"/> with associated data <paramref name="aad"/>.</summary>
    /// <param name="key">Content-encryption key. MUST be exactly <see cref="KeySizeBytes"/> bytes.</param>
    /// <param name="iv">Initialization vector / nonce. MUST be exactly <see cref="IvSizeBytes"/> bytes.</param>
    /// <param name="aad">Associated data covered by the authentication tag but not encrypted.</param>
    /// <param name="plaintext">Data to encrypt.</param>
    /// <returns>The ciphertext and the authentication tag, each freshly allocated.</returns>
    /// <exception cref="ArgumentException">When <paramref name="key"/> or <paramref name="iv"/> have the wrong length.</exception>
    (byte[] Ciphertext, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext);

    /// <summary>Verify the tag and decrypt <paramref name="ciphertext"/>.</summary>
    /// <param name="key">Content-encryption key.</param>
    /// <param name="iv">The IV / nonce used at encryption time.</param>
    /// <param name="aad">Associated data the tag was computed over.</param>
    /// <param name="ciphertext">Ciphertext bytes.</param>
    /// <param name="tag">Authentication tag (exactly <see cref="TagSizeBytes"/> bytes).</param>
    /// <returns>The recovered plaintext.</returns>
    /// <exception cref="CryptographicException">When the tag does not authenticate.</exception>
    /// <exception cref="ArgumentException">When any input has an invalid length.</exception>
    byte[] Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag);
}
