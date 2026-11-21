using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Jose;
using DidComm.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #100 OD-1 over the real endpoints, every way into the mailbox: Message Pickup delivers only "a
/// DIDComm v2 Message in encrypted form" (3.0 L172; 4.0 L261), so the mailbox stores nothing else,
/// whoever supplies it. (a) A pass-through forward whose attachment is plaintext or signed-only
/// (routing.md L57 only says SHOULD be encrypted) is dropped as <c>Dropped-PayloadNotEncrypted</c>,
/// nothing is stored, and the sender still holds its 202. (b) In rewrap mode, and in pass-through
/// mode with an extra routing key, the mailbox stores the mediator's own anoncrypted forward, a JWE,
/// so the same plaintext arrives wrapped. (c) The host's
/// own <c>DeliverAsync</c> is refused with <c>NotEncrypted</c>. (d) Neither reaches a 3.0 or 4.0
/// live session. (e) A later 3.0 or 4.0 <c>delivery-request</c> finds nothing.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class MediatorNotEncryptedTests
{
    private const string Marker = "E2ENOTENCMARKER";

    public static TheoryData<string> NotEncryptedInners => new() { "plaintext", "a General JWS", "a Flattened JWS" };

    [Theory]
    [MemberData(nameof(NotEncryptedInners))]
    public async Task A_pass_through_forward_whose_message_is_not_encrypted_is_dropped_after_its_202_and_nothing_is_delivered(string inner)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();

        // PostForwardAsync requires the 202 with an empty body: the drop happens after it.
        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, await InnerAsync(alice, bob.Did, inner))).Packed);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedPayloadNotEncrypted);
        mediator.Store.MessageCount.Should().Be(0);
        await NothingToPickUpAsync(mediator, bob);
        mediator.LeakingLogLines(Marker).Should().BeEmpty("no line quotes the message");

        // The control: the same forward with the message encrypted is stored and delivered.
        var encrypted = await alice.AuthcryptAsync(alice.Note(bob.Did, "encrypted " + Marker), bob.Did);
        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, encrypted)).Packed);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedPayloadNotEncrypted, ForwardOutcomes.Queued);
        SameJson((await mediator.FetchAsync(bob)).Should().ContainSingle().Subject.Payload, encrypted);
    }

    [Fact]
    public async Task In_rewrap_mode_a_plaintext_message_is_stored_inside_the_mediators_own_JWE_and_delivered()
    {
        // By design: the sink stores what the processor hands it, and in rewrap mode that is a fresh
        // forward anoncrypted to the recipient (FR-ROUTE-06), whatever it wraps.
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });
        var alice = await mediator.NewSenderAsync();
        var plaintext = await InnerAsync(alice, b.Did, "plaintext");

        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, b.Did, plaintext)).Packed);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        await ShouldPeelToAsync(b, delivered.Payload, plaintext);
    }

    [Fact]
    public async Task In_pass_through_mode_with_an_extra_routing_key_a_plaintext_message_is_stored_inside_the_added_JWE_layer()
    {
        // The same rule as rewrap mode: what the mailbox is given is the forward the mediator added
        // for the recipient's extra routing key (FR-ROUTE-05), anoncrypted, so its outermost layer is
        // a JWE whatever the sender's attachment was. The provider answers for B with B's own X25519
        // key, whose secret Bob holds.
        var provider = new RoutingKeyFor();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
            o.ConfigureServices = services => services.AddSingleton<IRecipientRoutingKeyProvider>(provider));
        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var bIdentity = await MediatorCast.MintAsync(new DidCommServiceSpec(mediator.Did, Accept: new[] { "didcomm/v2" }));
        var b = mediator.Own(TestPeer.Create(bIdentity));
        var x25519 = bIdentity.Privates.Single(k => k.Crv == "X25519");
        provider.Set(b.Did, new Jwk { Kty = x25519.Kty, Crv = x25519.Crv, X = x25519.X, Kid = x25519.Kid });
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });
        var alice = await mediator.NewSenderAsync();
        var plaintext = await InnerAsync(alice, b.Did, "plaintext");

        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, b.Did, plaintext)).Packed);
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        await ShouldPeelToAsync(b, delivered.Payload, plaintext);
    }

    [Fact]
    public async Task The_hosts_own_DeliverAsync_is_refused_too_and_counts_no_forward()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();

        var result = await mediator.Queue.DeliverAsync(bob.Did, Encoding.UTF8.GetBytes(await InnerAsync(alice, bob.Did, "plaintext")));

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotEncrypted, null, null));
        mediator.Store.MessageCount.Should().Be(0);
        mediator.Outcomes.Should().BeEmpty("no forward was involved");
        await NothingToPickUpAsync(mediator, bob);
        mediator.LeakingLogLines(Marker).Should().BeEmpty();
    }

    [Theory]
    [InlineData("3.0")]
    [InlineData("4.0")]
    public async Task A_message_that_is_not_encrypted_never_reaches_a_live_session_and_the_next_encrypted_one_does(string version)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await GoLiveAsync(mediator, socket, bob, version);

        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, await InnerAsync(alice, bob.Did, "a General JWS"))).Packed);
        (await mediator.Queue.DeliverAsync(bob.Did, Encoding.UTF8.GetBytes(await InnerAsync(alice, bob.Did, "plaintext")))).Outcome.Should().Be(PickupDeliveryOutcome.NotEncrypted, "the host's own call, which pushes live itself");
        await mediator.FlushAsync();
        await mediator.PushesIdleAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedPayloadNotEncrypted);
        mediator.PushOutcomes.Should().BeEmpty("nothing was stored, so nothing was handed to the push workers");
        mediator.Store.MessageCount.Should().Be(0);

        // The first message the live connection carries is the encrypted one that follows.
        var encrypted = await alice.AuthcryptAsync(alice.Note(bob.Did, "live"), bob.Did);
        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bob.Did, encrypted)).Packed);
        var push = await mediator.ReceiveOverSocketAsync(socket, bob);
        var pushed = version == "3.0" ? push.Delivery(thid: null) : Delivery40(push.Message);
        SameJson(pushed.Should().ContainSingle().Subject.Payload, encrypted);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- helpers --------------------------------------------------------------------------------

    // What the mailbox stored is a forward the mediator anoncrypted to B (a JWE), and inside it is
    // the plaintext the sender wrapped.
    private static async Task ShouldPeelToAsync(TestPeer b, ReadOnlyMemory<byte> stored, string plaintext)
    {
        EncryptedForm.IsEncrypted(stored).Should().BeTrue("what is stored is the mediator's JWE");
        var packed = Encoding.UTF8.GetString(stored.Span);
        var outer = await b.Client.UnpackAsync(packed);
        outer.Encrypted.Should().BeTrue();
        outer.SenderKid.Should().BeNull("the mediator's layer is anoncrypted");
        outer.Message.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        var peeled = await new ForwardProcessor(b.Client, b.Services.GetRequiredService<IDidKeyService>(), new ForwardProcessorOptions()).ProcessAsync(packed);
        peeled.NextHop.Should().Be(b.Did);
        JsonNode.DeepEquals(JsonNode.Parse(peeled.OnwardPacked), JsonNode.Parse(plaintext)).Should().BeTrue("inside is the plaintext the sender wrapped");
    }

    // A host provider of one extra routing key for one recipient, set once the recipient exists.
    private sealed class RoutingKeyFor : IRecipientRoutingKeyProvider
    {
        private (string Did, Jwk Key)? _entry;
        private readonly Lock _gate = new();

        public void Set(string recipientDid, Jwk key)
        {
            lock (_gate)
                _entry = (recipientDid, key);
        }

        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct)
        {
            lock (_gate)
            {
                return ValueTask.FromResult<IReadOnlyList<Jwk>>(
                    _entry is { } e && string.Equals(e.Did, recipientDid, StringComparison.Ordinal) ? new[] { e.Key } : Array.Empty<Jwk>());
            }
        }
    }

    // A forward carries its attachment as JSON, so the relayed message is the same JSON, not the same text.
    private static void SameJson(ReadOnlyMemory<byte> delivered, string sent)
        => JsonNode.DeepEquals(JsonNode.Parse(delivered.Span), JsonNode.Parse(sent)).Should().BeTrue("the encrypted message is relayed as it was sent");

    private static async Task<string> InnerAsync(MediatorSender alice, string to, string form)
    {
        var note = alice.Note(to, "not encrypted " + Marker);
        return form switch
        {
            "plaintext" => await alice.Client.PackPlaintextAsync(note),
            "a General JWS" => await alice.Client.PackSignedAsync(note, alice.Did),
            "a Flattened JWS" => Flattened(await alice.Client.PackSignedAsync(note, alice.Did)),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "no such form"),
        };
    }

    // The same signature as a Flattened JWS (RFC 7515 §7.2.2).
    private static string Flattened(string generalJws)
    {
        var general = JsonNode.Parse(generalJws)!.AsObject();
        var signature = general["signatures"]!.AsArray().Single()!.AsObject();
        return new JsonObject
        {
            ["payload"] = general["payload"]!.GetValue<string>(),
            ["protected"] = signature["protected"]!.GetValue<string>(),
            ["signature"] = signature["signature"]!.GetValue<string>(),
        }.ToJsonString();
    }

    // 3.0: a delivery-request is answered with a status that counts nothing. 4.0: with an empty
    // delivery (4.0 L210, L259).
    private static async Task NothingToPickUpAsync(MediatorTestServer mediator, TestPeer bob)
    {
        var request30 = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        (await mediator.AskAsync(bob, request30)).Status(request30.Id).MessageCount.Should().Be(0);

        var request40 = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var answer40 = await mediator.AskAsync(bob, request40);
        answer40.Message.Type.Should().Be(MessagePickup40.DeliveryType);
        answer40.Message.Thid.Should().Be(request40.Id);
        Delivery40(answer40.Message).Should().BeEmpty();
    }

    private static async Task GoLiveAsync(MediatorTestServer mediator, WebSocket socket, TestPeer bob, string version)
    {
        if (version == "3.0")
        {
            await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
            return;
        }
        var live = MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, true);
        var answer = await mediator.AskOverSocketAsync(socket, bob, live);
        MessagePickup40.TryParseStatus(answer.Message, out var status).Should().BeTrue();
        status!.LiveDelivery.Should().BeTrue();
    }

    private static IReadOnlyList<DeliveredMessage> Delivery40(Message message)
    {
        MessagePickup40.TryParseDelivery(message, out var messages, out _).Should().BeTrue();
        return messages!;
    }
}
