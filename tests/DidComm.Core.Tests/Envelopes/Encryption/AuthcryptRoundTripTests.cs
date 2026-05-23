using System.Text;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Encryption;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Envelopes.Encryption;

public sealed class AuthcryptRoundTripTests
{
    private static readonly DidCommDefaultCryptoProvider _crypto = new();

    [Theory]
    [InlineData(KeyType.X25519)]
    [InlineData(KeyType.P256)]
    [InlineData(KeyType.P384)]
    [InlineData(KeyType.P521)]
    public void Pack_then_unpack_round_trips_on_every_curve(KeyType keyType)
    {
        var alice = TestKeyMaterial.Generate(keyType, "did:example:alice#enc-1");
        var bob = TestKeyMaterial.Generate(keyType, "did:example:bob#enc-1");
        var plaintext = Encoding.UTF8.GetBytes("{\"id\":\"m1\",\"type\":\"https://didcomm.org/empty/1.0/empty\"}");

        var packed = JweBuilder.PackAuthcrypt(
            plaintext,
            new[] { bob.PublicJwk },
            alice.PrivateJwk,
            alice.PublicJwk.Kid!,
            "A256CBC-HS512",
            _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        var result = JweParser.Parse(packed, secrets, senderLookup, _crypto);

        result.IsAuthenticated.Should().BeTrue();
        result.Algorithm.Should().Be("ECDH-1PU+A256KW");
        result.ContentEncryption.Should().Be("A256CBC-HS512");
        result.SenderKid.Should().Be(alice.PublicJwk.Kid);
        result.RecipientKid.Should().Be(bob.PublicJwk.Kid);
        Encoding.UTF8.GetString(result.Plaintext).Should().Be(Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Authcrypt_refuses_a256gcm_per_fr_enc_09()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#enc");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#enc");

        Action act = () => JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256GCM", _crypto);

        act.Should().Throw<ArgumentException>().WithMessage("*FR-ENC-09*");
    }

    [Fact]
    public void Authcrypt_refuses_mismatched_sender_and_recipient_curves()
    {
        var aliceX = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bobP = TestKeyMaterial.Generate(KeyType.P256, "did:example:bob#p");

        Action act = () => JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bobP.PublicJwk },
            aliceX.PrivateJwk, aliceX.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unpack_without_sender_lookup_throws_for_authcrypt()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");

        var packed = JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });

        Action act = () => JweParser.Parse(packed, secrets, senderLookup: null, _crypto);
        act.Should().Throw<CryptoException>().WithMessage("*sender*");
    }

    [Fact]
    public void Tampered_tag_breaks_authcrypt_unwrap_via_kek_mismatch()
    {
        // FR-ENC-15 ordering: the tag is bound into the KEK derivation via SuppPubInfo.
        // Tampering with the tag therefore breaks BOTH the AEAD integrity check AND the KEK,
        // so unwrap fails before AEAD decrypt is reached. This is the load-bearing guarantee.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);

        // Flip a byte in the tag (the JSON property after "tag":")
        var idx = packed.IndexOf("\"tag\":\"", StringComparison.Ordinal) + "\"tag\":\"".Length;
        var bumped = packed[idx] == 'A' ? 'B' : 'A';
        var tampered = string.Concat(packed.AsSpan(0, idx), bumped.ToString(), packed.AsSpan(idx + 1));

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        Action act = () => JweParser.Parse(tampered, secrets, senderLookup, _crypto);
        act.Should().Throw<CryptoException>();
    }
}
