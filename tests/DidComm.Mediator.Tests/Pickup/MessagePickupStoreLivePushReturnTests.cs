using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// A live push that comes back unacknowledged is never pushed again (owner decision on #100,
/// 2026-11-20; Message Pickup 4.0 L304: "Messages already in the queue are not affected by Live
/// Mode; they MUST still be requested with delivery-request messages", L308; 3.0 L203), as
/// <see cref="InMemoryMessageStore"/> keeps it: a live-push lease (<see cref="MessageLease.LivePush"/>)
/// marks every record it takes, in the same step (<see cref="QueuedMessage.LivePushReturned"/>),
/// and the store remembers which lease set the mark; whatever ends that lease — its connection's
/// close, its expiry, a give-back by id — the mark stays; only the same lease clears it, by saying
/// its push was not sent (<see cref="IMessageStore.ReturnUnsentAsync"/>, or a pass-over), also after
/// the lease ended; a live-push request never takes a marked record, a delivery-request takes it as
/// before and never sets or clears a mark; and the mark changes no count, bound or status (PR #143
/// review P2-1: the mark no longer depends on which end of a lease lands first).
/// </summary>
public sealed class MessagePickupStoreLivePushReturnTests
{
    private static readonly TimeSpan LeaseFor = TimeSpan.FromSeconds(60);

    // What the push workers ask for: one record at a time, under a live-push lease owned by the session.
    private static MessageLeaseRequest LiveTake(string owner = "socket", DateTimeOffset? now = null, DateTimeOffset? until = null, int max = int.MaxValue, string leaseId = KitLeaseId)
        => new(Alice, null, max, long.MaxValue, now ?? T0, new MessageLease(owner, until ?? Far) { Id = leaseId, LivePush = true });

    // What a delivery-request asks for: the same, under a lease that is not a live push's.
    private static MessageLeaseRequest PickupTake(string? owner = null, DateTimeOffset? now = null, DateTimeOffset? until = null, int max = int.MaxValue)
        => Take(Alice, max: max, now: now, owner: owner, until: until);

    private static async Task<IReadOnlyList<QueuedMessage>> LeaseAsync(InMemoryMessageStore store, MessageLeaseRequest request)
        => await store.LeaseAsync(request, None);

    // ---- what marks a record -------------------------------------------------------------------

