using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using FluentAssertions;
using NetCrypto;
using Xunit;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Envelopes.Encryption;

/// <summary>
/// Envelope-level anoncrypt round trips. The JWE build/parse primitives (mixed-curve rejection, apv
/// recompute, wire-format tamper detection) now live in DataProofsDotnet.Jose and are covered there;
/// these tests exercise the DIDComm anoncrypt envelope through
/// <see cref="EnvelopeWriter.PackEncryptedAsync"/> + <see cref="EnvelopeReader.Unpack"/>.
/// </summary>
public sealed class AnoncryptRoundTripTests
{
    private static readonly JoseCryptoProvider _crypto = new();

    private static Message Empty(params string[] to)
    {
        var b = new MessageBuilder().WithType("https://didcomm.org/empty/1.0/empty");
        foreach (var t in to)
            b = b.WithTo(t);
        return b.Build();
    }

    [Theory]
    [InlineData(KeyType.X25519, "A256CBC-HS512")]
    [InlineData(KeyType.X25519, "A256GCM")]
    [InlineData(KeyType.X25519, "XC20P")]
    [InlineData(KeyType.P256, "A256CBC-HS512")]
    [InlineData(KeyType.P256, "A256GCM")]
    [InlineData(KeyType.P384, "A256CBC-HS512")]
    [InlineData(KeyType.P521, "A256GCM")]
    public async Task Pack_then_unpack_round_trips_across_all_supported_combinations(KeyType keyType, string enc)
    {
        var bob = TestKeyMaterial.Generate(keyType, "did:example:bob#key-1");
        var msg = Empty("did:example:bob");

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(msg, new[] { bob.PublicJwk }, enc),
            _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var result = EnvelopeReader.Unpack(packed, secrets, senderLookup: null, signerLookup: null, _crypto);

        result.Authenticated.Should().BeFalse();
        result.AnonymousSender.Should().BeTrue();
        result.KeyWrap.Should().Be("ECDH-ES+A256KW");
        result.ContentEncryption.Should().Be(enc);
        result.RecipientKid.Should().Be(bob.PublicJwk.Kid);
        result.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public async Task Multi_recipient_same_curve_yields_one_jwe_with_multiple_wraps()
    {
        var bob1 = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#key-x1");
        var bob2 = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#key-x2");
        var bob3 = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#key-x3");
        var msg = Empty("did:example:bob");

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob1.PublicJwk, bob2.PublicJwk, bob3.PublicJwk }, "XC20P"),
            _crypto);

        // Each recipient can independently decrypt.
        foreach (var owner in new[] { bob1, bob2, bob3 })
        {
            var result = EnvelopeReader.Unpack(
                packed,
                new DictionarySecretsLookup(new[] { owner.PrivateJwk }),
                senderLookup: null,
                signerLookup: null,
                _crypto);

            result.RecipientKid.Should().Be(owner.PublicJwk.Kid);
            result.Message.Id.Should().Be(msg.Id);
        }
    }

    [Fact]
    public async Task Mixed_curve_recipients_in_one_call_throws()
    {
        var bobX = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var bobP = TestKeyMaterial.Generate(KeyType.P256, "did:example:bob#p");
        var msg = Empty("did:example:bob");

        // FR-ENC-04 single-curve recipient rule is enforced by the JWE builder.
        Func<Task> act = () => EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(msg, new[] { bobX.PublicJwk, bobP.PublicJwk }, "A256GCM"),
            _crypto);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task No_matching_recipient_kid_throws()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = Empty("did:example:bob");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(msg, new[] { bob.PublicJwk }, "A256GCM"),
            _crypto);

        var unrelated = TestKeyMaterial.Generate(KeyType.X25519, "did:example:eve#x");
        var secrets = new DictionarySecretsLookup(new[] { unrelated.PrivateJwk });

        Action act = () => EnvelopeReader.Unpack(packed, secrets, senderLookup: null, signerLookup: null, _crypto);
        act.Should().Throw<DidCommException>();
    }
}
