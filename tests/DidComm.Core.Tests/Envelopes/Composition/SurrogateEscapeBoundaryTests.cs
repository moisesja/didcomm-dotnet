using System.Text;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Signing;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using AwesomeAssertions;
using NetCrypto;
using Xunit;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Envelopes.Composition;

/// <summary>
/// Pins how unpack treats an unpaired UTF-16 surrogate that reaches didcomm's own JSON parse
/// (issue #77). Every layer of an unpack is first classified by <c>EnvelopeDetector</c>, which
/// parses the whole JSON text with duplicate-member detection. To compare member names,
/// System.Text.Json decodes every one of them. A name written as a lone-surrogate escape
/// (<c>{"\uD800":1}</c>) therefore throws <see cref="InvalidOperationException"/> from the parse
/// itself, not <c>JsonException</c>. That escaped <c>UnpackAsync</c> as a raw, untyped fault
/// (FR-API-07) at every layer, before any authentication. Anyone who can deliver bytes could
/// trigger it.
/// <para>
/// The JOSE parser's own surrogate checks (see <see cref="JoseInputHardeningTests"/>) never see
/// this input, because the detector runs first. Each test starts from a real envelope and makes
/// one hostile change to it. A control with a well-formed escaped surrogate pair in the same
/// position shows the rejection comes from the lone surrogate, not from where it was inserted.
/// </para>
/// </summary>
public sealed class SurrogateEscapeBoundaryTests
{
    private static readonly JoseCryptoProvider _crypto = new();

    // Six ASCII characters on the wire: valid UTF-8 and valid JSON grammar, but the string they
    // denote is not Unicode text. Verbatim literals keep the backslash, so the escape itself reaches
    // the JSON text.
    private const string UnpairedSurrogateEscape = @"\uD800";

    // The well-formed control: U+1F600 written as an escaped surrogate pair.
    private const string SurrogatePairEscape = @"\uD83D\uDE00";

    // ---- Member names ----

    [Theory]
    [InlineData("{")]          // top level of the plaintext message
    [InlineData("\"body\":{")] // inside the message body
    public void Plaintext_member_name_escape_is_rejected_as_malformed(string after)
    {
        var packed = EnvelopeWriter.PackPlaintext(EmptyMessage());

        Unpack(WithMemberNamed(packed, after, SurrogatePairEscape)).Encrypted.Should().BeFalse();

        Action act = () => Unpack(WithMemberNamed(packed, after, UnpairedSurrogateEscape));
        act.Should().Throw<MalformedMessageException>();
    }

