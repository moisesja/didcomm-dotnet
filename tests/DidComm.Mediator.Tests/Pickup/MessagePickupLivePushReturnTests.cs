using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The coordinator's side of "a live push that comes back unacknowledged is never pushed again"
/// (owner decision on #100, 2026-11-20): every lease it takes for a live push says so
/// (<see cref="MessageLease.LivePush"/>) and no delivery-request's lease does; a push the session
/// took and that found the session closed is ended the way the close ends it (so the store marks
/// it); and a record a store hands to a live push although it is marked is refused.
/// </summary>
public sealed class MessagePickupLivePushReturnTests
{
    private static CancellationToken None => CancellationToken.None;

    [Fact]
    public async Task The_queues_live_push_and_the_push_workers_take_live_push_leases_and_a_delivery_request_never_does()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var store = fixture.FakeStore!;

        // The queue's own live push (JC-6): stored already leased to the live session.
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var admitted = store.Enqueued.Should().ContainSingle().Subject.Lease;
        admitted.Should().NotBeNull();
        admitted!.LivePush.Should().BeTrue("the record is admitted under a live push's lease");

        // The push workers' push: queued without a live push of its own, then taken for the session.
        await fixture.QueueAsync(peer, PickupFixture.Payload(40, 2), new PickupDeliveryOptions(AllowLive: false));
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent);
        var take = store.LeaseRequests.Should().ContainSingle().Subject;
        take.AfterSequence.Should().NotBeNull("precondition: the push workers' take");
        take.Lease.LivePush.Should().BeTrue();

        // A delivery-request, over HTTP and over the live socket.
        await fixture.QueueAsync(peer, PickupFixture.Payload(40, 3), new PickupDeliveryOptions(AllowLive: false));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle();
        await fixture.QueueAsync(peer, PickupFixture.Payload(40, 4), new PickupDeliveryOptions(AllowLive: false));
        (await fixture.FetchAsync(peer, mediator, socket, 10)).Should().NotBeNull();
        var pickups = store.LeaseRequests.Skip(1).ToArray();
        pickups.Should().HaveCount(2);
        pickups.Should().OnlyContain(r => !r.Lease.LivePush && r.AfterSequence == null, "a delivery-request's lease is never a live push's");
    }

    [Theory]
    [InlineData("the queue's own live push")]
    [InlineData("the push workers' push")]
    public async Task A_push_the_session_took_as_it_closed_is_ended_as_the_close_ends_it_and_comes_back_for_a_delivery_request_only(string path)
    {
        // The session takes the push (Sent) and its close lands before the coordinator looks again.
        // The close's own clean-up may have run before the lease existed, so the coordinator ends the
        // lease too — the way the close does, which marks it: the push came back unacknowledged. The
        // close's clean-up is held here until the coordinator is done, so a give-back by lease id
        // (which would leave the record unmarked) cannot hide behind it.
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var store = fixture.FakeStore!;
        string id;
        if (path == "the push workers' push")
            id = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1), new PickupDeliveryOptions(AllowLive: false));
        else
            id = "";

        var cleanUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnReleaseLeases = async (owner, ct) =>
        {
            await cleanUp.Task;
            return await store.Inner.ReleaseLeasesAsync(owner, ct);
        };
        socket.OnSend = (_, _) =>
        {
            socket.Close();
            return ReturnRouteSendResult.Sent;
        };

        if (path == "the push workers' push")
        {
            (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.NotSent, "a push to a session that closed is not counted as delivered");
        }
        else
        {
            var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1));
            result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the session closed as it took the push: the message is held in the queue");
            id = result.MessageId!;
        }
        store.Released.Should().BeEmpty("the lease is not given back by its id: that would say the session never took the push");

        cleanUp.SetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Queue.Coordinator.Tracker.PendingReleases == 0 && store.ReleasedOwners.Count >= 2, "the close's clean-up and the coordinator's ran");

        var now = fixture.Clock.Now;
        var live = new MessageLeaseRequest(peer, null, 10, long.MaxValue, now, new MessageLease("another-socket", now + TimeSpan.FromMinutes(1)) { LivePush = true });
        (await store.Inner.LeaseAsync(live, None)).Should().BeEmpty("a push that came back unacknowledged is never pushed live again");
        var pickup = new MessageLeaseRequest(peer, null, 10, long.MaxValue, now, new MessageLease(null, now + TimeSpan.FromMinutes(1)));
        var held = (await store.Inner.LeaseAsync(pickup, None)).Should().ContainSingle("a delivery-request still gets it").Subject;
        held.Id.Should().Be(id);
        held.LivePushReturned.Should().BeTrue();
    }

    [Fact]
    public async Task A_record_a_store_hands_to_a_live_push_although_it_came_back_from_one_is_refused_given_back_and_not_pushed()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var store = fixture.FakeStore!;
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1), new PickupDeliveryOptions(AllowLive: false));
        // A store that keeps the mark but does not honour it (here: the mark appears on what it hands out).
        store.OnLease = async (request, ct) =>
        {
            var leased = await store.Inner.LeaseAsync(request, ct);
            return leased.Select(m => m with { LivePushReturned = true }).ToArray();
        };

        var push = () => fixture.Queue.Coordinator.PushNextLiveAsync(peer, None).AsTask();

        await push.Should().ThrowAsync<InvalidOperationException>("outside the store contract: such a record is the recipient's to ask for");
        socket.Sent.Should().BeEmpty("nothing is pushed");
        store.Released.Should().ContainSingle().Which.Ids.Should().Equal(id);
        store.OnLease = null;
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("pickup still works").Which.Id.Should().Be(id);
    }
}
