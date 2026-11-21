using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red-team pass ADV-4 on "a live push that comes back unacknowledged is never pushed again"
/// (owner decision on #100, 2026-11-20; Message Pickup 4.0 L304, L308; 3.0 L203). Each test is one
/// attack. A passing test is an attack that held; a skipped one (ADV4-n) reproduces a finding and
/// asserts the documented contract it breaks.
/// </summary>
public sealed class MessagePickupLivePushReturnAdv4Tests
{
    private static readonly TimeSpan LeaseFor = new MessagePickupOptions().LeaseTimeout;

    private static MessageLeaseRequest LiveTake(string grant, string owner, DateTimeOffset now, DateTimeOffset? until = null, string? leaseId = null)
        => new(grant, null, int.MaxValue, long.MaxValue, now, new MessageLease(owner, until ?? Far) { Id = leaseId ?? Guid.NewGuid().ToString("N"), LivePush = true });

    private static MessageLeaseRequest PickupTake(string grant, DateTimeOffset now, string? owner = null, DateTimeOffset? until = null, string? leaseId = null)
        => new(grant, null, int.MaxValue, long.MaxValue, now, new MessageLease(owner, until ?? now + Tick) { Id = leaseId ?? Guid.NewGuid().ToString("N") });

    // A probe of the real store: what a live push would take now, and what a delivery-request would.
    private static async Task<string[]> LivePushableAsync(PickupFixture fixture, string peer)
    {
        var now = fixture.Clock.Now;
        var taken = await fixture.Memory.LeaseAsync(LiveTake(peer, "probe-" + Guid.NewGuid().ToString("N"), now, now + Tick), None);
        return taken.Select(m => m.Id).ToArray();
    }

    // ---- ADV4-1: a push that never reached the connection comes back marked --------------------

