using System.Text.Json.Nodes;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Envelopes.Composition;

public sealed class EnvelopeReaderTests
{
    private static readonly DidCommDefaultCryptoProvider _crypto = new();

    [Fact]
    public void Plaintext_round_trips_through_writer_and_reader()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackPlaintext(msg);
        var unpacked = EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: null,
            _crypto);

        unpacked.Encrypted.Should().BeFalse();
        unpacked.Authenticated.Should().BeFalse();
        unpacked.NonRepudiation.Should().BeFalse();
        unpacked.Stack.Should().Equal(EnvelopeKind.Plaintext);
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public void Signed_round_trips_with_metadata()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackSigned(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk }),
            _crypto);

        var unpacked = EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null,
            _crypto);

        unpacked.NonRepudiation.Should().BeTrue();
        unpacked.SignatureAlgorithm.Should().Be("EdDSA");
        unpacked.SignerKid.Should().Be(signer.PublicJwk.Kid);
        unpacked.Stack.Should().Equal(EnvelopeKind.Signed, EnvelopeKind.Plaintext);
    }

    [Fact]
    public void Anoncrypt_round_trips_with_metadata()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackEncrypted(
            new PackEncryptedParameters(msg, new[] { bob.PublicJwk }, "A256GCM"),
            _crypto);

        var unpacked = EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null,
            signerLookup: null,
            _crypto);

        unpacked.Encrypted.Should().BeTrue();
        unpacked.AnonymousSender.Should().BeTrue();
        unpacked.Authenticated.Should().BeFalse();
        unpacked.ContentEncryption.Should().Be("A256GCM");
        unpacked.KeyWrap.Should().Be("ECDH-ES+A256KW");
        unpacked.RecipientKid.Should().Be(bob.PublicJwk.Kid);
        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Plaintext);
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public void Authcrypt_round_trips_with_metadata()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackEncrypted(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderPrivateJwk: alice.PrivateJwk,
                Skid: alice.PublicJwk.Kid),
            _crypto);

        var unpacked = EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null,
            _crypto);

        unpacked.Encrypted.Should().BeTrue();
        unpacked.Authenticated.Should().BeTrue();
        unpacked.AnonymousSender.Should().BeFalse();
        unpacked.NonRepudiation.Should().BeFalse();
        unpacked.SenderKid.Should().Be(alice.PublicJwk.Kid);
        unpacked.KeyWrap.Should().Be("ECDH-1PU+A256KW");
        unpacked.ContentEncryption.Should().Be("A256CBC-HS512");
    }

    [Fact]
    public void Anoncrypt_then_sign_unwraps_to_inner_plaintext()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackEncrypted(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256GCM",
                SignerPrivateJwks: new[] { signer.PrivateJwk }),
            _crypto);

        var unpacked = EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null,
            signerLookup: _ => signer.PublicJwk,
            _crypto);

        unpacked.Encrypted.Should().BeTrue();
        unpacked.NonRepudiation.Should().BeTrue();
        unpacked.AnonymousSender.Should().BeTrue();
        unpacked.SignerKid.Should().Be(signer.PublicJwk.Kid);
        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Signed, EnvelopeKind.Plaintext);
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public void Anoncrypt_authcrypt_protect_sender_unwraps_recursively()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackEncrypted(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderPrivateJwk: alice.PrivateJwk,
                Skid: alice.PublicJwk.Kid,
                ProtectSender: true),
            _crypto);

        var unpacked = EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null,
            _crypto);

        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Encrypted, EnvelopeKind.Plaintext);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public void Consistency_check_blocks_recipient_kid_not_in_to_header()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithTo("did:example:carol") // Bob isn't in the 'to' list
            .Build();

        var packed = EnvelopeWriter.PackEncrypted(
            new PackEncryptedParameters(msg, new[] { bob.PublicJwk }, "A256GCM"),
            _crypto);

        Action act = () => EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null,
            signerLookup: null,
            _crypto);

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-02*");
    }

    [Fact]
    public void Consistency_check_blocks_authcrypt_skid_not_matching_plaintext_from()
    {
        // FR-CONSIST-01: a self-consistent forged envelope — cryptographically valid authcrypt
        // from Alice, but the inner plaintext claims it came from Carol. The skid authenticates
        // Alice, so the mismatched 'from' MUST be rejected.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:carol")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackEncrypted(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderPrivateJwk: alice.PrivateJwk,
                Skid: alice.PublicJwk.Kid),
            _crypto);

        Action act = () => EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null,
            _crypto);

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-01*");
    }

    [Fact]
    public void Consistency_check_blocks_authcrypt_sign_inner_signer_not_matching_skid()
    {
        // FR-CONSIST-05: authcrypt(sign(...)) where the inner signer (Carol) differs from the
        // authcrypt sender (Alice). The inner signature verifies and its 'from' agrees with the
        // signer (so FR-CONSIST-03 passes), but the signer/sender disagreement MUST be rejected.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var carol = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:carol#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:carol")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackEncrypted(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderPrivateJwk: alice.PrivateJwk,
                Skid: alice.PublicJwk.Kid,
                SignerPrivateJwks: new[] { carol.PrivateJwk }),
            _crypto);

        Action act = () => EnvelopeReader.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: kid => kid == carol.PublicJwk.Kid ? carol.PublicJwk : null,
            _crypto);

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-05*");
    }
}
