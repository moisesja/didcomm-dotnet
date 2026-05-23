using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Signing;
using DidComm.Messages;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Envelopes.Signing;

public sealed class JwsRoundTripTests
{
    private static readonly DidCommDefaultCryptoProvider _crypto = new();

    [Theory]
    [InlineData(KeyType.Ed25519, "EdDSA")]
    [InlineData(KeyType.P256, "ES256")]
    [InlineData(KeyType.Secp256k1, "ES256K")]
    public void Sign_then_verify_round_trips_for_each_alg(KeyType keyType, string expectedAlg)
    {
        var signer = TestKeyMaterial.Generate(keyType, "did:example:alice#key-1");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = JwsBuilder.Build(msg, new[] { signer.PrivateJwk }, _crypto);

        var result = JwsParser.Parse(packed,
            kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null,
            _crypto);

        result.SignatureAlgorithm.Should().Be(expectedAlg);
        result.SignerKid.Should().Be("did:example:alice#key-1");
        result.Message.Id.Should().Be(msg.Id);
        result.Message.From.Should().Be("did:example:alice");
    }

    [Fact]
    public void Tampered_payload_fails_verification()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = JwsBuilder.Build(msg, new[] { signer.PrivateJwk }, _crypto);

        // Flip a byte in the base64url payload to invalidate the signature.
        var tampered = packed.Replace("\"payload\":\"e", "\"payload\":\"f", StringComparison.Ordinal);

        Action act = () => JwsParser.Parse(tampered,
            _ => signer.PublicJwk,
            _crypto);

        act.Should().Throw<CryptoException>();
    }

    [Fact]
    public void Verifier_with_no_matching_kid_throws_crypto()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = JwsBuilder.Build(msg, new[] { signer.PrivateJwk }, _crypto);

        Action act = () => JwsParser.Parse(packed, _ => null, _crypto);
        act.Should().Throw<CryptoException>();
    }

    [Fact]
    public void Single_signer_emits_flattened_form()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = JwsBuilder.Build(msg, new[] { signer.PrivateJwk }, _crypto);

        // Flattened JSON: top-level "signature" + "protected", no "signatures" array.
        packed.Should().Contain("\"signature\":");
        packed.Should().NotContain("\"signatures\":");
    }

    [Fact]
    public void Multiple_signers_emit_general_form_and_either_verifies()
    {
        var signerA = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#ed");
        var signerB = TestKeyMaterial.Generate(KeyType.P256, "did:example:alice#p256");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = JwsBuilder.Build(msg, new[] { signerA.PrivateJwk, signerB.PrivateJwk }, _crypto);

        packed.Should().Contain("\"signatures\":");

        // General form's only top-level "signature" property is inside the array — assert by
        // parsing rather than substring.
        using var doc = System.Text.Json.JsonDocument.Parse(packed);
        doc.RootElement.TryGetProperty("signature", out _).Should().BeFalse("general form has no top-level 'signature'");

        var byKid = new Dictionary<string, Jwk>
        {
            [signerA.PublicJwk.Kid!] = signerA.PublicJwk,
            [signerB.PublicJwk.Kid!] = signerB.PublicJwk,
        };

        var resolveA = (string kid) => kid == signerA.PublicJwk.Kid ? signerA.PublicJwk : null;
        var resultA = JwsParser.Parse(packed, resolveA, _crypto);
        resultA.SignerKid.Should().Be(signerA.PublicJwk.Kid);

        var resolveB = (string kid) => kid == signerB.PublicJwk.Kid ? signerB.PublicJwk : null;
        var resultB = JwsParser.Parse(packed, resolveB, _crypto);
        resultB.SignerKid.Should().Be(signerB.PublicJwk.Kid);
    }

    [Fact]
    public void Sign_then_encrypt_inner_to_required_throws_when_missing()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build(); // no 'to'

        Action act = () => JwsBuilder.Build(msg, new[] { signer.PrivateJwk }, _crypto, requireInnerToHeader: true);
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-SIG-06*");
    }

    [Fact]
    public void Sign_then_encrypt_inner_to_passes_when_present()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = JwsBuilder.Build(msg, new[] { signer.PrivateJwk }, _crypto, requireInnerToHeader: true);
        packed.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Consistency_check_blocks_mismatched_signer_kid()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:mallory#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = JwsBuilder.Build(msg, new[] { signer.PrivateJwk }, _crypto);

        Action act = () => JwsParser.Parse(packed, _ => signer.PublicJwk, _crypto);
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-03*");
    }
}
