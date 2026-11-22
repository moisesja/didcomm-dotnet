using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// Red-team pass ADV-3 over Message Pickup 4.0 (#100): replay against the real
/// <c>AddMediator()</c> + <c>MapDidCommMediator</c> host. Every attack here was defeated: FR-RR-08
/// admits a (sender, message id) once whatever the protocol version or the spelling of the type,
/// and a captured 4.0 request played again changes nothing.
/// </summary>
public sealed class PickupV4ReplayAdv3EndToEndTests
{
    [Fact]
    public async Task An_id_used_by_a_3_0_request_is_refused_when_a_4_0_request_reuses_it_in_another_case()
    {
        // Admission is keyed by sender and id (ignoring letter case), never by type: the same id
        // as a 3.0 status-request and then as a 4.0 delivery-request is admitted once.
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "first");
        var id = "adv3-" + Guid.NewGuid().ToString("N");

        var v3 = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        v3.Id = id;
        (await mediator.AskAsync(bob, v3)).Status(id).MessageCount.Should().Be(1);
        var v4 = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        v4.Id = id.ToUpperInvariant();
        var again = await mediator.PostAsync(await mediator.AuthcryptAsync(bob, mediator.Prepare(v4)));
        var fresh = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskAsync(bob, fresh)).Delivery40(fresh.Id);

        again.Status.Should().Be(HttpStatusCode.Accepted, "the id was used by this sender already");
        again.Body.Should().BeEmpty();
        delivered.Should().ContainSingle("the refused request leased nothing");
    }

    [Fact]
    public async Task A_spelled_4_0_acknowledgement_without_return_route_is_admitted_once_and_never_without_created_time()
    {
        // A 4.0 messages-received carries no return_route (L35); only its protocol puts it in the
        // replay scope. Spelled as a sloppy peer might ("Message_Pickup", "Messages_Received",
        // an upper-case host), it still reaches the 4.0 handler, so it must still be in scope:
        // without created_time it is refused, and played again it removes nothing more.
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer);
        var (bob, recipient, alice) = await EnrolAsync(mediator);
        await PickupE2E.SendAsync(mediator, alice, recipient, "one");
        await PickupE2E.SendAsync(mediator, alice, recipient, "two");
        var fetch = MessagePickup40.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        var delivered = (await mediator.AskAsync(bob, fetch)).Delivery40(fetch.Id);
        delivered.Should().HaveCount(2);
        const string Spelled = "https://DIDComm.org/Message_Pickup/4.0/Messages_Received";

        var unstamped = Spelled40(Spelled, bob.Did, mediator.Did, delivered[0].Id);
        (await mediator.PostAsync(await PickupV4E2E.PackAsIsAsync(mediator, bob, unstamped))).Status.Should().Be(HttpStatusCode.Accepted);
        mediator.Store.MessageCount.Should().Be(2, "an in-scope message without created_time is refused before dispatch");

        var stamped = PickupV4E2E.Stamped(mediator, Spelled40(Spelled, bob.Did, mediator.Did, delivered[0].Id));
        var packed = await PickupV4E2E.PackAsIsAsync(mediator, bob, stamped);
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        mediator.Store.MessageCount.Should().Be(1, "precondition: the spelled acknowledgement reached the 4.0 handler");
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);

        await mediator.FlushObserversAsync();
        observer.From(bob.Did).Count(r => r.Id == stamped.Id).Should().Be(1, "the replay never reached the dispatcher");
        observer.From(bob.Did).Should().NotContain(r => r.Id == unstamped.Id);
        mediator.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_captured_4_0_live_delivery_change_played_on_another_socket_turns_nothing_on()
    {
        // The first socket turns live delivery on and closes. The same packed envelope sent on a
        // second socket is dropped by replay admission: the next answer on that socket is the
        // fresh status-request's, saying live delivery is off.
        await using var mediator = await MediatorTestServer.StartAsync();
        var (bob, _, _) = await EnrolAsync(mediator);
        var change = mediator.Prepare(MessagePickup40.CreateLiveDeliveryChange(bob.Did, mediator.Did, liveDelivery: true));
        var packed = await mediator.AuthcryptAsync(bob, change);
        using (var first = await mediator.ConnectAsync())
        {
            await PickupV4E2E.SendAsync(first, packed);
            (await mediator.ReceiveOverSocketAsync(first, bob)).Status40(change.Id).LiveDelivery.Should().BeTrue();
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }

        using var second = await mediator.ConnectAsync();
        await PickupV4E2E.SendAsync(second, packed);
        var status = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        var answer = await mediator.AskOverSocketAsync(second, bob, status);

        answer.Status40(status.Id).LiveDelivery.Should().BeFalse("the replayed change was not applied to the new connection");
    }

    private static Message Spelled40(string type, string from, string mediator, string id)
        => new MessageBuilder().WithType(type).WithFrom(from).WithTo(mediator)
            .WithBody(new JsonObject { ["message_id_list"] = new JsonArray(id) }).Build();

    private static async Task<(TestPeer Bob, TestPeer Recipient, MediatorSender Alice)> EnrolAsync(MediatorTestServer mediator)
    {
        var bob = await mediator.NewPeerAsync();
        var recipient = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, recipient.Did);
        return (bob, recipient, await mediator.NewSenderAsync());
    }
}
