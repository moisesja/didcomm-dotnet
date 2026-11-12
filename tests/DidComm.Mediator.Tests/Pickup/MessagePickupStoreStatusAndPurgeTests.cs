using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// What a grant's status reports and what the three purges remove, as
/// <see cref="InMemoryMessageStore"/> keeps the contract (FR-PICK-02, -04; FR-ROUTE-09; JC-7 and the
/// owner decision on removed DIDs and revoked grants): the status is over due, unexpired records
/// with leased ones included, and a purge touches exactly what it names.
/// </summary>
public sealed class MessagePickupStoreStatusAndPurgeTests
{
    // ---- status (JC-7) --------------------------------------------------------------------

    [Fact]
    public async Task Status_counts_what_is_due_and_unexpired_leased_records_included()
    {
        var now = T0.AddMinutes(10);
        var store = NewStore();
        await store.AddAsync(Message("leased", size: 2, receivedAt: T0.AddMinutes(2)));
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("leased");
        await store.AddAsync(Message("waiting", size: 1, receivedAt: T0.AddMinutes(1)));
        await store.AddAsync(Message("pushed", size: 4, receivedAt: T0.AddMinutes(3)), Lease("socket", Far));
        await store.AddAsync(Message("not-due", size: 8, receivedAt: T0.AddMinutes(4), notBefore: now + Tick));
        await store.AddAsync(Message("due-now", size: 16, receivedAt: T0.AddMinutes(5), notBefore: now));
        await store.AddAsync(Message("expired", size: 32, receivedAt: T0, expiresAt: now));
        await store.AddAsync(Message("expiring-next", size: 64, receivedAt: T0.AddMinutes(6), expiresAt: now + Tick));

        // Due and unexpired at `now`: waiting, leased, pushed, due-now, expiring-next.
        (await store.GetStatusAsync(Alice, null, now, None)).Should().Be(new QueueStatus(5, 1 + 2 + 4 + 16 + 64, T0.AddMinutes(1), T0.AddMinutes(6)));
        // The record that is not yet due is retained and counts against the bounds; it is only not reported.
        (store.MessageCount, store.TotalBytes).Should().Be((6, 1L + 2 + 4 + 8 + 16 + 64));

        // One tick on: "not-due" is due, "expiring-next" is expired.
        (await store.GetStatusAsync(Alice, null, now + Tick, None)).Should().Be(new QueueStatus(5, 1 + 2 + 4 + 8 + 16, T0.AddMinutes(1), T0.AddMinutes(5)));
        (store.MessageCount, store.TotalBytes).Should().Be((5, 1L + 2 + 4 + 8 + 16));
    }

    [Fact]
    public async Task Status_reports_the_oldest_and_newest_by_ReceivedAt_whatever_the_arrival_order()
    {
        var store = NewStore();
        await store.AddAsync(Message("m1", receivedAt: T0.AddSeconds(30)));
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(1, 10, T0.AddSeconds(30), T0.AddSeconds(30)));

