using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// What <see cref="IMessageStore.LeaseAsync"/> selects, as <see cref="InMemoryMessageStore"/> keeps
/// the contract (FR-PICK-03/04, JC-5, JC-18): arrival order, the count and byte caps, not-before
/// and expiry judged at the caller's instant, and leases that hold exactly until the instant they
/// end.
/// </summary>
public sealed class MessagePickupStoreSelectionTests
{
    // ---- arrival order --------------------------------------------------------------------

    [Fact]
    public async Task Lease_hands_out_records_in_the_order_they_were_enqueued_not_in_ReceivedAt_order()
    {
        var store = NewStore();
        await store.AddAsync(Message("third-received", receivedAt: T0.AddSeconds(30)));
        await store.AddAsync(Message("first-received", receivedAt: T0.AddSeconds(10)));
        await store.AddAsync(Message("second-received", receivedAt: T0.AddSeconds(20)));

        (await store.LeaseIdsAsync(Take())).Should().Equal("third-received", "first-received", "second-received");
    }

    [Fact]
    public async Task A_leased_record_is_the_record_that_was_stored_with_its_payload_unchanged()
    {
        var store = NewStore();
        // A slice of a larger buffer: the store keeps the bytes it was given, no more and no fewer.
        var buffer = Bytes(300, "buffer");
        var stored = new QueuedMessage("m1", Alice, R1, buffer.AsMemory(17, 257), T0.AddSeconds(-5), T0.AddSeconds(-1), T0.AddHours(1));
        await store.AddAsync(stored);

        var leased = (await store.LeaseAsync(Take(), None)).Should().ContainSingle().Subject;

        leased.Id.Should().Be("m1");
        leased.GrantPeerDid.Should().Be(Alice);
        leased.RecipientDid.Should().Be(R1);
        leased.ReceivedAt.Should().Be(T0.AddSeconds(-5));
        leased.NotBefore.Should().Be(T0.AddSeconds(-1));
        leased.ExpiresAt.Should().Be(T0.AddHours(1));
        leased.Payload.ToArray().Should().Equal(buffer.AsSpan(17, 257).ToArray());
    }

    [Fact]
    public async Task A_recipient_filter_selects_that_recipients_records_in_arrival_order_and_marks_no_others()
    {
        var store = NewStore();
        await store.AddAsync(Message("a", recipient: R1));
        await store.AddAsync(Message("b", recipient: R2));
        await store.AddAsync(Message("c", recipient: R1));
        await store.AddAsync(Message("d", recipient: R2));
        await store.AddAsync(Message("e", recipient: R1));

        (await store.LeaseIdsAsync(Take(recipient: R1, max: 2))).Should().Equal("a", "c");
        (await store.LeaseIdsAsync(Take(recipient: R1))).Should().Equal("e");
        (await store.LeaseIdsAsync(Take(recipient: "did:example:nobody"))).Should().BeEmpty();
        (await store.LeaseIdsAsync(Take(recipient: R2.ToUpperInvariant()))).Should().BeEmpty("recipient DIDs are compared ordinally");

        // The filtered leases marked nothing of the other recipient.
        (await store.LeaseIdsAsync(Take(recipient: R2))).Should().Equal("b", "d");
        (await store.LeaseIdsAsync(Take())).Should().BeEmpty();
    }

    [Fact]
    public async Task Lease_never_crosses_grants()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a1", "a2");
        await store.AddAsync(Bob, "b1");

        var alices = await store.LeaseAsync(Take(Alice), None);
        alices.Select(m => m.Id).Should().Equal("a1", "a2");
        alices.Should().OnlyContain(m => m.GrantPeerDid == Alice);

