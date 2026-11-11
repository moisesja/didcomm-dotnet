using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using AwesomeAssertions;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Unit-level guard rails for <see cref="ForwardProcessor"/>: option validation, non-forward
/// rejection, the FR-ROUTE-11 posture refusals, FR-ROUTE-07 silence on <c>please_ack</c>,
/// delay/expires propagation and attachment decoding. Structural tests feed the processor a
/// synthetic "decrypted by the mediator" <c>UnpackResult</c> (see <see cref="ForwardTestEnvelopes"/>);
/// crypto round-trips through the processor live in the interop project (Alice→Mediator→Bob).
/// </summary>
public sealed class ForwardProcessorTests
{
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";

    [Fact]
    public void Options_validate_rejects_rewrap_combined_with_extra_routing_keys()
    {
        var bad = new ForwardProcessorOptions(
            Mode: RewrapMode.ReanoncryptToNext,
            ExtraRecipientRoutingKeys: new[] { Jwk("did:example:x#k") });

        Action act = () => bad.Validate();
        act.Should().Throw<ArgumentException>()
            .WithMessage("*pick one transformation per mediator hop*");
    }

    [Fact]
    public void Options_validate_allows_passthrough_with_extra_keys()
    {
        var ok = new ForwardProcessorOptions(
            Mode: RewrapMode.PassThrough,
            ExtraRecipientRoutingKeys: new[] { Jwk("did:example:x#k") });

        Action act = () => ok.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Options_validate_allows_passthrough_with_no_extras()
    {
        new ForwardProcessorOptions().Validate(); // default ctor: PassThrough, null extras.
    }

    [Fact]
    public void Default_options_are_PassThrough_with_no_extras()
    {
        var opts = new ForwardProcessorOptions();
        opts.Mode.Should().Be(RewrapMode.PassThrough);
        opts.ExtraRecipientRoutingKeys.Should().BeNull();
    }

    [Fact]
    public void Construction_rejects_invalid_combinations_via_Validate()
    {
        var client = NewClient();
        var keyService = new EmptyKeyService();
        var bad = new ForwardProcessorOptions(
            Mode: RewrapMode.ReanoncryptToNext,
            ExtraRecipientRoutingKeys: new[] { Jwk("did:example:x#k") });

        Action act = () => _ = new ForwardProcessor(client, keyService, bad);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ProcessAsync_rejects_non_forward_plaintext_with_ConsistencyException()
    {
        // Kept on the plaintext string path: TryParse runs before the posture check, so a
        // non-forward is refused as such (not as "not encrypted"). The message must not echo the type.
        var client = NewClient();
        var nonForward = new MessageBuilder()
            .WithType("https://didcomm.org/trust-ping/2.0/ping")
            .WithBody(new JsonObject())
            .Build();
        var packed = await client.PackPlaintextAsync(nonForward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var act = async () => await processor.ProcessAsync(packed);

        var failure = (await act.Should().ThrowAsync<ConsistencyException>()).Which;
        failure.Message.Should().Contain("non-forward message");
        failure.Message.Should().NotContain("trust-ping", "the inbound type is a wire value and must not be echoed (FR-API-09 posture)");
    }

    // ── FR-ROUTE-11 posture refusals (#89) ───────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_string_overload_refuses_a_plaintext_forward_with_ConsistencyException()
    {
        // FR-ROUTE-11 (#89): a plaintext forward unpacks fine and is then refused — a mediator
        // relays only what was encrypted to a key it holds.
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner }));
        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var act = async () => await processor.ProcessAsync(packed);

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("FR-ROUTE-11").And.NotContain("did:example:n");
    }

