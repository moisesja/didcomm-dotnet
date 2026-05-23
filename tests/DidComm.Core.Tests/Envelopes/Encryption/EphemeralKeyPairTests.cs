using DidComm.Crypto.KeyAgreement;
using DidComm.Jose;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Envelopes.Encryption;

public sealed class EphemeralKeyPairTests
{
    [Theory]
    [InlineData(JoseAlgorithms.CrvX25519, 32, 32, "OKP")]
    [InlineData(JoseAlgorithms.CrvP256, 32, 33, "EC")]   // P-256 priv 32 bytes; compressed SEC1 = 1 + 32
    [InlineData(JoseAlgorithms.CrvP384, 48, 49, "EC")]
    [InlineData(JoseAlgorithms.CrvP521, 66, 67, "EC")]
    public void Generate_returns_curve_appropriate_lengths_and_kty(string crv, int expectedPrivLen, int expectedPubLen, string expectedKty)
    {
        var pair = EphemeralKeyPair.Generate(crv);

        pair.Curve.Should().Be(crv);
        pair.PrivateKey.Length.Should().Be(expectedPrivLen);
        pair.PublicKey.Length.Should().Be(expectedPubLen);

        var epk = pair.ToPublicEpkJwk();
        epk.Kty.Should().Be(expectedKty);
        epk.Crv.Should().Be(crv);
        epk.X.Should().NotBeNullOrEmpty();
        if (expectedKty == "EC")
            epk.Y.Should().NotBeNullOrEmpty();
        // The Phase 1 model uses `D = null` for public-only JWK; ephemeral epk omits private.
        epk.D.Should().BeNull();
    }

    [Fact]
    public void Successive_calls_yield_different_pairs()
    {
        var a = EphemeralKeyPair.Generate(JoseAlgorithms.CrvX25519);
        var b = EphemeralKeyPair.Generate(JoseAlgorithms.CrvX25519);
        a.PrivateKey.Should().NotEqual(b.PrivateKey);
        a.PublicKey.Should().NotEqual(b.PublicKey);
    }

    [Fact]
    public void Clear_zeroes_private_key()
    {
        var pair = EphemeralKeyPair.Generate(JoseAlgorithms.CrvX25519);
        pair.Clear();
        pair.PrivateKey.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Unknown_curve_throws()
    {
        Action act = () => EphemeralKeyPair.Generate("not-a-curve");
        act.Should().Throw<NotSupportedException>();
    }
}
