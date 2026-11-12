using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport.MessagePickupStoreKit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// <see cref="InMemoryMessageStore"/> under concurrent callers (the "MUST be thread-safe" of the
/// <see cref="IMessageStore"/> contract; FR-PICK-10). Every assertion is an invariant that holds
/// for every interleaving — never which racer won (L-044). The racers are released together by a
/// barrier, phase after phase, so in every phase all of them are making their call at once; the
/// barrier's phase count is asserted, so a test cannot quietly degrade to one caller after another.
/// </summary>
public sealed class MessagePickupStoreConcurrencyTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Run <paramref name="call"/> from <paramref name="racers"/> threads at once, <paramref name="rounds"/>
    /// times: all racers make their call of a round in the same barrier phase. Returns the results
    /// by round, then by racer.
    /// </summary>
    private static async Task<T[][]> RaceAsync<T>(int racers, int rounds, Func<int, int, ValueTask<T>> call)
    {
        RaiseThreadPoolFloor(racers);
        using var barrier = new Barrier(racers);
        var tasks = Enumerable.Range(0, racers).Select(racer => Task.Run(async () =>
        {
            var mine = new T[rounds];
            try
            {
                for (var round = 0; round < rounds; round++)
                {
                    if (!barrier.SignalAndWait(Patience))
                        throw new TimeoutException("The other racers did not arrive.");
                    mine[round] = await call(round, racer);
                }
            }
            finally
            {
                barrier.RemoveParticipant();
            }
            return mine;
        })).ToArray();
        var perRacer = await Task.WhenAll(tasks);

        barrier.CurrentPhaseNumber.Should().Be(rounds, "every round was entered by all racers together");
        return Enumerable.Range(0, rounds).Select(round => perRacer.Select(results => results[round]).ToArray()).ToArray();
    }

    // ---- enqueue is one atomic check-and-insert -------------------------------------------

    [Fact]
    public async Task Racing_enqueues_for_the_last_slots_of_a_grant_admit_exactly_as_many_as_the_bound_allows()
    {
        const int racers = 12, rounds = 150, bound = 5;
        var stores = Enumerable.Range(0, rounds).Select(_ => NewStore(queueMessages: bound)).ToArray();

        var outcomes = await RaceAsync(racers, rounds, (round, racer) => stores[round].EnqueueAsync(Message($"m-{racer}"), null, T0, None));

        for (var round = 0; round < rounds; round++)
        {
            outcomes[round].Count(o => o == EnqueueOutcome.Enqueued).Should().Be(bound);
            outcomes[round].Count(o => o == EnqueueOutcome.GrantMessageLimitReached).Should().Be(racers - bound);
            (stores[round].MessageCount, stores[round].TotalBytes).Should().Be((bound, bound * 10L));
        }
    }

    [Fact]
    public async Task Racing_enqueues_for_the_last_bytes_of_a_grant_never_pass_the_byte_bound()
    {
        // Twelve 30-byte records race for 100 bytes: three fit, whichever three they are.
        const int racers = 12, rounds = 150;
        var stores = Enumerable.Range(0, rounds).Select(_ => NewStore(queueBytes: 100)).ToArray();

        var outcomes = await RaceAsync(racers, rounds, (round, racer) => stores[round].EnqueueAsync(Message($"m-{racer}", size: 30), null, T0, None));

        for (var round = 0; round < rounds; round++)
        {
            outcomes[round].Count(o => o == EnqueueOutcome.Enqueued).Should().Be(3);
            outcomes[round].Count(o => o == EnqueueOutcome.GrantByteLimitReached).Should().Be(racers - 3);
            (stores[round].MessageCount, stores[round].TotalBytes).Should().Be((3, 90L));
        }
    }

    [Fact]
    public async Task Racing_enqueues_of_different_grants_never_pass_the_store_wide_bounds()
    {
        // Each racer has a grant of its own, so only the store-wide bounds can refuse: seven records
        // of 30 bytes fit the count bound (7), six fit the byte bound (200).
        const int racers = 12, rounds = 150;
        var stores = Enumerable.Range(0, rounds).Select(_ => NewStore(queueMessages: 5, queueBytes: 100, storeMessages: 7, storeBytes: 200)).ToArray();

        var outcomes = await RaceAsync(racers, rounds, (round, racer) => stores[round].EnqueueAsync(Message("m", $"did:example:grant-{racer}", size: 30), null, T0, None));

        for (var round = 0; round < rounds; round++)
        {
            outcomes[round].Count(o => o == EnqueueOutcome.Enqueued).Should().Be(6);
            outcomes[round].Count(o => o == EnqueueOutcome.StoreLimitReached).Should().Be(racers - 6);
            (stores[round].MessageCount, stores[round].TotalBytes).Should().Be((6, 180L));
        }
    }

    [Fact]
    public async Task Racing_enqueues_of_one_id_store_exactly_one_record()
    {
        const int racers = 12, rounds = 150;
        var stores = Enumerable.Range(0, rounds).Select(_ => NewStore()).ToArray();

        var outcomes = await RaceAsync(racers, rounds, (round, racer) => stores[round].EnqueueAsync(Message("contested", size: 1 + racer), null, T0, None));

        for (var round = 0; round < rounds; round++)
        {
            outcomes[round].Count(o => o == EnqueueOutcome.Enqueued).Should().Be(1);
            outcomes[round].Count(o => o == EnqueueOutcome.DuplicateId).Should().Be(racers - 1);
            stores[round].MessageCount.Should().Be(1);
            // Whichever racer won, the counters describe the one record that is held.
            var held = (await stores[round].LeaseAsync(Take(), None)).Should().ContainSingle().Subject;
            stores[round].TotalBytes.Should().Be(held.Payload.Length);
        }
    }

    // ---- leaving and releasing are counted once -------------------------------------------

    [Fact]
    public async Task Racing_acknowledgements_of_the_same_ids_remove_each_record_exactly_once()
    {
        const int racers = 12, rounds = 150;
        var stores = new InMemoryMessageStore[rounds];
        for (var round = 0; round < rounds; round++)
        {
            stores[round] = NewStore();
            await stores[round].AddAsync(Alice, "a", "b", "c", "keeper");
            await stores[round].LeaseAsync(Take(max: 2, owner: "socket"), None);
        }

        var removed = await RaceAsync(racers, rounds, (round, racer) => stores[round].AcknowledgeAsync(Alice, racer % 2 == 0 ? new[] { "a", "b", "c" } : new[] { "c", "nope", "b", "a", "a" }, None));

        for (var round = 0; round < rounds; round++)
        {
            removed[round].Sum().Should().Be(3, "the counts returned are the truth: three records left, whoever removed which");
            removed[round].Should().OnlyContain(n => n >= 0 && n <= 3);
            (stores[round].MessageCount, stores[round].TotalBytes).Should().Be((1, 10L));
            (await stores[round].ReleaseLeasesAsync("socket", None)).Should().Be(0);
        }
    }

    [Fact]
    public async Task Racing_releases_end_each_lease_exactly_once()
    {
        const int racers = 12, rounds = 150;
        var stores = new InMemoryMessageStore[rounds];
        for (var round = 0; round < rounds; round++)
        {
            stores[round] = NewStore();
            await stores[round].AddAsync(Alice, "a", "b", "c", "d");
            await stores[round].AddAsync(Bob, "x");
            await stores[round].LeaseAsync(Take(Alice, max: 3, owner: "socket"), None);
            await stores[round].LeaseAsync(Take(Bob, owner: "socket"), None);
        }

        // A third of the racers release by owner, the others by ids: four leases in all, each ended once.
        var ended = await RaceAsync(racers, rounds, (round, racer) => (racer % 3) switch
        {
            0 => stores[round].ReleaseLeasesAsync("socket", None),
            1 => stores[round].ReleaseAsync(Alice, new[] { "a", "b", "c", "d" }, None),
            _ => stores[round].ReleaseAsync(Bob, new[] { "x", "x" }, None),
        });

        for (var round = 0; round < rounds; round++)
        {
            ended[round].Sum().Should().Be(4);
            (await stores[round].LeaseIdsAsync(Take(Alice))).Should().Equal("a", "b", "c", "d");
            (await stores[round].LeaseIdsAsync(Take(Bob))).Should().Equal("x");
            stores[round].MessageCount.Should().Be(5);
        }
    }

    // ---- a live push is admitted and leased in one step (JC-6) -----------------------------

    [Fact]
    public async Task A_record_enqueued_with_a_lease_is_never_taken_by_a_concurrent_lease()
    {
        const int pushers = 4, takers = 8, rounds = 250;
        var store = NewStore();

        var results = await RaceAsync(pushers + takers, rounds, async (round, racer) =>
        {
            if (racer < pushers)
            {
                (await store.EnqueueAsync(Message($"push-{racer}-{round}"), new MessageLease($"socket-{racer}", Far), T0, None)).Should().Be(EnqueueOutcome.Enqueued);
                return 0;
            }
            return (await store.LeaseAsync(Take(max: 10, owner: "taker"), None)).Count;
        });

        results.Sum(round => round.Sum()).Should().Be(0, "every record was leased from birth: there never was an instant at which a lease could take one");
        store.MessageCount.Should().Be(pushers * rounds);
        for (var pusher = 0; pusher < pushers; pusher++)
            (await store.ReleaseLeasesAsync($"socket-{pusher}", None)).Should().Be(rounds);
        (await store.ReleaseLeasesAsync("taker", None)).Should().Be(0);
        (await store.LeaseIdsAsync(Take())).Should().HaveCount(pushers * rounds);
    }

    // ---- everything at once ---------------------------------------------------------------

    [Fact]
    public async Task Concurrent_enqueue_lease_acknowledge_release_and_purge_keep_every_bound_and_every_counter_true()
    {
        const int workers = 8, phases = 100, stepsPerPhase = 30;
        var options = new MessagePickupOptions { MaxQueueMessages = 40, MaxQueueBytes = 4_000, MaxStoreMessages = 120, MaxStoreBytes = 9_000 };
        var store = new InMemoryMessageStore(options);
        var grants = Enumerable.Range(0, 5).Select(i => $"did:example:grant-{i}").ToArray();
        var recipients = new[] { R1, R2, "did:example:recipient-3" };
        var buffer = Bytes(400, "shared");

        // Workers judge at T0. A record is waiting, not yet due at T0 (due by `late`), expired on
        // arrival, or expiring long after `late`; leases never run out. So at `late`, once the
        // expired are purged and the leases released, every surviving record is deliverable.
        var late = T0.AddHours(1);
        var notYetDue = T0.AddMinutes(30);
        var expiredOnArrival = T0.AddSeconds(-1);
        var expiresMuchLater = T0.AddHours(10);

        // Which worker holds a live lease on which record. A record is added when a lease hands it
        // out and taken off before its holder releases or acknowledges it, so a failed add means
        // two live leases held the same record.
        var holders = new ConcurrentDictionary<(string Grant, string Id), int>();
        var violations = new ConcurrentQueue<string>();
        long admitted = 0, admittedExpired = 0, acknowledged = 0, purged = 0, leases = 0, refusals = 0;

        void CheckBounds(string where)
        {
            var count = store.MessageCount;
            var bytes = store.TotalBytes;
            if (count < 0 || count > options.MaxStoreMessages)
                violations.Enqueue($"{where}: MessageCount {count}");
            if (bytes < 0 || bytes > options.MaxStoreBytes)
                violations.Enqueue($"{where}: TotalBytes {bytes}");
        }

        async ValueTask CheckGrantAsync(string where, string grant)
        {
            var status = await store.GetStatusAsync(grant, null, late, None);
            if (status.MessageCount < 0 || status.MessageCount > options.MaxQueueMessages || status.TotalBytes < 0 || status.TotalBytes > options.MaxQueueBytes)
                violations.Enqueue($"{where}: a grant reports {status.MessageCount} records and {status.TotalBytes} bytes");
        }

        // A reader beside the workers, for as long as they run. The workers start only once it is
        // looking, and each of them also looks after every step of its own. It stops when told, and
        // by itself after a while, so that no way out of this test leaves it spinning.
        using var stop = new CancellationTokenSource();
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = Task.Run(async () =>
        {
            var observations = 0;
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (!stop.IsCancellationRequested && started.Elapsed < Patience * 4)
            {
                CheckBounds("observer");
                foreach (var grant in grants)
                    await CheckGrantAsync("observer", grant);
                observations++;
                observing.TrySetResult();
                await Task.Yield();
            }
            return observations;
        });
        await observing.Task.WaitAsync(Patience);

        RaiseThreadPoolFloor(workers + 1);
        using var barrier = new Barrier(workers);
        var running = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker * 7919 + 94);
            var token = $"socket-{worker}";
            var mine = new List<(string Grant, string Id)>();
            var minted = 0;
            try
            {
                for (var step = 0; step < phases * stepsPerPhase; step++)
                {
                    if (step % stepsPerPhase == 0 && !barrier.SignalAndWait(Patience))
                        throw new TimeoutException("The other workers did not arrive.");

                    var grant = grants[random.Next(grants.Length)];
                    var recipient = recipients[random.Next(recipients.Length)];
                    switch (random.Next(100))
                    {
                        case < 40:
                        {
                            var id = $"w{worker}-{minted++}";
                            var shape = random.Next(10);
                            var record = new QueuedMessage(
                                id, grant, recipient, buffer.AsMemory(0, random.Next(0, buffer.Length + 1)), T0.AddSeconds(-random.Next(1000)),
                                NotBefore: shape == 0 ? notYetDue : null,
                                ExpiresAt: shape == 1 ? expiredOnArrival : shape == 2 ? expiresMuchLater : null);
                            var live = shape == 3;
                            var outcome = await store.EnqueueAsync(record, live ? new MessageLease(token, Far) : null, T0, None);
                            if (outcome == EnqueueOutcome.Enqueued)
                            {
                                Interlocked.Increment(ref admitted);
                                if (shape == 1)
                                    Interlocked.Increment(ref admittedExpired);
                                if (live)
                                {
                                    if (!holders.TryAdd((grant, id), worker))
                                        violations.Enqueue("a record leased from birth was already held");
                                    mine.Add((grant, id));
                                }
                            }
                            else if (outcome == EnqueueOutcome.DuplicateId)
                            {
                                violations.Enqueue("DuplicateId for an id that was never used");
                            }
                            else
                            {
                                Interlocked.Increment(ref refusals);
                            }
                            break;
                        }
                        case < 65:
                        {
                            var filter = random.Next(3) == 0 ? recipient : null;
                            var max = random.Next(1, 7);
                            long budget = random.Next(100, 1500);
                            var leased = await store.LeaseAsync(new MessageLeaseRequest(grant, filter, max, budget, T0, new MessageLease(token, Far)), None);
                            Interlocked.Add(ref leases, leased.Count);
                            long total = 0;
                            foreach (var record in leased)
                            {
                                total += record.Payload.Length;
                                if (record.GrantPeerDid != grant || (filter is not null && record.RecipientDid != filter))
                                    violations.Enqueue("a lease returned a record of another grant or recipient");
                                if (record.NotBefore is { } notBefore && notBefore > T0)
                                    violations.Enqueue("a lease returned a record that is not yet due");
                                if (record.ExpiresAt is { } expiresAt && T0 >= expiresAt)
                                    violations.Enqueue("a lease returned an expired record");
                                if (!holders.TryAdd((grant, record.Id), worker))
                                    violations.Enqueue("a record was handed out while another live lease held it");
                                mine.Add((grant, record.Id));
                            }
                            if (leased.Count > max || (leased.Count > 1 && total > budget))
                                violations.Enqueue($"a lease returned {leased.Count} records of {total} bytes for max {max} and budget {budget}");
                            break;
                        }
                        case < 85 when mine.Count > 0:
                        {
                            // A few of this worker's own leases in one grant: acknowledged, or released by id.
                            var acknowledge = random.Next(2) == 0;
                            var inGrant = mine.Where(held => held.Grant == grant).Take(random.Next(1, 4)).ToArray();
                            foreach (var held in inGrant)
                            {
                                mine.Remove(held);
                                holders.TryRemove(held, out _);
                            }
                            var ids = inGrant.Select(held => held.Id).ToArray();
                            var done = acknowledge
                                ? await store.AcknowledgeAsync(grant, ids, None)
                                : await store.ReleaseAsync(grant, ids, None);
                            // Fewer when another worker's purge got there first; never more.
                            if (done < 0 || done > ids.Length)
                                violations.Enqueue($"{(acknowledge ? "acknowledge" : "release")} of {ids.Length} ids returned {done}");
                            if (acknowledge)
                                Interlocked.Add(ref acknowledged, done);
                            break;
                        }
                        case < 90:
                        {
                            // The connection closes: every lease of this worker ends, in every grant.
                            var held = mine.Count;
                            foreach (var key in mine)
                                holders.TryRemove(key, out _);
                            mine.Clear();
                            var ended = await store.ReleaseLeasesAsync(token, None);
                            if (ended < 0 || ended > held)
                                violations.Enqueue($"release by owner returned {ended} for {held} leases");
                            break;
                        }
                        default:
                        {
                            // A DID leaves the grant, a grant is revoked, the host sweeps the expired.
                            var removed = random.Next(10) switch
                            {
                                < 4 => await store.PurgeRecipientAsync(grant, recipient, None),
                                < 6 => await store.PurgeGrantAsync(grant, None),
                                _ => await store.PurgeExpiredAsync(T0, None),
                            };
                            if (removed < 0)
                                violations.Enqueue($"a purge returned {removed}");
                            Interlocked.Add(ref purged, removed);
                            break;
                        }
                    }
                    CheckBounds("worker");
                    await CheckGrantAsync("worker", grant);
                }
            }
            finally
            {
                barrier.RemoveParticipant();
            }
        })).ToArray();
        try
        {
            await Task.WhenAll(running);
        }
        finally
        {
            // Also when a worker failed: the reader must not outlive the test.
            stop.Cancel();
        }
        var observed = await observer;

        barrier.CurrentPhaseNumber.Should().Be(phases, "every phase was entered by all workers together");
        violations.Should().BeEmpty();
        observed.Should().BeGreaterThan(0);
        output.WriteLine($"admitted {admitted} (expired on arrival {admittedExpired}), refused {refusals}, leased {leases}, acknowledged {acknowledged}, purged {purged}, observations {observed}, left {store.MessageCount} records / {store.TotalBytes} bytes");
        admitted.Should().BeGreaterThan(0);
        refusals.Should().BeGreaterThan(0, "the bounds were reached, not merely approached");
        leases.Should().BeGreaterThan(0);

        // Quiescence. What is left, recounted record by record, is what the counters say.
        purged += await store.PurgeExpiredAsync(late, None);
        var count = 0;
        long bytes = 0;
        foreach (var grant in grants)
        {
            var whole = await store.GetStatusAsync(grant, null, late, None);
            whole.MessageCount.Should().BeLessThanOrEqualTo(options.MaxQueueMessages);
            whole.TotalBytes.Should().BeLessThanOrEqualTo(options.MaxQueueBytes);
            var parts = new List<QueueStatus>();
            foreach (var recipient in recipients)
                parts.Add(await store.GetStatusAsync(grant, recipient, late, None));
            parts.Sum(p => p.MessageCount).Should().Be(whole.MessageCount);
            parts.Sum(p => p.TotalBytes).Should().Be(whole.TotalBytes);
            count += whole.MessageCount;
            bytes += whole.TotalBytes;
        }
        (store.MessageCount, store.TotalBytes).Should().Be((count, bytes), "the store-wide counters equal the sum over the grants");

        // Nothing was created or lost: what was admitted was acknowledged, purged, expired on the
        // way (only a record that arrived expired can go that way uncounted) or is still held.
        var expiredOnTheWay = admitted - acknowledged - purged - count;
        expiredOnTheWay.Should().BeInRange(0, admittedExpired);

        // And drained through leases: every survivor is handed out exactly once, in one piece.
        for (var worker = 0; worker < workers; worker++)
            await store.ReleaseLeasesAsync($"socket-{worker}", None);
        var drained = 0;
        long drainedBytes = 0;
        foreach (var grant in grants)
        {
            var records = await store.LeaseAsync(Take(grant, now: late, owner: "drain"), None);
            records.Select(r => r.Id).Should().OnlyHaveUniqueItems();
            drained += records.Count;
            drainedBytes += records.Sum(r => (long)r.Payload.Length);
            (await store.AcknowledgeAsync(grant, records.Select(r => r.Id).ToArray(), None)).Should().Be(records.Count);
        }
        (drained, drainedBytes).Should().Be((count, bytes));
        (store.MessageCount, store.TotalBytes).Should().Be((0, 0L));
        (await store.ReleaseLeasesAsync("drain", None)).Should().Be(0);
        (await store.PurgeExpiredAsync(DateTimeOffset.MaxValue, None)).Should().Be(0);
    }
}