    [Theory]
    [InlineData("{")]            // top level of the JWE General JSON serialization
    [InlineData("\"header\":{")] // the per-recipient unprotected header
    public async Task Anoncrypt_member_name_escape_is_rejected_before_any_key_work(string after)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);
        var bobSecrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });

        Unpack(WithMemberNamed(packed, after, SurrogatePairEscape), bobSecrets).Encrypted.Should().BeTrue();

        // The detector runs before any recipient-key lookup, so a holder and a stranger fail the same
        // way and the failure says nothing about who holds the key.
        var hostile = WithMemberNamed(packed, after, UnpairedSurrogateEscape);
        Action holder = () => Unpack(hostile, bobSecrets);
        Action stranger = () => Unpack(hostile);
        var holderFailure = holder.Should().Throw<MalformedMessageException>().Which;
        var strangerFailure = stranger.Should().Throw<MalformedMessageException>().Which;
        holderFailure.Message.Should().Be(strangerFailure.Message);
    }

    [Theory]
    [InlineData("{")]            // top level of the JWS General JSON serialization
    [InlineData("\"header\":{")] // the per-signature unprotected header
    public async Task Signed_member_name_escape_is_rejected_as_malformed(string after)
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(EmptyMessage(), new[] { signer.PrivateJwk.ToJwsSigner() }));
        Func<string, Jwk?> signerLookup = kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null;

        Unpack(WithMemberNamed(packed, after, SurrogatePairEscape), signerLookup: signerLookup)
            .SignerKid.Should().Be(signer.PublicJwk.Kid);

        Action act = () => Unpack(WithMemberNamed(packed, after, UnpairedSurrogateEscape), signerLookup: signerLookup);
        act.Should().Throw<MalformedMessageException>();
    }

    [Fact]
    public void Member_name_escape_inside_an_anoncrypted_plaintext_is_rejected_as_malformed()
    {
        // The inner layer. Anyone can anoncrypt to a recipient's public key, so the hostile plaintext
        // reaches the detector again once the holder has decrypted it. The JOSE parser never reads
        // the decrypted bytes as JSON, so nothing upstream catches it.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var bobSecrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var plaintext = EnvelopeWriter.PackPlaintext(EmptyMessage());

        Unpack(AnoncryptWrap(WithMemberNamed(plaintext, "{", SurrogatePairEscape), bob.PublicJwk), bobSecrets)
            .Encrypted.Should().BeTrue();

        Action act = () => Unpack(AnoncryptWrap(WithMemberNamed(plaintext, "{", UnpairedSurrogateEscape), bob.PublicJwk), bobSecrets);
        act.Should().Throw<MalformedMessageException>();
    }

    // ---- Member values ----

    [Theory]
    // An extension header. Message.AdditionalHeaders keeps these as raw JsonElement and decodes one
    // only when something reads it.
    [InlineData("{", "\"x-unread\":\"ESC\",", @"\uD800")]
    [InlineData("{", "\"x-unread\":\"ESC\",", @"\uDC00")] // a lone LOW surrogate
    // A nested extension-header value that didcomm itself decodes: TraceObserver reads
    // ~trace.report_uri on every inbound when tracing is enabled.
    [InlineData("{", "\"~trace\":{\"report_uri\":\"ESC\"},", @"\uD800")]
    // A body value, which Message deserialization decodes.
    [InlineData("\"body\":{", "\"x\":\"ESC\",", @"\uD800")]
    public void Plaintext_member_value_escape_is_rejected_as_malformed(string after, string member, string escape)
    {
        // A value is not decoded by the parse itself, so the detector checks every string of a
        // plaintext layer. Before, an extension-header value like this unpacked, and the fault
        // surfaced later as a raw InvalidOperationException wherever the value was read. A body
        // value already failed as malformed, from deserialization.
        var packed = EnvelopeWriter.PackPlaintext(EmptyMessage());

        Unpack(InsertAfter(packed, after, member.Replace("ESC", SurrogatePairEscape))).Encrypted.Should().BeFalse();

        Action act = () => Unpack(InsertAfter(packed, after, member.Replace("ESC", escape)));
        act.Should().Throw<MalformedMessageException>();
    }

    // ---- A lone surrogate character handed to the API directly ----

    [Fact]
    public void Lone_surrogate_character_in_the_packed_string_is_rejected_as_malformed()
    {
        // A different case from the escape: a .NET string that is not well-formed UTF-16. It cannot
        // come from decoding wire bytes (invalid sequences become U+FFFD), only from a caller's own
        // string. Transcoding it for the parse throws ArgumentException, which must not leave the
        // unpack boundary either.
        var packed = EnvelopeWriter.PackPlaintext(EmptyMessage());
        var hostile = packed.Insert(packed.IndexOf("\"body\":{", StringComparison.Ordinal) + "\"body\":{".Length, "\"x\":\"\uD800\",");

        Action act = () => Unpack(hostile);

        act.Should().Throw<MalformedMessageException>();
    }

    // ---- Building a forward from a caller-supplied payload ----

    [Fact]
    public void Forward_payload_with_a_member_name_escape_is_rejected_as_an_argument_error()
    {
        // ForwardMessage.Create parses each payload the caller hands it, with the same duplicate
        // detection. Its documented failure for a payload that is not valid JSON is ArgumentException.
        const string payload = "{\"" + UnpairedSurrogateEscape + "\":1}";

        Action act = () => ForwardMessage.Create("did:example:mediator", "did:example:bob", new[] { payload });

        act.Should().Throw<ArgumentException>().WithMessage("Forward payload is not valid JSON*");
    }

    // ---- Helpers ----

    private static Message EmptyMessage() => new MessageBuilder()
        .WithType("https://didcomm.org/empty/1.0/empty")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(new System.Text.Json.Nodes.JsonObject { ["k"] = "v" })
        .Build();

    private static UnpackResult Unpack(
        string packed, DictionarySecretsLookup? secrets = null, Func<string, Jwk?>? signerLookup = null)
        => EnvelopeReaderTestRunner.Unpack(
            packed, secrets ?? new DictionarySecretsLookup(Array.Empty<Jwk>()), senderLookup: null, signerLookup, _crypto);

    private static string AnoncryptWrap(string inner, Jwk recipientPublicJwk) =>
        DpEnc.JweBuilder.BuildEcdhEsA256Kw(
            Encoding.UTF8.GetBytes(inner), new[] { recipientPublicJwk }, "A256GCM", _crypto, MediaTypes.Encrypted);

    /// <summary>
    /// Insert a member whose NAME is <paramref name="rawJsonEscape"/>, right after the first
    /// occurrence of <paramref name="after"/>. This edits the string rather than going through
    /// JsonNode, because System.Text.Json writes a lone surrogate as U+FFFD, so the hostile escape
    /// would never reach the wire.
    /// </summary>
    private static string WithMemberNamed(string packed, string after, string rawJsonEscape)
        => InsertAfter(packed, after, $"\"{rawJsonEscape}\":1,");

    private static string InsertAfter(string packed, string after, string member)
    {
        var at = packed.IndexOf(after, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the envelope must contain '{after}'");
        return packed.Insert(at + after.Length, member);
    }
}
