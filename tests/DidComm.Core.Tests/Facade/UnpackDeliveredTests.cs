using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Protocols.Routing;
using Xunit;
using static DidComm.Tests.Facade.MediatedClientTestWorld;

namespace DidComm.Tests.Facade;

/// <summary>
/// #96 design §4.6: <see cref="DidCommClient.UnpackDeliveredAsync"/> opens the forward layers a
/// re-wrapping mediator added for this agent (FR-ROUTE-06, receiver side) — the mediator's own
/// acceptance rule (anoncrypted plaintext, no signature, sender key or <c>from_prior</c>, one
/// attachment, <c>next</c> = the DID that decrypted it), at most
/// <see cref="ForwardHandler.MaxSelfAddressedLayers"/> layers — and refuses any other forward as a
/// failure, never a message. Real envelopes throughout; the one-layer case is the real output of
/// <see cref="ForwardProcessor"/> in <see cref="RewrapMode.ReanoncryptToNext"/>.
/// </summary>
public sealed class UnpackDeliveredTests
{
    private readonly MediatedClientTestWorld _world = new();

    // ---- what opens ----

    [Fact]
    public async Task A_pass_through_delivery_is_the_message_itself_with_no_layer_opened()
    {
        var note = Note();
        var packed = await _world.AuthcryptAsync(note);

        var delivered = await _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        delivered.LayersOpened.Should().Be(0);
        delivered.InnermostPacked.Should().BeSameAs(packed);
        delivered.Result.Message.Id.Should().Be(note.Id);
        delivered.Result.Authenticated.Should().BeTrue("the innermost message may be authcrypt");
        delivered.Result.SenderKid.Should().Be(Alice + "#x1");
    }

