using AwesomeAssertions;
using DidComm.Mediator.Pickup;

namespace DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport;

/// <summary>
/// Shared pieces of the store tests (QA-2 of #94): fixed instants, short builders for records and
/// lease requests, and the helpers that keep a test to the sentence it is proving. The store reads
/// no clock, so every test passes the instant it wants judged.
/// </summary>
internal static class MessagePickupStoreKit
{
    public const string Alice = "did:example:alice";
    public const string Bob = "did:example:bob";
    public const string Carol = "did:example:carol";
    public const string R1 = "did:example:recipient-1";
    public const string R2 = "did:example:recipient-2";

    /// <summary>"Now" of a test that does not move time.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A lease expiry no test reaches: the lease is live for the whole test.</summary>
    public static readonly DateTimeOffset Far = T0.AddDays(365);

    /// <summary>The smallest step of time: one tick either side of a boundary.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    public static CancellationToken None => CancellationToken.None;

    /// <summary>
    /// The lease id every lease of these tests carries unless a test says otherwise, so that a
    /// release by ids (which names the lease it gives back) ends whatever lease a test put on a
    /// record. The tests about the lease id itself build their leases with distinct ids.
    /// </summary>
    public const string KitLeaseId = "kit-lease";

    /// <summary>A lease with the shared <see cref="KitLeaseId"/>.</summary>
    public static MessageLease Lease(string? owner, DateTimeOffset expiresAt) => new(owner, expiresAt) { Id = KitLeaseId };

    /// <summary>Give back records taken under the shared <see cref="KitLeaseId"/>.</summary>
    public static ValueTask<int> ReleaseAsync(this IMessageStore store, string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
        => store.ReleaseAsync(grantPeerDid, messageIds, KitLeaseId, ct);

    /// <summary>A store whose bounds are out of the way unless the test sets them.</summary>
    public static InMemoryMessageStore NewStore(int queueMessages = 10_000, long queueBytes = 1L << 30, int? storeMessages = null, long? storeBytes = null)
        => new(new MessagePickupOptions
        {
            MaxQueueMessages = queueMessages,
            MaxQueueBytes = queueBytes,
            MaxStoreMessages = storeMessages ?? Math.Max(queueMessages, 1_000_000),
            MaxStoreBytes = storeBytes ?? Math.Max(queueBytes, 1L << 40),
        });

    /// <summary>A record; the recipient DID defaults to the grant's own DID and the payload to <paramref name="size"/> bytes derived from the id.</summary>
    public static QueuedMessage Message(
        string id,
        string grant = Alice,
        string? recipient = null,
        int size = 10,
        DateTimeOffset? receivedAt = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null)
        => new(id, grant, recipient ?? grant, Bytes(size, id), receivedAt ?? T0, notBefore, expiresAt);

    /// <summary><paramref name="size"/> bytes that differ from record to record.</summary>
    public static byte[] Bytes(int size, string seed)
    {
        var bytes = new byte[size];
        var start = (byte)seed.Aggregate(17, (hash, c) => unchecked(hash * 31 + c));
        for (var i = 0; i < size; i++)
            bytes[i] = (byte)(start + i * 7);
        return bytes;
    }

    /// <summary>A lease request: everything of the grant, unbounded, at <see cref="T0"/>, under a lease nobody owns that outlives the test — unless said otherwise.</summary>
    public static MessageLeaseRequest Take(
        string grant = Alice,
        int max = int.MaxValue,
        long bytes = long.MaxValue,
        DateTimeOffset? now = null,
        string? recipient = null,
        string? owner = null,
        DateTimeOffset? until = null)
        => new(grant, recipient, max, bytes, now ?? T0, Lease(owner, until ?? Far));

    /// <summary>Enqueue and require that the record was admitted.</summary>
    public static async Task AddAsync(this IMessageStore store, QueuedMessage message, MessageLease? lease = null, DateTimeOffset? now = null)
        => (await store.EnqueueAsync(message, lease, now ?? T0, None)).Should().Be(EnqueueOutcome.Enqueued);

    /// <summary>Enqueue plain 10-byte records with these ids under <paramref name="grant"/>, in this order.</summary>
    public static async Task AddAsync(this IMessageStore store, string grant, params string[] ids)
    {
        foreach (var id in ids)
            await store.AddAsync(Message(id, grant));
    }

    /// <summary>Lease and return the ids, in the order the store handed them out.</summary>
    public static async Task<string[]> LeaseIdsAsync(this IMessageStore store, MessageLeaseRequest request)
        => (await store.LeaseAsync(request, None)).Select(m => m.Id).ToArray();

    /// <summary>What the store holds as its counters and a grant's status report it: the triple no lease or release may change.</summary>
    public static async Task<(int Count, long Bytes, QueueStatus Status)> HeldAsync(this InMemoryMessageStore store, string grant = Alice, DateTimeOffset? now = null)
        => (store.MessageCount, store.TotalBytes, await store.GetStatusAsync(grant, null, now ?? T0, None));

    /// <summary>
    /// Raise (never lower) the thread-pool floor so that <paramref name="parties"/> workers parked on
    /// a barrier do not starve the tests that run in parallel with this one: CI runners have two cores
    /// and the pool adds threads slowly.
    /// </summary>
    public static void RaiseThreadPoolFloor(int parties)
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var needed = Environment.ProcessorCount + 2 * parties;
        if (worker < needed)
            ThreadPool.SetMinThreads(needed, io);
    }
}
