using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Two MUSTs of Message Pickup the client did not enforce (#100, found on main; fixed here, L-075),
/// in both versions. <b>F-1</b>: "If a recipient_did was specified in the status-request message,
/// the matching value MUST be specified in the recipient_did attribute of the status message"
/// (3.0 L116, 4.0 L168) — a status that leaves it out is refused like one about another DID (a
/// delivery may leave it out: 3.0 L174, 4.0 L265 say when it is included, not that it must be).
/// <b>F-2</b>: "The ONLY valid type of attachment for this message is a DIDComm v2 Message in
/// encrypted form" (3.0 L172; 4.0 L261) — a delivered attachment that is not a JWE, as delivered,
/// is a failed item: never unpacked, never acknowledged; a message a mediator re-wrapped for us is
/// an anoncrypted forward, a JWE, and passes.
/// </summary>
public sealed class PickupClientStrictnessTests
{
    private const string Carol = "did:example:carol";

    private static FakePickupExchange Speaking(MessagePickupVersion version) => new(o => o.MessagePickupVersion = version);

    // ---- F-1 ------------------------------------------------------------------------------------

    [Fact]
    public async Task F1_a_3_0_status_answering_a_filtered_delivery_request_without_the_echo_is_refused()
    {
        var exchange = Speaking(MessagePickupVersion.V3).On(MessagePickup.DeliveryRequestType, r => Status(r, count: 0));

        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, limit: 5, recipientDid: Carol);

        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.Message.Should().Contain("does not repeat the recipient_did").And.NotContain(Carol);
    }

    [Fact]
    public async Task F1_a_3_0_status_answering_a_filtered_delivery_request_with_the_echo_is_a_drained_batch()
    {
        // Control: the echo is what makes the status an answer.
        var exchange = Speaking(MessagePickupVersion.V3).On(MessagePickup.DeliveryRequestType, r => Status(r, count: 0, recipientDid: Carol));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 5, recipientDid: Carol);

        batch.Drained.Should().BeTrue();
        batch.Status!.RecipientDid.Should().Be(Carol);
    }

    [Theory]
    [InlineData(MessagePickupVersion.V3)]
    [InlineData(MessagePickupVersion.V4)]
    public async Task F1_a_status_echoing_the_DID_asked_about_is_accepted(MessagePickupVersion version)
    {
        var exchange = Speaking(version)
            .On(MessagePickup.StatusRequestType, r => Status(r, count: 3, recipientDid: Carol))
            .On(MessagePickup40.StatusRequestType, r => Status40(r, count: 3, recipientDid: Carol));

        (await PickupTest.Client(exchange).GetStatusAsync(M, L, recipientDid: Carol)).MessageCount.Should().Be(3);
    }

    [Fact]
    public async Task F1_a_3_0_delivery_may_leave_the_echo_out()
    {
        var exchange = Speaking(MessagePickupVersion.V3).On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 5, recipientDid: L);

        batch.Items.Should().ContainSingle().Which.Unpacked.Should().NotBeNull();
    }

    [Fact]
    public void F1_the_status_rule_evaluates_both_conditions()
    {
        var spec = DeliveryReader.StatusRequestSpec;
        FluentActions.Invoking(() => DeliveryReader.RequireStatusFilterEcho(null, null, "r", spec)).Should().NotThrow("nothing asked, nothing echoed");
        FluentActions.Invoking(() => DeliveryReader.RequireStatusFilterEcho(Carol, Carol, "r", spec)).Should().NotThrow("the matching value");
        FluentActions.Invoking(() => DeliveryReader.RequireStatusFilterEcho(Carol + "#key-1", Carol, "r", spec)).Should().Throw<MediationProtocolException>("a DID URL of the DID asked about is not the matching value (#100 ADV3-2)").WithMessage("*another DID*");
        FluentActions.Invoking(() => DeliveryReader.RequireStatusFilterEcho(null, Carol, "r", spec)).Should().Throw<MediationProtocolException>().WithMessage("*does not repeat*");
        FluentActions.Invoking(() => DeliveryReader.RequireStatusFilterEcho(Carol, null, "r", spec)).Should().Throw<MediationProtocolException>().WithMessage("*another DID*");
        FluentActions.Invoking(() => DeliveryReader.RequireStatusFilterEcho("did:example:dave", Carol, "r", spec)).Should().Throw<MediationProtocolException>().WithMessage("*another DID*");
    }

    // ---- F-2: the form of a delivered attachment --------------------------------------------------

    public static TheoryData<string, string, bool> Forms => new()
    {
        { "a general JWE", """{"protected":"e30","recipients":[],"iv":"","ciphertext":"x","tag":""}""", true },
        { "a stand-in JWE (ciphertext is what makes one)", Jwe("ok:m1"), true },
        { "a plaintext DIDComm message", """{"id":"1","type":"https://didcomm.org/basicmessage/2.0/message","body":{}}""", false },
        { "a general JWS", """{"payload":"e30","signatures":[{"protected":"e30","signature":"x"}]}""", false },
        { "a flattened JWS", """{"payload":"e30","protected":"e30","signature":"x"}""", false },
        { "a JSON array", """[{"ciphertext":"x"}]""", false },
        { "a JSON string", "\"ciphertext\"", false },
        { "not JSON", "ok:m1", false },
        { "empty", string.Empty, false },
        { "a JWE whose ciphertext is named twice", """{"ciphertext":"x","ciphertext":"y"}""", false },
        { "ciphertext only nested", """{"inner":{"ciphertext":"x"}}""", false },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void F2_only_a_JSON_object_the_envelope_detector_calls_a_JWE_is_in_encrypted_form(string because, string payload, bool encrypted)
        => EncryptedForm.IsEncrypted(Encoding.UTF8.GetBytes(payload)).Should().Be(encrypted, because);

    [Fact]
    public void F2_bytes_that_are_not_UTF_8_are_not_in_encrypted_form()
        => EncryptedForm.IsEncrypted(new byte[] { (byte)'{', 0xC3, 0x28, (byte)'}' }).Should().BeFalse();

    [Fact]
    public void F2_a_reading_that_is_not_a_JWE_is_an_entry_without_a_payload_whose_error_quotes_nothing()
    {
        var contents = DeliveryReader.ReadEntries(new List<Attachment> { RawEntry("q1", "secret-plaintext"), Entry("q2", "ok:m2") }, 10);

        var failed = contents.Entries[0];
        failed.Id.Should().Be("q1", "the id is usable: the item is reported under it");
        failed.Payload.Should().BeNull("it is never handed to the unpack");
        failed.Error.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("encrypted form").And.NotContain("secret-plaintext");
        contents.Entries[1].Payload.Should().NotBeNull();
    }

    [Theory]
    [InlineData(MessagePickupVersion.V3)]
    [InlineData(MessagePickupVersion.V4)]
    public async Task F2_a_fetched_attachment_that_is_not_a_JWE_is_a_failed_item_never_unpacked_never_acknowledged(MessagePickupVersion version)
    {
        var attachments = new[] { RawEntry("plain", "ok:never"), Entry("jwe", "ok:m1") };
        var exchange = Speaking(version)
            .On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, attachments))
            .On(MessagePickup40.DeliveryRequestType, r => RawDelivery40(r, attachments));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 5);

        batch.Items.Select(i => i.Id).Should().Equal("plain", "jwe");
        batch.Items[0].Error.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("encrypted form");
        batch.Items[1].Unpacked.Should().NotBeNull();
        exchange.Unpacked.Should().Equal(new[] { "ok:m1" }, "the plaintext attachment never reached the unpack");
        exchange.AcknowledgedIds.Should().Equal(new[] { "jwe" }, "a failed item is never acknowledged");
    }

    [Theory]
    [InlineData(MessagePickupVersion.V3)]
    [InlineData(MessagePickupVersion.V4)]
    public async Task F2_a_pushed_attachment_that_is_not_a_JWE_is_yielded_as_a_failed_item_and_never_acknowledged(MessagePickupVersion version)
    {
        var (exchange, client) = Live(o => o.MessagePickupVersion = version);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        var push = version == MessagePickupVersion.V4 ? MessagePickup40.CreateDelivery(M, L, Array.Empty<DeliveredMessage>()) : MessagePickup.CreateDelivery(M, L, Array.Empty<DeliveredMessage>());
        push.Attachments = new List<Attachment> { RawEntry("plain", "ok:never"), Entry("jwe", "ok:m1") };

        exchange.Registration!.TryPush(Snapshot(push)).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Should().Match<PickupItem>(i => i.Id == "plain" && i.Error is MalformedMessageException);
        (await Next(stream)).Id.Should().Be("jwe");
        var parked = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        exchange.Unpacked.Should().Equal("ok:m1");
        exchange.AcknowledgedIds.Should().Equal("jwe");
        await StopAsync(stream, parked, cts);
    }

    // ---- F-2 with real envelopes, the real channel and unpack ------------------------------------

    [Fact]
    public async Task F2_with_real_envelopes_a_plaintext_or_a_signed_only_message_is_a_failed_item_and_stays_queued()
    {
        await using var w = await ClientWorld.CreateAsync();
        var send = new FakeSend();
        await using var channel = Create(w, send);
        var mediator = new Adv1Mediator(w);
        mediator.Serve(send);
        var note = Adv1Envelopes.Note(w.Alice.Did, w.Local.Did);
        mediator.Enqueue(w.Local.Did, "plaintext", await w.Client.PackPlaintextAsync(note));
        mediator.Enqueue(w.Local.Did, "signed", await w.Client.PackSignedAsync(note, w.Alice.Did));
        mediator.Enqueue(w.Local.Did, "anoncrypt", (await w.Client.PackEncryptedAsync(note, new PackEncryptedOptions(Recipients: new[] { w.Local.Did }))).Message);

        var batch = await new PickupClient(channel, logger: null).FetchAsync(w.Mediator.Did, w.Local.Did, 10, serviceEndpointOverride: Https);

        batch.Items.Select(i => (i.Id, Failed: i.Error is MalformedMessageException)).Should().Equal(("plaintext", true), ("signed", true), ("anoncrypt", false));
        batch.Items[2].Unpacked!.Message.Id.Should().Be(note.Id);
        mediator.Queued(w.Local.Did).Should().Equal(new[] { "plaintext", "signed" }, "neither was acknowledged");
    }

    [Fact]
    public async Task F2_is_judged_on_the_outermost_form_so_a_forward_re_wrapped_for_this_agent_passes_whatever_it_carries()
    {
        // A mediator that re-wraps (RewrapMode.ReanoncryptToNext) delivers an anoncrypted forward
        // whose next is this agent: a JWE as delivered. What it carries is the sender's choice;
        // here, a plaintext message, which the client opens through the layer (FR-ROUTE-06).
        await using var w = await ClientWorld.CreateAsync();
        var send = new FakeSend();
        await using var channel = Create(w, send);
        var mediator = new Adv1Mediator(w);
        mediator.Serve(send);
        var note = Adv1Envelopes.Note(w.Alice.Did, w.Local.Did);
        var layer = ForwardMessage.Create(w.Local.Did, w.Local.Did, new[] { await w.Client.PackPlaintextAsync(note) });
        mediator.Enqueue(w.Local.Did, "rewrapped", (await w.Client.PackEncryptedAsync(layer, new PackEncryptedOptions(Recipients: new[] { w.Local.Did }))).Message);

        var batch = await new PickupClient(channel, logger: null).FetchAsync(w.Mediator.Did, w.Local.Did, 10, serviceEndpointOverride: Https);

        var item = batch.Items.Should().ContainSingle().Subject;
        item.Error.Should().BeNull();
        item.Unpacked!.Message.Id.Should().Be(note.Id);
        mediator.Queued(w.Local.Did).Should().BeEmpty();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static InboundMessageSnapshot RawDelivery40(SentRequest request, IEnumerable<Attachment> attachments)
    {
        var message = MessagePickup40.CreateDelivery(M, L, Array.Empty<DeliveredMessage>(), thid: request.Request.Id);
        message.Attachments = attachments.ToList();
        return Snapshot(message);
    }
}
