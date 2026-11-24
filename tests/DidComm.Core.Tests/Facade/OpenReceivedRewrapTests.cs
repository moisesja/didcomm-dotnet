using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Threading;
using Xunit;
using static DidComm.Tests.Facade.MediatedClientTestWorld;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Facade;

/// <summary>
/// #110, owner decision OD-3: <see cref="DidCommClient.OpenReceivedRewrapAsync"/> goes on from a
/// message a plain receiving endpoint already unpacked. A <c>forward</c> re-wrapped for this agent
/// (the rule of <see cref="DidCommClient.UnpackDeliveredAsync"/>) is opened, at most
/// <see cref="ForwardHandler.MaxSelfAddressedLayers"/> layers, without unpacking the received layer
/// again; anything else — another forward included — comes back as it was received. Real envelopes
/// throughout; the one-layer case is also the real output of a mediator in
/// <see cref="RewrapMode.ReanoncryptToNext"/>.
/// </summary>
public sealed class OpenReceivedRewrapTests
{
    private readonly MediatedClientTestWorld _world = new();

    // ---- what is left as it arrived ----

    [Fact]
    public async Task A_message_that_is_not_a_forward_comes_back_as_it_was_received()
    {
        var packed = await _world.AuthcryptAsync(Note());
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);

        var result = await client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);

        result.Result.Should().BeSameAs(received);
        result.LayersOpened.Should().Be(0);
        result.InnermostPacked.Should().BeSameAs(packed);
    }

    public static TheoryData<string> OtherForwards => new()
    {
        "next is another DID",
        "next is another DID this agent holds",
        "authcrypted",
        "signed inside anoncrypt",
        "signed only",
        "plaintext",
        "two attachments",
        "no next",
        "no attachments",
    };

    [Theory]
    [MemberData(nameof(OtherForwards))]
    public async Task A_forward_that_is_not_a_layer_rewrapped_for_this_agent_comes_back_as_it_was_received(string shape)
    {
        // UnpackDeliveredAsync refuses each of these (a delivered forward that is not ours is a
        // failure); a plain receiving endpoint keeps today's behaviour for them instead: the forward
        // is what was received, and nothing is opened or relayed.
        var packed = await OtherForwardAsync(shape);
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);
        received.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri, "the baseline is a forward");

        var result = await client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);

        result.Result.Should().BeSameAs(received, shape);
        result.LayersOpened.Should().Be(0);
        DidCommClient.IsRewrapLayerForThisAgent(received).Should().BeFalse(shape);
    }

    // ---- what is opened ----

    [Fact]
    public async Task A_forward_rewrapped_for_this_agent_is_opened_to_the_message_inside_without_unpacking_it_again()
    {
        var note = Note();
        var inner = await _world.AuthcryptAsync(note);
        var packed = await _world.RewrappedAsync(inner, 1);
        var secrets = BobSecrets();
        var client = BobClient(secrets);
        var received = await client.UnpackAsync(packed);
        var lone = BobSecrets();
        _ = await BobClient(lone).UnpackAsync(inner);
        var before = secrets.FindPresentCalls;

        var result = await client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);

        result.LayersOpened.Should().Be(1);
        result.Result.Message.Id.Should().Be(note.Id);
        (secrets.FindPresentCalls - before).Should().Be(lone.FindPresentCalls,
            "only the layer inside is unpacked: the received layer was already decrypted once");
        DidCommClient.IsRewrapLayerForThisAgent(received).Should().BeTrue();
    }

    [Fact]
    public async Task The_real_rewrap_output_of_a_mediator_is_opened()
    {
        var note = Note();
        var delivered = await _world.MediatorRewrapAsync(await _world.AuthcryptAsync(note));
        var client = _world.BobClient();
        var received = await client.UnpackAsync(delivered);

        var result = await client.OpenReceivedRewrapAsync(received, delivered, CancellationToken.None);

        result.LayersOpened.Should().Be(1);
        result.Result.Message.Id.Should().Be(note.Id);
        result.Result.SenderKid.Should().Be(Alice + "#x1", "the message inside keeps its own envelope");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(ForwardHandler.MaxSelfAddressedLayers)]
    public async Task Nested_layers_rewrapped_for_this_agent_are_opened_up_to_the_bound(int layers)
    {
        var note = Note();
        var packed = await _world.RewrappedAsync(await _world.AuthcryptAsync(note), layers);
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);

        var result = await client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);

        result.LayersOpened.Should().Be(layers);
        result.Result.Message.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task One_layer_beyond_the_bound_is_refused_as_malformed()
    {
        var packed = await _world.RewrappedAsync(await _world.AuthcryptAsync(Note()), ForwardHandler.MaxSelfAddressedLayers + 1);
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);

        var act = () => client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);

        (await act.Should().ThrowAsync<MalformedMessageException>()).Which.Message.Should().Contain("more than 8 forward layers");
    }

    [Fact]
    public async Task A_forward_for_another_DID_inside_an_opened_layer_is_refused()
    {
        // A layer addressed to Bob around a forward Bob can decrypt but that names Mallory: Bob is not
        // a mediator, so what is inside is not his message.
        var forMallory = await _world.AnoncryptAsync(Forward(Bob, Mallory, JsonAttachment(await _world.AuthcryptAsync(Note(to: Mallory), to: Mallory))));
        var packed = await _world.RewrappedAsync(forMallory, 1);
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);

        var act = () => client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);

        (await act.Should().ThrowAsync<ConsistencyException>()).Which.Message.Should().NotContain(Mallory);
    }

    [Fact]
    public async Task An_attachment_that_is_not_a_packed_message_is_refused()
    {
        var packed = await _world.AnoncryptAsync(Forward(Bob, Bob, Base64Attachment(Base64Url("not a message"))));
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);

        var act = () => client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);

        await act.Should().ThrowAsync<DidCommException>();
    }

    // ---- the message inside is authenticated by its own envelope ----

    [Fact]
    public async Task An_authcrypted_message_inside_keeps_its_sender()
    {
        var result = await OpenOneLayerAsync(await _world.AuthcryptAsync(Note()));

        result.Authenticated.Should().BeTrue();
        result.AnonymousSender.Should().BeFalse();
        result.SenderKid.Should().Be(Alice + "#x1");
    }

    [Fact]
    public async Task An_anoncrypted_message_inside_stays_anonymous()
    {
        var result = await OpenOneLayerAsync(await _world.AnoncryptAsync(Note(from: null)));

        result.AnonymousSender.Should().BeTrue();
        result.Authenticated.Should().BeFalse();
        result.SenderKid.Should().BeNull();
        result.SignerKid.Should().BeNull();
    }

    [Fact]
    public async Task A_signed_message_inside_keeps_its_signer()
    {
        var result = await OpenOneLayerAsync(await _world.AnoncryptSignedAsync(Note(), signFrom: Alice));

        result.NonRepudiation.Should().BeTrue();
        result.SignerKid.Should().Be(Alice + "#s1");
        result.SenderKid.Should().BeNull("an anoncrypt layer names no sender");
    }

    // ---- the dispatcher's lookup the registry-aware endpoints make first ----

    [Fact]
    public void The_dispatcher_says_whether_a_handler_is_registered_for_a_type()
    {
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new StubHandler("https://didcomm.org/routing/2.0"));
        using var withRouting = new ProtocolDispatcher(registry, new InMemoryThreadStateStore());
        using var without = new ProtocolDispatcher(new ProtocolHandlerRegistry(), new InMemoryThreadStateStore());

        withRouting.HasHandlerFor(ForwardConstants.ForwardTypeUri).Should().BeTrue();
        without.HasHandlerFor(ForwardConstants.ForwardTypeUri).Should().BeFalse();
        withRouting.HasHandlerFor("https://didcomm.org/trust_ping/2.0/ping").Should().BeFalse();
        withRouting.HasHandlerFor(null).Should().BeFalse();
        withRouting.HasHandlerFor("not a type").Should().BeFalse();
    }

    // ---- the routing role's opening: a layer that does not decrypt, against one refused for shape ----

    public static TheoryData<string> ShapesTheRestrictedUnpackRefuses => new()
    {
        "authcrypt", "signed only", "anoncrypt of a signed message", "anoncrypt of an authcrypt message", "plaintext", "from_prior",
    };

    [Theory]
    [MemberData(nameof(ShapesTheRestrictedUnpackRefuses))]
    public async Task For_the_routing_role_an_innermost_message_the_restricted_unpack_refuses_for_its_shape_is_unpacked_in_full(string shape)
    {
        // ADV-110-1c-2: the full unpack is for a layer refused for its shape (the mediator's own
        // protocol messages arrive authcrypted), never for one that does not decrypt.
        // Plaintext, and the from_prior termination form (FR-ROT-06), carry no 'from'.
        var note = Note(from: shape is "plaintext" or "from_prior" ? null : Alice);
        var senders = _world.Senders();
        string inner;
        switch (shape)
        {
            case "authcrypt":
                inner = await _world.AuthcryptAsync(note);
                break;
            case "signed only":
                inner = await senders.PackSignedAsync(note, Alice);
                break;
            case "anoncrypt of a signed message":
                inner = await _world.AnoncryptSignedAsync(note, signFrom: Alice);
                break;
            case "anoncrypt of an authcrypt message":
                inner = (await senders.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, ProtectSender: true))).Message;
                break;
            case "plaintext":
                inner = await senders.PackPlaintextAsync(note);
                break;
            default:
                note.FromPrior = await DidComm.Protocols.Rotation.FromPriorBuilder.BuildTerminationAsync(Prior, Now.ToUnixTimeSeconds(), _world.PriorSigningKey.PrivateJwk);
                inner = await _world.AnoncryptAsync(note);
                break;
        }
        var packed = await _world.RewrappedAsync(inner, 1);
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);

        var result = await client.OpenSelfAddressedMessageAsync(received, CancellationToken.None);

        result.Should().NotBeNull(shape);
        result!.Value.Result.Message.Id.Should().Be(note.Id);
        result.Value.LayersOpened.Should().Be(1);
    }

    [Fact]
    public async Task In_the_restricted_unpack_an_authcrypt_layer_is_refused_for_its_shape_before_any_key_is_looked_up()
    {
        var secrets = BobSecrets();
        var client = BobClient(secrets);
        var authcrypt = await _world.AuthcryptAsync(Note());

        var act = () => client.UnpackAnoncryptedPlaintextAsync(authcrypt, CancellationToken.None);

        (await act.Should().ThrowAsync<MalformedMessageException>()).Which.RefusedShape.Should().BeTrue();
        secrets.FindPresentCalls.Should().Be(0, "the protected header names a sender: refused before a recipient key is selected");
    }

    // ---- a data.json attachment written back: as the packer wrote it, and never over the limit ----

    [Fact]
    public async Task A_packed_message_this_library_wrote_comes_back_byte_for_byte_from_data_json_and_from_base64()
    {
        var packed = await _world.AuthcryptAsync(Note());
        var json = ForwardMessage.Create(Bob, Bob, new[] { packed }).Attachments![0];
        var base64 = Base64Attachment(Base64Url(packed));

        ForwardProcessor.ExtractAttachmentText(json, _world.BobOptions.MaxReceiveBytes).Should().Be(packed);
        ForwardProcessor.ExtractAttachmentBytes(json, _world.BobOptions.MaxReceiveBytes).Should().Equal(System.Text.Encoding.UTF8.GetBytes(packed));
        ForwardProcessor.ExtractAttachmentBytes(base64, _world.BobOptions.MaxReceiveBytes).Should().Equal(System.Text.Encoding.UTF8.GetBytes(packed), "base64 carries the exact bytes");
    }

    [Theory]
    [InlineData("data.json")]
    [InlineData("data.base64")]
    public void An_attachment_longer_than_the_receive_limit_once_written_is_refused_as_an_oversized_message(string form)
    {
        // A DEL is one byte raw and six escaped: 2 000 of them are 12 000 bytes written back.
        var payload = "{\"pad\":\"" + RawDel(2_000) + "\"}";
        var attachment = form == "data.json"
            ? new Attachment { Id = "a1", MediaType = ForwardConstants.PayloadMediaType, Data = new AttachmentData { Json = System.Text.Json.Nodes.JsonNode.Parse(payload) } }
            : Base64Attachment(Base64Url(new string('x', 11_000).Insert(0, "{\"pad\":\"") + "\"}"));

        var text = () => ForwardProcessor.ExtractAttachmentText(attachment, 10_000);
        var bytes = () => ForwardProcessor.ExtractAttachmentBytes(attachment, 10_000);

        text.Should().Throw<MalformedMessageException>().Which.Message.Should().Contain("MaxReceiveBytes");
        bytes.Should().Throw<MalformedMessageException>().Which.Message.Should().Contain("MaxReceiveBytes");
        ForwardProcessor.ExtractAttachmentText(attachment, 20_000).Should().NotBeNullOrEmpty("under a higher limit the same attachment is written back");
    }

    // ---- support ----

    private async Task<UnpackResult> OpenOneLayerAsync(string inner)
    {
        var packed = await _world.RewrappedAsync(inner, 1);
        var client = _world.BobClient();
        var received = await client.UnpackAsync(packed);
        received.AnonymousSender.Should().BeTrue("the received layer is anoncrypted");
        var result = await client.OpenReceivedRewrapAsync(received, packed, CancellationToken.None);
        result.LayersOpened.Should().Be(1);
        return result.Result;
    }

    private async Task<string> OtherForwardAsync(string shape)
    {
        var forBob = await _world.AuthcryptAsync(Note());
        return shape switch
        {
            "next is another DID" => await _world.AnoncryptAsync(Forward(Bob, Mallory, JsonAttachment(forBob))),
            "next is another DID this agent holds" => await _world.AnoncryptAsync(Forward(Bob, Carol, JsonAttachment(forBob))),
            "authcrypted" => await _world.AuthcryptAsync(Forward(Bob, Bob, JsonAttachment(forBob)), from: Alice),
            "signed inside anoncrypt" => await _world.AnoncryptSignedAsync(Forward(Bob, Bob, JsonAttachment(forBob)), signFrom: Alice),
            "signed only" => await _world.Senders().PackSignedAsync(Forward(Bob, Bob, JsonAttachment(forBob)), Alice),
            "plaintext" => await _world.Senders().PackPlaintextAsync(Forward(Bob, Bob, JsonAttachment(forBob))),
            "two attachments" => await _world.AnoncryptAsync(Forward(Bob, Bob, JsonAttachment(forBob), JsonAttachment(forBob))),
            "no next" => await _world.AnoncryptAsync(new MessageBuilder().WithType(ForwardConstants.ForwardTypeUri).WithTo(Bob)
                .WithBody(new System.Text.Json.Nodes.JsonObject()).WithAttachment(JsonAttachment(forBob)).Build()),
            "no attachments" => await _world.AnoncryptAsync(Forward(Bob, Bob)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private SpySecretsResolver BobSecrets()
        => new(_world.BobKey.PrivateJwk, _world.BobSigningKey.PrivateJwk, _world.CarolKey.PrivateJwk, _world.CarolSigningKey.PrivateJwk);

    private DidCommClient BobClient(SpySecretsResolver secrets)
        => new(secrets, new NetDidKeyService(_world.Resolver), _world.Services, _world.Router, _world.BobOptions, new JoseCryptoProvider());

    private sealed class StubHandler(string protocolUri) : IProtocolHandler
    {
        public string ProtocolUri { get; } = protocolUri;

        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct) => Task.FromResult<Message?>(null);
    }
}
