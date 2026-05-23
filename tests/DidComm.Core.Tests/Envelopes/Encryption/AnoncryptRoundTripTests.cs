using System.Text;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Encryption;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Envelopes.Encryption;

public sealed class AnoncryptRoundTripTests
{
    private static readonly DidCommDefaultCryptoProvider _crypto = new();

    [Theory]
    [InlineData(KeyType.X25519, "A256CBC-HS512")]
    [InlineData(KeyType.X25519, "A256GCM")]
    [InlineData(KeyType.X25519, "XC20P")]
    [InlineData(KeyType.P256, "A256CBC-HS512")]
    [InlineData(KeyType.P256, "A256GCM")]
    [InlineData(KeyType.P384, "A256CBC-HS512")]
    [InlineData(KeyType.P521, "A256GCM")]
    public void Pack_then_unpack_round_trips_across_all_supported_combinations(KeyType keyType, string enc)
    {
        var bob = TestKeyMaterial.Generate(keyType, "did:example:bob#key-1");
        var plaintext = Encoding.UTF8.GetBytes("{\"id\":\"m1\",\"type\":\"https://didcomm.org/empty/1.0/empty\"}");

        var packed = JweBuilder.PackAnoncrypt(plaintext, new[] { bob.PublicJwk }, enc, _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var result = JweParser.Parse(packed, secrets, senderLookup: null, _crypto);

        result.IsAuthenticated.Should().BeFalse();
        result.Algorithm.Should().Be("ECDH-ES+A256KW");
        result.ContentEncryption.Should().Be(enc);
        result.RecipientKid.Should().Be(bob.PublicJwk.Kid);
        Encoding.UTF8.GetString(result.Plaintext).Should().Be(Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Multi_recipient_same_curve_yields_one_jwe_with_multiple_wraps()
    {
        var bob1 = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#key-x1");
        var bob2 = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#key-x2");
        var bob3 = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#key-x3");
        var plaintext = Encoding.UTF8.GetBytes("{\"id\":\"m1\",\"type\":\"https://didcomm.org/empty/1.0/empty\"}");

        var packed = JweBuilder.PackAnoncrypt(
            plaintext,
            new[] { bob1.PublicJwk, bob2.PublicJwk, bob3.PublicJwk },
            "XC20P", _crypto);

        // Each recipient can independently decrypt.
        foreach (var owner in new[] { bob1, bob2, bob3 })
        {
            var result = JweParser.Parse(
                packed,
                new DictionarySecretsLookup(new[] { owner.PrivateJwk }),
                senderLookup: null, _crypto);

            result.RecipientKid.Should().Be(owner.PublicJwk.Kid);
            Encoding.UTF8.GetString(result.Plaintext).Should().Be(Encoding.UTF8.GetString(plaintext));
        }
    }

    [Fact]
    public void Mixed_curve_recipients_in_one_call_throws()
    {
        var bobX = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var bobP = TestKeyMaterial.Generate(KeyType.P256, "did:example:bob#p");

        Action act = () => JweBuilder.PackAnoncrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bobX.PublicJwk, bobP.PublicJwk },
            "A256GCM", _crypto);

        act.Should().Throw<ArgumentException>().WithMessage("*FR-ENC-04*");
    }

    [Fact]
    public void Apv_tampering_is_detected_on_unpack()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = JweBuilder.PackAnoncrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            "A256GCM", _crypto);

        // Tamper by inserting a second fake recipient entry — apv recomputed at parse time
        // will no longer match the protected header's apv.
        var tampered = packed.Replace(
            $"{{\"header\":{{\"kid\":\"{bob.PublicJwk.Kid}\"}},\"encrypted_key\":",
            $"{{\"header\":{{\"kid\":\"did:example:mallory#k\"}},\"encrypted_key\":\"AAAA\"}},{{\"header\":{{\"kid\":\"{bob.PublicJwk.Kid}\"}},\"encrypted_key\":",
            StringComparison.Ordinal);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });

        Action act = () => JweParser.Parse(tampered, secrets, senderLookup: null, _crypto);
        act.Should().Throw<CryptoException>().WithMessage("*apv*");
    }

    [Fact]
    public void No_matching_recipient_kid_throws()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = JweBuilder.PackAnoncrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            "A256GCM", _crypto);

        var unrelated = TestKeyMaterial.Generate(KeyType.X25519, "did:example:eve#x");
        var secrets = new DictionarySecretsLookup(new[] { unrelated.PrivateJwk });

        Action act = () => JweParser.Parse(packed, secrets, senderLookup: null, _crypto);
        act.Should().Throw<CryptoException>();
    }
}
