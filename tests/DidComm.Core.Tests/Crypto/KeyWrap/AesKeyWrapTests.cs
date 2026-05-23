using DidComm.Crypto.KeyWrap;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Crypto.KeyWrap;

/// <summary>
/// Tests for <see cref="AesKeyWrap"/> — RFC 3394 / RFC 7518 §4.4 (<c>A256KW</c>).
/// The §4.6 vector wraps 256 bits of data under a 256-bit KEK, producing a 320-bit output.
/// </summary>
public sealed class AesKeyWrapTests
{
    // RFC 3394 §4.6 — "Wrap 256 bits of Key Data with a 256-bit KEK"

    private static readonly byte[] Kek_4_6 = Hex.Decode(@"
        00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
        10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F");

    private static readonly byte[] Plaintext_4_6 = Hex.Decode(@"
        00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF
        00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F");

    private static readonly byte[] Wrapped_4_6 = Hex.Decode(@"
        28 C9 F4 04 C4 B8 10 F4 CB CC B3 5C FB 87 F8 26
        3F 57 86 E2 D8 0E D3 26 CB C7 F0 E7 1A 99 F4 3B
        FB 98 8B 9B 7A 02 DD 21");

    [Fact]
    public void Wrap_matches_RFC_3394_section_4_6()
    {
        var actual = AesKeyWrap.Wrap(Kek_4_6, Plaintext_4_6);

        actual.Should().Equal(Wrapped_4_6,
            because: "RFC 3394 §4.6 publishes the exact wrapped output for these inputs (256-bit KEK, 256-bit data).");
    }

    [Fact]
    public void Unwrap_recovers_plaintext_from_RFC_3394_section_4_6_vector()
    {
        var recovered = AesKeyWrap.Unwrap(Kek_4_6, Wrapped_4_6);

        recovered.Should().Equal(Plaintext_4_6);
    }

    [Theory]
    [InlineData(16)]   // wrapping a 128-bit CEK (e.g. A128GCM-CEK under A256KW)
    [InlineData(24)]   // wrapping a 192-bit CEK
    [InlineData(32)]   // 256-bit CEK (A256GCM/A256KW)
    [InlineData(48)]   // 384-bit CEK
    [InlineData(64)]   // 512-bit CEK (A256CBC-HS512 input)
    public void Round_trip_works_for_every_valid_block_aligned_length(int cekLength)
    {
        var kek = new byte[32];
        RandomNumberGenerator.Fill(kek);
        var cek = new byte[cekLength];
        RandomNumberGenerator.Fill(cek);

        var wrapped = AesKeyWrap.Wrap(kek, cek);
        wrapped.Length.Should().Be(cekLength + 8,
            because: "RFC 3394 prepends 8 bytes of integrity material (the unwrapped IV) to the ciphertext.");

        var recovered = AesKeyWrap.Unwrap(kek, wrapped);
        recovered.Should().Equal(cek);
    }

    [Fact]
    public void Unwrap_rejects_tampered_wrapped_key()
    {
        var tampered = (byte[])Wrapped_4_6.Clone();
        tampered[0] ^= 0x01;

        var act = () => AesKeyWrap.Unwrap(Kek_4_6, tampered);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*integrity check failed*",
                because: "RFC 3394 §2.2.3 mandates rejecting wrapped keys whose recovered IV does not match A6A6A6A6A6A6A6A6.");
    }

    [Fact]
    public void Unwrap_rejects_wrong_kek()
    {
        var wrongKek = (byte[])Kek_4_6.Clone();
        wrongKek[0] ^= 0x01;

        var act = () => AesKeyWrap.Unwrap(wrongKek, Wrapped_4_6);
        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(0)]   // empty
    [InlineData(16)]  // A128/A192 KEK — wrong size for A256KW
    [InlineData(48)]  // arbitrary wrong size
    public void Wrap_rejects_non_256_bit_kek(int kekLength)
    {
        var act = () => AesKeyWrap.Wrap(new byte[kekLength], new byte[16]);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]    // empty plaintext
    [InlineData(7)]    // not block-aligned
    [InlineData(15)]   // not block-aligned
    [InlineData(33)]   // not block-aligned
    public void Wrap_rejects_non_block_aligned_plaintext(int plaintextLength)
    {
        var act = () => AesKeyWrap.Wrap(new byte[32], new byte[plaintextLength]);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]    // empty
    [InlineData(8)]    // only the IV, no key blocks
    [InlineData(15)]   // not block-aligned
    public void Unwrap_rejects_malformed_wrapped_input(int wrappedLength)
    {
        var act = () => AesKeyWrap.Unwrap(new byte[32], new byte[wrappedLength]);
        act.Should().Throw<ArgumentException>();
    }
}
