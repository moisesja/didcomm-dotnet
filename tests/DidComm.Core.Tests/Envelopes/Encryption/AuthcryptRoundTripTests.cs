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
/// Envelope-level authcrypt round trips. The JWE build/parse primitives (the FR-ENC-09 CBC-HMAC pin,
/// apu↔skid binding, crit rejection, wire-format validation) now live in DataProofsDotnet.Jose and
/// are covered there; these tests exercise the DIDComm authcrypt envelope through
/// <see cref="EnvelopeWriter.PackEncryptedAsync"/> + <see cref="EnvelopeReader.UnpackAsync"/>, including
/// the higher-level behavioral guarantees the envelope layer is responsible for.
/// </summary>
public sealed class AuthcryptRoundTripTests
{
    private static readonly JoseCryptoProvider _crypto = new();

    private static Message Empty(string from, string to) =>
        new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom(from)
            .WithTo(to)
            .Build();

    [Theory]
    [InlineData(KeyType.X25519)]
    [InlineData(KeyType.P256)]
    [InlineData(KeyType.P384)]
    [InlineData(KeyType.P521)]
    public async Task Pack_then_unpack_round_trips_on_every_curve(KeyType keyType)
    {
        var alice = TestKeyMaterial.Generate(keyType, "did:example:alice#enc-1");
        var bob = TestKeyMaterial.Generate(keyType, "did:example:bob#enc-1");
        var msg = Empty("did:example:alice", "did:example:bob");

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid),
            _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        var result = EnvelopeReaderTestRunner.Unpack(packed, secrets, senderLookup, signerLookup: null, _crypto);

        result.Authenticated.Should().BeTrue();
        result.KeyWrap.Should().Be("ECDH-1PU+A256KW");
        result.ContentEncryption.Should().Be("A256CBC-HS512");
        result.SenderKid.Should().Be(alice.PublicJwk.Kid);
        result.RecipientKid.Should().Be(bob.PublicJwk.Kid);
        result.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public async Task Authcrypt_refuses_a256gcm_per_fr_enc_09()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#enc");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#enc");
        var msg = Empty("did:example:alice", "did:example:bob");

        // FR-ENC-09: authcrypt (ECDH-1PU) is pinned to A256CBC-HS512; A256GCM is refused by the builder.
        Func<Task> act = () => EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256GCM",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid),
            _crypto);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Authcrypt_refuses_mismatched_sender_and_recipient_curves()
    {
        var aliceX = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bobP = TestKeyMaterial.Generate(KeyType.P256, "did:example:bob#p");
        var msg = Empty("did:example:alice", "did:example:bob");

        Func<Task> act = () => EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bobP.PublicJwk }, "A256CBC-HS512",
                SenderKey: aliceX.PrivateJwk.ToEcdhKey(_crypto),
                Skid: aliceX.PublicJwk.Kid),
            _crypto);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Unpack_without_sender_lookup_throws_for_authcrypt()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = Empty("did:example:alice", "did:example:bob");

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid),
            _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });

        // Authcrypt requires the sender public key to derive Zs; with no sender lookup unpack fails.
        Action act = () => EnvelopeReaderTestRunner.Unpack(packed, secrets, senderLookup: null, signerLookup: null, _crypto);
        act.Should().Throw<CryptoException>();
    }

    [Fact]
    public async Task Tampered_tag_breaks_authcrypt_unwrap_via_kek_mismatch()
    {
        // FR-ENC-15 ordering: the tag is bound into the KEK derivation via SuppPubInfo. Tampering
        // with the tag therefore breaks BOTH the AEAD integrity check AND the KEK, so unwrap fails.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = Empty("did:example:alice", "did:example:bob");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid),
            _crypto);

        // Flip a byte in the tag (the JSON property after "tag":")
        var idx = packed.IndexOf("\"tag\":\"", StringComparison.Ordinal) + "\"tag\":\"".Length;
        var bumped = packed[idx] == 'A' ? 'B' : 'A';
        var tampered = string.Concat(packed.AsSpan(0, idx), bumped.ToString(), packed.AsSpan(idx + 1));

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        Action act = () => EnvelopeReaderTestRunner.Unpack(tampered, secrets, senderLookup, signerLookup: null, _crypto);
        act.Should().Throw<CryptoException>();
    }
}
