using DidComm.Jose;

namespace DidComm.Crypto.Aead;

/// <summary>
/// JOSE <c>A256GCM</c> (RFC 7518 §5.3). 32-byte key, 12-byte IV, 16-byte tag. Anoncrypt only —
/// FR-ENC-09 forbids GCM for authcrypt because 1PU mandates the AES-CBC-HMAC family.
/// </summary>
/// <remarks>
/// Thin pass-through to <see cref="AesGcm"/>. The BCL handles the constant-time tag check
/// internally and raises <see cref="AuthenticationTagMismatchException"/> on failure; we surface
/// the standard <see cref="CryptographicException"/> for parity with the other AEADs.
/// </remarks>
internal sealed class AesGcmAead : IAead
{
    public string Name => JoseAlgorithms.A256Gcm;

    public int KeySizeBytes => 32;

    public int IvSizeBytes => 12;

    public int TagSizeBytes => 16;

    public (byte[] Ciphertext, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext)
    {
        ValidateLengths(key, iv);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(iv, plaintext, ciphertext, tag, aad);

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
            throw new ArgumentException($"A256GCM tag must be {TagSizeBytes} bytes, got {tag.Length}.", nameof(tag));

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSizeBytes);
        try
        {
            aes.Decrypt(iv, ciphertext, tag, plaintext, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("A256GCM authentication tag verification failed.");
        }

        return plaintext;
    }

    private void ValidateLengths(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"A256GCM key must be {KeySizeBytes} bytes, got {key.Length}.", nameof(key));
        if (iv.Length != IvSizeBytes)
            throw new ArgumentException($"A256GCM IV must be {IvSizeBytes} bytes, got {iv.Length}.", nameof(iv));
    }
}