    [Fact(Skip = "ADV4-1 (Low): a queue push admitted to a session that closes before it is sent is marked when the close's clean-up runs before the give-back by id")]
    public async Task Adv4_1_a_queue_push_whose_session_closes_before_it_is_sent_may_still_be_pushed_live()
    {
        // PickupCoordinator.DeliverAsync: "Not pushed (... a downgraded or closing session ...): the
        // record ... loses this lease, so a delivery-request gets it — and so may a later live push,
        // since this one never reached the connection." The peer has two live sessions; the record is
        // admitted under the newer one's live-push lease, and that session closes before the push is
        // sent. When the close's clean-up (ReleaseLeasesAsync by owner) finishes before the coordinator
        // gives the lease back by id, the store marks the record as a returned live push.
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        var newer = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newer);
        var store = fixture.FakeStore!;
        var tracker = fixture.Queue.Coordinator.Tracker;
        store.OnEnqueue = async (message, lease, now, ct) =>
        {
            var outcome = await store.Inner.EnqueueAsync(message, lease, now, ct);
            newer.Close();
            await MessagePickupLive.UntilAsync(() => store.ReleasedOwners.Count == 1 && tracker.PendingReleases == 0, "the close's clean-up ran to its end");
            return outcome;
        };

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1));
        store.OnEnqueue = null;

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "precondition: the session closed before the push");
        newer.Sent.Should().BeEmpty("precondition: the push never reached the connection");
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent, "a push that never reached the connection is not a returned live push");
        older.Sent.Should().ContainSingle();
    }

    [Fact(Skip = "ADV4-1 (Low): a worker push whose session closes after the take and before the send is marked when the close's clean-up runs before the give-back by id")]
    public async Task Adv4_1_a_worker_push_whose_session_closes_before_it_is_sent_may_still_be_pushed_live()
    {
        // PickupCoordinator.PushNextLiveAsync remarks: "Anything else — not taken, the session closing
        // first ... — gives the lease back unmarked ... (it never reached the connection, so a later
        // wake-up pushes it)."
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        var newer = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newer);
        var store = fixture.FakeStore!;
        var tracker = fixture.Queue.Coordinator.Tracker;
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1), new PickupDeliveryOptions(AllowLive: false));
        store.OnLease = async (request, ct) =>
        {
            var leased = await store.Inner.LeaseAsync(request, ct);
            if (request.Lease.LivePush && leased.Count > 0 && !newer.Closed.IsCancellationRequested)
            {
                newer.Close();
                await MessagePickupLive.UntilAsync(() => store.ReleasedOwners.Count == 1 && tracker.PendingReleases == 0, "the close's clean-up ran to its end");
            }
            return leased;
        };

        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.NotSent, "precondition: the session closed before the send");
        store.OnLease = null;

        newer.Sent.Should().BeEmpty("precondition: the push never reached the connection");
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent, "a push that never reached the connection is not a returned live push");
        MessagePickup.TryParseDelivery(older.Sent.Single(), out var delivered, out _).Should().BeTrue();
        delivered!.Select(m => m.Id).Should().Equal(id);
    }

    [Fact]
    public async Task Adv4_1_counterpart_the_same_interleaving_with_the_give_back_by_id_first_leaves_the_record_pushable()
    {
        // The same scenario as the first ADV4-1 test, the only change being which of the two ends of
        // the lease lands first: the outcome depends on a thread-pool race, not on what reached the wire.
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        var newer = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newer);
        var store = fixture.FakeStore!;
        var tracker = fixture.Queue.Coordinator.Tracker;
        var cleanUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnReleaseLeases = async (owner, ct) =>
        {
            await cleanUp.Task;
            return await store.Inner.ReleaseLeasesAsync(owner, ct);
        };
        store.OnEnqueue = async (message, lease, now, ct) =>
        {
            var outcome = await store.Inner.EnqueueAsync(message, lease, now, ct);
            newer.Close();
            return outcome;
        };

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1));
        store.OnEnqueue = null;
        cleanUp.SetResult();
        await MessagePickupLive.UntilAsync(() => store.ReleasedOwners.Count == 1 && tracker.PendingReleases == 0, "the close's clean-up ran to its end");

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        newer.Sent.Should().BeEmpty();
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent);
        older.Sent.Should().ContainSingle();
    }

    // ---- what held -------------------------------------------------------------------------------

    [Theory]
    [InlineData("3.0", "4.0", "runs out")]
    [InlineData("4.0", "3.0", "runs out")]
    [InlineData("3.0", "4.0", "closes")]
    [InlineData("4.0", "3.0", "closes")]
    public async Task A_push_returned_by_one_live_session_is_not_pushed_on_the_peers_other_live_session_in_either_version(string olderVersion, string newerVersion, string how)
    {
        // Two live sessions of one peer (or, #98 D4, a captured live-delivery-change replayed on a
        // second socket): the record goes to the newer one, comes back unacknowledged, and the older
        // one — live since before the record arrived — must not get it pushed. A new arrival is.
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await TurnLiveOnAsync(fixture, peer, mediator, older, olderVersion);
        var newer = fixture.Session(peer, mediator, live: true);
        await TurnLiveOnAsync(fixture, peer, mediator, newer, newerVersion);
        var store = fixture.FakeStore!;
        var tracker = fixture.Queue.Coordinator.Tracker;

        var first = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1));
        first.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        newer.Sent.Should().ContainSingle("precondition: pushed to the newest live session");

        if (how == "runs out")
        {
            fixture.Clock.Advance(LeaseFor);
            (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty, "the newer session is still open; its push ran out");
        }
        newer.Close();
        await MessagePickupLive.UntilAsync(() => store.ReleasedOwners.Count == 1 && tracker.PendingReleases == 0, "the close's clean-up ran");

        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty, "the returned push is never pushed live again, on any session");
        older.Sent.Should().BeEmpty();

        var second = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 2));
        second.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "a new arrival is pushed as before");
        older.Sent.Should().ContainSingle();
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(new[] { first.MessageId }, "a delivery-request gets the returned push");
    }

    [Fact]
    public async Task A_close_whose_clean_up_fails_in_the_store_still_leaves_the_push_marked_once_its_lease_runs_out()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var store = fixture.FakeStore!;
        var tracker = fixture.Queue.Coordinator.Tracker;
        store.OnReleaseLeases = (_, _) => throw new InvalidOperationException("store outage");

        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        socket.Close();
        await MessagePickupLive.UntilAsync(() => store.ReleasedOwners.Count == 1 && tracker.PendingReleases == 0, "the failed clean-up ran");

        fixture.Clock.Advance(LeaseFor);
        (await LivePushableAsync(fixture, peer)).Should().BeEmpty("the lease ran out unacknowledged: judged returned when read");
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty);
        older.Sent.Should().BeEmpty();
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(pushed.MessageId);
    }

    [Fact]
    public async Task A_close_dropped_by_a_full_clean_up_backlog_still_leaves_the_push_marked_once_its_lease_runs_out()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var store = fixture.FakeStore!;
        var tracker = fixture.Queue.Coordinator.Tracker;
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        // A hostile peer's churn fills the backlog behind a store call that does not return.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnReleaseLeases = async (owner, ct) =>
        {
            await gate.Task;
            return await store.Inner.ReleaseLeasesAsync(owner, ct);
        };
        for (var i = 0; i < PickupSessionTracker.MaxPendingReleases; i++)
            tracker.ReleaseLeasesInBackground("churn-" + i);
        var droppedBefore = tracker.DroppedReleases;
        socket.Close();
        tracker.DroppedReleases.Should().Be(droppedBefore + 1, "precondition: the close's clean-up was dropped");

        fixture.Clock.Advance(LeaseFor);
        (await LivePushableAsync(fixture, peer)).Should().BeEmpty("the lease ran out unacknowledged");
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty);
        older.Sent.Should().BeEmpty();
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(pushed.MessageId);

        gate.SetResult();
        await MessagePickupLive.UntilAsync(() => tracker.PendingReleases == 0, "the backlog drained");
    }

    [Fact]
    public async Task A_live_push_passed_over_as_too_large_is_not_marked_and_is_pushed_live_once_the_period_ends()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1), new PickupDeliveryOptions(AllowLive: false));
        socket.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;

        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.PassedOver);
        socket.OnSend = null;
        fixture.Clock.Advance(LeaseFor);

        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent, "it never reached the connection");
        MessagePickup.TryParseDelivery(socket.Sent.Single(), out var delivered, out _).Should().BeTrue();
        delivered!.Select(m => m.Id).Should().Equal(id);
    }

    [Fact]
    public async Task A_push_the_session_had_no_room_for_is_not_marked_and_the_next_wake_up_pushes_it()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        socket.OnSend = (_, _) => ReturnRouteSendResult.NotSent;
        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1));
        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.NotSent);
        socket.OnSend = null;

        // The lease of each refused push was given back by id; time passing marks nothing.
        fixture.Clock.Advance(LeaseFor * 3);
        (await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent);
        socket.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Readings_out_of_order_and_a_clock_stepped_back_never_hand_a_returned_push_to_a_live_push()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(Alice, "s1", T0, until))).Should().Equal("a");

        // The lease ran out (truth: until + 1 s). A live push whose reading is stale, or whose host
        // clock stepped back, sees the record as still leased; one with a later reading sees it as
        // returned. Neither takes it, in any order.
        foreach (var now in new[] { until + TimeSpan.FromSeconds(1), until - TimeSpan.FromSeconds(30), until, T0, until + TimeSpan.FromDays(400) })
            (await store.LeaseIdsAsync(LiveTake(Alice, "s2", now, now + LeaseFor))).Should().BeEmpty($"a live push at {now:O}");

        // A delivery-request with a stepped-back reading cannot take it either (still leased at that
        // instant); one after the expiry does, and the record carries the mark from then on.
        (await store.LeaseIdsAsync(PickupTake(Alice, until - Tick))).Should().BeEmpty();
        var taken = await store.LeaseAsync(PickupTake(Alice, until, until: until + LeaseFor), None);
        taken.Should().ContainSingle().Which.LivePushReturned.Should().BeTrue();
        foreach (var now in new[] { T0, until, until + LeaseFor + Tick, Far })
            (await store.LeaseIdsAsync(LiveTake(Alice, "s3", now, now + LeaseFor))).Should().BeEmpty($"a live push at {now:O}, after a delivery-request took it over");
    }

    [Fact]
    public async Task The_mark_survives_many_cycles_of_every_lease_ending_and_never_appears_without_a_live_push()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "pushed", "never-pushed");
        var now = T0;
        (await store.LeaseAsync(new MessageLeaseRequest(Alice, null, 1, long.MaxValue, now, new MessageLease("s0", now + LeaseFor) { Id = "push", LivePush = true }), None))
            .Single().Id.Should().Be("pushed");
        await store.ReleaseLeasesAsync("s0", None);
        var before = await store.HeldAsync(now: now);

        for (var cycle = 0; cycle < 50; cycle++)
        {
            now += LeaseFor + Tick;
            var lease = "pickup-" + cycle;
            var owner = "socket-" + cycle;
            var taken = await store.LeaseAsync(PickupTake(Alice, now, owner: owner, until: now + LeaseFor, leaseId: lease), None);
            taken.Select(m => (m.Id, m.LivePushReturned)).Should().Equal(("pushed", true), ("never-pushed", false));
            switch (cycle % 4)
            {
                case 0:
                    (await store.ReleaseAsync(Alice, new[] { "pushed", "never-pushed" }, lease, None)).Should().Be(2);
                    break;
                case 1:
                    (await store.ReleaseLeasesAsync(owner, None)).Should().Be(2);
                    break;
                case 2:
                    (await store.PassOverAsync(Alice, new[] { "pushed", "never-pushed" }, lease, now + Tick, None)).Should().Be(2);
                    break;
                default:
                    break; // the delivery-request's lease runs out
            }
            (await store.LeaseIdsAsync(LiveTake(Alice, "live-" + cycle, now + LeaseFor, now + LeaseFor + LeaseFor, leaseId: "live-" + cycle)))
                .Should().Equal(new[] { "never-pushed" }, "cycle {0}", cycle);
            (await store.ReleaseAsync(Alice, new[] { "never-pushed" }, "live-" + cycle, None)).Should().Be(1, "the live push gives back what it did not send");
        }

        (await store.HeldAsync(now: now)).Should().Be(before, "the mark changes no count, byte total or status");
    }

    [Fact]
    public async Task One_tenants_returned_pushes_and_its_sessions_close_never_mark_another_tenants_records()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "alice-1");
        await store.AddAsync(Bob, "bob-1");
        (await store.LeaseIdsAsync(LiveTake(Alice, "alice-socket", T0, T0 + LeaseFor))).Should().Equal("alice-1");
        (await store.LeaseIdsAsync(LiveTake(Bob, "bob-socket", T0, Far, leaseId: "bob-push"))).Should().Equal("bob-1");

        // Alice's socket closes and her other push runs out; Bob's live push is still in hand and
        // given back unsent (by id): none of it touches Bob's record.
        await store.ReleaseLeasesAsync("alice-socket", None);
        (await store.ReleaseAsync(Bob, new[] { "bob-1" }, "bob-push", None)).Should().Be(1);
        // A lease id of Bob's named against Alice's grant, and an owner token that is not Bob's, do nothing.
        (await store.ReleaseAsync(Alice, new[] { "bob-1" }, "bob-push", None)).Should().Be(0);
        (await store.ReleaseLeasesAsync("bob-socket", None)).Should().Be(0);

        var later = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(Bob, "bob-socket-2", later))).Should().Equal(new[] { "bob-1" }, "Bob's record was never returned from a live push");
        (await store.LeaseIdsAsync(LiveTake(Alice, "alice-socket-2", later))).Should().BeEmpty();
        (await store.GetStatusAsync(Alice, null, later, None)).MessageCount.Should().Be(1);
        (await store.GetStatusAsync(Bob, null, later, None)).MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_marked_record_ahead_of_others_blocks_neither_the_live_pushes_nor_a_delivery_request_and_expires_like_any_other()
    {
        var store = NewStore();
        await store.AddAsync(Message("old") with { ExpiresAt = T0 + LeaseFor + LeaseFor });
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(new MessageLeaseRequest(Alice, null, 1, long.MaxValue, T0, new MessageLease("s", Far) { LivePush = true }))).Should().Equal("old");
        await store.ReleaseLeasesAsync("s", None);

        (await store.LeaseIdsAsync(new MessageLeaseRequest(Alice, null, 1, long.MaxValue, T0, new MessageLease("s2", Far) { Id = "p", LivePush = true }))).Should().Equal(new[] { "a" }, "passed by, not a stop");
        (await store.LeaseIdsAsync(PickupTake(Alice, T0, until: T0 + LeaseFor))).Should().Equal(new[] { "old", "b" }, "a delivery-request takes it in its place");

        var expired = T0 + LeaseFor + LeaseFor;
        (await store.GetStatusAsync(Alice, null, expired, None)).MessageCount.Should().Be(2, "the marked record expired like any other");
        (await store.LeaseIdsAsync(PickupTake(Alice, expired))).Should().Equal("b");
    }

    private static async Task TurnLiveOnAsync(PickupFixture fixture, string peer, string mediator, IReturnRouteSession session, string version)
    {
        if (version == "3.0")
        {
            (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeTrue();
            return;
        }
        (await fixture.SendAsync(MessagePickup40.CreateLiveDeliveryChange(peer, mediator, true), mediator, session)).Status40().LiveDelivery.Should().BeTrue();
    }
}
