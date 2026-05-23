using DidComm.Crypto.Aead;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Crypto.Aead;

/// <summary>
/// Tests for <see cref="AesGcmAead"/> — JOSE <c>A256GCM</c>. AES-GCM itself is RFC 5288 with
/// canonical NIST CAVP vectors validated inside the BCL; we test the DIDComm wrapper's IO
/// contract (length validation, round-trip, tamper rejection).
/// </summary>
public sealed class AesGcmAeadTests
{
    private readonly AesGcmAead _aead = new();

    [Fact]
    public void Algorithm_metadata_matches_RFC_7518_section_5_3()
    {
        _aead.Name.Should().Be("A256GCM");
        _aead.KeySizeBytes.Should().Be(32);
        _aead.IvSizeBytes.Should().Be(12);
        _aead.TagSizeBytes.Should().Be(16);
    }

    [Fact]
    public void Round_trip_recovers_plaintext()
    {
        var random = new Random(Seed: 4321);
        Span<byte> key = stackalloc byte[32];
        Span<byte> iv = stackalloc byte[12];
        var aad = new byte[37];
        var plaintext = new byte[200];
        random.NextBytes(key);
        random.NextBytes(iv);
        random.NextBytes(aad);
        random.NextBytes(plaintext);

        var (ciphertext, tag) = _aead.Encrypt(key, iv, aad, plaintext);
        var recovered = _aead.Decrypt(key, iv, aad, ciphertext, tag);

        ciphertext.Length.Should().Be(plaintext.Length,
            because: "GCM is a stream cipher — ciphertext is the same length as plaintext (no padding).");
        tag.Length.Should().Be(16);
        recovered.Should().Equal(plaintext);
    }

    [Fact]
    public void Round_trip_handles_empty_aad()
    {
        var key = new byte[32];
        var iv = new byte[12];
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
    [InlineData(16)]   // would be valid for AES-128/192/256-KEY, not for A256GCM
    [InlineData(64)]   // CBC-HMAC size, wrong here
    public void Encrypt_rejects_wrong_key_length(int keyLength)
    {
        var act = () => _aead.Encrypt(new byte[keyLength], new byte[12], [], []);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(16)]   // CBC IV size, wrong here
    public void Encrypt_rejects_wrong_iv_length(int ivLength)
    {
        var act = () => _aead.Encrypt(new byte[32], new byte[ivLength], [], []);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Different_ivs_produce_different_ciphertext_for_same_plaintext()
    {
        // Spec: FR-ENC-08 — IVs come from CSPRNG so repeated packs differ.
        var key = new byte[32];
        var plaintext = "didcomm-dotnet"u8.ToArray();

        Span<byte> iv1 = stackalloc byte[12];
        Span<byte> iv2 = stackalloc byte[12];
        RandomNumberGenerator.Fill(iv1);
        RandomNumberGenerator.Fill(iv2);

        var (ct1, _) = _aead.Encrypt(key, iv1, [], plaintext);
        var (ct2, _) = _aead.Encrypt(key, iv2, [], plaintext);

        ct1.Should().NotEqual(ct2,
            because: "GCM is IV-deterministic — different IVs MUST produce different ciphertexts (and the IV-reuse case is the AEAD's catastrophic-failure mode, so freshness matters).");
    }

    private static (byte[] Key, byte[] Iv, byte[] Aad, byte[] Plaintext) GenerateRandomInputs()
    {
        var random = new Random(Seed: 0xC0FFEE);
        var key = new byte[32];
        var iv = new byte[12];
        var aad = new byte[19];
        var plaintext = new byte[97];
        random.NextBytes(key);
        random.NextBytes(iv);
        random.NextBytes(aad);
        random.NextBytes(plaintext);
        return (key, iv, aad, plaintext);
    }
}