    [Fact]
    public async Task A_live_push_given_back_by_its_connections_close_is_marked_and_only_a_delivery_request_takes_it_again()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(LiveTake(owner: "socket"))).Should().Equal("a", "b");

        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(2);

        (await store.LeaseIdsAsync(LiveTake(owner: "next-socket"))).Should().BeEmpty("a push that came back unacknowledged is never pushed live again");
        var picked = await LeaseAsync(store, PickupTake());
        picked.Select(m => m.Id).Should().Equal(new[] { "a", "b" }, "a delivery-request takes them as before, in order");
        picked.Should().OnlyContain(m => m.LivePushReturned, "the record says it came back from a live push");
    }

    [Fact]
    public async Task A_live_push_whose_lease_ran_out_stays_marked_and_only_a_delivery_request_takes_it()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(until: until))).Should().Equal("a");

        // Under its lease, at its end and long after: never taken by another live push.
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until - Tick))).Should().BeEmpty("still under its lease");
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until))).Should().BeEmpty("its live push ran out unacknowledged");
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until + TimeSpan.FromDays(1)))).Should().BeEmpty("the mark has no end");

        // A newer record behind it is not held up: the live push takes it.
        await store.AddAsync(Message("b"), now: until);
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until))).Should().Equal("b");

        var picked = (await LeaseAsync(store, PickupTake(now: until))).Should().ContainSingle().Subject;
        picked.Id.Should().Be("a");
        picked.LivePushReturned.Should().BeTrue("the live push that took it marked it, and its lease running out did not clear the mark");
    }

    [Fact]
    public async Task A_record_admitted_already_leased_to_a_live_push_is_marked_when_that_lease_ends_unacknowledged()
    {
        // The queue's own live push (DeliverAsync, JC-6): the record is stored and leased in one step.
        var store = NewStore();
        var until = T0 + LeaseFor;
        await store.AddAsync(Message("closed"), new MessageLease("socket", Far) { Id = "push-1", LivePush = true });
        await store.AddAsync(Message("timed-out"), new MessageLease("other-socket", until) { Id = "push-2", LivePush = true });

        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);

        (await store.LeaseIdsAsync(LiveTake(owner: "new", now: until))).Should().BeEmpty();
        (await store.LeaseIdsAsync(PickupTake(now: until))).Should().Equal("closed", "timed-out");
    }

    [Theory]
    [InlineData("returned as unsent", 1)]
    [InlineData("returned as unsent after it ran out", 1)]
    [InlineData("returned as unsent after its connection closed", 1)]
    [InlineData("passed over", 1)]
    [InlineData("passed over after its connection closed", 0)]
    public async Task A_live_push_its_own_lease_says_was_not_sent_is_unmarked_and_may_be_pushed_live_again(string how, int passedOver)
    {
        // ReturnUnsentAsync and PassOverAsync are how the mediator gives back a push the connection
        // did not take (no room for it, or too large): it never reached the recipient. Whether the
        // lease is still on the record, or its connection's close or its expiry ended it first, the
        // outcome is the same.
        var store = NewStore();
        await store.AddAsync(Alice, "a");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(until: until, leaseId: "push-1"))).Should().Equal("a");

        var now = T0;
        switch (how)
        {
            case "returned as unsent":
                (await store.ReturnUnsentAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
                break;
            case "returned as unsent after it ran out":
                now = until + Tick;
                (await store.ReturnUnsentAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
                break;
            case "returned as unsent after its connection closed":
                (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);
                (await store.ReturnUnsentAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1, "the mark is cleared although the lease had ended");
                break;
            case "passed over":
                (await store.PassOverAsync(Alice, new[] { "a" }, "push-1", T0 + Tick, None)).Should().Be(passedOver);
                now = T0 + Tick;
                break;
            default:
                (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);
                (await store.PassOverAsync(Alice, new[] { "a" }, "push-1", T0 + Tick, None)).Should().Be(passedOver, "no lease left to pass over, but the mark is cleared");
                now = T0 + Tick;
                break;
        }

        var again = (await LeaseAsync(store, LiveTake(owner: "socket", now: now, leaseId: "push-2"))).Should().ContainSingle("a later live push takes it again").Subject;
        again.Id.Should().Be("a");
        again.LivePushReturned.Should().BeFalse();
    }

    [Theory]
    [InlineData("released by its lease id")]
    [InlineData("released by its lease id after it ran out")]
    [InlineData("its connection closed")]
    [InlineData("it ran out")]
    [InlineData("its connection closed, then released by its lease id")]
    [InlineData("released by its lease id, then its connection closed")]
    public async Task A_live_push_whose_lease_ends_any_other_way_stays_marked_whichever_end_lands_first(string how)
    {
        // A give-back by id, the connection's close and the expiry say nothing about whether the
        // push was sent: none of them clears the mark, in any order (PR #143 review P2-1).
        var store = NewStore();
        await store.AddAsync(Alice, "a");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(until: until, leaseId: "push-1"))).Should().Equal("a");

        var now = until + Tick;
        switch (how)
        {
            case "released by its lease id":
                (await store.ReleaseAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
                now = T0;
                break;
            case "released by its lease id after it ran out":
                (await store.ReleaseAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
                break;
            case "its connection closed":
                (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);
                now = T0;
                break;
            case "it ran out":
                break;
            case "its connection closed, then released by its lease id":
                (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);
                (await store.ReleaseAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(0);
                now = T0;
                break;
            default:
                (await store.ReleaseAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
                (await store.ReleaseLeasesAsync("socket", None)).Should().Be(0);
                now = T0;
                break;
        }

        (await store.LeaseIdsAsync(LiveTake(owner: "socket-2", now: now, leaseId: "push-2"))).Should().BeEmpty("a live push that may have been sent is never pushed live again");
        var picked = (await LeaseAsync(store, PickupTake(now: now))).Should().ContainSingle("a delivery-request takes it").Subject;
        picked.LivePushReturned.Should().BeTrue();
    }

    [Fact]
    public async Task A_delivery_requests_lease_never_marks_whether_its_connection_closes_or_it_runs_out()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "over-the-socket", "over-http");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(PickupTake(owner: "socket", max: 1))).Should().Equal("over-the-socket");
        (await store.LeaseIdsAsync(PickupTake(until: until, max: 1))).Should().Equal("over-http");

        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);

        var pushed = await LeaseAsync(store, LiveTake(now: until));
        pushed.Select(m => m.Id).Should().Equal(new[] { "over-the-socket", "over-http" }, "only a live push that came back is kept from live pushes");
        pushed.Should().OnlyContain(m => !m.LivePushReturned);
    }

    // ---- what the mark does --------------------------------------------------------------------

    [Fact]
    public async Task A_live_push_request_passes_marked_records_by_and_a_delivery_request_takes_them_in_their_place_within_its_budgets()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c", "d");
        (await store.LeaseIdsAsync(LiveTake(max: 2))).Should().Equal("a", "b");
        await store.ReleaseLeasesAsync("socket", None);

        // Passed by, not a stop: the live push takes the first record that is not marked.
        (await store.LeaseIdsAsync(LiveTake(owner: "socket-2", max: 1))).Should().Equal("c");

        // A delivery-request: arrival order, its count limit and its byte budget as before.
        (await store.LeaseIdsAsync(PickupTake(max: 1, until: T0 + LeaseFor))).Should().Equal("a");
        (await store.LeaseIdsAsync(Take(Alice, bytes: 15, until: T0 + LeaseFor))).Should().Equal(new[] { "b" }, "10-byte records: the second would pass the budget of 15");
        (await store.LeaseIdsAsync(PickupTake(until: T0 + LeaseFor))).Should().Equal("d");
    }

    [Fact]
    public async Task The_mark_outlives_a_delivery_requests_lease_on_the_record_however_that_lease_ends()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "closed", "timed-out");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(owner: "s1", max: 1))).Should().Equal("closed");
        (await store.LeaseIdsAsync(LiveTake(owner: "s1b", until: until, max: 1))).Should().Equal("timed-out");
        await store.ReleaseLeasesAsync("s1", None); // "closed" marked; "timed-out" marked from `until`

        // A delivery-request over a socket takes both; its socket then closes.
        var later = until + Tick;
        (await LeaseAsync(store, PickupTake(owner: "s2", now: later))).Should().OnlyContain(m => m.LivePushReturned).And.HaveCount(2);
        (await store.ReleaseLeasesAsync("s2", None)).Should().Be(2);
        (await store.LeaseIdsAsync(LiveTake(owner: "s3", now: later))).Should().BeEmpty("the mark is the record's, not the lease's");

        // Another delivery-request over HTTP takes them; its lease runs out.
        (await store.LeaseIdsAsync(PickupTake(now: later, until: later + LeaseFor))).Should().Equal("closed", "timed-out");
        var muchLater = later + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(owner: "s3", now: muchLater))).Should().BeEmpty();
        (await store.LeaseIdsAsync(PickupTake(now: muchLater))).Should().Equal("closed", "timed-out");
    }

    [Fact]
    public async Task An_acknowledgement_removes_a_marked_record_like_any_other()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(LiveTake())).Should().Equal("a", "b");
        await store.ReleaseLeasesAsync("socket", None);

        (await store.AcknowledgeAsync(Alice, new[] { "a" }, None)).Should().Be(1);

        store.MessageCount.Should().Be(1);
        (await store.GetStatusAsync(Alice, null, T0, None)).MessageCount.Should().Be(1);
        (await store.LeaseIdsAsync(PickupTake())).Should().Equal("b");
    }

    [Fact]
    public async Task The_mark_changes_no_count_byte_total_status_or_bound()
    {
        var store = NewStore(queueMessages: 3);
        await store.AddAsync(Message("a", size: 10));
        await store.AddAsync(Message("b", size: 20));
        await store.AddAsync(Message("c", size: 30));
        var before = await store.HeldAsync();
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(owner: "socket", max: 1))).Should().Equal("a");
        (await store.LeaseIdsAsync(LiveTake(owner: "other", until: until, max: 1))).Should().Equal("b");

        await store.ReleaseLeasesAsync("socket", None);

        // "a" is marked, "b" is marked from `until` on: both still held, counted and reported.
        (await store.HeldAsync(now: until)).Should().Be(before);
        (await store.EnqueueAsync(Message("d"), null, until, None)).Should().Be(EnqueueOutcome.GrantMessageLimitReached, "a marked record counts against the grant's quota like any other");
        (await store.LeaseIdsAsync(PickupTake(now: until))).Should().Equal("a", "b", "c");
        (await store.HeldAsync(now: until)).Should().Be(before, "leasing marked records changes no count either");
    }

    [Fact]
    public async Task A_mark_set_by_the_caller_of_enqueue_is_ignored()
    {
        var store = NewStore();
        await store.AddAsync(Message("a") with { LivePushReturned = true });

        var pushed = (await LeaseAsync(store, LiveTake())).Should().ContainSingle("a new record has never been pushed").Subject;
        pushed.LivePushReturned.Should().BeFalse();
    }

    // ---- the mark is set at the take, by its lease, and cleared only by that lease ---------------

    [Fact]
    public async Task A_live_push_take_marks_each_record_in_the_same_step_and_returns_it_as_it_was_before()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");

        var taken = await LeaseAsync(store, LiveTake(max: 2, until: T0 + LeaseFor, leaseId: "push-1"));

        taken.Select(m => (m.Id, m.LivePushReturned)).Should().Equal(new[] { ("a", false), ("b", false) }, "the copies describe the records before the take");
        // The store's own records carry the mark from the take on: a delivery-request reads it once the lease ran out.
        var later = T0 + LeaseFor;
        (await LeaseAsync(store, PickupTake(now: later))).Select(m => (m.Id, m.LivePushReturned))
            .Should().Equal(new[] { ("a", true), ("b", true), ("c", false) }, "only what the live push took is marked");
    }

    [Fact]
    public async Task A_record_admitted_under_a_live_push_lease_is_marked_by_that_lease_and_one_admitted_under_another_lease_is_not()
    {
        var store = NewStore();
        await store.AddAsync(Message("pushed"), new MessageLease("socket", T0 + LeaseFor) { Id = "push-1", LivePush = true });
        await store.AddAsync(Message("picked"), new MessageLease("socket", T0 + LeaseFor) { Id = "pickup-1" });
        await store.AddAsync(Message("waiting"));

        var later = T0 + LeaseFor;
        (await LeaseAsync(store, PickupTake(now: later, until: later + Tick))).Select(m => (m.Id, m.LivePushReturned))
            .Should().Equal(new[] { ("pushed", true), ("picked", false), ("waiting", false) });

        // The mark is the admitting lease's: its own id clears it, any other does not.
        (await store.ReturnUnsentAsync(Alice, new[] { "pushed" }, "pickup-1", None)).Should().Be(0, "not the lease that set the mark");
        (await store.ReturnUnsentAsync(Alice, new[] { "pushed" }, "push-1", None)).Should().Be(1);
        (await store.LeaseIdsAsync(LiveTake(now: later + Tick))).Should().Equal(new[] { "pushed", "picked", "waiting" });
    }

    [Fact]
    public async Task Return_unsent_clears_only_the_mark_its_own_lease_set_and_ends_no_other_lease()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(owner: "s1", until: until, max: 1, leaseId: "push-a"))).Should().Equal("a");
        (await store.LeaseIdsAsync(LiveTake(owner: "s2", until: until, max: 1, leaseId: "push-b"))).Should().Equal("b");

        // Another lease's id, an unknown id, another grant's records: nothing changes.
        (await store.ReturnUnsentAsync(Alice, new[] { "a" }, "push-b", None)).Should().Be(0);
        (await store.ReturnUnsentAsync(Alice, new[] { "a", "b" }, "nobody", None)).Should().Be(0);
        (await store.ReturnUnsentAsync(Bob, new[] { "a" }, "push-a", None)).Should().Be(0, "an id means something only together with its grant");
        (await store.ReturnUnsentAsync(Alice, new[] { "missing" }, "push-a", None)).Should().Be(0);

        // Its lease ran out and a delivery-request took the record since: the mark goes, that lease stays.
        var pickupLease = new MessageLease(null, until + LeaseFor) { Id = "pickup" };
        (await store.LeaseIdsAsync(new MessageLeaseRequest(Alice, null, 1, long.MaxValue, until, pickupLease))).Should().Equal("a");
        (await store.ReturnUnsentAsync(Alice, new[] { "a" }, "push-a", None)).Should().Be(1, "the mark push-a set is cleared");
        (await store.LeaseIdsAsync(LiveTake(owner: "s3", now: until, leaseId: "push-c"))).Should().BeEmpty("a is still the delivery-request's, and b keeps the mark push-b set");
        (await store.ReturnUnsentAsync(Alice, new[] { "a" }, "push-a", None)).Should().Be(0, "nothing left to change: a second call is a no-op");

        // Once the delivery-request's lease runs out, a live push may take a again; b stays marked.
        (await store.LeaseIdsAsync(LiveTake(owner: "s3", now: until + LeaseFor, leaseId: "push-d"))).Should().Equal("a");
    }

    [Fact]
    public async Task Return_unsent_under_a_delivery_requests_lease_ends_that_lease_like_a_release_and_clears_no_mark()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "pushed", "plain");
        (await store.LeaseIdsAsync(LiveTake(until: T0 + LeaseFor, max: 1, leaseId: "push-1"))).Should().Equal("pushed");
        var until = T0 + LeaseFor;
        var pickupLease = new MessageLease(null, Far) { Id = "pickup" };
        (await store.LeaseIdsAsync(new MessageLeaseRequest(Alice, null, 10, long.MaxValue, until, pickupLease))).Should().Equal("pushed", "plain");

        (await store.ReturnUnsentAsync(Alice, new[] { "pushed", "plain" }, "pickup", None)).Should().Be(2, "both leases ended");

        var picked = await LeaseAsync(store, PickupTake(now: until));
        picked.Select(m => (m.Id, m.LivePushReturned)).Should().Equal(new[] { ("pushed", true), ("plain", false) }, "a delivery-request's lease never set a mark, so it clears none");
    }

    [Fact]
    public async Task A_pass_over_clears_only_the_mark_its_own_lease_set()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(LiveTake(owner: "s1", max: 1, leaseId: "push-a"))).Should().Equal("a");
        (await store.LeaseIdsAsync(LiveTake(owner: "s2", max: 1, leaseId: "push-b"))).Should().Equal("b");

        (await store.PassOverAsync(Alice, new[] { "a", "b" }, "push-a", T0 + Tick, None)).Should().Be(1, "only a still carries push-a");

        var later = T0 + Tick;
        (await store.ReleaseLeasesAsync("s2", None)).Should().Be(1);
        (await store.LeaseIdsAsync(LiveTake(owner: "s3", now: later, leaseId: "push-c"))).Should().Equal(new[] { "a" }, "b keeps the mark push-b set");
    }

    [Fact]
    public async Task Ending_leases_never_clears_a_mark_and_a_marked_record_is_never_handed_to_a_live_push_whatever_the_order()
    {
        var store = NewStore(queueMessages: 4);
        await store.AddAsync(Alice, "a", "b", "c", "d");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(owner: "s1", until: until, leaseId: "push-1"))).Should().Equal("a", "b", "c", "d");

        (await store.ReleaseAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
        (await store.ReleaseLeasesAsync("s1", None)).Should().Be(3);
        (await store.ReleaseAsync(Alice, new[] { "b" }, "push-1", None)).Should().Be(0);
        (await store.AcknowledgeAsync(Alice, new[] { "d" }, None)).Should().Be(1);

        foreach (var now in new[] { T0, until, until + LeaseFor, Far - Tick })
            (await store.LeaseIdsAsync(LiveTake(owner: "s2", now: now, until: now + Tick, leaseId: "push-2"))).Should().BeEmpty($"a live push at {now:O}");
        var (count, bytes, status) = await store.HeldAsync();
        (count, bytes, status.MessageCount, status.TotalBytes).Should().Be((3, 30L, 3, 30L), "only the acknowledgement removed something: the marks change no count");
        (await store.EnqueueAsync(Message("e"), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued, "a marked record counts against the quota like any other, no more");
        (await store.EnqueueAsync(Message("f"), null, T0, None)).Should().Be(EnqueueOutcome.GrantMessageLimitReached);
    }
}
