using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Xunit;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The pickup role end to end through the real dispatcher (no transport): the walkthrough of the
/// protocol page — status, delivery, acknowledgement, live mode — and the lease rules of FR-PICK-04.
/// </summary>
public sealed class MessagePickupFlowTests
{
    [Fact]
    public async Task Status_delivery_acknowledgement_walkthrough_over_a_request_scoped_session()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var session = fixture.Session(peer, mediator);

        (await fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, session))
            .Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));

        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await fixture.QueueAsync(peer, PickupFixture.Payload(100 + i, (byte)i)));
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        }

        var status = await fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, session);
        status.MessageCount.Should().Be(5);
        status.TotalBytes.Should().Be(100 + 101 + 102 + 103 + 104);
        status.LongestWaitedSeconds.Should().Be(50);
        status.NewestReceivedTime.Should().Be(status.OldestReceivedTime + 40);
        status.RecipientDid.Should().BeNull();

        // limit 3 of 5: one delivery, three attachments, FIFO, thid = the request id.
        var request = MessagePickup.CreateDeliveryRequest(peer, mediator, 3);
        var outcome = await fixture.SendAsync(request, mediator, session);
        outcome.Reply.Should().BeNull("the delivery travels as a session push, size-bounded");
        var delivery = session.Sent.Should().ContainSingle().Subject;
        delivery.Thid.Should().Be(request.Id);
        delivery.From.Should().Be(mediator);
        delivery.To.Should().Equal(peer);
        MessagePickup.TryParseDelivery(delivery, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().BeNull();
        messages!.Select(m => m.Id).Should().Equal(ids.Take(3));
        messages![1].Payload.ToArray().Should().Equal(PickupFixture.Payload(101, 1));

        // Delivered, not acknowledged: still counted, not handed out again.
        (await fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, session)).MessageCount.Should().Be(5);
        var second = await fixture.FetchAsync(peer, mediator, fixture.Session(peer, mediator), 10);
        second!.Value.Messages.Select(m => m.Id).Should().Equal(ids.Skip(3));

        // Acknowledge the first three (plus an id nobody holds): removed, status reflects it.
        var afterAck = await fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(peer, mediator, ids.Take(3).Append("no-such-id").ToArray()), mediator, session);
        afterAck.MessageCount.Should().Be(2);
        fixture.Memory.MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task A_delivered_message_is_not_delivered_again_within_its_lease_and_is_after_it()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(30));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(64));

        (await fixture.FetchAsync(peer, mediator, fixture.Session(peer, mediator), 10))!.Value.Messages.Should().ContainSingle(m => m.Id == id);

        // Within the lease: nothing to deliver → status, and the message is still counted.
        fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        var again = fixture.Session(peer, mediator);
        var status = await fixture.StatusOfAsync(MessagePickup.CreateDeliveryRequest(peer, mediator, 10), mediator, again);
        status.MessageCount.Should().Be(1);
        again.Sent.Should().BeEmpty();

        // The lease ran out: the same message, the same id, no copy.
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        (await fixture.FetchAsync(peer, mediator, fixture.Session(peer, mediator), 10))!.Value.Messages.Should().ContainSingle(m => m.Id == id);
        fixture.Memory.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Limit_zero_and_an_empty_queue_are_answered_with_status()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var session = fixture.Session(peer, mediator);

        (await fixture.StatusOfAsync(MessagePickup.CreateDeliveryRequest(peer, mediator, 10), mediator, session)).MessageCount.Should().Be(0);

        await fixture.QueueAsync(peer, PickupFixture.Payload(10));
        (await fixture.StatusOfAsync(MessagePickup.CreateDeliveryRequest(peer, mediator, 0), mediator, session)).MessageCount.Should().Be(1);
        session.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_mode_pushes_new_messages_and_ends_with_the_session()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);

        // Queued before live mode: stays queued when live mode turns on.
        var queuedId = await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        var status = await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket);
        status.LiveDelivery.Should().BeTrue();
        status.MessageCount.Should().Be(1);
        socket.Sent.Should().BeEmpty();

        // A new arrival is pushed as a delivery with one attachment and no thid, and retained.
        var live = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 7));
        live.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var push = socket.Sent.Should().ContainSingle().Subject;
        push.Thid.Should().BeNull();
        MessagePickup.TryParseDelivery(push, out var pushed, out _).Should().BeTrue();
        pushed!.Should().ContainSingle().Which.Id.Should().Be(live.MessageId);
        fixture.Memory.MessageCount.Should().Be(2);

        // Leased to the socket: a delivery-request gets only the older message.
        (await fixture.FetchAsync(peer, mediator, socket, 10))!.Value.Messages.Should().ContainSingle(m => m.Id == queuedId);

        // messages-received clears the live one.
        (await fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(peer, mediator, new[] { live.MessageId! }), mediator, socket)).MessageCount.Should().Be(1);

        // An unacknowledged push, then the socket closes: live mode is off, the message is deliverable again.
        var lost = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 9));
        lost.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        socket.Close();
        fixture.Sessions.Unregister(socket);

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(50, 11))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        var next = fixture.Session(peer, mediator, live: true);
        (await fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, next)).LiveDelivery.Should().BeFalse();
        await WaitUntilAsync(async () => (await fixture.FetchAsync(peer, mediator, fixture.Session(peer, mediator), 10))?.Messages.Any(m => m.Id == lost.MessageId) == true);
    }

    [Fact]
    public async Task Live_mode_on_a_connection_that_cannot_do_it_is_answered_with_the_problem_report()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var http = fixture.Session(peer, mediator);

        var request = MessagePickup.CreateLiveDeliveryChange(peer, mediator, true);
        var outcome = await fixture.SendAsync(request, mediator, http);
        var report = outcome.Reply!;
        report.Type.Should().Be("https://didcomm.org/report-problem/2.0/problem-report");
        report.Pthid.Should().Be(request.Id);
        ProblemReports.ReadCode(report).Should().Be("e.m.live-mode-not-supported");
        ProblemReports.ReadComment(report).Should().Be("Connection does not support Live Delivery");

        // Turning it off there is not a problem: status, live_delivery false.
        (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, false), mediator, http)).LiveDelivery.Should().BeFalse();
        // And without any session the same report.
        (await fixture.SendAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator)).Reply!.Type.Should().Be(ProblemReports.MessageType);
    }

    [Theory]
    [InlineData(HandlerEnvelope.Plaintext)]
    [InlineData(HandlerEnvelope.Anoncrypt)]
    [InlineData(HandlerEnvelope.SignedOnly)]
    [InlineData(HandlerEnvelope.AnoncryptOfSigned)]
    public async Task A_request_without_an_authcrypt_layer_is_neither_applied_nor_answered(HandlerEnvelope shape)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(10));

        (await fixture.SendAsync(MessagePickup.CreateStatusRequest(peer, mediator), mediator, shape: shape)).Result.Should().Be(DispatchResult.NoReply);
        (await fixture.SendAsync(MessagePickup.CreateMessagesReceived(peer, mediator, new[] { id }), mediator, shape: shape)).Result.Should().Be(DispatchResult.NoReply);
        fixture.Memory.MessageCount.Should().Be(1, "such a messages-received removes nothing");
    }

    [Fact]
    public async Task A_peer_sees_only_the_queue_of_dids_registered_under_its_own_grant()
    {
        await using var fixture = PickupFixture.Create();
        var mediator = (await fixture.NewPeerAsync()).Did;
        var alice = (await fixture.NewPeerAsync()).Did;
        var bob = (await fixture.NewPeerAsync()).Did;
        var aliceOther = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(alice, mediator, alice, aliceOther);
        await fixture.EnrolAsync(bob, mediator, bob);
        var aliceId = await fixture.QueueAsync(alice, PickupFixture.Payload(10));
        await fixture.QueueAsync(aliceOther, PickupFixture.Payload(11));

        // Bob names Alice's DID: the answer of an empty queue, echoing the value.
        var bobSession = fixture.Session(bob, mediator);
        var foreign = await fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(bob, mediator, alice), mediator, bobSession);
        foreign.Should().Be(new PickupStatus(0, alice, TotalBytes: 0, LiveDelivery: false));
        (await fixture.StatusOfAsync(MessagePickup.CreateDeliveryRequest(bob, mediator, 10, alice), mediator, bobSession)).MessageCount.Should().Be(0);
        bobSession.Sent.Should().BeEmpty();

        // Bob acknowledges Alice's id: nothing of hers is removed.
        (await fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(bob, mediator, new[] { aliceId }), mediator, bobSession)).MessageCount.Should().Be(0);
        fixture.Memory.MessageCount.Should().Be(2);

        // Alice filters by one of her DIDs: only that queue, and the delivery echoes the filter.
        var aliceSession = fixture.Session(alice, mediator);
        (await fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(alice, mediator, aliceOther), mediator, aliceSession)).MessageCount.Should().Be(1);
        var fetched = await fixture.FetchAsync(alice, mediator, aliceSession, 10, aliceOther);
        fetched!.Value.Messages.Should().ContainSingle();
        MessagePickup.TryParseDelivery(fetched.Value.Delivery, out _, out var echoed).Should().BeTrue();
        echoed.Should().Be(aliceOther);
    }

    [Fact]
    public async Task Five_300_KiB_messages_under_a_1_MiB_budget_travel_one_per_delivery_and_every_packed_delivery_fits()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        for (var i = 0; i < 5; i++)
            await fixture.QueueAsync(peer, PickupFixture.Payload(300 * 1024, (byte)i));

        for (var i = 0; i < 5; i++)
        {
            var session = fixture.Session(peer, mediator, pack: true);
            var fetched = await fixture.FetchAsync(peer, mediator, session, 10);
            fetched!.Value.Messages.Should().ContainSingle("two 300 KiB payloads would pack past 1 MiB");
            session.Attempts.Should().ContainSingle().Which.PackedBytes.Should().BeLessThanOrEqualTo(1024 * 1024);
            (await fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(peer, mediator, new[] { fetched.Value.Messages[0].Id }), mediator, session)).MessageCount.Should().Be(4 - i);
        }
        (await fixture.StatusOfAsync(MessagePickup.CreateDeliveryRequest(peer, mediator, 10), mediator, fixture.Session(peer, mediator))).MessageCount.Should().Be(0);

        // Admission at the 1 MiB default: 500 KiB is accepted, 600 KiB is not.
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(500 * 1024))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(600 * 1024))).Outcome.Should().Be(PickupDeliveryOutcome.PayloadTooLarge);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not become true.");
            await Task.Delay(20);
        }
    }
}
