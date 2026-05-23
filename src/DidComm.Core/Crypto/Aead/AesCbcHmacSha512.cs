using DidComm.Jose;

namespace DidComm.Crypto.Aead;

/// <summary>
/// JOSE <c>A256CBC-HS512</c> (RFC 7518 §5.2.5). The mandatory <c>enc</c> for DIDComm authcrypt
/// (FR-ENC-05); also accepted for anoncrypt. 64-byte key (32 MAC ‖ 32 ENC), 16-byte IV, 32-byte
/// authentication tag (truncated HMAC-SHA-512).
/// </summary>
/// <remarks>
/// <para>
/// The construction is an encrypt-then-MAC composition with explicit framing:
/// </para>
/// <list type="number">
///   <item>Split the key: <c>MAC_KEY = key[0..32]</c>, <c>ENC_KEY = key[32..64]</c>.</item>
///   <item>Encrypt the plaintext with AES-256-CBC using <c>ENC_KEY</c>, <c>IV</c>, and PKCS#7 padding.</item>
///   <item>Compute <c>HMAC-SHA-512(MAC_KEY, AAD ‖ IV ‖ CIPHERTEXT ‖ AL)</c> where <c>AL</c> is the
///         bit-length of <c>AAD</c> as a 64-bit big-endian unsigned integer.</item>
///   <item>The authentication tag is the first 32 bytes of the HMAC output (the leftmost half).</item>
/// </list>
/// <para>
/// Decryption verifies the tag with <see cref="CryptographicOperations.FixedTimeEquals"/> before
/// any decryption work runs (NFR-09). This is a JOSE-specific composition — no equivalent lives
/// in the BCL because AES-CBC-HMAC is not used outside JOSE/JWE.
/// </para>
/// </remarks>
internal sealed class AesCbcHmacSha512 : IAead
{
    public string Name => JoseAlgorithms.A256CbcHs512;

    public int KeySizeBytes => 64;

    public int IvSizeBytes => 16;

    public int TagSizeBytes => 32;

    public (byte[] Ciphertext, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext)
    {
        ValidateLengths(key, iv);

        var macKey = key[..32];
        var encKey = key.Slice(32, 32);

        // Step 2: AES-256-CBC encrypt with PKCS#7 padding.
        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = encKey.ToArray();
            aes.IV = iv.ToArray();
            ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
        }

        // Step 3-4: HMAC-SHA-512 over (AAD ‖ IV ‖ CT ‖ AL), truncate to 32 bytes.
        var tag = ComputeMac(macKey, aad, iv, ciphertext);

        return (ciphertext, tag);
    }

    public byte[] Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        ValidateLengths(key, iv);
        if (tag.Length != TagSizeBytes)
            throw new ArgumentException($"A256CBC-HS512 tag must be {TagSizeBytes} bytes, got {tag.Length}.", nameof(tag));

        var macKey = key[..32];
        var encKey = key.Slice(32, 32);

        // Verify MAC FIRST (encrypt-then-MAC; reject tampered ciphertexts before touching crypto state).
        var expectedTag = ComputeMac(macKey, aad, iv, ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
            throw new CryptographicException("A256CBC-HS512 authentication tag verification failed.");

        // MAC verified — safe to decrypt.
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = encKey.ToArray();
        aes.IV = iv.ToArray();
        return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
    }

    /// <summary>
    /// HMAC-SHA-512 over (AAD ‖ IV ‖ CIPHERTEXT ‖ AL) per RFC 7518 §5.2.2.1, returning the
    /// leftmost <see cref="TagSizeBytes"/> bytes. <c>AL</c> is the bit-length of <c>AAD</c> as
    /// a 64-bit big-endian unsigned integer.
    /// </summary>
    private byte[] ComputeMac(
        ReadOnlySpan<byte> macKey,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> ciphertext)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, macKey);
        hmac.AppendData(aad);
        hmac.AppendData(iv);
        hmac.AppendData(ciphertext);

        Span<byte> al = stackalloc byte[8];
        // RFC 7518 §5.2.2.1: AL = octets representing AAD length in BITS, big-endian, 64-bit.
        BinaryPrimitives.WriteUInt64BigEndian(al, checked((ulong)aad.Length * 8));
        hmac.AppendData(al);

        Span<byte> fullMac = stackalloc byte[64];
        hmac.GetHashAndReset(fullMac);

        return fullMac[..TagSizeBytes].ToArray();
    }

    private void ValidateLengths(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"A256CBC-HS512 key must be {KeySizeBytes} bytes, got {key.Length}.", nameof(key));
        if (iv.Length != IvSizeBytes)
            throw new ArgumentException($"A256CBC-HS512 IV must be {IvSizeBytes} bytes, got {iv.Length}.", nameof(iv));
    }
}
