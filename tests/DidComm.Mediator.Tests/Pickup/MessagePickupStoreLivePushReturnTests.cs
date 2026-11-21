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
/// that ends by its connection's close or by running out marks its record
/// (<see cref="QueuedMessage.LivePushReturned"/>); a live-push request never takes a marked record,
/// a delivery-request takes it as before; a give-back by lease id or a pass-over never marks; and
/// the mark changes no count, bound or status.
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
    public async Task A_live_push_whose_lease_ran_out_counts_as_returned_from_the_instant_it_ran_out()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(until: until))).Should().Equal("a");

        // One tick before the lease ends it is simply leased; from the instant it ends, it is marked.
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until - Tick))).Should().BeEmpty("still under its lease");
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until))).Should().BeEmpty("its live push ran out unacknowledged");
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until + TimeSpan.FromDays(1)))).Should().BeEmpty("the mark has no end");

        // A newer record behind it is not held up: the live push takes it.
        await store.AddAsync(Message("b"), now: until);
        (await store.LeaseIdsAsync(LiveTake(owner: "other", now: until))).Should().Equal("b");

        var picked = (await LeaseAsync(store, PickupTake(now: until))).Should().ContainSingle().Subject;
        picked.Id.Should().Be("a");
        picked.LivePushReturned.Should().BeTrue("the lease that ran out left its mark on the record when it was replaced");
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
    [InlineData("released by its lease id")]
    [InlineData("released by its lease id after it ran out")]
    [InlineData("passed over")]
    public async Task A_live_push_the_mediator_gives_back_itself_is_not_marked_and_may_be_pushed_live_again(string how)
    {
        // ReleaseAsync and PassOverAsync are how the mediator gives back a push the connection did
        // not take (no room for it, or too large): it never reached the recipient.
        var store = NewStore();
        await store.AddAsync(Alice, "a");
        var until = T0 + LeaseFor;
        (await store.LeaseIdsAsync(LiveTake(until: until, leaseId: "push-1"))).Should().Equal("a");

        var now = T0;
        switch (how)
        {
            case "released by its lease id":
                (await store.ReleaseAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
                break;
            case "released by its lease id after it ran out":
                now = until + Tick;
                (await store.ReleaseAsync(Alice, new[] { "a" }, "push-1", None)).Should().Be(1);
                break;
            default:
                (await store.PassOverAsync(Alice, new[] { "a" }, "push-1", T0 + Tick, None)).Should().Be(1);
                now = T0 + Tick;
                break;
        }

        var again = (await LeaseAsync(store, LiveTake(owner: "socket", now: now, leaseId: "push-2"))).Should().ContainSingle("a later live push takes it again").Subject;
        again.Id.Should().Be("a");
        again.LivePushReturned.Should().BeFalse();
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
}
