using System.Globalization;
using System.Numerics;

using FluentAssertions;
using NetCrypto;
using Xunit;

namespace DidComm.Tests.Envelopes.Signing;

/// <summary>
/// RFC 8812 §3.2 defines an ES256K signature as <c>R‖S</c>, 32 octets each, and imposes no
/// low-S / canonical / non-malleability requirement. Both <c>(r, s)</c> and <c>(r, n-s)</c> are
/// therefore conformant signatures over the same message and key — verification recovers a point
/// whose y is negated while x, the only coordinate compared against <c>r</c>, is unchanged.
/// <para>
/// NetCrypto up to 1.4.0 inherited libsecp256k1's Bitcoin-oriented policy through
/// NBitcoin.Secp256k1 and rejected the high-S form, so every ES256K message from didcomm-python
/// (which emits high-S) failed verification and looked exactly like tampering. Fixed in
/// NetCrypto 1.5.0 — crypto-dotnet#23. Pinned here because this is an interop contract we depend
/// on, and a substrate bump must not silently take it away.
/// </para>
/// </summary>
public sealed class Es256kHighSVerificationTests
{
    /// <summary>SEC 2 secp256k1 group order.</summary>
    private static readonly BigInteger GroupOrder = BigInteger.Parse(
        "00" + "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
        NumberStyles.HexNumber);

    [Fact]
    public void High_s_es256k_signature_verifies_because_rfc_8812_does_not_require_low_s()
    {
        var crypto = new DefaultCryptoProvider();
        var key = new DefaultKeyGenerator().Generate(KeyType.Secp256k1);
        var data = "the same message, signed once"u8.ToArray();

        var signature = crypto.Sign(KeyType.Secp256k1, key.PrivateKey, data);
        crypto.Verify(KeyType.Secp256k1, key.PublicKey, data, signature)
            .Should().BeTrue("the signature we just produced must verify");

        var malleated = FlipS(signature);

        // Guard the malleation itself: a botched flip and a genuine low-S rejection both end in
        // a failed Verify, so assert the bytes are the other valid representation before trusting
        // the result below.
        SValueOf(malleated).Should().Be(GroupOrder - SValueOf(signature), "S' must be exactly n - S");
        SValueOf(malleated).Should().BeGreaterThan(GroupOrder / 2, "the point is to test high-S");
        malleated.AsSpan(0, 32).SequenceEqual(signature.AsSpan(0, 32))
            .Should().BeTrue("R must be untouched — only S is malleated");

        crypto.Verify(KeyType.Secp256k1, key.PublicKey, data, malleated)
            .Should().BeTrue(
                "RFC 8812 imposes no low-S rule, so the (r, n-s) form is a conformant ES256K " +
                "signature and rejecting it refuses valid peer traffic (crypto-dotnet#23)");
    }

    private static BigInteger SValueOf(byte[] signature)
        => new(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);

    private static byte[] FlipS(byte[] signature)
    {
        var flipped = (GroupOrder - SValueOf(signature)).ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = (byte[])signature.Clone();
        Array.Clear(result, 32, 32);
        flipped.CopyTo(result.AsSpan(64 - flipped.Length));
        return result;
    }
}
