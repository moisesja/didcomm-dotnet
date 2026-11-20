using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Message Pickup 3.0 and 4.0 over one <see cref="MessagePickupQueue"/> (FR-PICK-12, #100, plan
/// threat model "cross-version"): each version answers an empty queue its own way over the same
/// store; a lease taken through one version is acknowledged through the other; a session's live
/// framing is set by the version of its last <c>live-delivery-change(true)</c>, and turning live
/// delivery off in either version turns it off; and recipients of different grants, one per
/// version, only ever see their own messages.
/// </summary>
public sealed class MessagePickupCrossVersionTests
{
    [Fact]
    public async Task Both_handlers_serve_the_same_queue_instance()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var services = h.Fixture.Host.Services;

        var queue = services.GetRequiredService<MessagePickupQueue>();
        services.GetRequiredService<MessagePickupHandler>().Should().NotBeNull();
        services.GetRequiredService<MessagePickup40Handler>().Should().NotBeNull();

        // A message the 3.0 handler's status counts is counted by 4.0's, and the other way round.
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.Fixture.QueueAsync(h.Alice, PickupFixture.Payload(7));
        (await h.SendAsync(h.StatusRequest(h.Alice), h.Http(h.Alice))).Status().MessageCount.Should().Be(1);
        (await h.SendAsync(h.StatusRequest40(h.Alice), h.Http(h.Alice))).Status40().MessageCount.Should().Be(1);
        (await queue.DeliverAsync(h.Alice, PickupFixture.Payload(3))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await h.SendAsync(h.StatusRequest40(h.Alice), h.Http(h.Alice))).Status40().TotalBytes.Should().Be(10);

        // And the per-session state: live delivery turned on through 4.0 is what 3.0 reports for that session.
        var socket = h.Live(h.Alice);
        (await h.SendAsync(h.LiveChange40(h.Alice, true), socket)).Status40().LiveDelivery.Should().BeTrue();
        (await h.SendAsync(h.StatusRequest(h.Alice), socket)).Status().LiveDelivery.Should().BeTrue("one session tracker serves both versions");
        (await h.SendAsync(h.LiveChange(h.Alice, false), socket)).Status().LiveDelivery.Should().BeFalse();
        (await h.SendAsync(h.StatusRequest40(h.Alice), socket)).Status40().LiveDelivery.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_queue_is_a_status_in_3_0_and_an_empty_delivery_in_4_0()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var session = h.Http(h.Alice);

        var request30 = h.DeliveryRequest(h.Alice);
        var request40 = h.DeliveryRequest40(h.Alice);
        var answer30 = await h.SendAsync(request30, session);
        var answer40 = await h.SendAsync(request40, session);

        answer30.ShouldBeStatus(request30, h.Mediator, h.Alice, 0, 0, live: false);
        answer40.ShouldBeEmptyDelivery40(request40, h.Mediator, h.Alice, recipientDid: null);
        session.Attempts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("3.0", "4.0")]
    [InlineData("4.0", "3.0")]
    public async Task A_lease_taken_through_one_version_is_acknowledged_through_the_other(string fetchedWith, string acknowledgedWith)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var fetch = h.Http(h.Alice);

        var request = fetchedWith == "3.0" ? h.DeliveryRequest(h.Alice, 2) : h.DeliveryRequest40(h.Alice, 2);
        (await h.SendAsync(request, fetch)).Result.Should().Be(DidComm.Protocols.DispatchResult.NoReply);
        var pushed = fetch.Sent.Should().ContainSingle().Subject;
        IReadOnlyList<DeliveredMessage>? delivered;
        (fetchedWith == "3.0" ? MessagePickup.TryParseDelivery(pushed, out delivered, out _) : MessagePickup40.TryParseDelivery(pushed, out delivered, out _)).Should().BeTrue();
        var fetched = delivered!.Select(m => m.Id).ToArray();
        fetched.Should().Equal(ids[0], ids[1]);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(new[] { ids[2] }, "the two are leased");

        if (acknowledgedWith == "3.0")
        {
            var ack = h.MessagesReceived(h.Alice, fetched);
            (await h.SendAsync(ack, h.Http(h.Alice))).ShouldBeStatus(ack, h.Mediator, h.Alice, 1, 30, live: false, times: Times30(h));
        }
        else
        {
            (await h.SendAsync(h.MessagesReceived40(h.Alice, fetched), h.Http(h.Alice))).ShouldBeNoReply40();
        }

        (await h.StoredAsync(h.Alice)).Should().Be(1, "acknowledgement is by grant and id, whichever version leased them");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids[2]);
    }

    [Fact]
    public async Task A_session_s_live_framing_is_the_version_of_its_last_live_delivery_change_that_turned_it_on()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);

        async Task<Message> PushAsync()
        {
            var before = socket.Sent.Count;
            (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(16))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
            return socket.Sent.Skip(before).Should().ContainSingle().Subject;
        }

        (await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket)).Reply!.Type.Should().Be(MessagePickup.StatusType);
        (await PushAsync()).Type.Should().Be(MessagePickup.DeliveryType, "turned on through 3.0");

        (await fixture.SendAsync(MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket)).Status40().LiveDelivery.Should().BeTrue();
        var as40 = await PushAsync();
        as40.Type.Should().Be(MessagePickup40.DeliveryType, "turned on again through 4.0: the session's pushes are 4.0 deliveries now");
        as40.Thid.Should().BeNull();

        (await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket)).Reply!.Type.Should().Be(MessagePickup.StatusType);
        (await PushAsync()).Type.Should().Be(MessagePickup.DeliveryType, "and back");
    }

    [Theory]
    [InlineData("3.0", "4.0")]
    [InlineData("4.0", "3.0")]
    public async Task Live_delivery_turned_off_in_either_version_is_off(string onWith, string offWith)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);

        var on = onWith == "3.0" ? MessagePickup.CreateLiveDeliveryChange(peer, mediator, true) : MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true);
        (await fixture.SendAsync(on, mediator, socket)).Reply.Should().NotBeNull();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(8))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        var off = offWith == "3.0" ? MessagePickup.CreateLiveDeliveryChange(peer, mediator, false) : MessagePickup40.CreateLiveDeliveryChange(peer, mediator, false);
        var answer = await fixture.SendAsync(off, mediator, socket);
        var reportedLive = offWith == "3.0" ? answer.Status().LiveDelivery : answer.Status40().LiveDelivery;

        reportedLive.Should().BeFalse();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(8))).Outcome.Should().Be(PickupDeliveryOutcome.Queued, "live delivery is off for the session, whichever version turned it off");
        socket.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_3_0_recipient_and_a_4_0_recipient_of_different_grants_interleaving_see_only_their_own_messages()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var alice = new List<string>();
        var bob = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            alice.Add(await h.QueueAsync(h.Alice, 10, (byte)i));
            bob.Add(await h.QueueAsync(h.Bob, 20, (byte)(10 + i)));
        }

        // Alice speaks 3.0, Bob 4.0, turn about.
        var aliceSession = h.Http(h.Alice);
        var bobSession = h.Http(h.Bob);
        (await h.SendAsync(h.StatusRequest(h.Alice), aliceSession)).Status().MessageCount.Should().Be(3);
        (await h.SendAsync(h.StatusRequest40(h.Bob), bobSession)).Status40().MessageCount.Should().Be(3);
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), aliceSession)).ShouldBeNoReply();
        (await h.SendAsync(h.DeliveryRequest40(h.Bob, 10), bobSession)).ShouldBeNoReply40();
        MessagePickup.TryParseDelivery(aliceSession.Sent.Single(), out var toAlice, out _).Should().BeTrue();
        MessagePickup40.TryParseDelivery(bobSession.Sent.Single(), out var toBob, out _).Should().BeTrue();
        toAlice!.Select(m => m.Id).Should().Equal(alice);
        toBob!.Select(m => m.Id).Should().Equal(bob);

        // Each acknowledges the other's ids first: nothing of the other grant goes.
        (await h.SendAsync(h.MessagesReceived40(h.Bob, alice), bobSession)).ShouldBeNoReply40();
        (await h.SendAsync(h.MessagesReceived(h.Alice, bob), aliceSession)).Status().MessageCount.Should().Be(3);
        (await h.StoredAsync(h.Alice)).Should().Be(3);
        (await h.StoredAsync(h.Bob)).Should().Be(3);

        // Then their own.
        (await h.SendAsync(h.MessagesReceived(h.Alice, alice), aliceSession)).Status().MessageCount.Should().Be(0);
        (await h.SendAsync(h.MessagesReceived40(h.Bob, bob), bobSession)).ShouldBeNoReply40();
        (await h.StoredAsync(h.Alice)).Should().Be(0);
        (await h.StoredAsync(h.Bob)).Should().Be(0);
    }

    [Fact]
    public async Task A_recipient_may_switch_versions_between_requests_with_thread_ids_compared_as_given()
    {
        // Case tests start from ids certain to hold letters (L-075 test rule): the answers carry the
        // request's thread exactly as written, upper case included, in both versions.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var thread = new LetterIds("switch").First.ToUpperInvariant();
        var session = h.Http(h.Alice);

        var status30 = await h.SendAsync(h.StatusRequest(h.Alice, thid: thread), session);
        var status40 = await h.SendAsync(h.StatusRequest40(h.Alice, thid: thread), session);

        status30.Reply!.Thid.Should().Be(thread);
        status40.Reply!.Thid.Should().Be(thread);
        status40.Status40().MessageCount.Should().Be(1);
        (await h.SendAsync(h.DeliveryRequest40(h.Alice, 1, thid: thread), session)).ShouldBeNoReply40();
        session.Sent.Should().ContainSingle().Which.Thid.Should().Be(thread);
        (await h.SendAsync(h.MessagesReceived(h.Alice, new[] { id }, thread), session)).Status().MessageCount.Should().Be(0);
    }

    private static MessagePickupHandlerTimes Times30(MessagePickupHandlerHarness h)
    {
        var t0 = h.Start.ToUnixTimeSeconds();
        return new MessagePickupHandlerTimes(0, t0, t0);
    }
}