    [Fact]
    public async Task ProcessAsync_refuses_an_UnpackResult_with_Encrypted_false()
    {
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });

        var act = async () => await NewProcessor().ProcessAsync(AsPlaintext(forward));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("not encrypted").And.Contain("FR-ROUTE-11");
    }

    [Fact]
    public async Task ProcessAsync_refuses_an_encrypted_UnpackResult_without_RecipientKid()
    {
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });

        var act = async () => await NewProcessor().ProcessAsync(AsEncryptedWithoutRecipientKid(forward));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("not decrypted with a key this mediator holds");
    }

    [Fact]
    public async Task ProcessAsync_refuses_a_signed_only_forward()
    {
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });

        var act = async () => await NewProcessor().ProcessAsync(AsSignedOnly(forward));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("signed-only").And.NotContain(SignerKid);
    }

    // ── Structural contract on a forward the mediator decrypted ──────────────────────────

    [Fact]
    public async Task ProcessAsync_passes_through_inner_payload_bytes_in_default_mode()
    {
        // Rewritten for FR-ROUTE-11 (#89): the pass-through contract is unchanged, the input is now a forward decrypted by the mediator.
        var innerPayload = """{"protected":"abc","ciphertext":"xyz","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:next", new[] { innerPayload });

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.NextHop.Should().Be("did:example:next");
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(innerPayload);
    }

    [Fact]
    public async Task ProcessAsync_propagates_expires_time_when_set_on_the_inbound_forward()
    {
        // Rewritten for FR-ROUTE-11 (#89): expiry propagation is unchanged, the input is now a forward decrypted by the mediator.
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var forward = ForwardMessage.Create(
            mediator: "did:example:m",
            next: "did:example:n",
            packedPayloads: new[] { Inner },
            expiresTimeEpochSeconds: futureExpiry);

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.ExpiresTime.Should().Be(futureExpiry);
    }

    [Fact]
    public async Task ProcessAsync_returns_null_expires_when_inbound_forward_omits_it()
    {
        // Rewritten for FR-ROUTE-11 (#89): same contract, synthetic decrypted input.
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.ExpiresTime.Should().BeNull();
        result.NotBefore.Should().BeNull("no delay_milli was present");
    }

    [Fact]
    public async Task ProcessAsync_ignores_please_ack_on_a_forward_FR_ROUTE_07()
    {
        // Rewritten for FR-ROUTE-11 (#89): the FR-ROUTE-07 silence is unchanged, the input is now a forward decrypted by the mediator.
        // FR-ROUTE-07: the mediator MUST NOT honor `please_ack` on a forward. We assert the
        // contract by confirming the processor produces a normal result regardless of the
        // header's presence — there is no ack-emitting side channel to inspect because the
        // mediator never propagates one.
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["please_ack"] = System.Text.Json.JsonDocument.Parse("""["RECEIPT"]""").RootElement.Clone(),
        };

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.NextHop.Should().Be("did:example:n");
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(Inner);
    }

    [Fact]
    public async Task ProcessAsync_resolves_negative_delay_milli_to_a_random_bounded_TimeSpan()
    {
        // Rewritten for FR-ROUTE-11 (#89): delay resolution is unchanged, the input is now a forward decrypted by the mediator.
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });
        // negative → randomized between 0 and |n|
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = System.Text.Json.JsonDocument.Parse("-5000").RootElement.Clone(),
        };

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.Delay.Should().NotBeNull();
        result.Delay!.Value.TotalMilliseconds.Should().BeInRange(0, 5000);
    }

    [Fact]
    public async Task ProcessAsync_resolves_positive_delay_milli_exactly()
    {
        // Rewritten for FR-ROUTE-11 (#89): delay resolution is unchanged, the input is now a forward decrypted by the mediator.
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = System.Text.Json.JsonDocument.Parse("750").RootElement.Clone(),
        };

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.Delay.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task ProcessAsync_throws_when_forward_attachment_has_neither_json_nor_base64()
    {
        // Moved to the decrypted path for FR-ROUTE-11 (#89): the posture check now precedes attachment extraction, so a plaintext input would fail for the wrong reason.
        var malformed = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = "did:example:n" })
            .WithAttachment(new Attachment { Data = new AttachmentData { Hash = "stub-multihash" } })
            .Build();

        var act = async () => await NewProcessor().ProcessAsync(AsDecryptedByMediator(malformed));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("missing both 'data.json' and 'data.base64'");
    }

    [Fact]
    public async Task ProcessAsync_decodes_a_base64url_attachment_payload()
    {
        // Rewritten for FR-ROUTE-11 (#89): base64url decoding is unchanged, the input is now a forward decrypted by the mediator.
        // DIDComm attachments encode data.base64 as base64url (unpadded). The value below
        // base64url-decodes cleanly but is NOT valid standard base64 (unpadded length), so this
        // exercises the base64url path rather than Convert.FromBase64String.
        var innerPayload = """{"protected":"abc","ciphertext":"xyz","iv":"","tag":"","recipients":[]}""";
        var base64url = Base64Url.EncodeUtf8(innerPayload);
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = "did:example:n" })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = base64url } })
            .Build();

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(innerPayload);
    }

    [Fact]
    public async Task ProcessAsync_maps_malformed_base64_attachment_to_MalformedMessageException()
    {
        // Moved to the decrypted path for FR-ROUTE-11 (#89): the posture check now precedes attachment extraction.
        // Issue #24 (red-team): a forward attachment whose data.base64 is genuinely invalid base64url
        // ('@' is non-alphabet; '+'/'/' are standard-base64, not base64url) must surface as
        // MalformedMessageException, not a raw FormatException escaping the mediator's ProcessAsync.
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = "did:example:n" })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = "@@@@" } }) // not base64url at all
            .Build();

        var act = async () => await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        (await act.Should().ThrowAsync<MalformedMessageException>())
            .Which.Message.Should().Contain("not valid base64url");
    }

    [Fact]
    public async Task ProcessAsync_relays_a_padded_base64_attachment_FR_ATT_02()
    {
        // Rewritten for FR-ROUTE-11 (#89): lenient base64 is unchanged, the input is now a forward decrypted by the mediator.
        // PR #38 review: attachment data.base64 has no spec no-pad requirement (FR-ATT-02, cf. Aries
        // RFC 0017), and the mediator only relays the bytes for the recipient to re-parse — so a peer's
        // '=' padded forward attachment must still be relayed, not refused. (Only the strict JOSE/JWT/
        // OOB paths reject padding.)
        var innerPayload = "forward-me!"; // 11 bytes → base64 carries a trailing '=' pad
        var bytes = Encoding.UTF8.GetBytes(innerPayload);
        var paddedBase64Url = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_'); // padded base64url
        paddedBase64Url.Should().EndWith("="); // sanity: this value really is padded

        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = "did:example:n" })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = paddedBase64Url } })
            .Build();

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(innerPayload);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-long.MaxValue)]
    public async Task ProcessAsync_does_not_crash_on_extreme_negative_delay_milli(long extreme)
    {
        // Rewritten for FR-ROUTE-11 (#89): the overflow guard is unchanged, the input is now a forward decrypted by the mediator.
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { Inner });
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = System.Text.Json.JsonDocument.Parse(extreme.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

        var result = await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        result.Delay.Should().NotBeNull();
        result.Delay!.Value.TotalMilliseconds.Should().BeInRange(0, int.MaxValue);
    }

    [Fact]
    public async Task ProcessAsync_throws_when_forward_carries_more_than_one_attachment()
    {
        // Moved to the decrypted path for FR-ROUTE-11 (#89): the posture check now precedes the attachment-count check.
        var inner1 = """{"protected":"a","ciphertext":"1","iv":"","tag":"","recipients":[]}""";
        var inner2 = """{"protected":"b","ciphertext":"2","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { inner1, inner2 });

        var act = async () => await NewProcessor().ProcessAsync(AsDecryptedByMediator(forward));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("exactly one packed payload");
    }

    private static Jwk Jwk(string kid) => new() { Kty = "OKP", Crv = "X25519", X = "stub", Kid = kid };
}
