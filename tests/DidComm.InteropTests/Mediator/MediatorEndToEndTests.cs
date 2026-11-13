using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports;
using Xunit;

// L-014: alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// #95 acceptance, end to end over one TestServer running <c>AddMediator()</c> +
/// <c>MapDidCommMediator("/")</c>: Bob finds the mediator from its out-of-band invitation, enrols
/// (Coordinate Mediation 3.0, hand-rolled and stamped), registers a did:peer recipient DID B whose
/// service is <c>{uri: routing_did, accept: ["didcomm/v2"]}</c> (FR-MED-07), and Alice sends with
/// <c>SendAsync</c>, which resolves B and wraps one forward for the mediator (FR-ROUTE-04). Bob then
/// picks the message up over HTTP, or receives it live over the mediator's WebSocket.
/// </summary>
public sealed class MediatorEndToEndTests
{
    [Fact]
    public async Task Over_HTTP_Alice_sends_with_forward_the_mediator_queues_and_Bob_picks_up_and_acknowledges()
    {
        await using var mediator = await MediatorTestServer.StartAsync();

        // Bob discovers the mediator from its invitation (the URL the XML docs tell a host to publish).
        var invitationUrl = OutOfBandApi.ToShortUrl($"{mediator.PublicBase}/oob", MediatorEndpointRouteBuilderExtensions.InvitationOobId);
        var invitation = OutOfBandApi.FromPlaintext((await mediator.GetAsync(invitationUrl)).Body);
        invitation.Message.From.Should().Be(mediator.Did);

        var bob = await mediator.NewPeerAsync();
        var routingDids = await mediator.RequestMediationAsync(bob);
        routingDids.Should().Equal(invitation.Message.From);
        var b = await mediator.NewRecipientAsync();
        b.Did.Should().NotBe(bob.Did);
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });

        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);
        var sent = await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did));

        sent.EndpointUsed.Should().Be(mediator.Endpoint, "B's service names the routing DID, whose own service is the mediator's endpoint (FR-ROUTE-04)");
        sent.Transport.Accepted.Should().BeTrue();
        sent.Transport.HttpStatusCode.Should().Be((int)HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.Store.MessageCount.Should().Be(1);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);

        var delivered = await mediator.FetchAsync(bob);
        var message = delivered.Should().ContainSingle().Subject;
        MediatorRecipient.IsRandomQueueId(message.Id).Should().BeTrue("queue ids come from the queue's own random source (FR-PICK-04)");
        // The attachment unpacks with B's keys directly: exactly one forward layer was added and removed.
        var inner = await MediatorRecipient.UnpackFromSenderAsync(b, message.Payload, alice);
        inner.Message.Id.Should().Be(note.Id);
        inner.Message.Body!["content"]!.GetValue<string>().Should().Be("hello");

        (await mediator.AcknowledgeAsync(bob, message.Id)).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
        mediator.LeakingLogLines(b.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task Over_a_live_WebSocket_session_the_forward_is_pushed_to_Bob_and_never_queued_for_pickup()
    {
        // The host keeps same-socket replies off and turns STOMP on globally: the mediator's socket
        // overrides both for itself (MapDidCommMediator).
        await using var mediator = await MediatorTestServer.StartAsync(o => o.GlobalStomp = true);
        var bob = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(bob)).Should().Equal(mediator.Did);
        var b = await mediator.NewRecipientAsync();
        await mediator.AddRecipientsAsync(bob, new[] { b.Did });

        using var socket = await mediator.ConnectAsync();
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(0);
        var live = MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true);
        (await mediator.AskOverSocketAsync(socket, bob, live)).Status(live.Id).LiveDelivery.Should().BeTrue();

        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(b.Did);
        (await alice.Client.SendAsync(note, new SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();

        var push = await mediator.ReceiveOverSocketAsync(socket, bob);
        var pushed = push.Delivery(thid: null).Should().ContainSingle("a live delivery carries one message").Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, pushed.Payload, alice)).Message.Id.Should().Be(note.Id);

        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.DeliveredLive }, "delivered live, so never counted as queued for pickup");
        mediator.Store.MessageCount.Should().Be(1, "the live push is the store's own copy, held until it is acknowledged");

        var ack = MessagePickup.CreateMessagesReceived(bob.Did, mediator.Did, new[] { pushed.Id });
        (await mediator.AskOverSocketAsync(socket, bob, ack)).Status(ack.Id).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(b.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_for_a_recipient_whose_socket_is_not_live_is_queued_and_fetched_on_that_socket()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        using var socket = await mediator.ConnectAsync();
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).LiveDelivery.Should().BeFalse();

        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskOverSocketAsync(socket, bob, request)).Delivery(request.Id);
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Should().ContainSingle().Subject.Payload, alice)).Message.Id.Should().Be(note.Id);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines().Should().BeEmpty();
    }
}
