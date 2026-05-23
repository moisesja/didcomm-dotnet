using DidComm.Crypto.Aead;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Crypto.Aead;

/// <summary>
/// Tests for <see cref="XChaCha20Poly1305Aead"/> — JOSE <c>XC20P</c>. The primitive is
/// libsodium's XChaCha20-Poly1305 (validated against draft-irtf-cfrg-xchacha-03 by NSec);
/// we test the DIDComm wrapper's IO contract and tag splitting.
/// </summary>
public sealed class XChaCha20Poly1305AeadTests
{
    private readonly XChaCha20Poly1305Aead _aead = new();

    [Fact]
    public void Algorithm_metadata_matches_libsodium_XChaCha20_Poly1305_IETF()
    {
        _aead.Name.Should().Be("XC20P");
        _aead.KeySizeBytes.Should().Be(32);
        _aead.IvSizeBytes.Should().Be(24,
            because: "XC20P uses a 24-byte (192-bit) nonce — the extended ChaCha20 nonce that distinguishes it from plain ChaCha20-Poly1305 (12-byte).");
        _aead.TagSizeBytes.Should().Be(16);
    }

    [Fact]
    public void Round_trip_recovers_plaintext()
    {
        var (key, iv, aad, plaintext) = GenerateRandomInputs();

        var (ciphertext, tag) = _aead.Encrypt(key, iv, aad, plaintext);
        var recovered = _aead.Decrypt(key, iv, aad, ciphertext, tag);

        ciphertext.Length.Should().Be(plaintext.Length);
        tag.Length.Should().Be(16);
        recovered.Should().Equal(plaintext);
    }

    [Fact]
    public void Round_trip_handles_empty_aad()
    {
        var key = new byte[32];
        var iv = new byte[24];
        var plaintext = "didcomm-dotnet"u8.ToArray();

        var (ciphertext, tag) = _aead.Encrypt(key, iv, [], plaintext);
        var recovered = _aead.Decrypt(key, iv, [], ciphertext, tag);

        recovered.Should().Equal(plaintext);
    }

    [Fact]
    public void Decrypt_rejects_tampered_ciphertext()
    {
        var (key, iv, aad, plaintext) = GenerateRandomInputs();
        var (ciphertext, tag) = _aead.Encrypt(key, iv, aad, plaintext);

        ciphertext[0] ^= 0x01;

        var act = () => _aead.Decrypt(key, iv, aad, ciphertext, tag);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*tag verification failed*");
    }

    [Fact]
    public void Decrypt_rejects_tampered_tag()
    {
        var (key, iv, aad, plaintext) = GenerateRandomInputs();
        var (ciphertext, tag) = _aead.Encrypt(key, iv, aad, plaintext);

        tag[^1] ^= 0x80;

        var act = () => _aead.Decrypt(key, iv, aad, ciphertext, tag);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_rejects_wrong_aad()
    {
        var (key, iv, aad, plaintext) = GenerateRandomInputs();
        var (ciphertext, tag) = _aead.Encrypt(key, iv, aad, plaintext);

        aad[0] ^= 0x01;

        var act = () => _aead.Decrypt(key, iv, aad, ciphertext, tag);
        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(64)]
    public void Encrypt_rejects_wrong_key_length(int keyLength)
    {
        var act = () => _aead.Encrypt(new byte[keyLength], new byte[24], [], []);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]   // valid for A256GCM, not for XC20P
    [InlineData(16)]   // CBC IV, not XC20P
    public void Encrypt_rejects_wrong_iv_length(int ivLength)
    {
        var act = () => _aead.Encrypt(new byte[32], new byte[ivLength], [], []);
        act.Should().Throw<ArgumentException>();
    }

    private static (byte[] Key, byte[] Iv, byte[] Aad, byte[] Plaintext) GenerateRandomInputs()
    {
        var random = new Random(Seed: 0xBEEF);
        var key = new byte[32];
        var iv = new byte[24];
        var aad = new byte[23];
        var plaintext = new byte[101];
        random.NextBytes(key);
        random.NextBytes(iv);
        random.NextBytes(aad);
        random.NextBytes(plaintext);
        return (key, iv, aad, plaintext);
    }
}
