using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The coordinator's side of "a live push that comes back unacknowledged is never pushed again"
/// (owner decision on #100, 2026-11-20): every lease it takes for a live push says so
/// (<see cref="MessageLease.ForPush"/>) and no delivery-request's lease does; a push the session
/// took, or whose send threw or was cancelled once the session had it, counts as pushed — its lease
/// is given back by id and the mark its take set stays, whichever of that give-back and the close's
/// clean-up lands first (owner decision 2026-11-21, OD-5; PR #143 review P2-1); and a record a store
/// hands to a live push although it is marked is refused and given back as unsent.
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
        admitted!.ForPush.Should().BeTrue("the record is admitted under a live push's lease");

        // The push workers' push: queued without a live push of its own, then taken for the session.
        await fixture.QueueAsync(peer, PickupFixture.Payload(40, 2), new PickupDeliveryOptions(AllowLive: false));
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent);
        var take = store.LeaseRequests.Should().ContainSingle().Subject;
        take.AfterSequence.Should().NotBeNull("precondition: the push workers' take");
        take.Lease.ForPush.Should().BeTrue();

        // A delivery-request, over HTTP and over the live socket.
        await fixture.QueueAsync(peer, PickupFixture.Payload(40, 3), new PickupDeliveryOptions(AllowLive: false));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle();
        await fixture.QueueAsync(peer, PickupFixture.Payload(40, 4), new PickupDeliveryOptions(AllowLive: false));
        (await fixture.FetchAsync(peer, mediator, socket, 10)).Should().NotBeNull();
        var pickups = store.LeaseRequests.Skip(1).ToArray();
        pickups.Should().HaveCount(2);
        pickups.Should().OnlyContain(r => !r.Lease.ForPush && r.AfterSequence == null, "a delivery-request's lease is never a live push's");
    }

    [Theory]
    [InlineData("the queue's own live push")]
    [InlineData("the push workers' push")]
    public async Task A_push_the_session_took_as_it_closed_is_given_back_by_id_and_comes_back_for_a_delivery_request_only(string path)
    {
        // The session takes the push (Sent) and its close lands before the coordinator looks again.
        // Whether it reached the connection cannot be told: the coordinator gives the lease back by
        // its id (the close's own clean-up may have run before the lease existed), and the mark the
        // take set stays. The close's clean-up is held here until the coordinator is done, so the
        // give-back by id lands first: it must not unmark the record.
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
        store.Released.Should().ContainSingle("the lease is given back by its id").Which.Ids.Should().Equal(id);
        store.ReturnedUnsent.Should().BeEmpty("the session took the push: it is not given back as unsent");

        cleanUp.SetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Queue.Coordinator.Tracker.PendingReleases == 0 && store.ReleasedOwners.Count >= 1, "the close's clean-up ran");

        var now = fixture.Clock.Now;
        var live = new MessageLeaseRequest(peer, null, 10, long.MaxValue, now, new MessageLease("another-socket", now + TimeSpan.FromMinutes(1)) { ForPush = true });
        (await store.Inner.LeaseAsync(live, None)).Should().BeEmpty("a push that came back unacknowledged is never pushed live again");
        var pickup = new MessageLeaseRequest(peer, null, 10, long.MaxValue, now, new MessageLease(null, now + TimeSpan.FromMinutes(1)));
        var held = (await store.Inner.LeaseAsync(pickup, None)).Should().ContainSingle("a delivery-request still gets it").Subject;
        held.Id.Should().Be(id);
        held.PushTaken.Should().BeTrue();
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
            return leased.Select(m => m with { PushTaken = true }).ToArray();
        };

        var push = () => fixture.Queue.Coordinator.PushNextLiveAsync(peer, None).AsTask();

        await push.Should().ThrowAsync<InvalidOperationException>("outside the store contract: such a record is the recipient's to ask for");
        socket.Sent.Should().BeEmpty("nothing is pushed");
        store.ReturnedUnsent.Should().ContainSingle("taken and not handed over: given back as unsent").Which.Ids.Should().Equal(id);
        store.Released.Should().BeEmpty();
        store.OnLease = null;
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("pickup still works").Which.Id.Should().Be(id);
    }

    // ---- PR #143 review P2-1: the reviewer's reproduction -----------------------------------------

    [Theory]
    [InlineData("cancelled")]
    [InlineData("faulted")]
    [InlineData("returned Sent")]
    public async Task A_queue_push_whose_session_closes_during_the_send_and_whose_send_then_throws_is_never_pushed_live_again(string send)
    {
        // The reviewer's reproduction (PR #143, P2-1): an older and a newer live session; the close's
        // clean-up is held; the newer session's send closes the session, cancels the delivery's token
        // and throws (or, the control, returns Sent). The coordinator gives the lease back by its id
        // before the clean-up runs. That give-back used to end the live push's lease without marking
        // the record, so the next live push sent it to the older session. A send that threw once the
        // session had the push counts as pushed (owner decision 2026-11-21, OD-5): the mark its take
        // set stays, and only a delivery-request gets it.
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        var newer = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newer);
        var store = fixture.FakeStore!;
        var cleanUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnReleaseLeases = async (owner, ct) =>
        {
            await cleanUp.Task;
            return await store.Inner.ReleaseLeasesAsync(owner, ct);
        };
        using var delivery = new CancellationTokenSource();
        newer.OnSend = (_, _) =>
        {
            newer.Close();
            if (send == "returned Sent")
                return ReturnRouteSendResult.Sent;
            delivery.Cancel();
            throw send == "cancelled" ? new OperationCanceledException(delivery.Token) : new HandlerBoomException(HandlerBoomException.Marker);
        };

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1), ct: delivery.Token);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        store.Released.Should().ContainSingle("the coordinator gave the lease back by its id, before the close's clean-up").Which.Ids.Should().Equal(result.MessageId);
        store.ReturnedUnsent.Should().BeEmpty("the send may have reached the connection");
        await MessagePickupLive.UntilAsync(() => store.ReleasedOwners.Count == 1, "the close's clean-up is waiting at the store's door");
        cleanUp.SetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Queue.Coordinator.Tracker.PendingReleases == 0, "the close's clean-up ran to its end");

        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty, "never pushed live again, to any session");
        older.Sent.Should().BeEmpty();
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(new[] { result.MessageId }, "a delivery-request delivers it");
        fixture.Memory.MessageCount.Should().Be(1);
    }
}
