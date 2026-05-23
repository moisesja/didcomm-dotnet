using DidComm.Crypto.Kdf;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Crypto.Kdf;

public sealed class EcdhEsKdfTests
{
    private static readonly DidCommDefaultCryptoProvider _crypto = new();

    [Theory]
    [InlineData(KeyType.X25519)]
    [InlineData(KeyType.P256)]
    public void Sender_and_receiver_compute_the_same_kek(KeyType keyType)
    {
        var gen = new DefaultKeyGenerator();
        var ephemeral = gen.Generate(keyType);
        var recipient = gen.Generate(keyType);

        var apv = new byte[] { 1, 2, 3, 4 };
        var algId = System.Text.Encoding.ASCII.GetBytes("ECDH-ES+A256KW");

        var kekSender = EcdhEsKdf.DeriveKey(
            _crypto.NetDidProvider, keyType,
            ephemeral.PrivateKey, recipient.PublicKey,
            algId, apv, keyDataLen: 32);

        var kekReceiver = EcdhEsKdf.DeriveKeyForReceiver(
            _crypto.NetDidProvider, keyType,
            recipient.PrivateKey, ephemeral.PublicKey,
            algId, apv, keyDataLen: 32);

        kekSender.Should().Equal(kekReceiver);
        kekSender.Length.Should().Be(32);
    }

    [Fact]
    public void Different_apv_produces_different_kek()
    {
        var gen = new DefaultKeyGenerator();
        var ephemeral = gen.Generate(KeyType.X25519);
        var recipient = gen.Generate(KeyType.X25519);

        var algId = System.Text.Encoding.ASCII.GetBytes("ECDH-ES+A256KW");

        var kek1 = EcdhEsKdf.DeriveKey(
            _crypto.NetDidProvider, KeyType.X25519,
            ephemeral.PrivateKey, recipient.PublicKey,
            algId, new byte[] { 1 }, keyDataLen: 32);

        var kek2 = EcdhEsKdf.DeriveKey(
            _crypto.NetDidProvider, KeyType.X25519,
            ephemeral.PrivateKey, recipient.PublicKey,
            algId, new byte[] { 2 }, keyDataLen: 32);

        kek1.Should().NotEqual(kek2);
    }
}