    [Fact]
    public async Task The_real_rewrap_output_of_a_mediator_opens_to_the_senders_message()
    {
        var note = Note();
        var inner = await _world.AuthcryptAsync(note);
        var delivered = await _world.MediatorRewrapAsync(inner);

        var result = await _world.BobClient().UnpackDeliveredAsync(delivered, CancellationToken.None);

        result.LayersOpened.Should().Be(1);
        result.Result.Message.Id.Should().Be(note.Id);
        result.Result.SenderKid.Should().Be(Alice + "#x1", "the innermost message keeps its own envelope");
        result.InnermostPacked.Should().NotBe(delivered);
        (await _world.BobClient().UnpackAsync(result.InnermostPacked)).Message.Id.Should().Be(note.Id,
            "InnermostPacked is the text the innermost message was unpacked from");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers)]
    public async Task Nested_self_addressed_layers_open_up_to_the_bound(int layers)
    {
        var note = Note();
        var inner = await _world.AuthcryptAsync(note);
        var packed = await _world.RewrappedAsync(inner, layers);

        var result = await _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        result.LayersOpened.Should().Be(layers);
        result.Result.Message.Id.Should().Be(note.Id);
        (await _world.BobClient().UnpackAsync(result.InnermostPacked)).Message.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task One_layer_beyond_the_bound_is_refused_as_malformed()
    {
        var inner = await _world.AuthcryptAsync(Note());
        var packed = await _world.RewrappedAsync(inner, ForwardHandler.MaxSelfAddressedLayers + 1);

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        (await act.Should().ThrowAsync<MalformedMessageException>()).Which.Message.Should().Contain("more than 8 forward layers");
    }

    [Fact]
    public async Task A_layer_with_a_base64_attachment_opens()
    {
        var note = Note();
        var inner = await _world.AuthcryptAsync(note);
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, Base64Attachment(Base64Url(inner))));

        var result = await _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        result.LayersOpened.Should().Be(1);
        result.Result.Message.Id.Should().Be(note.Id);
        result.InnermostPacked.Should().Be(inner, "base64 carries the exact bytes");
    }

    [Fact]
    public async Task A_layer_with_a_data_json_attachment_opens()
    {
        var note = Note();
        var inner = await _world.AuthcryptAsync(note);
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, JsonAttachment(inner)));

        var result = await _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        result.LayersOpened.Should().Be(1);
        result.Result.Message.Id.Should().Be(note.Id);
    }

    [Theory]
    [InlineData("#x1")]
    [InlineData("?service=a")]
    [InlineData("/path")]
    public async Task A_next_that_is_a_DID_URL_of_the_decrypting_DID_counts_as_this_agent(string suffix)
    {
        var note = Note();
        var inner = await _world.AuthcryptAsync(note);
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob + suffix, JsonAttachment(inner)));

        var result = await _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        result.LayersOpened.Should().Be(1);
        result.Result.Message.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task An_anonymous_innermost_message_is_returned_with_its_own_packed_text()
    {
        var note = Note(from: null);
        var inner = await _world.AnoncryptAsync(note);
        var packed = await _world.RewrappedAsync(inner, 1);

        var result = await _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        result.Result.AnonymousSender.Should().BeTrue();
        result.Result.Message.Id.Should().Be(note.Id);
        result.LayersOpened.Should().Be(1);
    }

    // ---- forwards that are not for this agent are failures, never messages ----

    [Fact]
    public async Task A_forward_naming_another_DID_is_refused()
    {
        var inner = await _world.AuthcryptAsync(Note(to: Mallory), to: Mallory);
        var packed = await _world.AnoncryptAsync(Forward(Bob, Mallory, JsonAttachment(inner)));

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        (await act.Should().ThrowAsync<ConsistencyException>()).Which.Message.Should().Contain("re-wrapped for this agent").And.NotContain(Mallory);
    }

    [Fact]
    public async Task A_forward_naming_another_DID_this_agent_holds_is_refused()
    {
        // Bob holds Carol's keys too, but only the DID that decrypted the layer counts as this agent
        // (the mediator's rule): a forward Bob decrypted that names Carol is not opened.
        var inner = await _world.AuthcryptAsync(Note(to: Carol), to: Carol);
        var packed = await _world.AnoncryptAsync(Forward(Bob, Carol, JsonAttachment(inner)));

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task An_authcrypt_forward_is_refused()
    {
        var inner = await _world.AuthcryptAsync(Note());
        var packed = await _world.AuthcryptAsync(Forward(Bob, Bob, JsonAttachment(inner)), from: Alice);

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task A_signed_anoncrypt_forward_is_refused()
    {
        var inner = await _world.AuthcryptAsync(Note());
        var packed = await _world.AnoncryptSignedAsync(Forward(Bob, Bob, JsonAttachment(inner)), signFrom: Alice);

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task A_signed_only_forward_is_refused()
    {
        var inner = await _world.AuthcryptAsync(Note());
        var packed = await _world.Senders().PackSignedAsync(Forward(Bob, Bob, JsonAttachment(inner)), Alice);

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task A_plaintext_forward_is_refused()
    {
        var inner = await _world.AuthcryptAsync(Note());
        var packed = await _world.Senders().PackPlaintextAsync(Forward(Bob, Bob, JsonAttachment(inner)));

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task A_forward_carrying_from_prior_is_refused()
    {
        var inner = await _world.AuthcryptAsync(Note());
        var jwt = await FromPriorBuilder.BuildTerminationAsync(Prior, Now.ToUnixTimeSeconds(), _world.PriorSigningKey.PrivateJwk);
        var forward = Forward(Bob, Bob, JsonAttachment(inner));
        forward.FromPrior = jwt;
        var packed = await _world.AnoncryptAsync(forward);
        var unpacked = await _world.BobClient().UnpackAsync(packed);
        unpacked.FromPrior.Should().NotBeNull("the layer unpacks: a termination from_prior rides on anoncrypt");

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task A_forward_with_two_attachments_is_refused()
    {
        var inner = await _world.AuthcryptAsync(Note());
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, JsonAttachment(inner), JsonAttachment(inner)));

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task A_refused_layer_deep_in_the_chain_fails_the_whole_message()
    {
        var inner = await _world.AuthcryptAsync(Note(to: Mallory), to: Mallory);
        var forMallory = await _world.AnoncryptAsync(Forward(Bob, Mallory, JsonAttachment(inner)));
        var packed = await _world.RewrappedAsync(forMallory, 3);

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    // ---- attachment decoding: the mediator's one rule ----

    [Theory]
    [InlineData("   ")]
    [InlineData("!!!not-base64!!!")]
    public async Task An_attachment_that_is_empty_or_not_base64url_is_malformed(string base64)
    {
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, Base64Attachment(base64)));

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<MalformedMessageException>();
    }

    [Fact]
    public async Task An_attachment_with_no_data_is_refused()
    {
        var attachment = new Attachment { Id = "a1", MediaType = ForwardConstants.PayloadMediaType, Data = new AttachmentData { Links = new[] { "https://example.org/x" }, Hash = "abc" } };
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, attachment));

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<ConsistencyException>();
    }

    [Fact]
    public async Task An_attachment_that_is_not_a_packed_message_is_malformed()
    {
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, Base64Attachment(Base64Url("not a message"))));

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        await act.Should().ThrowAsync<DidCommException>();
    }

    // ---- every layer under the full receive rules ----

    [Fact]
    public async Task An_expired_inner_layer_is_refused_as_expired()
    {
        var expired = Now.ToUnixTimeSeconds() - 3600;
        var inner = await _world.AuthcryptAsync(Note());
        var expiredLayer = ForwardMessage.Create(Bob, Bob, new[] { inner }, expiresTimeEpochSeconds: expired);
        var packed = await _world.RewrappedAsync(await _world.AnoncryptAsync(expiredLayer), 1);

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        (await act.Should().ThrowAsync<MalformedMessageException>()).Which.Expired.Should().BeTrue();
    }

    [Fact]
    public async Task An_expired_innermost_message_is_refused_as_expired()
    {
        var inner = await _world.AuthcryptAsync(Note(expires: Now.ToUnixTimeSeconds() - 3600));
        var packed = await _world.RewrappedAsync(inner, 2);

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        (await act.Should().ThrowAsync<MalformedMessageException>()).Which.Expired.Should().BeTrue();
    }

    [Fact]
    public async Task An_opened_layer_over_MaxReceiveBytes_is_refused()
    {
        // data.json is re-serialized when it is extracted, with the default encoder, which escapes
        // every 'é' as é (six bytes for two): the opened layer is larger than the one it came
        // out of, so the size rule must run on each layer, not only on the delivered text.
        var bulky = new JsonObject { ["protected"] = new string('é', 20_000), ["ciphertext"] = "x", ["iv"] = "", ["tag"] = "", ["recipients"] = new JsonArray() };
        var attachment = new Attachment { Id = "a1", MediaType = ForwardConstants.PayloadMediaType, Data = new AttachmentData { Json = bulky } };
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, attachment));
        _world.BobOptions.MaxReceiveBytes = 90_000;
        Encoding.UTF8.GetByteCount(packed).Should().BeLessThan(90_000, "the delivered layer itself fits");
        Encoding.UTF8.GetByteCount(bulky.ToJsonString()).Should().BeGreaterThan(90_000, "the opened layer does not");

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, CancellationToken.None);

        (await act.Should().ThrowAsync<MalformedMessageException>()).Which.Message.Should().Contain("MaxReceiveBytes");
    }

    [Fact]
    public async Task Cancellation_stops_the_opening()
    {
        var packed = await _world.RewrappedAsync(await _world.AuthcryptAsync(Note()), 2);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _world.BobClient().UnpackDeliveredAsync(packed, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- the shape rule, condition by condition ----

    public static TheoryData<string, Func<UnpackResult, UnpackResult>> Breaks => new()
    {
        { "signed inside", r => r with { Stack = new[] { EnvelopeKind.Encrypted, EnvelopeKind.Signed, EnvelopeKind.Plaintext } } },
        { "envelope inside", r => r with { Stack = new[] { EnvelopeKind.Encrypted, EnvelopeKind.Encrypted, EnvelopeKind.Plaintext } } },
        { "plaintext", r => r with { Stack = new[] { EnvelopeKind.Plaintext } } },
        { "not encrypted", r => r with { Encrypted = false } },
        { "not anonymous", r => r with { AnonymousSender = false } },
        { "authenticated", r => r with { Authenticated = true } },
        { "non-repudiation", r => r with { NonRepudiation = true } },
        { "sender kid", r => r with { SenderKid = Alice + "#x1" } },
        { "signer kid", r => r with { SignerKid = Alice + "#s1" } },
        { "from_prior claims", r => r with { FromPrior = FromPriorClaims.ForTermination(Prior, 1) } },
        { "decrypted by another DID", r => r with { RecipientKid = Carol + "#x1" } },
        { "no decrypting kid", r => r with { RecipientKid = null } },
        { "decrypting kid not a DID", r => r with { RecipientKid = "x1" } },
    };

    [Theory]
    [MemberData(nameof(Breaks))]
    public void Each_condition_of_the_shape_rule_is_required(string condition, Func<UnpackResult, UnpackResult> breakIt)
    {
        var forward = Forward(Bob, Bob, JsonAttachment("{}"));
        var good = SelfShaped(forward);
        DidCommClient.IsRewrapLayerForThisAgent(good, Bob, forward.Attachments!.ToArray()).Should().BeTrue("the baseline is a rewrapped layer");

        DidCommClient.IsRewrapLayerForThisAgent(breakIt(good), Bob, forward.Attachments!.ToArray()).Should().BeFalse(condition);
    }

    [Fact]
    public void A_raw_from_prior_header_is_refused_too()
    {
        var forward = Forward(Bob, Bob, JsonAttachment("{}"));
        forward.FromPrior = "eyJ.x.y";

        DidCommClient.IsRewrapLayerForThisAgent(SelfShaped(forward), Bob, forward.Attachments!.ToArray()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Not_exactly_one_attachment_is_refused(int count)
    {
        var forward = Forward(Bob, Bob, JsonAttachment("{}"));
        var attachments = Enumerable.Repeat(forward.Attachments![0], count).ToArray();

        DidCommClient.IsRewrapLayerForThisAgent(SelfShaped(forward), Bob, attachments).Should().BeFalse();
    }

    [Theory]
    [InlineData("did:example:carol")]
    [InlineData("not-a-did")]
    public void A_next_other_than_the_decrypting_DID_is_refused(string next)
    {
        var forward = Forward(Bob, next, JsonAttachment("{}"));

        DidCommClient.IsRewrapLayerForThisAgent(SelfShaped(forward), next, forward.Attachments!.ToArray()).Should().BeFalse();
    }

    private static UnpackResult SelfShaped(Message forward)
        => new(
            Message: forward,
            Stack: new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
            Encrypted: true,
            Authenticated: false,
            NonRepudiation: false,
            AnonymousSender: true,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: null,
            RecipientKid: Bob + "#x1",
            AllRecipientKids: new[] { Bob + "#x1" },
            FromPrior: null);
}