        await store.AddAsync(Message("m2", receivedAt: T0.AddSeconds(10)));
        await store.AddAsync(Message("m3", receivedAt: T0.AddSeconds(50)));
        await store.AddAsync(Message("m4", receivedAt: T0.AddSeconds(20)));
        // Held but not yet due: it is not reported, so its ReceivedAt is not the oldest either.
        await store.AddAsync(Message("hidden", receivedAt: T0.AddSeconds(-100), notBefore: T0.AddHours(1)));

        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(4, 40, T0.AddSeconds(10), T0.AddSeconds(50)));
        (await store.LeaseIdsAsync(Take(until: T0))).Should().Equal(new[] { "m1", "m2", "m3", "m4" }, "selection is by arrival, the times are by ReceivedAt");

        (await store.AcknowledgeAsync(Alice, new[] { "m3" }, None)).Should().Be(1);
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(3, 30, T0.AddSeconds(10), T0.AddSeconds(30)));
        (await store.AcknowledgeAsync(Alice, new[] { "m2" }, None)).Should().Be(1);
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(2, 20, T0.AddSeconds(20), T0.AddSeconds(30)));
        (await store.GetStatusAsync(Alice, null, T0.AddHours(1), None)).Should().Be(new QueueStatus(3, 30, T0.AddSeconds(-100), T0.AddSeconds(30)));
    }

    [Fact]
    public async Task Status_with_a_recipient_filter_reports_that_recipient_only()
    {
        var store = NewStore();
        await store.AddAsync(Message("a", recipient: R1, size: 1, receivedAt: T0.AddSeconds(1)));
        await store.AddAsync(Message("b", recipient: R2, size: 2, receivedAt: T0.AddSeconds(2)));
        await store.AddAsync(Message("c", recipient: R1, size: 4, receivedAt: T0.AddSeconds(3)));
        await store.AddAsync(Message("d", recipient: R1, size: 8, receivedAt: T0.AddSeconds(4), notBefore: T0.AddHours(1)));
        await store.AddAsync(Message("x", Bob, recipient: R1, size: 16));

        (await store.GetStatusAsync(Alice, R1, T0, None)).Should().Be(new QueueStatus(2, 5, T0.AddSeconds(1), T0.AddSeconds(3)));
        (await store.GetStatusAsync(Alice, R2, T0, None)).Should().Be(new QueueStatus(1, 2, T0.AddSeconds(2), T0.AddSeconds(2)));
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(3, 7, T0.AddSeconds(1), T0.AddSeconds(3)));
        (await store.GetStatusAsync(Alice, "did:example:nobody", T0, None)).Should().Be(QueueStatus.Empty);
        (await store.GetStatusAsync(Alice, R1.ToUpperInvariant(), T0, None)).Should().Be(QueueStatus.Empty, "recipient DIDs are compared ordinally");
        (await store.GetStatusAsync(Alice, Alice, T0, None)).Should().Be(QueueStatus.Empty, "the grant's own DID is a recipient DID like any other: nothing was queued for it");
    }

    [Fact]
    public async Task Status_of_nothing_is_the_empty_status()
    {
        QueueStatus.Empty.Should().Be(new QueueStatus(0, 0, null, null));
        var store = NewStore();
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(QueueStatus.Empty, "nobody ever queued for this grant");

        await store.AddAsync(Message("not-due", notBefore: T0.AddHours(1)));
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(QueueStatus.Empty, "the only record is not yet due");
        store.MessageCount.Should().Be(1);

        await store.AddAsync(Message("expiring", expiresAt: T0.AddMinutes(1)));
        (await store.GetStatusAsync(Alice, null, T0.AddMinutes(1), None)).Should().Be(QueueStatus.Empty, "the only due record is expired");

        (await store.AcknowledgeAsync(Alice, new[] { "not-due" }, None)).Should().Be(1);
        (await store.GetStatusAsync(Alice, null, T0.AddHours(2), None)).Should().Be(QueueStatus.Empty, "everything was acknowledged or expired");
        (store.MessageCount, store.TotalBytes).Should().Be((0, 0L));
    }

    [Fact]
    public async Task Reading_the_status_changes_nothing()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(Take(max: 1, owner: "socket"))).Should().Equal("a");

        var first = await store.GetStatusAsync(Alice, null, T0, None);
        for (var i = 0; i < 3; i++)
            (await store.GetStatusAsync(Alice, i % 2 == 0 ? null : Alice, T0, None)).Should().Be(first);

        (store.MessageCount, store.TotalBytes).Should().Be((2, 20L));
        (await store.LeaseIdsAsync(Take())).Should().Equal(new[] { "b" }, "the status neither took nor ended a lease");
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);
    }

    // ---- purge by recipient DID -----------------------------------------------------------

    [Fact]
    public async Task PurgeRecipient_removes_that_recipients_records_in_any_state_and_nothing_else()
    {
        var store = NewStore();
        await store.AddAsync(Message("r1-leased", recipient: R1, size: 1));
        await store.AddAsync(Message("r2-leased", recipient: R2, size: 2));
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("r1-leased", "r2-leased");
        await store.AddAsync(Message("r1-waiting", recipient: R1, size: 4));
        await store.AddAsync(Message("r1-not-due", recipient: R1, size: 8, notBefore: T0.AddDays(1)));
        await store.AddAsync(Message("r2-waiting", recipient: R2, size: 16));
        await store.AddAsync(Message("r1-pushed", recipient: R1, size: 32), Lease("socket", Far));
        await store.AddAsync(Message("bobs-r1", Bob, recipient: R1, size: 64));

        (await store.PurgeRecipientAsync(Alice, R1, None)).Should().Be(4);

        (store.MessageCount, store.TotalBytes).Should().Be((3, 2L + 16 + 64));
        (await store.GetStatusAsync(Alice, R1, T0.AddDays(2), None)).Should().Be(QueueStatus.Empty);
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(2, 18, T0, T0));
        (await store.GetStatusAsync(Bob, R1, T0, None)).MessageCount.Should().Be(1, "the same DID under another grant is not this grant's to purge");
        (await store.LeaseIdsAsync(Take())).Should().Equal(new[] { "r2-waiting" }, "the lease on the other recipient's record is intact");
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1, "of the owner's three leases only the other recipient's is left");

        (await store.PurgeRecipientAsync(Alice, R1, None)).Should().Be(0);
        (await store.PurgeRecipientAsync(Alice, "did:example:nobody", None)).Should().Be(0);
        (await store.PurgeRecipientAsync("did:example:nobody", R1, None)).Should().Be(0);
        (await store.PurgeRecipientAsync(Alice, R2.ToUpperInvariant(), None)).Should().Be(0, "recipient DIDs are compared ordinally");
        store.MessageCount.Should().Be(3);
    }

    // ---- purge by grant -------------------------------------------------------------------

    [Fact]
    public async Task PurgeGrant_removes_everything_of_that_grant_and_nothing_else()
    {
        var store = NewStore();
        await store.AddAsync(Message("a-leased", Alice, R1, size: 1));
        await store.AddAsync(Message("b-leased", Bob, size: 2));
        (await store.LeaseIdsAsync(Take(Alice, owner: "socket"))).Should().Equal("a-leased");
        (await store.LeaseIdsAsync(Take(Bob, owner: "socket"))).Should().Equal("b-leased");
        await store.AddAsync(Message("a-not-due", Alice, R2, size: 4, notBefore: T0.AddDays(1)));
        await store.AddAsync(Message("a-waiting", Alice, size: 8));
        await store.AddAsync(Message("b-waiting", Bob, size: 16));

        (await store.PurgeGrantAsync(Alice, None)).Should().Be(3);

        (store.MessageCount, store.TotalBytes).Should().Be((2, 18L));
        (await store.GetStatusAsync(Alice, null, T0.AddDays(2), None)).Should().Be(QueueStatus.Empty);
        (await store.LeaseIdsAsync(Take(Alice, now: T0.AddDays(2)))).Should().BeEmpty();
        (await store.LeaseIdsAsync(Take(Bob))).Should().Equal(new[] { "b-waiting" }, "Bob's lease is intact");
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1, "the owner is left with its lease in the other grant");

        (await store.PurgeGrantAsync(Alice, None)).Should().Be(0);
        (await store.PurgeGrantAsync("did:example:nobody", None)).Should().Be(0);
        (await store.PurgeGrantAsync(Bob.ToUpperInvariant(), None)).Should().Be(0, "grant keys are compared ordinally");
        store.MessageCount.Should().Be(2);
    }

    // ---- purge of expired records ---------------------------------------------------------

    [Fact]
    public async Task PurgeExpired_removes_exactly_the_expired_records_of_every_grant()
    {
        var at = T0.AddMinutes(10);
        var store = NewStore();
        await store.AddAsync(Message("a-expired-leased", Alice, size: 1, expiresAt: at.AddMinutes(-1)));
        (await store.LeaseIdsAsync(Take(Alice, owner: "socket"))).Should().Equal("a-expired-leased");
        await store.AddAsync(Message("a-expired", Alice, size: 2, expiresAt: at));
        await store.AddAsync(Message("a-live", Alice, size: 4, expiresAt: at + Tick));
        await store.AddAsync(Message("a-forever", Alice, size: 8));
        await store.AddAsync(Message("b-expired", Bob, size: 16, expiresAt: at - Tick));
        await store.AddAsync(Message("c-forever", Carol, size: 32));
        (await store.LeaseIdsAsync(Take(Carol, owner: "socket"))).Should().Equal("c-forever");

        (await store.PurgeExpiredAsync(at.AddMinutes(-1) - Tick, None)).Should().Be(0, "nothing is expired yet");
        store.MessageCount.Should().Be(6);

        (await store.PurgeExpiredAsync(at, None)).Should().Be(3);

        (store.MessageCount, store.TotalBytes).Should().Be((3, 4L + 8 + 32));
        (await store.PurgeExpiredAsync(at, None)).Should().Be(0);
        (await store.GetStatusAsync(Bob, null, T0, None)).Should().Be(QueueStatus.Empty);
        (await store.LeaseIdsAsync(Take(Alice, now: at, until: at))).Should().Equal("a-live", "a-forever");
        (await store.LeaseIdsAsync(Take(Carol, now: at))).Should().BeEmpty("the lease of a record that did not expire is intact");
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1, "the expired record took its lease with it");

        (await store.PurgeExpiredAsync(at + Tick, None)).Should().Be(1);
        (await store.PurgeExpiredAsync(DateTimeOffset.MaxValue, None)).Should().Be(0, "records without an expiry never expire");
        (store.MessageCount, store.TotalBytes).Should().Be((2, 40L));
    }

    [Fact]
    public async Task Expiry_is_found_whatever_order_the_expiries_arrived_and_left_in()
    {
        // The store may keep the earliest expiry of a grant as a shortcut; these are the orders in
        // which a stale or missing shortcut would let an expired record through or hide one.
        var store = NewStore();
        await store.AddAsync(Message("a", expiresAt: T0.AddSeconds(30)));
        await store.AddAsync(Message("b", expiresAt: T0.AddSeconds(10)));
        await store.AddAsync(Message("c", expiresAt: T0.AddSeconds(20)));
        await store.AddAsync(Message("forever"));

        // The earliest expiry leaves by acknowledgement.
        (await store.AcknowledgeAsync(Alice, new[] { "b" }, None)).Should().Be(1);
        (await store.PurgeExpiredAsync(T0.AddSeconds(10), None)).Should().Be(0);
        (await store.LeaseIdsAsync(Take(now: T0.AddSeconds(20) - Tick, until: T0))).Should().Equal("a", "c", "forever");
        (await store.LeaseIdsAsync(Take(now: T0.AddSeconds(20), until: T0))).Should().Equal("a", "forever");

        // A record arrives whose expiry is earlier than everything left.
        await store.AddAsync(Message("d", expiresAt: T0.AddSeconds(25)), now: T0.AddSeconds(20));
        (await store.GetStatusAsync(Alice, null, T0.AddSeconds(25) - Tick, None)).MessageCount.Should().Be(3);
        (await store.GetStatusAsync(Alice, null, T0.AddSeconds(25), None)).MessageCount.Should().Be(2);

        // A record arrives already expired: it is admitted (nothing is judged but the bounds) and never delivered.
        await store.AddAsync(Message("e", expiresAt: T0.AddSeconds(1)), now: T0.AddSeconds(25));
        (await store.LeaseIdsAsync(Take(now: T0.AddSeconds(25), until: T0))).Should().Equal("a", "forever");

        (await store.PurgeExpiredAsync(T0.AddSeconds(30), None)).Should().Be(1);
        (await store.LeaseIdsAsync(Take(now: T0.AddDays(1000)))).Should().Equal("forever");
        store.MessageCount.Should().Be(1);
    }

    public enum Touch
    {
        Enqueue,
        Lease,
        Status,
    }

    [Theory]
    [InlineData(Touch.Enqueue)]
    [InlineData(Touch.Lease)]
    [InlineData(Touch.Status)]
    public async Task Expiry_is_applied_when_the_grant_is_next_touched(Touch touch)
    {
        var at = T0.AddMinutes(5);
        var store = NewStore();
        await store.AddAsync(Message("expiring", size: 100, expiresAt: at));
        await store.AddAsync(Message("keeper", size: 1));
        (store.MessageCount, store.TotalBytes).Should().Be((2, 101L));

        switch (touch)
        {
            case Touch.Enqueue:
                await store.AddAsync(Message("new", size: 2), now: at);
                (store.MessageCount, store.TotalBytes).Should().Be((2, 3L));
                break;
            case Touch.Lease:
                (await store.LeaseIdsAsync(Take(now: at))).Should().Equal("keeper");
                (store.MessageCount, store.TotalBytes).Should().Be((1, 1L));
                break;
            case Touch.Status:
                (await store.GetStatusAsync(Alice, null, at, None)).Should().Be(new QueueStatus(1, 1, T0, T0));
                (store.MessageCount, store.TotalBytes).Should().Be((1, 1L));
                break;
        }

        (await store.PurgeExpiredAsync(at, None)).Should().Be(0, "the touch already removed it");
    }

    // ---- a grant that was emptied is clean ------------------------------------------------

    public enum Emptied
    {
        ByAcknowledge,
        ByPurgeGrant,
        ByPurgeRecipient,
        ByPurgeExpired,
        ByExpiryOnLease,
        ByExpiryOnStatus,
        ByExpiryOnEnqueue,
    }

    [Theory]
    [InlineData(Emptied.ByAcknowledge)]
    [InlineData(Emptied.ByPurgeGrant)]
    [InlineData(Emptied.ByPurgeRecipient)]
    [InlineData(Emptied.ByPurgeExpired)]
    [InlineData(Emptied.ByExpiryOnLease)]
    [InlineData(Emptied.ByExpiryOnStatus)]
    [InlineData(Emptied.ByExpiryOnEnqueue)]
    public async Task A_grant_that_was_emptied_takes_new_records_as_if_it_had_never_existed(Emptied how)
    {
        // The grant is filled to all four bounds, under a lease, so anything left behind would show.
        var at = T0.AddMinutes(5);
        var store = NewStore(queueMessages: 2, queueBytes: 20, storeMessages: 2, storeBytes: 20);
        await store.AddAsync(Message("a", expiresAt: at));
        await store.AddAsync(Message("b", expiresAt: at));
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("a", "b");
        (await store.EnqueueAsync(Message("c", size: 0), null, T0, None)).Should().Be(EnqueueOutcome.GrantMessageLimitReached);

        switch (how)
        {
            case Emptied.ByAcknowledge:
                (await store.AcknowledgeAsync(Alice, new[] { "a", "b" }, None)).Should().Be(2);
                break;
            case Emptied.ByPurgeGrant:
                (await store.PurgeGrantAsync(Alice, None)).Should().Be(2);
                break;
            case Emptied.ByPurgeRecipient:
                (await store.PurgeRecipientAsync(Alice, Alice, None)).Should().Be(2);
                break;
            case Emptied.ByPurgeExpired:
                (await store.PurgeExpiredAsync(at, None)).Should().Be(2);
                break;
            case Emptied.ByExpiryOnLease:
                (await store.LeaseIdsAsync(Take(now: at))).Should().BeEmpty();
                break;
            case Emptied.ByExpiryOnStatus:
                (await store.GetStatusAsync(Alice, null, at, None)).Should().Be(QueueStatus.Empty);
                break;
            case Emptied.ByExpiryOnEnqueue:
                // An enqueue that is refused still applies the expiry first, and must leave the
                // grant it emptied on the way as clean as an admitted one would.
                (await store.EnqueueAsync(Message("too-big", size: 21), null, at, None)).Should().Be(EnqueueOutcome.GrantByteLimitReached);
                break;
        }

        (await store.HeldAsync(now: at)).Should().Be((0, 0L, QueueStatus.Empty));
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(0, "no lease outlives its record");

        await store.AddAsync(Message("a"), now: at);
        await store.AddAsync(Message("b"), now: at);
        (await store.EnqueueAsync(Message("c", size: 0), null, at, None)).Should().Be(EnqueueOutcome.GrantMessageLimitReached);
        (await store.LeaseIdsAsync(Take(now: at.AddYears(1)))).Should().Equal(new[] { "a", "b" }, "the new records carry no expiry and no lease of their predecessors");
    }
}