        (await store.LeaseIdsAsync(Take("did:example:nobody"))).Should().BeEmpty();
        (await store.LeaseIdsAsync(Take(Alice.ToUpperInvariant()))).Should().BeEmpty("grant keys are compared ordinally");
        (await store.LeaseIdsAsync(Take(Bob))).Should().Equal(new[] { "b1" }, "leasing Alice's records marked none of Bob's");
    }

    // ---- the count cap --------------------------------------------------------------------

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(int.MaxValue, 5)]
    public async Task MaxMessages_caps_the_lease_and_what_was_not_taken_stays_deliverable(int maxMessages, int taken)
    {
        var ids = new[] { "a", "b", "c", "d", "e" };
        var store = NewStore();
        await store.AddAsync(Alice, ids);

        (await store.LeaseIdsAsync(Take(max: maxMessages))).Should().Equal(ids.Take(taken));

        // Nothing beyond the cap was marked: the rest is handed out by the next lease.
        (await store.LeaseIdsAsync(Take())).Should().Equal(ids.Skip(taken));
    }

    // ---- the byte budget (JC-18) ----------------------------------------------------------

    [Theory]
    [InlineData(-1L, 1)]            // the first deliverable record is taken whatever the budget
    [InlineData(0L, 1)]
    [InlineData(1L, 1)]
    [InlineData(9L, 1)]
    [InlineData(10L, 1)]
    [InlineData(29L, 1)]            // 10 + 20 would pass 29
    [InlineData(30L, 2)]            // total == budget is taken
    [InlineData(31L, 2)]
    [InlineData(59L, 2)]            // 10 + 20 + 30 would pass 59
    [InlineData(60L, 3)]
    [InlineData(64L, 3)]            // budget + 1 == 65 is not taken
    [InlineData(65L, 4)]
    [InlineData(66L, 4)]
    [InlineData(long.MaxValue, 4)]
    public async Task The_byte_budget_takes_records_while_their_total_stays_within_it(long budget, int taken)
    {
        var sizes = new (string Id, int Size)[] { ("ten", 10), ("twenty", 20), ("thirty", 30), ("five", 5) };
        var store = NewStore();
        foreach (var (id, size) in sizes)
            await store.AddAsync(Message(id, size: size));

        var leased = await store.LeaseAsync(Take(bytes: budget), None);

        leased.Select(m => m.Id).Should().Equal(sizes.Take(taken).Select(s => s.Id));
        (await store.LeaseIdsAsync(Take())).Should().Equal(sizes.Skip(taken).Select(s => s.Id));
    }

    [Fact]
    public async Task The_byte_budget_stops_at_the_first_record_that_does_not_fit_and_never_skips_to_a_smaller_one()
    {
        var store = NewStore();
        await store.AddAsync(Message("small", size: 10));
        await store.AddAsync(Message("big", size: 100));
        await store.AddAsync(Message("tiny", size: 1));

        // "tiny" would fit beside "small", but it is behind "big": the result is a prefix of what is deliverable.
        (await store.LeaseIdsAsync(Take(bytes: 11))).Should().Equal("small");
        // "big" is now the first deliverable record: taken whatever its size, and alone.
        (await store.LeaseIdsAsync(Take(bytes: 11))).Should().Equal("big");
        (await store.LeaseIdsAsync(Take(bytes: 11))).Should().Equal("tiny");
    }

    [Fact]
    public async Task The_first_deliverable_record_is_taken_whatever_its_size_even_when_it_is_not_the_first_record()
    {
        var store = NewStore();
        await store.AddAsync(Message("leased", recipient: R1, size: 1));
        await store.AddAsync(Message("not-due", recipient: R1, size: 1, notBefore: T0.AddHours(1)));
        await store.AddAsync(Message("other-recipient", recipient: R2, size: 1));
        await store.AddAsync(Message("huge", recipient: R1, size: 1000));
        await store.AddAsync(Message("after", recipient: R1, size: 1));
        (await store.LeaseIdsAsync(Take(recipient: R1, max: 1))).Should().Equal("leased");

        (await store.LeaseIdsAsync(Take(recipient: R1, bytes: 1))).Should().Equal("huge");
        (await store.LeaseIdsAsync(Take(recipient: R1, bytes: 1))).Should().Equal("after");
    }

    [Fact]
    public async Task The_caps_count_only_the_records_the_lease_can_take()
    {
        // Records of another recipient, not yet due, expired or leased are passed over: they use up
        // neither the count nor the bytes of the request.
        var store = NewStore();
        await store.AddAsync(Message("held", recipient: R1, size: 500));
        (await store.LeaseIdsAsync(Take(recipient: R1))).Should().Equal("held");
        await store.AddAsync(Message("a", recipient: R1, size: 10));
        await store.AddAsync(Message("other", recipient: R2, size: 500));
        await store.AddAsync(Message("b", recipient: R1, size: 10));
        await store.AddAsync(Message("delayed", recipient: R1, size: 500, notBefore: T0.AddHours(1)));
        await store.AddAsync(Message("stale", recipient: R1, size: 500, expiresAt: T0));
        await store.AddAsync(Message("c", recipient: R1, size: 10));
        await store.AddAsync(Message("d", recipient: R1, size: 10));

        (await store.LeaseIdsAsync(Take(recipient: R1, max: 3, bytes: 30))).Should().Equal("a", "b", "c");
        (await store.LeaseIdsAsync(Take(recipient: R1, max: 3, bytes: 30))).Should().Equal("d");
    }

    [Fact]
    public async Task Empty_payloads_cost_nothing_against_the_byte_budget()
    {
        var store = NewStore();
        await store.AddAsync(Message("ten", size: 10));
        await store.AddAsync(Message("empty-1", size: 0));
        await store.AddAsync(Message("empty-2", size: 0));
        await store.AddAsync(Message("one", size: 1));

        (await store.LeaseIdsAsync(Take(bytes: 10))).Should().Equal("ten", "empty-1", "empty-2");
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MinValue + 9)]
    public async Task A_byte_budget_at_the_bottom_of_the_range_still_means_only_the_first_deliverable_record(long budget)
    {
        // Every record after the first carries the total past a budget this low, so only the first
        // may be taken — "the first deliverable record is taken whatever its size", nothing more.
        // (Guards the subtraction that compares a size against what is left of the budget.)
        var store = NewStore();
        await store.AddAsync(Message("first", size: 10));
        await store.AddAsync(Message("second", size: 20));
        await store.AddAsync(Message("third", size: 30));

        (await store.LeaseIdsAsync(Take(bytes: budget))).Should().Equal("first");
    }

    // ---- not before (FR-ROUTE-10) ---------------------------------------------------------

    [Fact]
    public async Task A_record_that_is_not_yet_due_is_skipped_without_blocking_later_ones_and_keeps_its_place_once_due()
    {
        var due = T0.AddSeconds(10);
        var store = NewStore();
        await store.AddAsync(Message("delayed", notBefore: due));
        await store.AddAsync(Alice, "b", "c");

        (await store.LeaseIdsAsync(Take(now: T0, max: 1))).Should().Equal("b");
        (await store.ReleaseAsync(Alice, new[] { "b" }, None)).Should().Be(1);

        (await store.LeaseIdsAsync(Take(now: due - Tick))).Should().Equal("b", "c");
        (await store.ReleaseAsync(Alice, new[] { "b", "c" }, None)).Should().Be(2);

        // Due: handed out first, where it arrived — not behind the records that overtook it.
        (await store.LeaseIdsAsync(Take(now: due))).Should().Equal("delayed", "b", "c");
    }

    [Theory]
    [InlineData(-1L, true)]   // NotBefore one tick in the past
    [InlineData(0L, true)]    // NotBefore == now is due
    [InlineData(1L, false)]   // NotBefore one tick ahead
    public async Task A_record_is_due_from_the_instant_of_its_NotBefore(long notBeforeTicksFromNow, bool due)
    {
        var store = NewStore();
        await store.AddAsync(Message("m", notBefore: T0.AddTicks(notBeforeTicksFromNow)));

        (await store.LeaseIdsAsync(Take(now: T0))).Should().Equal(due ? new[] { "m" } : Array.Empty<string>());
    }

    // ---- expiry (FR-ROUTE-09) -------------------------------------------------------------

    [Theory]
    [InlineData(-1L, true)]   // now one tick before ExpiresAt: still delivered
    [InlineData(0L, false)]   // now == ExpiresAt: expired
    [InlineData(1L, false)]
    public async Task A_record_is_expired_from_the_instant_of_its_ExpiresAt(long nowTicksFromExpiry, bool delivered)
    {
        var expiresAt = T0.AddMinutes(5);
        var store = NewStore();
        await store.AddAsync(Message("m", expiresAt: expiresAt));

        (await store.LeaseIdsAsync(Take(now: expiresAt.AddTicks(nowTicksFromExpiry)))).Should().Equal(delivered ? new[] { "m" } : Array.Empty<string>());
    }

    [Fact]
    public async Task An_expired_record_is_never_returned_and_nothing_counts_it_afterwards()
    {
        var at = T0.AddSeconds(10);
        var store = NewStore();
        await store.AddAsync(Message("expiring", size: 100, expiresAt: at));
        await store.AddAsync(Message("keeper", size: 7));

        (await store.LeaseIdsAsync(Take(now: at))).Should().Equal(new[] { "keeper" }, "an expired record does not block the ones behind it");

        store.MessageCount.Should().Be(1);
        store.TotalBytes.Should().Be(7);
        (await store.GetStatusAsync(Alice, null, at, None)).Counts().Should().Be(new QueueStatus(1, 7, T0, T0));
        (await store.AcknowledgeAsync(Alice, new[] { "expiring" }, None)).Should().Be(0);
        (await store.ReleaseAsync(Alice, new[] { "expiring" }, None)).Should().Be(0);
        (await store.PurgeExpiredAsync(at, None)).Should().Be(0);
        (await store.EnqueueAsync(Message("expiring"), null, at, None)).Should().Be(EnqueueOutcome.Enqueued, "the id is free again");
    }

    [Fact]
    public async Task Expiry_wins_over_a_live_lease_and_the_lease_goes_with_the_record()
    {
        var at = T0.AddSeconds(10);
        var store = NewStore();
        await store.AddAsync(Message("expiring", expiresAt: at));
        await store.AddAsync(Message("keeper"));
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("expiring", "keeper");

        (await store.LeaseIdsAsync(Take(now: at))).Should().BeEmpty();
        store.MessageCount.Should().Be(1);

        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(1, "only the surviving record still carried the owner's lease");
        (await store.LeaseIdsAsync(Take(now: at))).Should().Equal("keeper");
    }

    // ---- leases (FR-PICK-04, JC-5) --------------------------------------------------------

    [Fact]
    public async Task A_record_under_a_live_lease_is_skipped_and_the_records_behind_it_are_still_handed_out()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "a", "b", "c");

        (await store.LeaseIdsAsync(Take(max: 1))).Should().Equal("a");
        (await store.LeaseIdsAsync(Take(max: 1))).Should().Equal("b");
        (await store.LeaseIdsAsync(Take())).Should().Equal("c");
        (await store.LeaseIdsAsync(Take())).Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1L, false)]  // now one tick before the lease's expiry: still leased
    [InlineData(0L, true)]    // now == the lease's ExpiresAt: no longer leased
    [InlineData(1L, true)]
    public async Task A_lease_holds_until_the_instant_of_its_expiry(long nowTicksFromLeaseExpiry, bool handedOutAgain)
    {
        var leaseEnds = T0.AddSeconds(60);
        var store = NewStore();
        await store.AddAsync(Alice, "m");
        (await store.LeaseIdsAsync(Take(until: leaseEnds))).Should().Equal("m");

        (await store.LeaseIdsAsync(Take(now: leaseEnds.AddTicks(nowTicksFromLeaseExpiry)))).Should().Equal(handedOutAgain ? new[] { "m" } : Array.Empty<string>());
    }

    [Fact]
    public async Task A_record_whose_lease_ran_out_is_handed_out_again_with_the_same_id_in_the_same_position()
    {
        var leaseEnds = T0.AddSeconds(60);
        var store = NewStore();
        await store.AddAsync(Message("a", size: 11));
        await store.AddAsync(Message("b", size: 12));
        await store.AddAsync(Message("c", size: 13));
        await store.AddAsync(Message("d", size: 14));
        var before = await store.HeldAsync();

        var first = await store.LeaseAsync(Take(max: 3, owner: "socket", until: leaseEnds), None);
        first.Select(m => m.Id).Should().Equal("a", "b", "c");

        var again = await store.LeaseAsync(Take(now: leaseEnds, max: 10), None);
        again.Select(m => m.Id).Should().Equal(new[] { "a", "b", "c", "d" }, "the lapsed leases left every record where it was");
        again.Take(3).Select(m => m.Payload.ToArray()).Should().BeEquivalentTo(first.Select(m => m.Payload.ToArray()), o => o.WithStrictOrdering());
        (await store.HeldAsync()).Should().Be(before, "a lease that runs out moves nothing and copies nothing");
    }

    [Fact]
    public async Task A_lease_that_is_already_over_when_it_is_taken_holds_nothing()
    {
        var store = NewStore();
        await store.AddAsync(Alice, "m");

        (await store.LeaseIdsAsync(Take(now: T0, until: T0))).Should().Equal("m");
        (await store.LeaseIdsAsync(Take(now: T0, until: T0 - Tick))).Should().Equal("m");
        (await store.LeaseIdsAsync(Take(now: T0, until: T0 + Tick))).Should().Equal("m");
        (await store.LeaseIdsAsync(Take(now: T0))).Should().BeEmpty("the last lease ends one tick after now");
    }

    // ---- two concurrent leases never return the same record --------------------------------

    [Fact]
    public async Task Concurrent_leases_never_hand_out_the_same_record_and_lose_none()
    {
        const int records = 1_500;
        const int workers = 8;
        var store = NewStore();
        var position = new Dictionary<string, int>(records, StringComparer.Ordinal);
        for (var i = 0; i < records; i++)
        {
            var id = $"m{i:D5}";
            position[id] = i;
            await store.AddAsync(Message(id, size: 1 + i % 9));
        }

        // Every worker still running makes its call in the same phase of the barrier, so the calls
        // overlap as far as the scheduler allows in every phase. Leases outlive the test and nothing
        // is released, so an empty lease means the queue is leased out for good.
        RaiseThreadPoolFloor(workers);
        using var barrier = new Barrier(workers);
        var racers = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            var mine = new List<string>();
            try
            {
                for (var round = 0; ; round++)
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("The other workers did not arrive.");
                    var max = 1 + (worker + round) % 5;
                    long budget = 4 + (worker * 3 + round) % 30;
                    var leased = await store.LeaseAsync(Take(max: max, bytes: budget, owner: $"worker-{worker}"), None);
                    if (leased.Count == 0)
                        return mine;

                    // Per lease, for every interleaving (L-044): within the caps, and in arrival order.
                    leased.Count.Should().BeLessThanOrEqualTo(max);
                    if (leased.Count > 1)
                        leased.Sum(m => (long)m.Payload.Length).Should().BeLessThanOrEqualTo(budget);
                    leased.Select(m => position[m.Id]).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
                    mine.AddRange(leased.Select(m => m.Id));
                }
            }
            finally
            {
                barrier.RemoveParticipant();
            }
        })).ToArray();
        var perWorker = await Task.WhenAll(racers);

        // Who got which record is not asserted: only that every record went to exactly one lease.
        var all = perWorker.SelectMany(ids => ids).ToArray();
        all.Should().OnlyHaveUniqueItems("two leases never return the same record");
        all.Should().HaveCount(records, "no deliverable record was passed over for good");
        perWorker.Should().OnlyContain(ids => ids.Count > 0, "every worker leased in the first phase, when there was enough for all");
        (await store.LeaseIdsAsync(Take())).Should().BeEmpty();
        store.MessageCount.Should().Be(records);
    }
}
