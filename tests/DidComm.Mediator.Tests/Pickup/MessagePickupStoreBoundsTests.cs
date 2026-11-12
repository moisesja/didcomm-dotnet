using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The bounds of <see cref="InMemoryMessageStore"/> at their exact edges (FR-PICK-10, D7; JC-1,
/// JC-6): <see cref="IMessageStore.EnqueueAsync"/> is one check-and-insert that answers with an
/// <see cref="EnqueueOutcome"/>, counts every retained record whatever its state, and changes
/// nothing when it refuses.
/// </summary>
public sealed class MessagePickupStoreBoundsTests
{
    private static ValueTask<EnqueueOutcome> TryAddAsync(InMemoryMessageStore store, QueuedMessage message, DateTimeOffset? now = null, MessageLease? lease = null)
        => store.EnqueueAsync(message, lease, now ?? T0, None);

    // ---- the per-grant bounds -------------------------------------------------------------

    [Fact]
    public async Task The_per_grant_message_bound_refuses_the_record_after_the_last_one_it_admits()
    {
        var store = NewStore(queueMessages: 3);
        await store.AddAsync(Alice, "a", "b", "c");
        var full = await store.HeldAsync();

        (await TryAddAsync(store, Message("d"))).Should().Be(EnqueueOutcome.GrantMessageLimitReached);
        (await TryAddAsync(store, Message("e", size: 0))).Should().Be(EnqueueOutcome.GrantMessageLimitReached, "an empty record is a record");
        (await store.HeldAsync()).Should().Be(full, "a refusal changes nothing");
        (await store.LeaseIdsAsync(Take(until: T0))).Should().Equal("a", "b", "c");

        // The bound is the grant's own: another grant is not affected, and a freed slot is usable.
        (await TryAddAsync(store, Message("d", Bob))).Should().Be(EnqueueOutcome.Enqueued);
        (await store.AcknowledgeAsync(Alice, new[] { "b" }, None)).Should().Be(1);
        (await TryAddAsync(store, Message("d"))).Should().Be(EnqueueOutcome.Enqueued);
        (await TryAddAsync(store, Message("e"))).Should().Be(EnqueueOutcome.GrantMessageLimitReached);
        (await store.LeaseIdsAsync(Take())).Should().Equal("a", "c", "d");
    }

    [Fact]
    public async Task The_per_grant_byte_bound_admits_a_record_that_lands_exactly_on_it_and_refuses_one_byte_more()
    {
        var store = NewStore(queueBytes: 100);
        await store.AddAsync(Message("sixty", size: 60));
        var before = await store.HeldAsync();

        (await TryAddAsync(store, Message("forty-one", size: 41))).Should().Be(EnqueueOutcome.GrantByteLimitReached);
        (await store.HeldAsync()).Should().Be(before, "a refusal changes nothing");

        (await TryAddAsync(store, Message("forty", size: 40))).Should().Be(EnqueueOutcome.Enqueued);
        store.TotalBytes.Should().Be(100);
        (await TryAddAsync(store, Message("one", size: 1))).Should().Be(EnqueueOutcome.GrantByteLimitReached);
        (await TryAddAsync(store, Message("nothing", size: 0))).Should().Be(EnqueueOutcome.Enqueued, "no bytes do not take the grant past its budget");
        (await TryAddAsync(store, Message("one", Bob, size: 1))).Should().Be(EnqueueOutcome.Enqueued, "the budget is the grant's own");
        (await store.GetStatusAsync(Alice, null, T0, None)).Should().Be(new QueueStatus(3, 100, T0, T0));
    }

    [Theory]
    [InlineData(100, EnqueueOutcome.Enqueued)]
    [InlineData(101, EnqueueOutcome.GrantByteLimitReached)]
    public async Task A_single_record_is_admitted_up_to_the_whole_byte_budget_of_an_empty_grant(int size, EnqueueOutcome expected)
    {
        var store = NewStore(queueBytes: 100);

        (await TryAddAsync(store, Message("only", size: size))).Should().Be(expected);

        var admitted = expected == EnqueueOutcome.Enqueued;
        (store.MessageCount, store.TotalBytes).Should().Be(admitted ? (1, 100L) : (0, 0L));
        (await store.GetStatusAsync(Alice, null, T0, None)).MessageCount.Should().Be(admitted ? 1 : 0);
        (await store.PurgeGrantAsync(Alice, None)).Should().Be(admitted ? 1 : 0, "a refused first record leaves nothing behind for the grant");
    }

    // ---- the store-wide bounds ------------------------------------------------------------

    [Fact]
    public async Task The_store_wide_message_bound_holds_across_grants()
    {
        var store = NewStore(queueMessages: 2, storeMessages: 5);
        await store.AddAsync(Alice, "a1", "a2");
        await store.AddAsync(Bob, "b1", "b2");
        await store.AddAsync(Carol, "c1");
        store.MessageCount.Should().Be(5);

        // Carol has room in her own grant; the store has none. A new grant gets the same answer.
        (await TryAddAsync(store, Message("c2", Carol))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await TryAddAsync(store, Message("d1", "did:example:dave"))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await TryAddAsync(store, Message("e1", "did:example:erin", size: 0))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (store.MessageCount, store.TotalBytes).Should().Be((5, 50L));
        (await store.GetStatusAsync(Carol, null, T0, None)).MessageCount.Should().Be(1);
        (await store.GetStatusAsync("did:example:dave", null, T0, None)).Should().Be(QueueStatus.Empty);

        // A slot freed in one grant is a slot for any grant.
        (await store.AcknowledgeAsync(Alice, new[] { "a1" }, None)).Should().Be(1);
        (await TryAddAsync(store, Message("d1", "did:example:dave"))).Should().Be(EnqueueOutcome.Enqueued);
        (await TryAddAsync(store, Message("c2", Carol))).Should().Be(EnqueueOutcome.StoreLimitReached);
        store.MessageCount.Should().Be(5);
    }

    [Fact]
    public async Task The_store_wide_byte_bound_holds_across_grants_and_admits_a_record_that_lands_exactly_on_it()
    {
        var store = NewStore(queueBytes: 60, storeBytes: 100);
        await store.AddAsync(Message("a", Alice, size: 60));
        await store.AddAsync(Message("b", Bob, size: 39));

        (await TryAddAsync(store, Message("c", Carol, size: 2))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await TryAddAsync(store, Message("b2", Bob, size: 2))).Should().Be(EnqueueOutcome.StoreLimitReached, "Bob is at 39 of his 60, but the store is at 99 of 100");
        (store.MessageCount, store.TotalBytes).Should().Be((2, 99L));

        (await TryAddAsync(store, Message("c", Carol, size: 1))).Should().Be(EnqueueOutcome.Enqueued);
        store.TotalBytes.Should().Be(100);
        (await TryAddAsync(store, Message("c2", Carol, size: 1))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await TryAddAsync(store, Message("b2", Bob, size: 1))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (store.MessageCount, store.TotalBytes).Should().Be((3, 100L));

        (await store.PurgeGrantAsync(Bob, None)).Should().Be(1);
        (await TryAddAsync(store, Message("c2", Carol, size: 39))).Should().Be(EnqueueOutcome.Enqueued);
        store.TotalBytes.Should().Be(100);
    }

    // ---- ids ------------------------------------------------------------------------------

    [Fact]
    public async Task An_id_the_grant_already_holds_is_refused_as_DuplicateId_and_the_stored_record_is_untouched()
    {
        var store = NewStore();
        await store.AddAsync(Message("m1", size: 5));
        await store.AddAsync(Message("m2", size: 6));
        (await store.LeaseIdsAsync(Take(max: 1, owner: "socket"))).Should().Equal("m1");
        var before = await store.HeldAsync();

        (await TryAddAsync(store, Message("m1", size: 500))).Should().Be(EnqueueOutcome.DuplicateId, "a leased record still holds its id");
        (await TryAddAsync(store, Message("m2", recipient: R1, size: 500))).Should().Be(EnqueueOutcome.DuplicateId);
        (await TryAddAsync(store, Message("m2", size: 500), lease: Lease("other-socket", Far))).Should().Be(EnqueueOutcome.DuplicateId);

        (await store.HeldAsync()).Should().Be(before, "a refusal changes nothing");
        (await store.ReleaseLeasesAsync("other-socket", None)).Should().Be(0, "a refused record leaves no lease behind");
        var second = (await store.LeaseAsync(Take(), None)).Should().ContainSingle().Subject;
        second.Id.Should().Be("m2");
        second.Payload.Length.Should().Be(6);
        second.RecipientDid.Should().Be(Alice);
    }

    [Fact]
    public async Task An_id_is_unique_within_its_grant_only_and_free_again_once_its_record_is_gone()
    {
        var store = NewStore();
        await store.AddAsync(Message("m1", Alice));

        (await TryAddAsync(store, Message("m1", Bob))).Should().Be(EnqueueOutcome.Enqueued, "the same id under another grant is another record");
        (await TryAddAsync(store, Message("M1", Alice))).Should().Be(EnqueueOutcome.Enqueued, "ids are compared ordinally");
        (await TryAddAsync(store, Message("m1", Alice))).Should().Be(EnqueueOutcome.DuplicateId);

        (await store.AcknowledgeAsync(Alice, new[] { "m1" }, None)).Should().Be(1);
        (await TryAddAsync(store, Message("m1", Alice))).Should().Be(EnqueueOutcome.Enqueued);
        (await store.LeaseIdsAsync(Take(Alice))).Should().Equal(new[] { "M1", "m1" }, "the re-used id is a new arrival, at the end");
    }

    // ---- every retained record counts, whatever its state ---------------------------------

    [Fact]
    public async Task Leased_and_not_yet_due_records_count_against_the_message_bound()
    {
        var store = NewStore(queueMessages: 3);
        await store.AddAsync(Message("leased"));
        (await store.LeaseIdsAsync(Take(owner: "socket"))).Should().Equal("leased");
        await store.AddAsync(Message("not-due", notBefore: T0.AddDays(1)));
        await store.AddAsync(Message("pushed"), Lease("socket", Far));

        (await store.LeaseIdsAsync(Take())).Should().BeEmpty("nothing is deliverable");
        (await TryAddAsync(store, Message("one-more"))).Should().Be(EnqueueOutcome.GrantMessageLimitReached);
        (await TryAddAsync(store, Message("one-more"), lease: Lease("socket", Far))).Should().Be(EnqueueOutcome.GrantMessageLimitReached);

        store.MessageCount.Should().Be(3);
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(2, "the refused push left no lease behind");
    }

    [Fact]
    public async Task Leased_and_not_yet_due_records_count_against_the_byte_bounds()
    {
        var store = NewStore(queueBytes: 90, storeBytes: 100);
        await store.AddAsync(Message("leased", size: 30));
        (await store.LeaseIdsAsync(Take())).Should().Equal("leased");
        await store.AddAsync(Message("not-due", size: 30, notBefore: T0.AddDays(1)));
        await store.AddAsync(Message("pushed", size: 30), Lease("socket", Far));

        (await TryAddAsync(store, Message("one-more", size: 1))).Should().Be(EnqueueOutcome.GrantByteLimitReached);
        (await TryAddAsync(store, Message("b1", Bob, size: 11))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await TryAddAsync(store, Message("b1", Bob, size: 10))).Should().Be(EnqueueOutcome.Enqueued);
        (store.MessageCount, store.TotalBytes).Should().Be((4, 100L));
    }

    [Fact]
    public async Task A_stream_of_live_pushes_that_is_never_acknowledged_stays_within_the_grants_bounds_before_during_and_after_the_connection_closes()
    {
        // FR-PICK-08/10 as the store sees them: every push is admitted under a lease or not at all,
        // and when the connection closes the leases end where the records are — no second copy.
        const int maxMessages = 12;
        const long maxBytes = 1_000;
        var store = NewStore(queueMessages: maxMessages, queueBytes: maxBytes);
        var lease = Lease("socket", T0.AddSeconds(60));
        var admitted = new List<string>();
        long admittedBytes = 0;
        var refused = new HashSet<EnqueueOutcome>();

        for (var n = 0; n < 60; n++)
        {
            var size = 40 + n * 13 % 170;
            var outcome = await store.EnqueueAsync(Message($"push-{n}", size: size), lease, T0, None);
            if (outcome == EnqueueOutcome.Enqueued)
            {
                admitted.Add($"push-{n}");
                admittedBytes += size;
            }
            else
            {
                refused.Add(outcome);
            }
            store.MessageCount.Should().BeLessThanOrEqualTo(maxMessages);
            store.TotalBytes.Should().BeLessThanOrEqualTo(maxBytes);
        }

        admitted.Should().NotBeEmpty();
        refused.Should().NotBeEmpty("the stream went past the budget").And.OnlyContain(o => o == EnqueueOutcome.GrantMessageLimitReached || o == EnqueueOutcome.GrantByteLimitReached);
        var during = await store.HeldAsync();
        during.Should().Be((admitted.Count, admittedBytes, new QueueStatus(admitted.Count, admittedBytes, T0, T0)), "a pushed, unacknowledged record is retained and reported");
        (await store.LeaseIdsAsync(Take())).Should().BeEmpty("everything retained is leased to the connection");

        // The connection closes.
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(admitted.Count, "only what was admitted ever carried a lease");

        (await store.HeldAsync()).Should().Be(during, "ending the leases copied nothing and removed nothing");
        (await store.EnqueueAsync(Message("after-close", size: (int)maxBytes), null, T0, None)).Should().NotBe(EnqueueOutcome.Enqueued, "the budget is still used by what is retained");
        (await store.LeaseIdsAsync(Take())).Should().Equal(admitted, "every pushed record is deliverable again, in the order it arrived");
    }

    // ---- records expired at the instant passed in do not count ----------------------------

    [Theory]
    [InlineData(-1L, EnqueueOutcome.GrantMessageLimitReached)]  // one tick before the expiry: the grant is full
    [InlineData(0L, EnqueueOutcome.Enqueued)]                   // now == ExpiresAt: the expired records no longer count
    [InlineData(1L, EnqueueOutcome.Enqueued)]
    public async Task Records_expired_at_now_do_not_count_against_the_message_bound(long nowTicksFromExpiry, EnqueueOutcome expected)
    {
        var expiresAt = T0.AddMinutes(5);
        var store = NewStore(queueMessages: 2, storeMessages: 2);
        await store.AddAsync(Message("old-1", expiresAt: expiresAt));
        await store.AddAsync(Message("old-2", expiresAt: expiresAt));
        (await store.LeaseIdsAsync(Take(max: 1, owner: "socket"))).Should().Equal("old-1");

        (await TryAddAsync(store, Message("new"), now: expiresAt.AddTicks(nowTicksFromExpiry))).Should().Be(expected);

        var admitted = expected == EnqueueOutcome.Enqueued;
        store.MessageCount.Should().Be(admitted ? 1 : 2);
        (await store.ReleaseLeasesAsync("socket", None)).Should().Be(admitted ? 0 : 1, "an expired record takes its lease with it");
    }

    [Theory]
    [InlineData(-1L, EnqueueOutcome.GrantByteLimitReached)]
    [InlineData(0L, EnqueueOutcome.Enqueued)]
    public async Task Records_expired_at_now_do_not_count_against_the_byte_bound(long nowTicksFromExpiry, EnqueueOutcome expected)
    {
        var expiresAt = T0.AddMinutes(5);
        var store = NewStore(queueBytes: 100, storeBytes: 100);
        await store.AddAsync(Message("old", size: 100, expiresAt: expiresAt));

        (await TryAddAsync(store, Message("new", size: 100), now: expiresAt.AddTicks(nowTicksFromExpiry))).Should().Be(expected);

        store.TotalBytes.Should().Be(100);
        var held = await store.LeaseIdsAsync(Take(now: expiresAt.AddTicks(nowTicksFromExpiry)));
        held.Should().Equal(expected == EnqueueOutcome.Enqueued ? "new" : "old");
    }

    [Fact]
    public async Task An_id_held_only_by_a_record_expired_at_now_is_free()
    {
        var expiresAt = T0.AddMinutes(5);
        var store = NewStore();
        await store.AddAsync(Message("m1", size: 5, expiresAt: expiresAt));

        (await TryAddAsync(store, Message("m1", size: 9), now: expiresAt - Tick)).Should().Be(EnqueueOutcome.DuplicateId);
        (await TryAddAsync(store, Message("m1", size: 9), now: expiresAt)).Should().Be(EnqueueOutcome.Enqueued);

        (store.MessageCount, store.TotalBytes).Should().Be((1, 9L));
    }

    [Fact]
    public async Task Expired_records_of_another_grant_never_keep_a_new_message_out_of_a_full_store()
    {
        // The enqueue contract: a record that is expired at the instant of the call does not count
        // against the bounds, the grant's or the store's. An idle grant's expired records are swept
        // when the store-wide bound would otherwise refuse a message; nobody has to schedule a purge.
        var expiresAt = T0.AddMinutes(5);
        var store = NewStore(queueMessages: 2, storeMessages: 2);
        await store.AddAsync(Message("a1", Alice, expiresAt: expiresAt));
        await store.AddAsync(Message("a2", Alice, expiresAt: expiresAt));

        // One tick before the expiry the store is simply full, and nothing is removed by the refusal.
        (await TryAddAsync(store, Message("b0", Bob), now: expiresAt - Tick)).Should().Be(EnqueueOutcome.StoreLimitReached);
        store.MessageCount.Should().Be(2);

        (await TryAddAsync(store, Message("b1", Bob), now: expiresAt)).Should().Be(EnqueueOutcome.Enqueued);
        (store.MessageCount, store.TotalBytes).Should().Be((1, 10L), "Alice's two expired records were removed to make room, not merely ignored");
        (await store.PurgeExpiredAsync(expiresAt, None)).Should().Be(0);
    }

    [Fact]
    public async Task A_full_store_with_nothing_expired_stays_full_however_often_it_is_asked()
    {
        var store = NewStore(queueMessages: 2, storeMessages: 3);
        await store.AddAsync(Message("a1", Alice, expiresAt: T0.AddMinutes(5)));
        await store.AddAsync(Message("a2", Alice));
        await store.AddAsync(Message("b1", Bob, expiresAt: T0.AddHours(1)));

        for (var i = 0; i < 3; i++)
            (await TryAddAsync(store, Message($"c{i}", Carol), now: T0.AddMinutes(1))).Should().Be(EnqueueOutcome.StoreLimitReached);
        store.MessageCount.Should().Be(3);

        // The earliest expiry passes: exactly that record goes, one new record fits, then full again.
        (await TryAddAsync(store, Message("c3", Carol), now: T0.AddMinutes(5))).Should().Be(EnqueueOutcome.Enqueued);
        (await TryAddAsync(store, Message("c4", Carol), now: T0.AddMinutes(5))).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await store.LeaseIdsAsync(Take(Alice, now: T0.AddMinutes(5)))).Should().Equal("a2");
        (await store.LeaseIdsAsync(Take(Bob, now: T0.AddMinutes(5)))).Should().Equal("b1");
    }

    // ---- floods (the issue's adversarial acceptance) --------------------------------------

    [Fact]
    public async Task A_100_MB_flood_for_one_grant_never_takes_the_grant_past_MaxQueueBytes()
    {
        // One 64 KiB buffer, sliced: 100 MB is attempted, 8 MiB at most is ever referenced.
        var buffer = Bytes(64 * 1024, "flood");
        var options = new MessagePickupOptions { MaxQueueMessages = 100_000, MaxStoreMessages = 100_000 };
        var maxQueueBytes = options.MaxQueueBytes;
        maxQueueBytes.Should().Be(8L * 1024 * 1024, "the byte bound under test is the default");
        var store = new InMemoryMessageStore(options);
        var random = new Random(94);

        long attempted = 0, admitted = 0;
        var refused = 0;
        for (var n = 0; attempted < 100_000_000; n++)
        {
            var size = random.Next(1, buffer.Length + 1);
            var outcome = await store.EnqueueAsync(new QueuedMessage($"flood-{n}", Alice, Alice, buffer.AsMemory(0, size), T0), null, T0, None);
            attempted += size;
            if (outcome == EnqueueOutcome.Enqueued)
                admitted += size;
            else if (outcome == EnqueueOutcome.GrantByteLimitReached)
                refused++;
            else
                Assert.Fail($"Attempt {n}: unexpected outcome {outcome}.");
            if (store.TotalBytes > maxQueueBytes)
                Assert.Fail($"Attempt {n}: the store holds {store.TotalBytes} bytes, over the bound of {maxQueueBytes}.");
        }

        refused.Should().BeGreaterThan(0);
        store.TotalBytes.Should().Be(admitted).And.BeLessThanOrEqualTo(maxQueueBytes);
        (await store.GetStatusAsync(Alice, null, T0, None)).TotalBytes.Should().Be(admitted);

        // And the bound is exact: what is left of it can still be used to the byte, and not one more.
        var room = (int)(maxQueueBytes - store.TotalBytes);
        room.Should().BeLessThan(buffer.Length);
        if (room > 0)
            (await store.EnqueueAsync(new QueuedMessage("last", Alice, Alice, buffer.AsMemory(0, room), T0), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        store.TotalBytes.Should().Be(maxQueueBytes);
        (await store.EnqueueAsync(new QueuedMessage("one-byte", Alice, Alice, buffer.AsMemory(0, 1), T0), null, T0, None)).Should().Be(EnqueueOutcome.GrantByteLimitReached);
    }

    [Fact]
    public async Task A_100_MB_flood_at_the_default_bounds_stays_within_both_per_grant_bounds()
    {
        var buffer = Bytes(128 * 1024, "flood");
        var defaults = new MessagePickupOptions();
        var store = new InMemoryMessageStore();

        long attempted = 0;
        var outcomes = new HashSet<EnqueueOutcome>();
        for (var n = 0; attempted < 100_000_000; n++)
        {
            // Two large records, then a small one: the byte bound is met first (the 64th large record
            // does not fit), the small ones then fill the grant up to the message bound.
            var size = n % 3 == 2 ? 1 + n % 4096 : buffer.Length;
            outcomes.Add(await store.EnqueueAsync(new QueuedMessage($"flood-{n}", Alice, Alice, buffer.AsMemory(0, size), T0), null, T0, None));
            attempted += size;
            if (store.MessageCount > defaults.MaxQueueMessages || store.TotalBytes > defaults.MaxQueueBytes)
                Assert.Fail($"Attempt {n}: the store holds {store.MessageCount} records and {store.TotalBytes} bytes.");
        }

        outcomes.Should().BeEquivalentTo(new[] { EnqueueOutcome.Enqueued, EnqueueOutcome.GrantByteLimitReached, EnqueueOutcome.GrantMessageLimitReached }, "the flood met both per-grant bounds and nothing else");
        var status = await store.GetStatusAsync(Alice, null, T0, None);
        status.MessageCount.Should().Be(store.MessageCount).And.BeLessThanOrEqualTo(defaults.MaxQueueMessages);
        status.TotalBytes.Should().Be(store.TotalBytes).And.BeLessThanOrEqualTo(defaults.MaxQueueBytes);
    }

    [Fact]
    public async Task Floods_across_many_grants_stay_within_the_store_wide_bounds()
    {
        const int grants = 64;
        var options = new MessagePickupOptions
        {
            MaxQueueMessages = 50,
            MaxQueueBytes = 1L * 1024 * 1024,
            MaxStoreMessages = 400,
            MaxStoreBytes = 4L * 1024 * 1024,
        };
        var store = new InMemoryMessageStore(options);
        var buffer = Bytes(64 * 1024, "flood");
        var random = new Random(9494);
        var names = Enumerable.Range(0, grants).Select(i => $"did:example:grant-{i}").ToArray();

        var outcomes = new Dictionary<EnqueueOutcome, int>();
        for (var n = 0; n < grants * 150; n++)
        {
            // Mostly small records (the count bound), now and then a large one (the byte bound).
            var size = random.Next(8) == 0 ? random.Next(1, buffer.Length + 1) : random.Next(1, 2048);
            var grant = names[random.Next(grants)];
            var outcome = await store.EnqueueAsync(new QueuedMessage($"flood-{n}", grant, grant, buffer.AsMemory(0, size), T0), null, T0, None);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            if (store.MessageCount > options.MaxStoreMessages || store.TotalBytes > options.MaxStoreBytes)
                Assert.Fail($"Attempt {n}: the store holds {store.MessageCount} records and {store.TotalBytes} bytes.");
        }

        outcomes.Should().ContainKey(EnqueueOutcome.StoreLimitReached, "the flood reached the store-wide bound");
        var count = 0;
        long bytes = 0;
        foreach (var grant in names)
        {
            var status = await store.GetStatusAsync(grant, null, T0, None);
            status.MessageCount.Should().BeLessThanOrEqualTo(options.MaxQueueMessages);
            status.TotalBytes.Should().BeLessThanOrEqualTo(options.MaxQueueBytes);
            count += status.MessageCount;
            bytes += status.TotalBytes;
        }
        count.Should().Be(store.MessageCount).And.Be(outcomes[EnqueueOutcome.Enqueued]).And.BeLessThanOrEqualTo(options.MaxStoreMessages);
        bytes.Should().Be(store.TotalBytes).And.BeLessThanOrEqualTo(options.MaxStoreBytes);
    }
}
