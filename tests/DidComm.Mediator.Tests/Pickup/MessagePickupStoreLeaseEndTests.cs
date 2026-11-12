using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// How a lease ends and how a record leaves, as <see cref="InMemoryMessageStore"/> keeps the
/// contract (FR-PICK-04, -08; JC-4, JC-5, JC-6): a release — by ids or by owner — is a mark taken
/// off a record that stays where it is, and an acknowledgement removes exactly the caller's own
/// records, whatever their state.
/// </summary>
public sealed class MessagePickupStoreLeaseEndTests
{
    // ---- release by ids -------------------------------------------------------------------

    [Fact]
    public async Task A_released_record_is_deliverable_at_once_in_its_original_position()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c", "d", "e");
        (await store.LeaseIdsAsync(Take(max: 3))).Should().Equal("a", "b", "c");

        (await store.ReleaseAsync(Alice, new[] { "b" }, None)).Should().Be(1);

        // Same instant, no waiting: "b" comes back ahead of the records that arrived after it.
        (await store.LeaseIdsAsync(Take())).Should().Equal("b", "d", "e");
    }

    [Fact]
    public async Task Release_by_ids_ends_exactly_the_leases_named_and_reports_how_many()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c", "d");
        (await store.LeaseIdsAsync(Take(max: 3, owner: "socket"))).Should().Equal("a", "b", "c");

        (await store.ReleaseAsync(Alice, new[] { "c", "a" }, None)).Should().Be(2);

        (await store.LeaseIdsAsync(Take())).Should().Equal(new[] { "a", "c", "d" }, "\"b\" was not named and is still leased");
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1, "the owner is left with the one lease that was not released by id");
    }

    [Fact]
    public async Task Release_ignores_ids_the_grant_does_not_hold()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a");
        await store.AddAsync(Bob, "b", "shared-id");
        await store.AddAsync(Alice, "shared-id");
        (await store.LeaseIdsAsync(Take(Alice))).Should().Equal("a", "shared-id");
        (await store.LeaseIdsAsync(Take(Bob))).Should().Equal("b", "shared-id");

        (await store.ReleaseAsync(Alice, new[] { "no-such-id", "b" }, None)).Should().Be(0);
        (await store.ReleaseAsync("did:example:nobody", new[] { "a", "b" }, None)).Should().Be(0);
        (await store.ReleaseAsync(Alice, Array.Empty<string>(), None)).Should().Be(0);
        (await store.LeaseIdsAsync(Take(Alice))).Should().BeEmpty();
        (await store.LeaseIdsAsync(Take(Bob))).Should().BeEmpty("an id under another grant is a plain miss: Bob's leases are untouched");

        // An id both grants hold: only the caller's own record is released.
        (await store.ReleaseAsync(Alice, new[] { "shared-id" }, None)).Should().Be(1);
        (await store.LeaseIdsAsync(Take(Bob))).Should().BeEmpty();
        (await store.LeaseIdsAsync(Take(Alice))).Should().Equal("shared-id");
    }

    [Fact]
    public async Task Release_leaves_a_record_that_is_not_leased_as_it_is()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");
        (await store.LeaseIdsAsync(Take(max: 1))).Should().Equal("a");

        (await store.ReleaseAsync(Alice, new[] { "b", "c" }, None)).Should().Be(0);

        (await store.LeaseIdsAsync(Take())).Should().Equal(new[] { "b", "c" }, "they kept their place and \"a\" kept its lease");
    }

    [Fact]
    public async Task Release_counts_an_id_given_twice_once()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(Take())).Should().Equal("a", "b");

        (await store.ReleaseAsync(Alice, new[] { "a", "a", "b", "a", "b" }, None)).Should().Be(2);
        (await store.ReleaseAsync(Alice, new[] { "a", "b" }, None)).Should().Be(0);

        (await store.LeaseIdsAsync(Take())).Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_record_whose_lease_ran_out_stays_deliverable_whether_or_not_it_is_also_released()
    {
        var leaseEnds = T0.AddSeconds(60);
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(Take(owner: "socket", until: leaseEnds))).Should().Equal("a", "b");

        // The store takes no instant here, so the count for a lapsed lease is not asserted: only
        // that releasing it does no harm.
        await store.ReleaseAsync(Alice, new[] { "a" }, None);
        await store.ReleaseLeasesAsync("socket", None);

        (await store.LeaseIdsAsync(Take(now: leaseEnds))).Should().Equal("a", "b");
        store.MessageCount.Should().Be(2);
    }

    // ---- release by owner -----------------------------------------------------------------

    [Fact]
    public async Task Release_by_owner_ends_only_that_owners_leases_across_all_grants()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a1", "a2", "a3");
        await store.AddAsync(Bob, "b1", "b2");
        await store.AddAsync(Carol, "c1");
        (await store.LeaseIdsAsync(Take(Alice, max: 2, owner: "socket-x"))).Should().Equal("a1", "a2");
        (await store.LeaseIdsAsync(Take(Alice, owner: "socket-y"))).Should().Equal("a3");
        (await store.LeaseIdsAsync(Take(Bob, max: 1, owner: "socket-x"))).Should().Equal("b1");
        (await store.LeaseIdsAsync(Take(Bob, owner: null))).Should().Equal("b2");
        (await store.LeaseIdsAsync(Take(Carol, owner: "socket-y"))).Should().Equal("c1");
        var before = (store.MessageCount, store.TotalBytes);

        (await store.ReleaseLeasesAsync("socket-x", None)).Should().Be(3);

        (await store.LeaseIdsAsync(Take(Alice))).Should().Equal(new[] { "a1", "a2" }, "socket-y's lease on a3 is not socket-x's to end");
        (await store.LeaseIdsAsync(Take(Bob))).Should().Equal(new[] { "b1" }, "a lease without an owner ends by time only");
        (await store.LeaseIdsAsync(Take(Carol))).Should().BeEmpty();
        (store.MessageCount, store.TotalBytes).Should().Be(before);

        (await store.ReleaseLeasesAsync("socket-x", None)).Should().Be(0, "its leases are gone and the records were leased again without an owner");
        (await store.ReleaseLeasesAsync("socket-y", None)).Should().Be(2);
        (await store.LeaseIdsAsync(Take(Alice))).Should().Equal("a3");
        (await store.LeaseIdsAsync(Take(Carol))).Should().Equal("c1");
    }

    [Fact]
    public async Task Release_by_owner_puts_each_record_back_in_its_original_position()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c", "d", "e");
        (await store.LeaseIdsAsync(Take(max: 1, owner: "other"))).Should().Equal("a");
        (await store.LeaseIdsAsync(Take(max: 3, owner: "socket"))).Should().Equal("b", "c", "d");

        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(3);

        (await store.LeaseIdsAsync(Take())).Should().Equal("b", "c", "d", "e");
    }

    [Fact]
    public async Task An_owner_with_no_leases_releases_nothing()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");
        (await store.ReleaseLeasesAsync("never-seen", None)).Should().Be(0);

        // Every way an owner's leases can already be gone: released by id, acknowledged, purged.
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("a", "b", "c");
        (await store.ReleaseAsync(Alice, new[] { "a" }, None)).Should().Be(1);
        (await store.AcknowledgeAsync(Alice, new[] { "b" }, None)).Should().Be(1);
        (await store.PurgeRecipientAsync(Alice, Alice, None)).Should().Be(2);
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(0);

        (await store.ReleaseLeasesAsync(string.Empty, None)).Should().Be(0, "no lease was taken under the empty token, and a lease without an owner is not a lease of that owner");
        store.MessageCount.Should().Be(0);
    }

    [Theory]
    [InlineData("second-socket")]
    [InlineData(null)]
    public async Task A_record_leased_again_after_its_first_lease_ran_out_is_not_released_by_the_first_owner(string? secondOwner)
    {
        var firstEnds = T0.AddSeconds(10);
        var store = NewStore();
        await store.AddAsync(Alice, "m");
        (await store.LeaseIdsAsync(Take(owner: "first-socket", until: firstEnds))).Should().Equal("m");
        (await store.LeaseIdsAsync(Take(now: firstEnds, owner: secondOwner, until: Far))).Should().Equal("m");

        // The first connection closes late: its lease is history, the record is somebody else's now.
        (await store.ReleaseLeasesAsync("first-socket", None)).Should().Be(0);

        (await store.LeaseIdsAsync(Take(now: firstEnds + Tick))).Should().BeEmpty("the second lease still holds");
        if (secondOwner is not null)
        {
            (await store.ReleaseLeasesAsync(secondOwner, None)).Should().Be(1);
            (await store.LeaseIdsAsync(Take(now: firstEnds + Tick))).Should().Equal("m");
        }
    }

    [Fact]
    public async Task Owner_tokens_are_compared_ordinally()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "m");
        (await store.LeaseIdsAsync(Take(owner: "Socket-1"))).Should().Equal("m");

        (await store.ReleaseLeasesAsync("socket-1", None)).Should().Be(0);
        (await store.ReleaseLeasesAsync("Socket-1 ", None)).Should().Be(0);
        (await store.LeaseIdsAsync(Take())).Should().BeEmpty();
        (await store.ReleaseLeasesAsync("Socket-1", None)).Should().Be(1);
    }

    // ---- a lease is a mark, never a move (FR-PICK-08) -------------------------------------

    [Fact]
    public async Task Leasing_and_releasing_never_change_what_the_store_holds()
    {
        var leaseEnds = T0.AddSeconds(60);
        var store = NewStore();
        for (var i = 0; i < 5; i++)
            await store.AddAsync(Message($"m{i}", size: 100 + i, receivedAt: T0.AddSeconds(i)));
        var held = await store.HeldAsync();
        held.Should().Be((5, 510L, new QueueStatus(5, 510, T0, T0.AddSeconds(4))));

        (await store.LeaseIdsAsync(Take(max: 3, owner: "socket", until: leaseEnds))).Should().HaveCount(3);
        (await store.HeldAsync()).Should().Be(held, "a lease is a mark on the record");

        (await store.ReleaseAsync(Alice, new[] { "m1" }, None)).Should().Be(1);
        (await store.HeldAsync()).Should().Be(held, "a release by id copies nothing");

        (await store.LeaseIdsAsync(Take(owner: "socket", until: leaseEnds))).Should().Equal("m1", "m3", "m4");
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(5);
        (await store.HeldAsync()).Should().Be(held, "a release by owner copies nothing");

        (await store.LeaseIdsAsync(Take(until: leaseEnds))).Should().HaveCount(5);
        (await store.LeaseIdsAsync(Take(now: leaseEnds))).Should().Equal("m0", "m1", "m2", "m3", "m4");
        store.MessageCount.Should().Be(held.Count, "a lease that lapsed and was taken again is still one record");
        store.TotalBytes.Should().Be(held.Bytes);
    }

    // ---- leased from birth: the live push (JC-6) ------------------------------------------

    [Fact]
    public async Task A_record_enqueued_with_a_lease_is_leased_from_birth_and_counted_like_any_other()
    {
        var store = NewStore();
        await store.AddAsync(Message("waiting", size: 1));
        await store.AddAsync(Message("pushed", size: 2), Lease("socket", T0.AddSeconds(60)));
        await store.AddAsync(Message("behind", size: 4));

        (await store.LeaseIdsAsync(Take(until: T0.AddSeconds(30)))).Should().Equal(new[] { "waiting", "behind" }, "the pushed record never was deliverable");
        (await store.HeldAsync()).Should().Be((3, 7L, new QueueStatus(3, 7, T0, T0)));
    }

    [Fact]
    public async Task A_record_enqueued_with_a_lease_becomes_deliverable_in_its_place_when_that_lease_ends()
    {
        var pushLeaseEnds = T0.AddSeconds(60);

        async Task<InMemoryMessageStore> PushedBetweenTwoAsync()
        {
            var store = NewStore();
            await store.AddAsync(Message("before"));
            await store.AddAsync(Message("pushed"), Lease("socket", pushLeaseEnds));
            await store.AddAsync(Message("after"));
            return store;
        }

        // By time.
        var byTime = await PushedBetweenTwoAsync();
        (await byTime.LeaseIdsAsync(Take(now: pushLeaseEnds - Tick, until: pushLeaseEnds - Tick))).Should().Equal("before", "after");
        (await byTime.LeaseIdsAsync(Take(now: pushLeaseEnds))).Should().Equal("before", "pushed", "after");

        // By its owner: the connection closed.
        var byOwner = await PushedBetweenTwoAsync();
        (await byOwner.ReleaseLeasesAsync("socket", None)).Should().Be(1);
        (await byOwner.LeaseIdsAsync(Take())).Should().Equal("before", "pushed", "after");

        // By id: the push was not taken.
        var byId = await PushedBetweenTwoAsync();
        (await byId.ReleaseAsync(Alice, new[] { "pushed" }, None)).Should().Be(1);
        (await byId.ReleaseLeasesAsync("socket", None)).Should().Be(0);
        (await byId.LeaseIdsAsync(Take())).Should().Equal("before", "pushed", "after");
    }

    // ---- acknowledgement ------------------------------------------------------------------

    public enum RecordState
    {
        Waiting,
        Leased,
        LeasedWithoutOwner,
        LeaseRanOut,
        LeasedFromBirth,
        NotYetDue,
    }

    [Theory]
    [InlineData(RecordState.Waiting)]
    [InlineData(RecordState.Leased)]
    [InlineData(RecordState.LeasedWithoutOwner)]
    [InlineData(RecordState.LeaseRanOut)]
    [InlineData(RecordState.LeasedFromBirth)]
    [InlineData(RecordState.NotYetDue)]
    public async Task Acknowledge_removes_a_record_in_any_state(RecordState state)
    {
        var later = T0.AddHours(2);
        var store = NewStore();
        await store.AddAsync(Message("bystander-1", size: 3));
        switch (state)
        {
            case RecordState.Waiting:
                await store.AddAsync(Message("target", size: 50));
                break;
            case RecordState.Leased:
                await store.AddAsync(Message("target", size: 50));
                (await store.LeaseIdsAsync(Take(recipient: Alice, owner: "socket"))).Should().Contain("target");
                (await store.ReleaseAsync(Alice, new[] { "bystander-1" }, None)).Should().Be(1);
                break;
            case RecordState.LeasedWithoutOwner:
                await store.AddAsync(Message("target", size: 50));
                (await store.LeaseIdsAsync(Take())).Should().Contain("target");
                (await store.ReleaseAsync(Alice, new[] { "bystander-1" }, None)).Should().Be(1);
                break;
            case RecordState.LeaseRanOut:
                await store.AddAsync(Message("target", size: 50));
                (await store.LeaseIdsAsync(Take(owner: "socket", until: T0.AddSeconds(1)))).Should().Contain("target");
                break;
            case RecordState.LeasedFromBirth:
                await store.AddAsync(Message("target", size: 50), Lease("socket", Far));
                break;
            case RecordState.NotYetDue:
                await store.AddAsync(Message("target", size: 50, notBefore: T0.AddHours(1)));
                break;
        }
        await store.AddAsync(Message("bystander-2", size: 4));

        (await store.AcknowledgeAsync(Alice, new[] { "target" }, None)).Should().Be(1);

        store.MessageCount.Should().Be(2);
        store.TotalBytes.Should().Be(7);
        (await store.GetStatusAsync(Alice, null, later, None)).MessageCount.Should().Be(2);
        (await store.ReleaseLeasesAsync("socket", None)).Should().BeLessThanOrEqualTo(1, "the acknowledged record's lease went with it; at most a bystander's is left");
        (await store.LeaseIdsAsync(Take(now: later))).Should().Equal("bystander-1", "bystander-2");
        (await store.AcknowledgeAsync(Alice, new[] { "target" }, None)).Should().Be(0, "it is gone");
    }

    [Fact]
    public async Task Acknowledge_ignores_unknown_duplicate_and_foreign_ids_and_counts_exactly_what_it_removed()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");
        await store.AddAsync(Bob, "x", "y");

        (await store.AcknowledgeAsync(Alice, new[] { "a", "a", "no-such-id", "x", "c", "a", "y", "c" }, None)).Should().Be(2);

        (await store.LeaseIdsAsync(Take(Alice))).Should().Equal("b");
        (await store.LeaseIdsAsync(Take(Bob))).Should().Equal(new[] { "x", "y" }, "a recipient cannot acknowledge another grant's ids");
        store.MessageCount.Should().Be(3);

        (await store.AcknowledgeAsync(Alice, Array.Empty<string>(), None)).Should().Be(0);
        (await store.AcknowledgeAsync("did:example:nobody", new[] { "b", "x" }, None)).Should().Be(0);
        (await store.AcknowledgeAsync(Alice, new[] { "B", "b " }, None)).Should().Be(0, "ids are compared ordinally");
        store.MessageCount.Should().Be(3);
    }

    [Fact]
    public async Task An_acknowledged_record_is_not_resurrected_by_a_release()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("a", "b");
        (await store.AcknowledgeAsync(Alice, new[] { "a" }, None)).Should().Be(1);

        (await store.ReleaseAsync(Alice, new[] { "a" }, None)).Should().Be(0);
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1, "only \"b\" still carried the owner's lease");

        (await store.LeaseIdsAsync(Take())).Should().Equal("b");
        (await store.HeldAsync()).Should().Be((1, 10L, new QueueStatus(1, 10, T0, T0)));
        (await store.AcknowledgeAsync(Alice, new[] { "a" }, None)).Should().Be(0);
    }

    [Fact]
    public async Task Acknowledging_the_last_record_of_a_grant_leaves_nothing_behind()
    {
        var store = NewStore(queueMessages: 2, queueBytes: 20);
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("a", "b");

        (await store.AcknowledgeAsync(Alice, new[] { "b", "a" }, None)).Should().Be(2);

        (await store.HeldAsync()).Should().Be((0, 0L, QueueStatus.Empty));
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(0);
        (await store.PurgeGrantAsync(Alice, None)).Should().Be(0);
        // The grant's whole budget is free again, ids included.
        await store.AddAsync(Alice, "a", "b");
        (await store.LeaseIdsAsync(Take())).Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_message_queued_for_several_recipients_is_one_record_each_acknowledged_separately()
    {
        // FR-PICK-05 as the store sees it: one enqueue per recipient, each with its own id. The
        // payload memory may be the same; the records are not.
        var payload = Bytes(64, "to-many");
        var store = NewStore();
        await store.AddAsync(new QueuedMessage("copy-1", Alice, R1, payload, T0));
        await store.AddAsync(new QueuedMessage("copy-2", Alice, R2, payload, T0));
        await store.AddAsync(new QueuedMessage("copy-3", Bob, Bob, payload, T0));
        (store.MessageCount, store.TotalBytes).Should().Be((3, 192L), "each copy counts in full against the bounds");

        (await store.LeaseIdsAsync(Take(Alice, owner: "socket"))).Should().Equal("copy-1", "copy-2");
        (await store.AcknowledgeAsync(Alice, new[] { "copy-1" }, None)).Should().Be(1);

        (store.MessageCount, store.TotalBytes).Should().Be((2, 128L));
        (await store.GetStatusAsync(Alice, R1, T0, None)).Should().Be(QueueStatus.Empty);
        (await store.GetStatusAsync(Alice, R2, T0, None)).Should().Be(new QueueStatus(1, 64, T0, T0), "the other recipient's copy is still held");
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1);
        var second = (await store.LeaseAsync(Take(Alice), None)).Should().ContainSingle().Subject;
        second.Id.Should().Be("copy-2");
        second.Payload.ToArray().Should().Equal(payload);
        (await store.LeaseAsync(Take(Bob), None)).Should().ContainSingle().Which.Payload.ToArray().Should().Equal(payload);
    }

    // ---- everything is confined to a grant (JC-4) -----------------------------------------

    [Fact]
    public async Task The_same_id_under_two_grants_is_two_independent_records()
    {
        var store = NewStore();
        await store.AddAsync(Message("m1", Alice, size: 10));
        await store.AddAsync(Message("m1", Bob, size: 20));

        var alices = (await store.LeaseAsync(Take(Alice), None)).Should().ContainSingle().Subject;
        alices.Payload.Length.Should().Be(10);
        var bobs = (await store.LeaseAsync(Take(Bob), None)).Should().ContainSingle("Alice's lease did not mark Bob's record").Subject;
        bobs.Payload.Length.Should().Be(20);

        (await store.ReleaseAsync(Alice, new[] { "m1" }, None)).Should().Be(1);
        (await store.LeaseIdsAsync(Take(Bob))).Should().BeEmpty("Bob's record is still leased");

        (await store.AcknowledgeAsync(Alice, new[] { "m1" }, None)).Should().Be(1);
        (await store.GetStatusAsync(Bob, null, T0, None)).Should().Be(new QueueStatus(1, 20, T0, T0));
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(QueueStatus.Empty);
        (store.MessageCount, store.TotalBytes).Should().Be((1, 20L));

        // Alice's key no longer reaches any record called "m1".
        (await store.AcknowledgeAsync(Alice, new[] { "m1" }, None)).Should().Be(0);
        (await store.ReleaseAsync(Alice, new[] { "m1" }, None)).Should().Be(0);
        (await store.PurgeGrantAsync(Alice, None)).Should().Be(0);
        (await store.AcknowledgeAsync(Bob, new[] { "m1" }, None)).Should().Be(1);
        store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task The_same_recipient_did_under_two_grants_stays_isolated_for_lease_status_acknowledge_and_purge()
    {
        const string shared = "did:example:shared-recipient";
        var store = NewStore();
        await store.AddAsync(Message("a1", Alice, shared, size: 1));
        await store.AddAsync(Message("a2", Alice, R1, size: 2));
        await store.AddAsync(Message("b1", Bob, shared, size: 4));
        await store.AddAsync(Message("b2", Bob, shared, size: 8));

        (await store.GetStatusAsync(Alice, shared, T0, None)).Should().Be(new QueueStatus(1, 1, T0, T0));
        (await store.GetStatusAsync(Bob, shared, T0, None)).Should().Be(new QueueStatus(2, 12, T0, T0));

        (await store.LeaseIdsAsync(Take(Alice, recipient: shared, owner: "alice-socket"))).Should().Equal("a1");
        (await store.LeaseIdsAsync(Take(Bob, recipient: shared, max: 1, owner: "bob-socket"))).Should().Equal("b1");

        (await store.AcknowledgeAsync(Alice, new[] { "b1", "b2" }, None)).Should().Be(0);
        (await store.ReleaseAsync(Alice, new[] { "b1" }, None)).Should().Be(0);

        (await store.PurgeRecipientAsync(Alice, shared, None)).Should().Be(1);
        (await store.GetStatusAsync(Bob, shared, T0, None)).Should().Be(new QueueStatus(2, 12, T0, T0), "purging the DID under Alice's grant leaves Bob's records for the same DID");
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(1, 2, T0, T0));
        (await store.ReleaseLeasesAsync("bob-socket", None)).Should().Be(1, "Bob's lease survived Alice's purge");

        (await store.PurgeRecipientAsync(Bob, shared, None)).Should().Be(2);
        (store.MessageCount, store.TotalBytes).Should().Be((1, 2L));
    }
}
