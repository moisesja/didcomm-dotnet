using DidComm.Crypto.KeyAgreement;
using DidComm.Jose;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;

namespace DidComm.Tests.Crypto.KeyAgreement;

public sealed class KeyTypeMapperTests
{
    [Theory]
    [InlineData(JoseAlgorithms.CrvX25519, KeyType.X25519)]
    [InlineData(JoseAlgorithms.CrvP256, KeyType.P256)]
    [InlineData(JoseAlgorithms.CrvP384, KeyType.P384)]
    [InlineData(JoseAlgorithms.CrvP521, KeyType.P521)]
    public void Maps_key_agreement_curves(string crv, KeyType expected)
    {
        KeyTypeMapper.FromCurveForKeyAgreement(crv).Should().Be(expected);
    }

    [Theory]
    [InlineData(JoseAlgorithms.CrvEd25519, KeyType.Ed25519)]
    [InlineData(JoseAlgorithms.CrvP256, KeyType.P256)]
    [InlineData(JoseAlgorithms.CrvSecp256k1, KeyType.Secp256k1)]
    public void Maps_signing_curves(string crv, KeyType expected)
    {
        KeyTypeMapper.FromCurveForSigning(crv).Should().Be(expected);
    }

    [Theory]
    [InlineData(JoseAlgorithms.CrvEd25519, JoseAlgorithms.EdDSA)]
    [InlineData(JoseAlgorithms.CrvP256, JoseAlgorithms.ES256)]
    [InlineData(JoseAlgorithms.CrvP384, JoseAlgorithms.ES384)]
    [InlineData(JoseAlgorithms.CrvP521, JoseAlgorithms.ES512)]
    [InlineData(JoseAlgorithms.CrvSecp256k1, JoseAlgorithms.ES256K)]
    public void Maps_curve_to_jws_alg(string crv, string expectedAlg)
    {
        KeyTypeMapper.ToJwsAlgorithm(crv).Should().Be(expectedAlg);
    }

    [Theory]
    [InlineData(JoseAlgorithms.A256CbcHs512, 64, 16)]
    [InlineData(JoseAlgorithms.A256Gcm, 32, 12)]
    [InlineData(JoseAlgorithms.XC20P, 32, 24)]
    public void Aead_key_and_iv_lengths(string enc, int cekLen, int ivLen)
    {
        KeyTypeMapper.ContentEncryptionKeySizeBytes(enc).Should().Be(cekLen);
        KeyTypeMapper.IvSizeBytes(enc).Should().Be(ivLen);
    }

    [Fact]
    public void Unknown_curve_throws_not_supported()
    {
        Action act = () => KeyTypeMapper.FromCurveForKeyAgreement("Curve123");
        act.Should().Throw<NotSupportedException>();
    }
}
