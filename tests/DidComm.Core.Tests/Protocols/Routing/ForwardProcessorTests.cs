using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Secrets;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Phase 4 Checkpoint E — unit-level guard rails for <see cref="ForwardProcessor"/>: option
/// validation, non-forward rejection, FR-ROUTE-07 silence on <c>please_ack</c>, delay/expires
/// propagation. Crypto round-trips through the processor live in the interop project
/// (Checkpoint F's Alice→Mediator→Bob test).
/// </summary>
public sealed class ForwardProcessorTests
{
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
        var client = NewClient();
        var nonForward = new MessageBuilder()
            .WithType("https://didcomm.org/trust-ping/2.0/ping")
            .WithBody(new JsonObject())
            .Build();
        var packed = await client.PackPlaintextAsync(nonForward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var act = async () => await processor.ProcessAsync(packed);

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("non-forward message");
    }

    [Fact]
    public async Task ProcessAsync_passes_through_inner_payload_bytes_in_default_mode()
    {
        // Drive a plaintext forward through the processor — no crypto, just structural
        // extraction. (UnpackAsync handles plaintext envelopes too.)
        var innerPayload = """{"protected":"abc","ciphertext":"xyz","iv":"","tag":"","recipients":[]}""";
        var forwardMessage = ForwardMessage.Create("did:example:m", "did:example:next", new[] { innerPayload });
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forwardMessage);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var result = await processor.ProcessAsync(packed);

        result.NextHop.Should().Be("did:example:next");
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(innerPayload);
    }

    [Fact]
    public async Task ProcessAsync_propagates_expires_time_when_set_on_the_inbound_forward()
    {
        var inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
        // Use a future timestamp so UnpackAsync's FR-API-05 expiry check doesn't reject the
        // forward before the processor sees it.
        var futureExpiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var forward = ForwardMessage.Create(
            mediator: "did:example:m",
            next: "did:example:n",
            packedPayloads: new[] { inner },
            expiresTimeEpochSeconds: futureExpiry);
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());
        var result = await processor.ProcessAsync(packed);

        result.ExpiresTime.Should().Be(futureExpiry);
    }

    [Fact]
    public async Task ProcessAsync_returns_null_expires_when_inbound_forward_omits_it()
    {
        var inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { inner });
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());
        var result = await processor.ProcessAsync(packed);

        result.ExpiresTime.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_ignores_please_ack_on_a_forward_FR_ROUTE_07()
    {
        // FR-ROUTE-07: the mediator MUST NOT honor `please_ack` on a forward. We assert the
        // contract by confirming the processor produces a normal result regardless of the
        // header's presence — there is no ack-emitting side channel to inspect because the
        // mediator never propagates one.
        var inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { inner });
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["please_ack"] = System.Text.Json.JsonDocument.Parse("""["RECEIPT"]""").RootElement.Clone(),
        };
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());
        var result = await processor.ProcessAsync(packed);

        result.NextHop.Should().Be("did:example:n");
        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(inner);
    }

    [Fact]
    public async Task ProcessAsync_resolves_negative_delay_milli_to_a_random_bounded_TimeSpan()
    {
        var inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { inner });
        // negative → randomized between 0 and |n|
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = System.Text.Json.JsonDocument.Parse("-5000").RootElement.Clone(),
        };
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());
        var result = await processor.ProcessAsync(packed);

        result.Delay.Should().NotBeNull();
        result.Delay!.Value.TotalMilliseconds.Should().BeInRange(0, 5000);
    }

    [Fact]
    public async Task ProcessAsync_resolves_positive_delay_milli_exactly()
    {
        var inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { inner });
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = System.Text.Json.JsonDocument.Parse("750").RootElement.Clone(),
        };
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());
        var result = await processor.ProcessAsync(packed);

        result.Delay.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task ProcessAsync_throws_when_forward_attachment_has_neither_json_nor_base64()
    {
        var malformed = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = "did:example:n" })
            .WithAttachment(new Attachment { Data = new AttachmentData { Hash = "stub-multihash" } })
            .Build();
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(malformed);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var act = async () => await processor.ProcessAsync(packed);

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("missing both 'data.json' and 'data.base64'");
    }

    [Fact]
    public async Task ProcessAsync_decodes_a_base64url_attachment_payload()
    {
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
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());
        var result = await processor.ProcessAsync(packed);

        Encoding.UTF8.GetString(result.OnwardPacked).Should().Be(innerPayload);
    }

    [Fact]
    public async Task ProcessAsync_maps_malformed_base64_attachment_to_MalformedMessageException()
    {
        // Issue #24 (red-team): a forward attachment whose data.base64 is not strict base64url (e.g.
        // '=' padding) must surface as MalformedMessageException, not a raw FormatException escaping
        // the mediator's ProcessAsync.
        var forward = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = "did:example:n" })
            .WithAttachment(new Attachment { Data = new AttachmentData { Base64 = "SGVsbG8=" } }) // padded → rejected
            .Build();
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var act = async () => await processor.ProcessAsync(packed);

        (await act.Should().ThrowAsync<MalformedMessageException>())
            .Which.Message.Should().Contain("not valid base64url");
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-long.MaxValue)]
    public async Task ProcessAsync_does_not_crash_on_extreme_negative_delay_milli(long extreme)
    {
        var inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { inner });
        forward.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["delay_milli"] = System.Text.Json.JsonDocument.Parse(extreme.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone(),
        };
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());
        var result = await processor.ProcessAsync(packed);

        result.Delay.Should().NotBeNull();
        result.Delay!.Value.TotalMilliseconds.Should().BeInRange(0, int.MaxValue);
    }

    [Fact]
    public async Task ProcessAsync_throws_when_forward_carries_more_than_one_attachment()
    {
        var inner1 = """{"protected":"a","ciphertext":"1","iv":"","tag":"","recipients":[]}""";
        var inner2 = """{"protected":"b","ciphertext":"2","iv":"","tag":"","recipients":[]}""";
        var forward = ForwardMessage.Create("did:example:m", "did:example:n", new[] { inner1, inner2 });
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(forward);

        var processor = new ForwardProcessor(client, new EmptyKeyService(), new ForwardProcessorOptions());

        var act = async () => await processor.ProcessAsync(packed);

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("exactly one packed payload");
    }

    private static DidCommClient NewClient() =>
        new(new EmptySecretsResolver(), new EmptyKeyService(), new DidCommOptions());

    private static Jwk Jwk(string kid) => new() { Kty = "OKP", Crv = "X25519", X = "stub", Kid = kid };

    private sealed class EmptySecretsResolver : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => Task.FromResult<Jwk?>(null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class EmptyKeyService : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());
        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(false);
        public void RejectUnsupportedMethod(string did) { }
    }
}
