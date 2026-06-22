using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using FluentAssertions;
using NetCrypto;
using Xunit;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Envelopes.Signing;

/// <summary>
/// Envelope-level signed round trips. The JWS build/parse primitives now live in
/// DataProofsDotnet.Jose; these tests exercise the DIDComm signed envelope through
/// <see cref="EnvelopeWriter.PackSignedAsync"/> + <see cref="EnvelopeReader.UnpackAsync"/> so the
/// from↔signer binding (FR-CONSIST-03), FR-SIG-06 inner-'to', and the flattened/general JWS shapes
/// are covered at the composition seam.
/// </summary>
public sealed class JwsRoundTripTests
{
    private static readonly JoseCryptoProvider _crypto = new();

    private static UnpackResult Unpack(string packed, Func<string, Jwk?> signerLookup) =>
        EnvelopeReaderTestRunner.Unpack(
            packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: signerLookup,
            _crypto);

    [Theory]
    [InlineData(KeyType.Ed25519, "EdDSA")]
    [InlineData(KeyType.P256, "ES256")]
    [InlineData(KeyType.Secp256k1, "ES256K")]
    public async Task Sign_then_verify_round_trips_for_each_alg(KeyType keyType, string expectedAlg)
    {
        var signer = TestKeyMaterial.Generate(keyType, "did:example:alice#key-1");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk.ToJwsSigner() }));

        var result = Unpack(packed, kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null);

        result.SignatureAlgorithm.Should().Be(expectedAlg);
        result.SignerKid.Should().Be("did:example:alice#key-1");
        result.Message.Id.Should().Be(msg.Id);
        result.Message.From.Should().Be("did:example:alice");
    }

    [Fact]
    public async Task Tampered_payload_fails_verification()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk.ToJwsSigner() }));

        // Flip a byte in the base64url payload to invalidate the signature.
        var tampered = packed.Replace("\"payload\":\"e", "\"payload\":\"f", StringComparison.Ordinal);

        Action act = () => Unpack(tampered, _ => signer.PublicJwk);

        act.Should().Throw<CryptoException>();
    }

    [Fact]
    public async Task Verifier_with_no_matching_kid_throws_crypto()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk.ToJwsSigner() }));

        Action act = () => Unpack(packed, _ => null);
        act.Should().Throw<CryptoException>();
    }

    [Fact]
    public async Task Single_signer_emits_flattened_form()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk.ToJwsSigner() }));

        // Flattened JSON: top-level "signature" + "protected", no "signatures" array.
        packed.Should().Contain("\"signature\":");
        packed.Should().NotContain("\"signatures\":");
    }

    [Fact]
    public async Task Multiple_signers_emit_general_form_and_either_verifies()
    {
        var signerA = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#ed");
        var signerB = TestKeyMaterial.Generate(KeyType.P256, "did:example:alice#p256");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signerA.PrivateJwk.ToJwsSigner(), signerB.PrivateJwk.ToJwsSigner() }));

        packed.Should().Contain("\"signatures\":");

        // General form's only top-level "signature" property is inside the array — assert by
        // parsing rather than substring.
        using var doc = System.Text.Json.JsonDocument.Parse(packed);
        doc.RootElement.TryGetProperty("signature", out _).Should().BeFalse("general form has no top-level 'signature'");

        var resultA = Unpack(packed, kid => kid == signerA.PublicJwk.Kid ? signerA.PublicJwk : null);
        resultA.SignerKid.Should().Be(signerA.PublicJwk.Kid);

        var resultB = Unpack(packed, kid => kid == signerB.PublicJwk.Kid ? signerB.PublicJwk : null);
        resultB.SignerKid.Should().Be(signerB.PublicJwk.Kid);
    }

    [Fact]
    public async Task Sign_then_encrypt_inner_to_required_throws_when_missing()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build(); // no 'to'

        // FR-SIG-06 is enforced when a signed JWM is nested inside an encrypt layer.
        Func<Task> act = () => EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256GCM",
                Signers: new[] { signer.PrivateJwk.ToJwsSigner() }),
            _crypto);

        await act.Should().ThrowAsync<MalformedMessageException>().WithMessage("*FR-SIG-06*");
    }

    [Fact]
    public async Task Sign_then_encrypt_inner_to_passes_when_present()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256GCM",
                Signers: new[] { signer.PrivateJwk.ToJwsSigner() }),
            _crypto);
        packed.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Consistency_check_blocks_mismatched_signer_kid()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:mallory#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .Build();

        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk.ToJwsSigner() }));

        Action act = () => Unpack(packed, _ => signer.PublicJwk);
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-03*");
    }

    [Fact]
    public async Task Verified_jws_without_a_signer_kid_fails_closed()
    {
        // A JWS that VERIFIES but carries no kid (neither protected nor unprotected header) would
        // leave FR-CONSIST-03 with nothing to bind 'from' against. EnvelopeReader must reject it
        // rather than report the message authenticated with an unbound signer.
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var kidless = await BuildKidlessSignedJws(signer);

        // The lookup returns the correct key for any kid (here the empty string), so the signature
        // verifies — the only thing missing is the surfaced kid.
        Action act = () => Unpack(kidless, _ => signer.PublicJwk);

        act.Should().Throw<CryptoException>().WithMessage("*signer kid*");
    }

    /// <summary>Build a flattened JWS whose signer has no <c>kid</c> in either header (verifies, but
    /// surfaces no signer identity) — exercising the EnvelopeReader fail-closed guard.</summary>
    private static async Task<string> BuildKidlessSignedJws(TestKeyMaterial signer)
    {
        var priv = DataProofsDotnet.Jose.Base64Url.Decode(signer.PrivateJwk.D!);
        var keyPair = new DefaultKeyGenerator().FromPrivateKey(KeyType.Ed25519, priv);
        var jwsSigner = new DataProofsDotnet.Jose.Signing.JwsSigner(
            new KeyPairSigner(keyPair, new DefaultCryptoProvider()), kid: null);
        return await DataProofsDotnet.Jose.Signing.JwsBuilder.BuildJsonAsync(
            System.Text.Encoding.UTF8.GetBytes("{}"), new[] { jwsSigner }, MediaTypes.Signed);
    }
}
