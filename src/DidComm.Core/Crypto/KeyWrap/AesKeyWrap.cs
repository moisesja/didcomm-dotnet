namespace DidComm.Crypto.KeyWrap;

/// <summary>
/// AES Key Wrap with a 256-bit key (RFC 3394 / NIST SP 800-38F, called <c>A256KW</c> in
/// RFC 7518 §4.4). Wraps a CEK with a KEK so that JWE recipients can decrypt the CEK with a
/// per-recipient ECDH-derived key.
/// </summary>
/// <remarks>
/// <para>
/// .NET does not expose a public AES Key Wrap API. This implementation follows RFC 3394 §2.2:
/// six rounds of <c>(A, t = (n*j)+i) ↦ AES-ECB-encrypt(KEK, A ‖ R[i])</c>, splitting the
/// ciphertext into <c>A</c> (high 64 bits, XOR'd with <c>t</c>) and <c>R[i]</c> (low 64 bits).
/// Unwrap inverts the loop and verifies the recovered <c>A</c> equals the default IV
/// <c>A6A6A6A6A6A6A6A6</c> via <see cref="CryptographicOperations.FixedTimeEquals"/> (NFR-09).
/// </para>
/// <para>
/// The wrapped key length is the plaintext length plus 8 bytes (one extra AES block of integrity
/// material). The plaintext MUST be a multiple of 8 bytes; A256KW in JOSE always wraps
/// 16-, 24-, 32-, 48-, or 64-byte CEKs, so this is satisfied in practice.
/// </para>
/// </remarks>
internal static class AesKeyWrap
{
    private const int BlockSize = 8;
    private const int KekSize = 32; // A256KW: 256-bit KEK.

    // RFC 3394 §2.2.3.1 default Initial Value.
    private static ReadOnlySpan<byte> DefaultIv => [0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6];

    /// <summary>Wraps <paramref name="plaintext"/> (the CEK) under <paramref name="kek"/>.</summary>
    /// <param name="kek">256-bit key-encryption key.</param>
    /// <param name="plaintext">Key material to wrap. Length MUST be a positive multiple of 8.</param>
    /// <returns>Wrapped key, <c>plaintext.Length + 8</c> bytes.</returns>
    public static byte[] Wrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> plaintext)
    {
        if (kek.Length != KekSize)
            throw new ArgumentException($"A256KW KEK must be {KekSize} bytes, got {kek.Length}.", nameof(kek));
        if (plaintext.Length == 0 || plaintext.Length % BlockSize != 0)
            throw new ArgumentException($"A256KW plaintext must be a positive multiple of {BlockSize} bytes, got {plaintext.Length}.", nameof(plaintext));

        var n = plaintext.Length / BlockSize;

        // Initialize A = IV; R[1..n] = plaintext blocks.
        Span<byte> a = stackalloc byte[BlockSize];
        DefaultIv.CopyTo(a);

        var r = new byte[n * BlockSize];
        plaintext.CopyTo(r);

        using var aes = CreateEcbCipher(kek);
        Span<byte> block = stackalloc byte[BlockSize * 2];
        Span<byte> encrypted = stackalloc byte[BlockSize * 2];

        // Six wrap rounds.
        for (var j = 0; j < 6; j++)
        {
            for (var i = 0; i < n; i++)
            {
                a.CopyTo(block);
                r.AsSpan(i * BlockSize, BlockSize).CopyTo(block[BlockSize..]);

                // B = AES(K, A ‖ R[i])
                aes.EncryptEcb(block, encrypted, PaddingMode.None);

                // A = MSB64(B) XOR t, where t = (n * j) + i + 1
                var t = (ulong)(n * j) + (ulong)i + 1UL;
                ReadUInt64(encrypted, out var aHigh);
                aHigh ^= t;
                WriteUInt64(a, aHigh);

                // R[i] = LSB64(B)
                encrypted.Slice(BlockSize, BlockSize).CopyTo(r.AsSpan(i * BlockSize, BlockSize));
            }
        }

        var output = new byte[BlockSize + r.Length];
        a.CopyTo(output);
        Buffer.BlockCopy(r, 0, output, BlockSize, r.Length);
        return output;
    }

    /// <summary>Unwraps <paramref name="wrapped"/> under <paramref name="kek"/> and returns the original key.</summary>
    /// <param name="kek">256-bit key-encryption key.</param>
    /// <param name="wrapped">Output of a prior <see cref="Wrap"/> call. Length MUST be a multiple of 8 and ≥ 16.</param>
    /// <returns>The recovered key.</returns>
    /// <exception cref="CryptographicException">When the integrity check (<c>A == IV</c>) fails.</exception>
    public static byte[] Unwrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped)
    {
        if (kek.Length != KekSize)
            throw new ArgumentException($"A256KW KEK must be {KekSize} bytes, got {kek.Length}.", nameof(kek));
        if (wrapped.Length < 2 * BlockSize || wrapped.Length % BlockSize != 0)
            throw new ArgumentException($"A256KW wrapped key must be a multiple of {BlockSize} bytes and at least {2 * BlockSize}, got {wrapped.Length}.", nameof(wrapped));

        var n = (wrapped.Length / BlockSize) - 1;

        Span<byte> a = stackalloc byte[BlockSize];
        wrapped[..BlockSize].CopyTo(a);

        var r = new byte[n * BlockSize];
        wrapped.Slice(BlockSize, n * BlockSize).CopyTo(r);

        using var aes = CreateEcbCipher(kek);
        Span<byte> block = stackalloc byte[BlockSize * 2];
        Span<byte> decrypted = stackalloc byte[BlockSize * 2];

        // Six unwrap rounds (reverse order).
        for (var j = 5; j >= 0; j--)
        {
            for (var i = n - 1; i >= 0; i--)
            {
                // B = AES-1(K, (A XOR t) ‖ R[i]), t = (n * j) + i + 1
                var t = (ulong)(n * j) + (ulong)i + 1UL;
                ReadUInt64(a, out var aHigh);
                aHigh ^= t;
                WriteUInt64(block, aHigh);
                r.AsSpan(i * BlockSize, BlockSize).CopyTo(block[BlockSize..]);

                aes.DecryptEcb(block, decrypted, PaddingMode.None);

                // A = MSB64(B), R[i] = LSB64(B)
                decrypted[..BlockSize].CopyTo(a);
                decrypted.Slice(BlockSize, BlockSize).CopyTo(r.AsSpan(i * BlockSize, BlockSize));
            }
        }

        // Integrity check — constant time.
        Span<byte> expectedIv = stackalloc byte[BlockSize];
        DefaultIv.CopyTo(expectedIv);
        if (!CryptographicOperations.FixedTimeEquals(a, expectedIv))
            throw new CryptographicException("A256KW unwrap integrity check failed (IV mismatch).");

        return r;
    }

    private static Aes CreateEcbCipher(ReadOnlySpan<byte> kek)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = kek.ToArray();
        return aes;
    }

    private static void ReadUInt64(ReadOnlySpan<byte> source, out ulong value)
        => value = BinaryPrimitives.ReadUInt64BigEndian(source[..BlockSize]);

    private static void WriteUInt64(Span<byte> destination, ulong value)
        => BinaryPrimitives.WriteUInt64BigEndian(destination[..BlockSize], value);
}
