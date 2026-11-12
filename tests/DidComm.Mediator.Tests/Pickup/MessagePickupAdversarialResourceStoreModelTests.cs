using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-2, goal 1: the in-memory store against a model written from the <see cref="IMessageStore"/>
/// remarks alone — every operation, in random order, with expiries, not-before times, leases with
/// and without owners, lease ids that are reused, ids that collide and bounds small enough to be
/// hit all the time. The store's laziness (a grant's expired records leave when the grant is next
/// touched; the store-wide sweep runs only when the store-wide bound would refuse and the
/// earliest-expiry mark has passed) must never show: an enqueue is admitted exactly when the model
/// admits it, a lease hands out exactly the model's records, a status reports exactly the model's.
/// </summary>
public sealed class MessagePickupAdversarialResourceStoreModelTests
{
    private const int MaxQueueMessages = 8;
    private const long MaxQueueBytes = 200;
    private const int MaxStoreMessages = 20;
    private const long MaxStoreBytes = 500;

    private static readonly DateTimeOffset Start = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Grants = { "did:example:g0", "did:example:g1", "did:example:g2", "did:example:g3", "did:example:g4" };
    private static readonly string[] Recipients = { "did:example:r0", "did:example:r1", "did:example:r2" };
    private static readonly string?[] Owners = { null, "owner-0", "owner-1", "owner-2" };

