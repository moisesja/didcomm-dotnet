using System.Collections;
using System.Diagnostics;
using System.Reflection;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-2, goal 1, the in-memory store's bookkeeping under attack: the store-wide sweep's
/// earliest-expiry mark (can it stick low, so a flood against a full store walks every grant per
/// refused message; or high, so expired records keep messages out?), clocks that run backwards or
/// sit at the ends of time, the largest numbers the options and the requests can carry, and the
/// lease-owner index (can it grow without bound, or keep what is gone?).
/// </summary>
/// <remarks>
/// Three tests read private fields of <see cref="InMemoryMessageStore"/> by name
/// (<c>_nextExpiry</c>, <c>_owners</c>, <c>_grants</c>): what they assert — the cost of a refusal,
/// the size of an index — has no public observable. A rename fails them loudly, on purpose.
/// </remarks>
public sealed class MessagePickupAdversarialResourceStoreTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken None = CancellationToken.None;

    private static InMemoryMessageStore Store(int perGrant = 4, int storeWide = 8, long perGrantBytes = 1_000, long storeBytes = 2_000)
        => new(new MessagePickupOptions { MaxQueueMessages = perGrant, MaxQueueBytes = perGrantBytes, MaxStoreMessages = storeWide, MaxStoreBytes = storeBytes });

    private static QueuedMessage Record(string grant, string id, DateTimeOffset? expiresAt = null, int size = 10, DateTimeOffset? notBefore = null, string? recipient = null)
        => new(id, grant, recipient ?? grant, new byte[size], T0, notBefore, expiresAt);

    private static T Private<T>(InMemoryMessageStore store, string field)
    {
        var info = typeof(InMemoryMessageStore).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"InMemoryMessageStore no longer has a field '{field}': update this test with the store.");
        return (T)info.GetValue(store)!;
    }

    private static DateTimeOffset NextExpiryMark(InMemoryMessageStore store) => Private<DateTimeOffset>(store, "_nextExpiry");
    private static int OwnerCount(InMemoryMessageStore store) => ((ICollection)Private<object>(store, "_owners")).Count;
    private static int GrantCount(InMemoryMessageStore store) => ((ICollection)Private<object>(store, "_grants")).Count;

    // ---- the store-wide sweep: stuck high? ------------------------------------------------------

    public static TheoryData<string> WaysTheEarliestExpiringRecordGoesAway => new()
    {
        "acknowledged", "released and leased again", "its recipient purged", "its grant purged", "swept by a status of its grant", "swept by PurgeExpired at an earlier instant",
    };

    /// <summary>
    /// JC-20 after the bookkeeping was disturbed: the record that set the earliest-expiry mark goes
    /// away by every route there is, another grant's record then expires, and a full store must
    /// still let the next message in.
    /// </summary>
    [Theory]
    [MemberData(nameof(WaysTheEarliestExpiringRecordGoesAway))]
    public async Task An_expired_record_of_another_grant_never_keeps_a_message_out_of_a_full_store_however_the_earliest_expiring_record_went_away(string how)
    {
        var store = Store(perGrant: 4, storeWide: 4);
        (await store.EnqueueAsync(Record("did:example:a", "a1", T0.AddSeconds(10)), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(Record("did:example:b", "b1", T0.AddSeconds(30)), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(Record("did:example:b", "b2"), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);

        switch (how)
        {
            case "acknowledged":
                (await store.AcknowledgeAsync("did:example:a", new[] { "a1" }, None)).Should().Be(1);
                break;
            case "released and leased again":
            {
                var lease = new MessageLease("owner", T0.AddSeconds(5));
                await store.LeaseAsync(new MessageLeaseRequest("did:example:a", null, 1, 100, T0, lease), None);
                await store.ReleaseAsync("did:example:a", new[] { "a1" }, lease.Id, None);
                await store.LeaseAsync(new MessageLeaseRequest("did:example:a", null, 1, 100, T0, new MessageLease(null, T0.AddSeconds(500))), None);
                (await store.AcknowledgeAsync("did:example:a", new[] { "a1" }, None)).Should().Be(1);
                break;
            }
            case "its recipient purged":
                (await store.PurgeRecipientAsync("did:example:a", "did:example:a", None)).Should().Be(1);
                break;
            case "its grant purged":
                (await store.PurgeGrantAsync("did:example:a", None)).Should().Be(1);
                break;
            case "swept by a status of its grant":
                (await store.GetStatusAsync("did:example:a", null, T0.AddSeconds(11), None)).MessageCount.Should().Be(0);
                break;
            default:
                (await store.PurgeExpiredAsync(T0.AddSeconds(11), None)).Should().Be(1);
                break;
        }

        // Refill to the store-wide bound with records that never expire.
        (await store.EnqueueAsync(Record("did:example:c", "c1"), null, T0.AddSeconds(12), None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(Record("did:example:c", "c2"), null, T0.AddSeconds(12), None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(Record("did:example:d", "full"), null, T0.AddSeconds(29), None)).Should().Be(EnqueueOutcome.StoreLimitReached, "nothing is expired one second before b1's expiry");

        // b1 expires; grant b is never touched; a message for grant d arrives at a full store.
        (await store.EnqueueAsync(Record("did:example:d", "d1"), null, T0.AddSeconds(30), None)).Should().Be(EnqueueOutcome.Enqueued, "an expired record never keeps a new message out, whichever grant holds it (JC-20)");
        store.MessageCount.Should().Be(4);
    }

    /// <summary>
    /// The property behind JC-20, for any sequence and any clock — running backwards, standing
    /// still, at either end of time: whenever the store refuses a message for the store-wide bound
    /// at an instant, nothing that is expired at that instant is left in it. And the cost side: a
    /// refusal leaves the earliest-expiry mark in the future, so the next refusal at that instant
    /// does not sweep again.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Whenever_the_store_wide_bound_refuses_nothing_expired_is_left_and_the_next_refusal_does_not_sweep_again_whatever_the_clock_does(int seed)
    {
        var random = new Random(seed);
        var store = Store(perGrant: 6, storeWide: 14, perGrantBytes: 150, storeBytes: 320);
        var grants = Enumerable.Range(0, 6).Select(i => "did:example:g" + i).ToArray();
        var owners = new[] { "o1", "o2", "o3" };
        var refusals = 0;

        DateTimeOffset Instant() => random.Next(40) switch
        {
            0 => DateTimeOffset.MinValue,
            1 => DateTimeOffset.MaxValue,
            2 => DateTimeOffset.MaxValue.AddTicks(-1),
            _ => T0.AddSeconds(random.Next(-20, 60)),
        };

        for (var step = 0; step < 4000; step++)
        {
            var now = Instant();
            var grant = grants[random.Next(grants.Length)];
            switch (random.Next(10))
            {
                case < 5:
                {
                    DateTimeOffset? expiresAt = random.Next(3) switch { 0 => null, 1 => Instant(), _ => T0.AddSeconds(random.Next(-20, 60)) };
                    DateTimeOffset? notBefore = random.Next(5) == 0 ? Instant() : null;
                    var lease = random.Next(5) == 0 ? new MessageLease(owners[random.Next(owners.Length)], Instant()) : null;
                    var outcome = await store.EnqueueAsync(Record(grant, "id-" + (random.Next(3) == 0 ? random.Next(20) : step), expiresAt, random.Next(1, 40), notBefore), lease, now, None);
                    if (outcome == EnqueueOutcome.StoreLimitReached)
                    {
                        refusals++;
                        if (now < DateTimeOffset.MaxValue)
                            NextExpiryMark(store).Should().BeAfter(now, $"seed {seed}, step {step}: a refusal sweeps at most once per instant");
                        (await store.PurgeExpiredAsync(now, None)).Should().Be(0, $"seed {seed}, step {step}: an expired record never keeps a new message out");
                    }
                    break;
                }
                case 5 or 6:
                    await store.LeaseAsync(new MessageLeaseRequest(grant, null, random.Next(0, 4), random.Next(-5, 100), now, new MessageLease(random.Next(2) == 0 ? owners[random.Next(owners.Length)] : null, Instant())), None);
                    break;
                case 7:
                    await store.AcknowledgeAsync(grant, new[] { "id-" + random.Next(20), "id-" + random.Next(Math.Max(1, step)) }, None);
                    break;
                case 8:
                    await store.GetStatusAsync(grant, null, now, None);
                    break;
                default:
                    if (random.Next(3) == 0)
                        await store.PurgeExpiredAsync(now, None);
                    else
                        await store.ReleaseLeasesAsync(owners[random.Next(owners.Length)], None);
                    break;
            }

            store.MessageCount.Should().BeInRange(0, 14);
            store.TotalBytes.Should().BeInRange(0, 320);
            OwnerCount(store).Should().BeLessThanOrEqualTo(Math.Min(owners.Length, store.MessageCount), "the owner index holds only owners of records that are held");
            GrantCount(store).Should().BeLessThanOrEqualTo(store.MessageCount, "no grant bucket outlives its last record");
        }

        refusals.Should().BeGreaterThan(0, "the test must reach the store-wide bound");
        foreach (var grant in grants)
            await store.PurgeGrantAsync(grant, None);
        store.MessageCount.Should().Be(0);
        store.TotalBytes.Should().Be(0);
        OwnerCount(store).Should().Be(0);
        GrantCount(store).Should().Be(0);
    }

    // ---- the store-wide sweep: stuck low? (cost of a flood against a full store) -----------------

    /// <summary>
    /// A flood against a full store. Every earliest-expiry mark — each grant's and the store's — is
    /// first made stale-low the way a peer can (the grant's earliest-expiring record is
    /// acknowledged), and the store is left full of records that cannot expire for an hour. The
    /// first refusal after the stale mark has passed pays for one sweep; from then on the mark is
    /// in the future, which is the whole condition for "no sweep": a refused message costs a
    /// constant, whatever the number of grants. The time per refusal is written to the test output
    /// (measured, not asserted: the proof is the mark).
    /// </summary>
    [Fact]
    public async Task A_flood_against_a_full_store_at_the_default_bounds_does_not_walk_the_store_once_per_refused_message()
    {
        var store = new InMemoryMessageStore();
        var now = T0;
        for (var g = 0; g < 5_000; g++)
        {
            var grant = "did:example:grant-" + g;
            (await store.EnqueueAsync(Record(grant, "later", now.AddHours(1)), null, now, None)).Should().Be(EnqueueOutcome.Enqueued);
            if (g < 4_999)
            {
                // Room for one more: a record that expires in a second, acknowledged at once.
                (await store.EnqueueAsync(Record(grant, "soon", now.AddSeconds(1)), null, now, None)).Should().Be(EnqueueOutcome.Enqueued);
                (await store.AcknowledgeAsync(grant, new[] { "soon" }, None)).Should().Be(1);
            }
        }
        store.MessageCount.Should().Be(5_000);
        NextExpiryMark(store).Should().Be(T0.AddSeconds(1), "the mark is stale: the record that set it is gone");

        // The stale mark has passed; the first refusal pays for the one sweep.
        now = now.AddSeconds(5);
        (await store.EnqueueAsync(Record("did:example:victim", "v0"), null, now, None)).Should().Be(EnqueueOutcome.StoreLimitReached);
        NextExpiryMark(store).Should().Be(T0.AddHours(1), "one sweep makes the mark true again, so the next refusal at this instant does not sweep");

        const int Flood = 20_000;
        var message = Record("did:example:victim", "flood");
        var refused = 0;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Flood; i++)
        {
            if (await store.EnqueueAsync(message, null, now, None) == EnqueueOutcome.StoreLimitReached)
                refused++;
        }
        clock.Stop();

        refused.Should().Be(Flood);
        NextExpiryMark(store).Should().Be(T0.AddHours(1));
        store.MessageCount.Should().Be(5_000);
        output.WriteLine($"Refused enqueue on a full store of 5 000 grants: {clock.Elapsed.TotalMilliseconds * 1000 / Flood:F3} µs each ({Flood} refusals in {clock.Elapsed.TotalMilliseconds:F1} ms).");
    }

    // ---- the ends of the number line -------------------------------------------------------------

    [Fact]
    public async Task The_largest_bounds_and_the_largest_requests_overflow_nothing()
    {
        var store = new InMemoryMessageStore(new MessagePickupOptions
        {
            MaxQueueMessages = int.MaxValue,
            MaxQueueBytes = long.MaxValue,
            MaxStoreMessages = int.MaxValue,
            MaxStoreBytes = long.MaxValue,
        });
        for (var i = 0; i < 50; i++)
            (await store.EnqueueAsync(Record("did:example:a", "m" + i, size: 1000), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);

        var everything = await store.LeaseAsync(new MessageLeaseRequest("did:example:a", null, int.MaxValue, long.MaxValue, T0, new MessageLease("o", DateTimeOffset.MaxValue)), None);
        everything.Should().HaveCount(50);
        (await store.ReleaseLeasesAsync("o", None)).Should().Be(50);

        var first = await store.LeaseAsync(new MessageLeaseRequest("did:example:a", null, int.MaxValue, long.MinValue, T0, new MessageLease(null, T0.AddSeconds(1))), None);
        first.Select(m => m.Id).Should().Equal(new[] { "m0" }, "a budget below zero means what zero means: the first deliverable record and no more");

        var status = await store.GetStatusAsync("did:example:a", null, DateTimeOffset.MaxValue, None);
        status.MessageCount.Should().Be(50);
        status.TotalBytes.Should().Be(50_000);
        store.TotalBytes.Should().Be(50_000);
    }

    [Fact]
    public async Task Expiries_not_before_times_and_leases_at_either_end_of_time_behave_as_the_contract_says()
    {
        var store = Store(perGrant: 10, storeWide: 10);
        var grant = "did:example:a";

        // Expired at every instant: admitted (the store judges the bounds, not the record), never returned, never counted.
        (await store.EnqueueAsync(Record(grant, "born-expired", DateTimeOffset.MinValue), null, DateTimeOffset.MinValue, None)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.GetStatusAsync(grant, null, DateTimeOffset.MinValue, None)).Should().Be(QueueStatus.Empty);
        store.MessageCount.Should().Be(0);

        // Never expires before the last instant; expired AT the last instant (now >= ExpiresAt).
        (await store.EnqueueAsync(Record(grant, "expires-last", DateTimeOffset.MaxValue), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        // Due only at the last instant.
        (await store.EnqueueAsync(Record(grant, "due-last", notBefore: DateTimeOffset.MaxValue), null, T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        // Leased for all time: only an acknowledgement or a release ends it (the role never takes such a lease).
        (await store.EnqueueAsync(Record(grant, "leased-for-ever"), new MessageLease("o", DateTimeOffset.MaxValue), T0, None)).Should().Be(EnqueueOutcome.Enqueued);

        var justBefore = DateTimeOffset.MaxValue.AddTicks(-1);
        (await store.LeaseAsync(new MessageLeaseRequest(grant, null, 10, 1000, justBefore, new MessageLease(null, justBefore)), None)).Select(m => m.Id)
            .Should().Equal("expires-last");
        (await store.GetStatusAsync(grant, null, justBefore, None)).MessageCount.Should().Be(2, "the not-yet-due record is retained but not reported");

        (await store.LeaseAsync(new MessageLeaseRequest(grant, null, 10, 1000, DateTimeOffset.MaxValue, new MessageLease(null, DateTimeOffset.MaxValue)), None)).Select(m => m.Id)
            .Should().Equal("due-last", "leased-for-ever");
        store.MessageCount.Should().Be(2, "the record that expires at the last instant is gone at the last instant");
        NextExpiryMark(store).Should().BeOnOrBefore(DateTimeOffset.MaxValue);
    }

    // ---- the lease-owner index --------------------------------------------------------------------

    /// <summary>
    /// Ten thousand connections, one after the other, each leasing the same few records after the
    /// previous lease ran out and none of them ever "closing" (no release by owner): the index
    /// keeps one entry per owner that still marks a record, not one per owner that ever did.
    /// </summary>
    [Fact]
    public async Task The_owner_index_holds_at_most_one_entry_per_held_record_however_many_owners_come_and_go_without_closing()
    {
        var store = Store(perGrant: 5, storeWide: 5);
        for (var i = 0; i < 5; i++)
            await store.EnqueueAsync(Record("did:example:a", "m" + i), null, T0, None);

        var now = T0;
        for (var owner = 0; owner < 10_000; owner++)
        {
            now = now.AddSeconds(2);
            var taken = await store.LeaseAsync(new MessageLeaseRequest("did:example:a", null, 1 + owner % 5, 1000, now, new MessageLease("owner-" + owner, now.AddSeconds(1))), None);
            taken.Should().NotBeEmpty();
            OwnerCount(store).Should().BeLessThanOrEqualTo(5);
        }

        // A late close of an early connection ends nothing: its marks were replaced long ago.
        (await store.ReleaseLeasesAsync("owner-0", None)).Should().Be(0);
        (await store.AcknowledgeAsync("did:example:a", new[] { "m0", "m1", "m2", "m3", "m4" }, None)).Should().Be(5);
        OwnerCount(store).Should().Be(0);
        GrantCount(store).Should().Be(0);
    }

    public static TheoryData<string> WaysARecordLeaves => new() { "acknowledged", "recipient purged", "grant purged", "expired and swept", "expired and the grant touched", "expired when a refused enqueue touched the grant" };

    [Theory]
    [MemberData(nameof(WaysARecordLeaves))]
    public async Task A_record_that_leaves_takes_its_owner_entry_and_its_empty_grant_bucket_with_it(string how)
    {
        var store = Store(perGrant: 1, storeWide: 4);
        (await store.EnqueueAsync(Record("did:example:a", "m", T0.AddSeconds(10)), new MessageLease("owner", T0.AddSeconds(60)), T0, None)).Should().Be(EnqueueOutcome.Enqueued);
        OwnerCount(store).Should().Be(1);

        switch (how)
        {
            case "acknowledged":
                await store.AcknowledgeAsync("did:example:a", new[] { "m" }, None);
                break;
            case "recipient purged":
                await store.PurgeRecipientAsync("did:example:a", "did:example:a", None);
                break;
            case "grant purged":
                await store.PurgeGrantAsync("did:example:a", None);
                break;
            case "expired and swept":
                await store.PurgeExpiredAsync(T0.AddSeconds(10), None);
                break;
            case "expired and the grant touched":
                await store.LeaseAsync(new MessageLeaseRequest("did:example:a", null, 1, 10, T0.AddSeconds(10), new MessageLease(null, T0.AddSeconds(70))), None);
                break;
            default:
                // The refused enqueue purges the grant's expired record first; the per-grant byte bound then refuses the new one.
                (await store.EnqueueAsync(Record("did:example:a", "too-big", size: 5_000), null, T0.AddSeconds(10), None)).Should().Be(EnqueueOutcome.GrantByteLimitReached);
                break;
        }

        store.MessageCount.Should().Be(0);
        store.TotalBytes.Should().Be(0);
        OwnerCount(store).Should().Be(0, "an owner entry does not outlive the record it marks");
        GrantCount(store).Should().Be(0, "a grant bucket does not outlive its last record");
        (await store.ReleaseLeasesAsync("owner", None)).Should().Be(0);
    }

    // ---- the cost of one request under the one lock (measured; see the report) --------------------

    /// <summary>
    /// A peer whose whole queue is leased asks again and again: every <c>delivery-request</c> and
    /// every <c>status-request</c> walks its grant's records under the store's one lock. The walk is
    /// bounded by the peer's own <c>MaxQueueMessages</c> — not by what other grants hold — so one
    /// peer cannot make everyone else's requests wait on the size of the store.
    /// </summary>
    [Fact]
    public async Task One_peers_requests_cost_a_walk_of_its_own_grant_and_not_of_the_store()
    {
        var store = new InMemoryMessageStore();
        var now = T0;
        // The attacker's grant at its bound, all leased; and 4 900 records of other grants.
        for (var i = 0; i < 100; i++)
            await store.EnqueueAsync(Record("did:example:attacker", "m" + i, size: 100), new MessageLease(null, now.AddHours(1)), now, None);
        for (var g = 0; g < 49; g++)
        {
            for (var i = 0; i < 100; i++)
                await store.EnqueueAsync(Record("did:example:other-" + g, "m" + i, size: 100), null, now, None);
        }
        store.MessageCount.Should().Be(5_000);

        const int Requests = 20_000;
        var request = new MessageLeaseRequest("did:example:attacker", null, 10, 564_525, now, new MessageLease(null, now.AddSeconds(60)));
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Requests; i++)
        {
            (await store.LeaseAsync(request, None)).Should().BeEmpty();
            (await store.GetStatusAsync("did:example:attacker", null, now, None)).MessageCount.Should().Be(100);
        }
        clock.Stop();
        output.WriteLine($"Lease + status of a fully leased grant of 100 records in a store of 5 000: {clock.Elapsed.TotalMilliseconds * 1000 / Requests:F2} µs per request pair ({Requests} pairs in {clock.ElapsedMilliseconds} ms).");

        // 100 records per walk, two walks: microseconds. A walk of the whole store would be 50 times that.
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
