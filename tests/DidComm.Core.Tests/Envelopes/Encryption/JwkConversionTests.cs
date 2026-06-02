using DidComm.Exceptions;
using DidComm.Jose;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Envelopes.Encryption;

/// <summary>
/// OKP (Ed25519 / X25519) key-import hardening: the EC path validates the point is on-curve, but
/// OKP keys import raw bytes, so <see cref="JwkConversion.ToNetDidJwk"/> — the single chokepoint
/// every public-key import flows through — must reject any <c>x</c> that doesn't decode to exactly
/// 32 bytes before it reaches the crypto primitive (RFC 8037 §2).
/// </summary>
public sealed class JwkConversionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void ToNetDidJwk_rejects_okp_key_whose_x_is_not_32_bytes(int length)
    {
        var jwk = new Jwk { Kty = "OKP", Crv = "Ed25519", X = Base64Url.Encode(new byte[length]), Kid = "did:example:a#k" };

        Action act = () => JwkConversion.ToNetDidJwk(jwk);

        act.Should().Throw<MalformedMessageException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void ToNetDidJwk_accepts_okp_key_with_32_byte_x()
    {
        var jwk = new Jwk { Kty = "OKP", Crv = "X25519", X = Base64Url.Encode(new byte[32]), Kid = "did:example:a#k" };

        Action act = () => JwkConversion.ToNetDidJwk(jwk);

        act.Should().NotThrow();
    }
}
