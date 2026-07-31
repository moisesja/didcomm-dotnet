using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Jose.Signing;
using DidComm.Messages;
using DidComm.Protocols;
using FluentAssertions;
using NetCrypto;
using Xunit;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Envelopes.Composition;

/// <summary>
/// FR-CONSIST-04 (#59) — the advisory "recipient not named in 'to'" check. The outcome rides on
/// <see cref="UnpackResult.RecipientAddressing"/>: <c>NotAddressed</c> is the spec's SHOULD-level
/// warning and never rejects the message. Own identity = the decrypting kid's DID subject plus the
/// declared own DID subjects. The envelope layer receives those already normalized; the facade's
/// construction-time normalization (DID URL ⇒ subject, dedupe, reject unparseable) is covered in
/// <c>DidCommClientUnitTests</c>.
/// </summary>
public sealed class RecipientAddressingTests
{
    private static readonly JoseCryptoProvider _crypto = new();

    private static IReadOnlySet<string> Own(params string[] didSubjects)
        => new HashSet<string>(didSubjects, StringComparer.Ordinal);

    private static Message BuildMessage(string? from = null, params string[] to)
    {
        var builder = new MessageBuilder().WithType("https://didcomm.org/empty/1.0/empty");
        if (from is not null) builder = builder.WithFrom(from);
        if (to.Length > 0) builder = builder.WithTo(to);
        return builder.Build();
    }

    private static async Task<string> PackAnoncryptAsync(Message msg, TestKeyMaterial recipient)
        => await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(msg, new[] { recipient.PublicJwk }, "A256GCM"), _crypto);

    private static async Task<string> PackSignedAsync(Message msg, TestKeyMaterial signer)
        => await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk.ToJwsSigner() }));

    [Fact]
    public async Task Encrypted_with_to_naming_the_decrypting_kid_reports_addressed()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await PackAnoncryptAsync(BuildMessage(to: "did:example:bob"), bob);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null, signerLookup: null, _crypto);

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.Addressed);
    }

    [Fact]
    public async Task Encrypted_without_to_reports_not_evaluated()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await PackAnoncryptAsync(BuildMessage(), bob);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null, signerLookup: null, _crypto);

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
    }

    [Fact]
    public async Task Encrypted_with_to_omitting_the_decrypting_kid_still_throws_via_consist_02()
    {
        // Documents why the NotAddressed warning is unreachable on the encrypted path: the
        // stricter FR-CONSIST-02 MUST rejects the envelope before the advisory would apply.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await PackAnoncryptAsync(BuildMessage(to: "did:example:carol"), bob);

        Action act = () => EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null, signerLookup: null, _crypto,
            ownDidSubjects: Own("did:example:bob"));

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-02*");
    }

    [Fact]
    public async Task Signed_only_with_own_identifier_absent_from_to_warns_and_still_delivers()
    {
        // The FR-CONSIST-04 acceptance criterion: warning surfaced, message delivered.
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var packed = await PackSignedAsync(
            BuildMessage(from: "did:example:alice", to: "did:example:carol"), signer);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null,
            _crypto,
            ownDidSubjects: Own("did:example:bob"));

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);
        unpacked.Message.Type.Should().Be("https://didcomm.org/empty/1.0/empty");
        unpacked.NonRepudiation.Should().BeTrue();
    }

    [Fact]
    public async Task Signed_only_with_own_identifier_in_to_reports_addressed()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var packed = await PackSignedAsync(
            BuildMessage(from: "did:example:alice", to: "did:example:bob"), signer);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null,
            _crypto,
            ownDidSubjects: Own("did:example:bob"));

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.Addressed);
    }

    [Fact]
    public async Task Signed_only_with_no_own_identifiers_reports_not_evaluated()
    {
        // No decrypting identity and nothing declared — there is nothing to check against.
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var packed = await PackSignedAsync(
            BuildMessage(from: "did:example:alice", to: "did:example:carol"), signer);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null,
            _crypto);

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
    }

    [Fact]
    public void Plaintext_with_own_identifier_absent_from_to_warns_and_still_delivers()
    {
        var packed = EnvelopeWriter.PackPlaintext(BuildMessage(to: "did:example:carol"));

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null, signerLookup: null, _crypto,
            ownDidSubjects: Own("did:example:bob"));

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);
        unpacked.Message.Type.Should().Be("https://didcomm.org/empty/1.0/empty");
    }

    [Fact]
    public void Plaintext_with_own_identifier_in_to_reports_addressed()
    {
        var packed = EnvelopeWriter.PackPlaintext(BuildMessage(to: "did:example:bob"));

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null, signerLookup: null, _crypto,
            ownDidSubjects: Own("did:example:bob"));

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.Addressed);
    }

    [Fact]
    public async Task Multi_recipient_to_naming_us_among_others_reports_addressed()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await PackAnoncryptAsync(
            BuildMessage(to: new[] { "did:example:carol", "did:example:bob" }), bob);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null, signerLookup: null, _crypto);

        unpacked.RecipientAddressing.Should().Be(RecipientAddressing.Addressed);
    }

    [Fact]
    public async Task Verified_snapshot_carries_the_recipient_addressing_outcome()
    {
        // #61 — the reader records the outcome on the immutable verified snapshot at
        // registration, so observers (which read the snapshot, not the result) see it too.
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var packed = await PackSignedAsync(
            BuildMessage(from: "did:example:alice", to: "did:example:carol"), signer);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null,
            _crypto,
            ownDidSubjects: Own("did:example:bob"));

        InboundMessageSnapshot.TryGetFor(unpacked.Message, out var snapshot).Should().BeTrue();
        snapshot.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed,
            "the snapshot must mirror the outcome the reader computed for this unpack");
    }
}