    public static TheoryData<int> Seeds
    {
        get
        {
            var data = new TheoryData<int>();
            for (var seed = 1; seed <= 40; seed++)
                data.Add(seed);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task The_store_admits_leases_and_reports_exactly_what_its_contract_says_whatever_the_order_of_operations(int seed)
    {
        var random = new Random(seed);
        var store = new InMemoryMessageStore(new MessagePickupOptions
        {
            MaxQueueMessages = MaxQueueMessages,
            MaxQueueBytes = MaxQueueBytes,
            MaxStoreMessages = MaxStoreMessages,
            MaxStoreBytes = MaxStoreBytes,
        });
        var model = new Model();
        var now = Start;
        var leaseIds = new List<string> { "never-used" };
        var none = CancellationToken.None;

        for (var step = 0; step < 1500; step++)
        {
            // Time never runs backwards here (the realistic case; the other is tested apart), but it
            // often stands still, and it sometimes jumps past every expiry at once.
            now += random.Next(8) switch
            {
                0 or 1 or 2 => TimeSpan.Zero,
                7 when random.Next(10) == 0 => TimeSpan.FromSeconds(60),
                _ => TimeSpan.FromSeconds(random.Next(1, 4)),
            };
            model.Expire(now);
            var where = $"seed {seed}, step {step}";

            // In a third of the steps the store is swept first: then even the counts that depend on
            // what is physically held must equal the model's.
            var exact = random.Next(3) == 0;
            if (exact)
                await store.PurgeExpiredAsync(now, none);

            var grant = Grants[random.Next(Grants.Length)];
            switch (random.Next(100))
            {
                case < 38:
                {
                    var id = random.Next(4) == 0 ? "id-" + random.Next(12) : "id-" + step;
                    var size = random.Next(1, 60);
                    DateTimeOffset? notBefore = random.Next(4) == 0 ? now + TimeSpan.FromSeconds(random.Next(-2, 12)) : null;
                    DateTimeOffset? expiresAt = random.Next(2) == 0 ? now + TimeSpan.FromSeconds(random.Next(-2, 25)) : null;
                    var message = new QueuedMessage(id, grant, Recipients[random.Next(Recipients.Length)], new byte[size], now - TimeSpan.FromSeconds(random.Next(0, 5)), notBefore, expiresAt);
                    MessageLease? lease = random.Next(4) == 0 ? NewLease(random, now, leaseIds) : null;

                    var actual = await store.EnqueueAsync(message, lease, now, none);
                    var expected = model.Enqueue(message, lease, now);
                    actual.Should().Be(expected, where);
                    break;
                }
                case < 60:
                {
                    var request = new MessageLeaseRequest(
                        grant,
                        random.Next(3) == 0 ? Recipients[random.Next(Recipients.Length)] : null,
                        random.Next(-1, 6),
                        random.Next(5) == 0 ? random.Next(-10, 5) : random.Next(1, 160),
                        now,
                        NewLease(random, now, leaseIds));

                    var actual = await store.LeaseAsync(request, none);
                    var expected = model.Lease(request);
                    actual.Select(m => m.Id).Should().Equal(expected, where);
                    break;
                }
                case < 70:
                {
                    var ids = Enumerable.Range(0, random.Next(0, 5)).Select(_ => model.SomeId(random, grant, step)).ToArray();
                    var actual = await store.AcknowledgeAsync(grant, ids, none);
                    var expected = model.Acknowledge(grant, ids);
                    Compare(actual, expected, ids.Distinct().Count(), exact, where);
                    break;
                }
                case < 78:
                {
                    var ids = Enumerable.Range(0, random.Next(0, 5)).Select(_ => model.SomeId(random, grant, step)).ToArray();
                    var leaseId = leaseIds[random.Next(leaseIds.Count)];
                    var actual = await store.ReleaseAsync(grant, ids, leaseId, none);
                    var expected = model.Release(grant, ids, leaseId);
                    Compare(actual, expected, ids.Distinct().Count(), exact, where);
                    break;
                }
                case < 83:
                {
                    var owner = Owners[random.Next(1, Owners.Length)]!;
                    var actual = await store.ReleaseLeasesAsync(owner, none);
                    var expected = model.ReleaseOwner(owner);
                    Compare(actual, expected, MaxStoreMessages, exact, where);
                    break;
                }
                case < 93:
                {
                    var recipient = random.Next(3) == 0 ? Recipients[random.Next(Recipients.Length)] : null;
                    var actual = await store.GetStatusAsync(grant, recipient, now, none);
                    actual.Should().Be(model.Status(grant, recipient, now), where);
                    break;
                }
                case < 96:
                {
                    var recipient = Recipients[random.Next(Recipients.Length)];
                    var actual = await store.PurgeRecipientAsync(grant, recipient, none);
                    var expected = model.PurgeWhere(r => r.Message.GrantPeerDid == grant && r.Message.RecipientDid == recipient);
                    Compare(actual, expected, MaxQueueMessages, exact, where);
                    break;
                }
                case < 98:
                {
                    var actual = await store.PurgeGrantAsync(grant, none);
                    var expected = model.PurgeWhere(r => r.Message.GrantPeerDid == grant);
                    Compare(actual, expected, MaxQueueMessages, exact, where);
                    break;
                }
                default:
                    (await store.PurgeExpiredAsync(now, none)).Should().BeLessThanOrEqualTo(MaxStoreMessages, where);
                    exact = true;
                    break;
            }

            // The bounds hold on what is physically held, at every instant.
            store.MessageCount.Should().BeLessThanOrEqualTo(MaxStoreMessages, where);
            store.TotalBytes.Should().BeLessThanOrEqualTo(MaxStoreBytes, where);
            store.MessageCount.Should().BeGreaterThanOrEqualTo(model.Count, where);
            if (exact)
            {
                store.MessageCount.Should().Be(model.Count, where);
                store.TotalBytes.Should().Be(model.Bytes, where);
            }
        }

        // The counters return to zero with the last record: nothing drifted.
        foreach (var grant in Grants)
            await store.PurgeGrantAsync(grant, none);
        store.MessageCount.Should().Be(0);
        store.TotalBytes.Should().Be(0);
    }

    private static MessageLease NewLease(Random random, DateTimeOffset now, List<string> leaseIds)
    {
        // Mostly a lease of its own; sometimes one whose id a host reuses; sometimes born expired.
        var lease = new MessageLease(Owners[random.Next(Owners.Length)], now + TimeSpan.FromSeconds(random.Next(-1, 12)));
        if (random.Next(6) == 0)
            lease = lease with { Id = leaseIds[random.Next(leaseIds.Count)] };
        leaseIds.Add(lease.Id);
        if (leaseIds.Count > 40)
            leaseIds.RemoveAt(1);
        return lease;
    }

    // Counts of what was removed or released depend on what the store still physically holds of the
    // records that are already expired: exact right after a sweep, at least the model's otherwise.
    private static void Compare(int actual, int expected, int atMost, bool exact, string where)
    {
        if (exact)
            actual.Should().Be(expected, where);
        else
            actual.Should().BeInRange(expected, Math.Max(expected, atMost), where);
    }

    /// <summary>The contract of <see cref="IMessageStore"/>, written the slow and obvious way.</summary>
    private sealed class Model
    {
        public sealed class Record(QueuedMessage message)
        {
            public QueuedMessage Message { get; } = message;
            public string? LeaseId { get; set; }
            public string? LeaseOwner { get; set; }
            public DateTimeOffset LeaseExpiresAt { get; set; }
        }

        // Every held record in arrival order (per grant it is the same order).
        private readonly List<Record> _records = new();

        public int Count => _records.Count;
        public long Bytes => _records.Sum(r => (long)r.Message.Payload.Length);

        /// <summary>A record is expired when now >= ExpiresAt; time does not run backwards, so it never comes back.</summary>
        public void Expire(DateTimeOffset now) => _records.RemoveAll(r => r.Message.ExpiresAt is { } at && now >= at);

        public string SomeId(Random random, string grant, int step)
        {
            var own = _records.Where(r => r.Message.GrantPeerDid == grant).ToArray();
            return random.Next(4) switch
            {
                0 => "id-" + random.Next(12),
                1 when _records.Count > 0 => _records[random.Next(_records.Count)].Message.Id,
                _ when own.Length > 0 => own[random.Next(own.Length)].Message.Id,
                _ => "id-" + random.Next(step + 1),
            };
        }

        public EnqueueOutcome Enqueue(QueuedMessage message, MessageLease? lease, DateTimeOffset now)
        {
            Expire(now);
            var own = _records.Where(r => r.Message.GrantPeerDid == message.GrantPeerDid).ToArray();
            long size = message.Payload.Length;
            if (own.Any(r => r.Message.Id == message.Id))
                return EnqueueOutcome.DuplicateId;
            if (own.Length >= MaxQueueMessages)
                return EnqueueOutcome.GrantMessageLimitReached;
            if (own.Sum(r => (long)r.Message.Payload.Length) + size > MaxQueueBytes)
                return EnqueueOutcome.GrantByteLimitReached;
            if (_records.Count >= MaxStoreMessages || Bytes + size > MaxStoreBytes)
                return EnqueueOutcome.StoreLimitReached;

            var record = new Record(message);
            if (lease is not null)
                Mark(record, lease);
            _records.Add(record);
            return EnqueueOutcome.Enqueued;
        }

        public IReadOnlyList<string> Lease(MessageLeaseRequest request)
        {
            Expire(request.Now);
            var taken = new List<string>();
            if (request.MaxMessages <= 0)
                return taken;
            var budget = Math.Max(0L, request.MaxPayloadBytes);
            long total = 0;
            foreach (var record in _records.Where(r => r.Message.GrantPeerDid == request.GrantPeerDid))
            {
                var message = record.Message;
                if (request.RecipientDid is not null && message.RecipientDid != request.RecipientDid)
                    continue;
                if (message.NotBefore is { } notBefore && notBefore > request.Now)
                    continue;
                if (record.LeaseId is not null && record.LeaseExpiresAt > request.Now)
                    continue;
                long size = message.Payload.Length;
                if (taken.Count > 0 && total + size > budget)
                    break;
                Mark(record, request.Lease);
                taken.Add(message.Id);
                total += size;
                if (taken.Count >= request.MaxMessages)
                    break;
            }
            return taken;
        }

        public int Acknowledge(string grant, IReadOnlyList<string> ids)
            => _records.RemoveAll(r => r.Message.GrantPeerDid == grant && ids.Contains(r.Message.Id));

        public int Release(string grant, IReadOnlyList<string> ids, string leaseId)
        {
            var released = 0;
            foreach (var record in _records.Where(r => r.Message.GrantPeerDid == grant && ids.Contains(r.Message.Id) && r.LeaseId == leaseId))
            {
                Unmark(record);
                released++;
            }
            return released;
        }

        public int ReleaseOwner(string owner)
        {
            var released = 0;
            foreach (var record in _records.Where(r => r.LeaseId is not null && r.LeaseOwner == owner))
            {
                Unmark(record);
                released++;
            }
            return released;
        }

        public QueueStatus Status(string grant, string? recipient, DateTimeOffset now)
        {
            Expire(now);
            var seen = _records
                .Where(r => r.Message.GrantPeerDid == grant)
                .Where(r => recipient is null || r.Message.RecipientDid == recipient)
                .Where(r => r.Message.NotBefore is not { } notBefore || notBefore <= now)
                .Select(r => r.Message)
                .ToArray();
            return seen.Length == 0
                ? QueueStatus.Empty
                : new QueueStatus(seen.Length, seen.Sum(m => (long)m.Payload.Length), seen.Min(m => m.ReceivedAt), seen.Max(m => m.ReceivedAt));
        }

        public int PurgeWhere(Func<Record, bool> match) => _records.RemoveAll(r => match(r));

        private static void Mark(Record record, MessageLease lease)
        {
            record.LeaseId = lease.Id;
            record.LeaseOwner = lease.Owner;
            record.LeaseExpiresAt = lease.ExpiresAt;
        }

        private static void Unmark(Record record)
        {
            record.LeaseId = null;
            record.LeaseOwner = null;
            record.LeaseExpiresAt = default;
        }
    }
}
