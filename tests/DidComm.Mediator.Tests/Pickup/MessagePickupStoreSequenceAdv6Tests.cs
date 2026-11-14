using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-6 (#95, PR #109 review round 2): the arrival-sequence contract of
/// <see cref="InMemoryMessageStore"/> under concurrency and around every removal path. The mediator
/// decides "arrived after live-on" by these numbers alone, so each test states an invariant that must hold for every interleaving (L-044) rather than one schedule.
/// </summary>
public sealed class MessagePickupStoreSequenceAdv6Tests
{
    private static readonly string[] Grants = { Alice, Bob, Carol, "did:example:dave" };

    [Fact]
    public async Task Concurrent_enqueues_across_grants_number_every_stored_record_once_without_gaps_and_in_each_grants_arrival_order_even_with_refusals_interleaved()
    {
        const int Writers = 16;
        const int PerWriter = 200;
        RaiseThreadPoolFloor(Writers);
        // Bob's quota is small: most of its enqueues are refused while the others succeed around them.
        var store = NewStore(queueMessages: 100);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new ConcurrentBag<EnqueueOutcome>();
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < PerWriter; i++)
            {
                var grant = Grants[(w + i) % Grants.Length];
                var id = $"w{w}-{i}";
                outcomes.Add(await store.EnqueueAsync(Message(id, grant), null, T0, None));
                if (i % 7 == 0)
                    outcomes.Add(await store.EnqueueAsync(Message(id, grant), null, T0, None)); // a duplicate: refused, takes no number
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(writers);

        var stored = outcomes.Count(o => o == EnqueueOutcome.Enqueued);
        outcomes.Should().Contain(o => o != EnqueueOutcome.Enqueued, "the test must interleave refusals");
        var all = new List<QueuedMessage>();
        foreach (var grant in Grants)
        {
            var records = await store.LeaseAsync(Take(grant: grant), None);
            records.Select(r => r.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("a grant's records are handed out in arrival order, which is that of their numbers");
            all.AddRange(records);
        }
        all.Should().HaveCount(stored);
        all.Select(r => r.Sequence).Should().BeEquivalentTo(Enumerable.Range(1, stored).Select(n => (long)n), "every stored record has a number of its own and a refused enqueue takes none");
        (await store.GetStatusAsync("did:example:nobody", null, T0, None)).HighWaterSequence.Should().Be(stored);
    }

    [Fact]
    public async Task Every_status_read_while_records_are_stored_counts_exactly_the_records_at_or_below_its_own_mark()
    {
        const int Writers = 8;
        const int PerWriter = 150;
        const int Readers = 4;
        RaiseThreadPoolFloor(Writers + Readers);
        var store = NewStore();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // L-044: the overlap is forced, not hoped for. Every writer stops halfway until every reader
        // has read a status after all writers got there: those reads see a half-written store.
        var halfway = 0;
        var readHalfway = 0;
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = Writers;
        var seen = new ConcurrentBag<(int Reader, int Order, long Mark, int Count)>();
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < PerWriter; i++)
            {
                if (i == PerWriter / 2)
                {
                    Interlocked.Increment(ref halfway);
                    await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                await store.AddAsync(Message($"w{w}-{i}", w % 2 == 0 ? Alice : Bob));
            }
            Interlocked.Decrement(ref writing);
        })).ToArray();
        var readers = Enumerable.Range(0, Readers).Select(r => Task.Run(async () =>
        {
            await start.Task;
            var countedHalfway = false;
            for (var order = 0; Volatile.Read(ref writing) > 0; order++)
            {
                var all = Volatile.Read(ref halfway) == Writers;
                var status = await store.GetStatusAsync(Alice, null, T0, None);
                seen.Add((r, order, status.HighWaterSequence, status.MessageCount));
                if (all && !countedHalfway)
                {
                    countedHalfway = true;
                    if (Interlocked.Increment(ref readHalfway) == Readers)
                        resume.TrySetResult();
                }
                if (order % 16 == 0)
                    await Task.Yield();
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(writers.Concat(readers)).WaitAsync(TimeSpan.FromSeconds(30));

        var alice = (await store.LeaseAsync(Take(grant: Alice), None)).Select(r => r.Sequence).ToArray();
        alice.Should().HaveCount(Writers / 2 * PerWriter);
        seen.Should().Contain(s => s.Count > 0 && s.Count < alice.Length, "some reads saw a half-written store");
        foreach (var (_, _, mark, count) in seen)
            count.Should().Be(alice.Count(s => s <= mark), "a status's counts and its mark are read in one step (mark {0})", mark);
        foreach (var reader in seen.GroupBy(s => s.Reader))
            reader.OrderBy(s => s.Order).Select(s => s.Mark).Should().BeInAscendingOrder("the mark never goes back");
    }

    [Fact]
    public async Task The_numbers_keep_rising_across_a_store_full_refusal_the_store_wide_expiry_sweep_a_grant_purge_and_the_expiry_purge()
    {
        var store = NewStore(queueMessages: 2, storeMessages: 2);
        await store.AddAsync(Message("a1", Alice, expiresAt: T0.AddSeconds(1)));
        await store.AddAsync(Message("b1", Bob));
        (await store.EnqueueAsync(Message("c1", Carol), null, T0, None)).Should().Be(EnqueueOutcome.StoreLimitReached);
        (await store.GetStatusAsync(Carol, null, T0, None)).HighWaterSequence.Should().Be(2, "the refusal took no number");

        // Full, with Alice's record expired: the store-wide sweep makes room inside the enqueue.
        (await store.EnqueueAsync(Message("c1", Carol), null, T0.AddSeconds(2), None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.PurgeGrantAsync(Bob, None)).Should().Be(1);
        await store.AddAsync(Message("d1", Alice, expiresAt: T0.AddSeconds(3)), now: T0.AddSeconds(2));
        (await store.PurgeExpiredAsync(T0.AddSeconds(5), None)).Should().Be(1);
        await store.AddAsync(Message("e1", Bob), now: T0.AddSeconds(5));

        (await store.LeaseAsync(Take(grant: Carol, now: T0.AddSeconds(5)), None)).Single().Sequence.Should().Be(3);
        (await store.LeaseAsync(Take(grant: Bob, now: T0.AddSeconds(5)), None)).Single().Sequence.Should().Be(5, "no removal hands a number out again");
        (await store.GetStatusAsync(Alice, null, T0.AddSeconds(5), None)).HighWaterSequence.Should().Be(5);
    }

    [Fact]
    public async Task Concurrent_takes_after_a_mark_while_records_are_stored_never_return_one_at_or_below_it_and_take_every_later_one_exactly_once()
    {
        const int Writers = 6;
        const int PerWriter = 100;
        const int Takers = 6;
        RaiseThreadPoolFloor(Writers + Takers);
        var store = NewStore();
        for (var i = 0; i < 50; i++)
            await store.AddAsync(Message($"before-{i}"));
        var mark = (await store.GetStatusAsync(Alice, null, T0, None)).HighWaterSequence;
        mark.Should().Be(50);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = Writers;
        var taken = new ConcurrentBag<QueuedMessage>();
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < PerWriter; i++)
                await store.AddAsync(Message($"after-{w}-{i}", w % 2 == 0 ? Alice : Bob));
            Interlocked.Decrement(ref writing);
        })).ToArray();
        var takers = Enumerable.Range(0, Takers).Select(t => Task.Run(async () =>
        {
            await start.Task;
            while (true)
            {
                var done = Volatile.Read(ref writing) == 0;
                var one = await store.LeaseAsync(new MessageLeaseRequest(Alice, null, 1, long.MaxValue, T0, new MessageLease($"taker-{t}", Far)) { AfterSequence = mark }, None);
                foreach (var record in one)
                    taken.Add(record);
                if (one.Count == 0 && done)
                    return;
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(writers.Concat(takers));

        taken.Should().OnlyContain(r => r.Sequence > mark, "AfterSequence is honoured under concurrent stores");
        taken.Select(r => r.Id).Should().OnlyHaveUniqueItems("two takes never return the same record");
        taken.Should().HaveCount(Writers / 2 * PerWriter, "every record of the grant after the mark was taken");
        (await store.LeaseIdsAsync(Take())).Should().HaveCount(50).And.OnlyContain(id => id.StartsWith("before-", StringComparison.Ordinal), "the records at or below the mark were never marked");
    }
}
