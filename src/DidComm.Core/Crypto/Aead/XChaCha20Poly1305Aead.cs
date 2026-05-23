using DidComm.Jose;
using NSec.Cryptography;

namespace DidComm.Crypto.Aead;

/// <summary>
/// JOSE <c>XC20P</c> — XChaCha20-Poly1305 (draft-irtf-cfrg-xchacha-03 + libsodium). 32-byte key,
/// 24-byte nonce, 16-byte tag. Anoncrypt only — FR-ENC-09 forbids XC20P for authcrypt.
/// </summary>
/// <remarks>
/// Thin pass-through to <see cref="AeadAlgorithm.XChaCha20Poly1305"/>. NSec exposes XChaCha20 with
/// the wire format DIDComm expects: tag follows ciphertext as a single buffer of
/// <c>ciphertext.Length + 16</c> bytes. We split it back out so the <see cref="IAead"/> contract
/// remains uniform.
/// </remarks>
internal sealed class XChaCha20Poly1305Aead : IAead
{
    private static readonly AeadAlgorithm Algorithm = AeadAlgorithm.XChaCha20Poly1305;

    public string Name => JoseAlgorithms.XC20P;

    public int KeySizeBytes => 32;

    public int IvSizeBytes => 24;

    public int TagSizeBytes => 16;

    public (byte[] Ciphertext, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext)
    {
        ValidateLengths(key, iv);

        using var nsecKey = Key.Import(Algorithm, key, KeyBlobFormat.RawSymmetricKey);
        var combined = Algorithm.Encrypt(nsecKey, iv, aad, plaintext);

        // NSec returns [ciphertext || tag]; split into the two pieces the IAead contract expects.
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        Buffer.BlockCopy(combined, 0, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(combined, ciphertext.Length, tag, 0, TagSizeBytes);

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
            throw new ArgumentException($"XC20P tag must be {TagSizeBytes} bytes, got {tag.Length}.", nameof(tag));

        using var nsecKey = Key.Import(Algorithm, key, KeyBlobFormat.RawSymmetricKey);

        // Re-combine [ciphertext || tag] for NSec's decrypt contract.
        var combined = new byte[ciphertext.Length + TagSizeBytes];
        ciphertext.CopyTo(combined);
        tag.CopyTo(combined.AsSpan(ciphertext.Length));

        var plaintext = Algorithm.Decrypt(nsecKey, iv, aad, combined)
            ?? throw new CryptographicException("XC20P authentication tag verification failed.");

        return plaintext;
    }

    private void ValidateLengths(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"XC20P key must be {KeySizeBytes} bytes, got {key.Length}.", nameof(key));
        if (iv.Length != IvSizeBytes)
            throw new ArgumentException($"XC20P IV must be {IvSizeBytes} bytes, got {iv.Length}.", nameof(iv));
    }
}
