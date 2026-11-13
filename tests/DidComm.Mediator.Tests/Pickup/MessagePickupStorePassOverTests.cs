using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="IMessageStore.PassOverAsync"/> (PR #107 review, finding 1): a record the taker could
/// not deliver for now is set aside by a mark of its own — no owner, ended only by time — so that
/// later requests get past it whatever happened to leases and connections in between. The record
/// stays stored, counted and in position, and remembers how often it was passed over.
/// </summary>
public sealed class MessagePickupStorePassOverTests
{
    private static MessageLeaseRequest Request(MessageLease lease, DateTimeOffset? now = null, int max = int.MaxValue)
        => new(Alice, null, max, long.MaxValue, now ?? T0, lease);

    private static MessageLease NewLease(string? owner = null, DateTimeOffset? until = null) => new(owner, until ?? Far);

    [Fact]
    public async Task A_passed_over_record_is_not_handed_out_before_the_instant_given_and_is_at_it()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");
        var lease = NewLease();
        (await store.LeaseIdsAsync(Request(lease, max: 1))).Should().Equal("a");
        var until = T0.AddSeconds(60);

        (await store.PassOverAsync(Alice, new[] { "a" }, lease.Id, until, None)).Should().Be(1);

        // Not under a lease any more, yet passed over: what is behind it is handed out, it is not.
        (await store.LeaseIdsAsync(Request(NewLease(until: T0.AddTicks(1))))).Should().Equal("b", "c");
        (await store.LeaseIdsAsync(Request(NewLease(until: until), until - Tick))).Should().Equal("b", "c");
        // At the instant it is due again — first in line, where it always was.
        (await store.LeaseIdsAsync(Request(NewLease(), until))).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task The_record_remembers_how_often_it_was_passed_over()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a");

        for (var n = 1; n <= 3; n++)
        {
            var at = T0.AddHours(n);
            var lease = NewLease(until: at.AddMinutes(1));
            var taken = (await store.LeaseAsync(Request(lease, at), None)).Should().ContainSingle().Subject;
            taken.PassOvers.Should().Be(n - 1);
            (await store.PassOverAsync(Alice, new[] { "a" }, lease.Id, at.AddMinutes(30), None)).Should().Be(1);
        }

        var last = NewLease();
        (await store.LeaseAsync(Request(last, T0.AddDays(1)), None)).Should().ContainSingle().Which.PassOvers.Should().Be(3);
    }

    [Fact]
    public async Task Only_the_lease_named_is_given_up_and_only_records_still_under_it_are_passed_over()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        await store.AddAsync(Bob, "a");
        var mine = NewLease(until: T0.AddSeconds(60));
        (await store.LeaseIdsAsync(Request(mine, max: 1))).Should().Equal("a");

        // Wrong lease id, a record that is not leased, an unknown id, another grant: nothing happens.
        (await store.PassOverAsync(Alice, new[] { "a" }, "another-lease", Far, None)).Should().Be(0);
        (await store.PassOverAsync(Alice, new[] { "b", "nobody" }, mine.Id, Far, None)).Should().Be(0);
        (await store.PassOverAsync(Bob, new[] { "a" }, mine.Id, Far, None)).Should().Be(0);
        (await store.PassOverAsync("did:example:nobody", new[] { "a" }, mine.Id, Far, None)).Should().Be(0);
        (await store.LeaseIdsAsync(Request(NewLease(until: T0.AddTicks(1))))).Should().Equal("b");

        // The first lease runs out and someone else takes the record: a late pass-over under the
        // first lease must not set aside what the second taker is delivering.
        var later = T0.AddSeconds(60);
        var theirs = NewLease(until: later.AddSeconds(60));
        (await store.LeaseIdsAsync(Request(theirs, later, max: 1))).Should().Equal("a");
        (await store.PassOverAsync(Alice, new[] { "a" }, mine.Id, Far, None)).Should().Be(0);
        (await store.ReleaseAsync(Alice, new[] { "a" }, theirs.Id, None)).Should().Be(1, "the second lease was still in place");
        (await store.LeaseAsync(Request(NewLease(), later), None)).Select(m => (m.Id, m.PassOvers)).Should().Equal(("a", 0), ("b", 0));

        // A list that names a record twice passes it over once.
        var third = NewLease();
        await store.AddAsync(Alice, "c");
        (await store.LeaseIdsAsync(Request(third, later))).Should().Equal("c");
        (await store.PassOverAsync(Alice, new[] { "c", "c" }, third.Id, Far, None)).Should().Be(1);
    }

    [Fact]
    public async Task The_mark_has_no_owner_nothing_but_time_ends_it()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        var lease = NewLease(owner: "socket");
        (await store.LeaseIdsAsync(Request(lease))).Should().Equal("a", "b");
        var until = T0.AddMinutes(10);
        (await store.PassOverAsync(Alice, new[] { "a" }, lease.Id, until, None)).Should().Be(1);

        // The connection closes; its other lease ends, the pass-over does not.
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1, "only b was still under the socket's lease");
        (await store.ReleaseAsync(Alice, new[] { "a" }, lease.Id, None)).Should().Be(0, "a is under no lease to give back");
        (await store.LeaseIdsAsync(Request(NewLease(until: T0.AddTicks(1)), T0.AddMinutes(5)))).Should().Equal("b");
        (await store.LeaseIdsAsync(Request(NewLease(), until))).Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_passed_over_record_stays_stored_and_counted_and_leaves_like_any_other()
    {
        var store = NewStore(queueMessages: 2, storeMessages: 2);
        await store.AddAsync(Message("a", size: 30, expiresAt: T0.AddHours(1)));
        await store.AddAsync(Message("b", size: 5));
        var lease = NewLease();
        (await store.LeaseIdsAsync(Request(lease, max: 1))).Should().Equal("a");
        (await store.PassOverAsync(Alice, new[] { "a" }, lease.Id, Far, None)).Should().Be(1);

        // Counted in status and against the bounds: nothing changed but who may be handed it.
        (store.MessageCount, store.TotalBytes).Should().Be((2, 35L));
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(2, 35, T0, T0));
        (await TryAddAsync(store, Message("c"))).Should().Be(EnqueueOutcome.GrantMessageLimitReached);

        // Its expiry still removes it, and so do an acknowledgement and the purges.
        (await TryAddAsync(store, Message("c"), now: T0.AddHours(1))).Should().Be(EnqueueOutcome.Enqueued);
        (await store.LeaseIdsAsync(Request(NewLease(until: T0.AddHours(1).AddTicks(1)), T0.AddHours(1)))).Should().Equal("b", "c");

        var again = NewStore();
        await again.AddAsync(Alice, "x", "y", "z");
        var taken = NewLease();
        (await again.LeaseIdsAsync(Request(taken))).Should().Equal("x", "y", "z");
        (await again.PassOverAsync(Alice, new[] { "x", "y", "z" }, taken.Id, Far, None)).Should().Be(3);
        (await again.AcknowledgeAsync(Alice, new[] { "x" }, None)).Should().Be(1);
        (await again.PurgeRecipientAsync(Alice, Alice, None)).Should().Be(2);
        again.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Arguments_are_checked_before_anything_changes()
    {
        var store = NewStore();
        var lease = NewLease();
        await store.AddAsync(Message("a"), lease);

        (await ((Func<Task>)(async () => await store.PassOverAsync(null!, new[] { "a" }, lease.Id, Far, None))).Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("grantPeerDid");
        (await ((Func<Task>)(async () => await store.PassOverAsync(Alice, null!, lease.Id, Far, None))).Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("messageIds");
        (await ((Func<Task>)(async () => await store.PassOverAsync(Alice, new[] { "a" }, null!, Far, None))).Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("leaseId");
        (await ((Func<Task>)(async () => await store.PassOverAsync(Alice, new[] { "a", null! }, lease.Id, Far, None))).Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("messageIds");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ((Func<Task>)(async () => await store.PassOverAsync(Alice, new[] { "a" }, lease.Id, Far, cancelled.Token))).Should().ThrowAsync<OperationCanceledException>();

        // Still under its lease, never passed over.
        (await store.ReleaseAsync(Alice, new[] { "a" }, lease.Id, None)).Should().Be(1);
        (await store.LeaseAsync(Request(NewLease()), None)).Should().ContainSingle().Which.PassOvers.Should().Be(0);
    }

    private static async Task<EnqueueOutcome> TryAddAsync(InMemoryMessageStore store, QueuedMessage message, DateTimeOffset? now = null)
        => await store.EnqueueAsync(message, null, now ?? T0, None);
}
