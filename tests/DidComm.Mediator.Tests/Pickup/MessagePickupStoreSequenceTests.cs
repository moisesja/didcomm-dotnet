using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The arrival sequence (#95, PR #109 review round 8), as <see cref="InMemoryMessageStore"/> keeps
/// the contract: the store numbers every record as it stores it, one counter for the whole store,
/// whatever the caller set; the record carries its number from then on; a lease can ask for the
/// records after a number; and every status reports the highest number assigned, read with the
/// counts. The mediator decides "arrived after" by it — never by two clock readings.
/// </summary>
public sealed class MessagePickupStoreSequenceTests
{
    [Fact]
    public async Task Every_record_gets_a_greater_sequence_than_every_record_stored_before_it_across_grants_whatever_the_caller_set()
    {
        var store = NewStore();
        await store.AddAsync(Message("a1") with { Sequence = 999 });
        await store.AddAsync(Message("b1", grant: Bob) with { Sequence = -5 });
        await store.AddAsync(Message("a2", receivedAt: T0.AddHours(-1)));

        var alice = await store.LeaseAsync(Take(), None);
        var bob = await store.LeaseAsync(Take(grant: Bob), None);

        alice.Select(r => (r.Id, r.Sequence)).Should().Equal(("a1", 1L), ("a2", 3L));
        bob.Select(r => (r.Id, r.Sequence)).Should().Equal(("b1", 2L));
    }

    [Fact]
    public async Task A_record_keeps_its_sequence_when_it_is_given_back_passed_over_or_its_lease_runs_out()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b");

        var first = (await store.LeaseAsync(Take(max: 1), None)).Should().ContainSingle().Subject;
        (await store.ReleaseAsync(Alice, new[] { "a" }, None)).Should().Be(1);
        var again = (await store.LeaseAsync(Take(max: 1, until: T0.AddSeconds(1)), None)).Should().ContainSingle().Subject;
        var afterLease = (await store.LeaseAsync(Take(max: 1, now: T0.AddSeconds(1)), None)).Should().ContainSingle().Subject;

        (first.Id, again.Id, afterLease.Id).Should().Be(("a", "a", "a"));
        new[] { first.Sequence, again.Sequence, afterLease.Sequence }.Should().AllBeEquivalentTo(1L);
    }

    [Fact]
    public async Task AfterSequence_selects_only_the_records_that_arrived_after_it_by_arrival_not_ReceivedAt_and_marks_no_others()
    {
        // What the mediator's pushes ask for: the live push the records after the live-on status, the
        // direct push the records after the last one it attempted.
        var store = NewStore();
        await store.AddAsync(Message("first", receivedAt: T0.AddMinutes(5)));
        await store.AddAsync(Message("second", receivedAt: T0.AddMinutes(-5)));
        await store.AddAsync(Message("third", receivedAt: T0));

        (await store.LeaseIdsAsync(Take() with { AfterSequence = 1 })).Should().Equal("second", "third");
        (await store.LeaseIdsAsync(Take())).Should().Equal(new[] { "first" }, "the filtered lease marked nothing else");
        (await store.LeaseIdsAsync(Take(grant: Alice) with { AfterSequence = 3 })).Should().BeEmpty();
    }

    [Fact]
    public async Task AfterSequence_combines_with_the_other_selections_and_never_skips_to_a_record_before_it()
    {
        var store = NewStore();
        await store.AddAsync(Message("old-r1", recipient: R1));
        await store.AddAsync(Message("not-due", recipient: R1, notBefore: T0.AddMinutes(1)));
        await store.AddAsync(Message("new-r2", recipient: R2));
        await store.AddAsync(Message("new-r1", recipient: R1));

        (await store.LeaseIdsAsync(Take(recipient: R1) with { AfterSequence = 1 })).Should().Equal(new[] { "new-r1" }, "the record that is not yet due is skipped, the older one is before the mark");
        (await store.LeaseIdsAsync(Take(now: T0.AddMinutes(1)) with { AfterSequence = 1 })).Should().Equal("not-due", "new-r2");
    }

    [Fact]
    public async Task Every_status_reports_the_highest_sequence_assigned_so_far_for_any_grant_even_an_empty_or_unknown_one()
    {
        var store = NewStore();
        (await store.GetStatusAsync(Alice, null, T0, None)).HighWaterSequence.Should().Be(0, "nothing was stored yet");

        await store.AddAsync(Alice, "a1", "a2");
        await store.AddAsync(Bob, "b1");

        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(2, 20, T0, T0) { HighWaterSequence = 3 });
        (await store.GetStatusAsync(Alice, R1, T0, None)).Should().Be(QueueStatus.Empty with { HighWaterSequence = 3 }, "a filtered status has the same mark");
        (await store.GetStatusAsync(Carol, null, T0, None)).Should().Be(QueueStatus.Empty with { HighWaterSequence = 3 }, "a grant with no queue reads the store's mark too");
    }

    [Fact]
    public async Task A_removal_never_lowers_the_mark_and_a_refused_enqueue_takes_no_sequence()
    {
        var store = NewStore(queueMessages: 2);
        await store.AddAsync(Alice, "a1");
        (await store.EnqueueAsync(Message("a1"), null, T0, None)).Should().Be(EnqueueOutcome.DuplicateId);
        await store.AddAsync(Alice, "a2");
        (await store.EnqueueAsync(Message("a3"), null, T0, None)).Should().Be(EnqueueOutcome.GrantMessageLimitReached);

        (await store.AcknowledgeAsync(Alice, new[] { "a2" }, None)).Should().Be(1);
        (await store.PurgeGrantAsync(Alice, None)).Should().Be(1);
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(QueueStatus.Empty with { HighWaterSequence = 2 });

        await store.AddAsync(Message("next"));
        (await store.LeaseAsync(Take(), None)).Should().ContainSingle().Which.Sequence.Should().Be(3, "a removal frees no number and a refusal used none");
    }
}
