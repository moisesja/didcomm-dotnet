using DidComm.Mediator.Pickup;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// A second <see cref="IMessageStore"/>, written from the contract text on <see cref="IMessageStore"/>
/// alone (not from <see cref="InMemoryMessageStore"/>), as a host's own store would be: the stand-in
/// for "a host store" in the direct-push tests (#110). Deliberately plain: one lock, one flat list of
/// records in arrival order, every operation a walk over it. Thread-safe; no clock of its own.
/// </summary>
/// <remarks>
/// What it keeps, per the contract: records confined to their grant; a lease as a mark on the
/// record (id, owner, expiry), ended only by the lease named; the pass-over mark and count; the push
/// mark (<see cref="QueuedMessage.PushTaken"/>) set in the same step as a push lease's take,
/// with the id of the lease that set it, and cleared only by that lease's
/// <see cref="ReturnUnsentAsync"/> or <see cref="PassOverAsync"/>; one arrival sequence for the
/// whole store; bounds checked and the record inserted in one step; expired records neither handed
/// out, reported nor counted (removed whenever an operation is judged at an instant).
/// </remarks>
internal sealed class ReferenceMessageStore : IMessageStore
{
    private readonly object _gate = new();
    private readonly List<Held> _records = new();
    private readonly int _maxGrantMessages;
    private readonly long _maxGrantBytes;
    private readonly int _maxStoreMessages;
    private readonly long _maxStoreBytes;
    private long _sequence;

    public ReferenceMessageStore() : this(new MessagePickupOptions()) { }

    public ReferenceMessageStore(MessagePickupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxGrantMessages = options.MaxQueueMessages;
        _maxGrantBytes = options.MaxQueueBytes;
        _maxStoreMessages = options.MaxStoreMessages;
        _maxStoreBytes = options.MaxStoreBytes;
    }

    /// <summary>The number of records held, in any state (expired ones not yet dropped included).</summary>
    public int MessageCount
    {
        get
        {
            lock (_gate)
                return _records.Count;
        }
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Id);
        ArgumentNullException.ThrowIfNull(message.GrantPeerDid);
        ArgumentNullException.ThrowIfNull(message.RecipientDid);
        ct.ThrowIfCancellationRequested();
        long size = message.Payload.Length;
        lock (_gate)
        {
            // Expired records never keep a new message out.
            DropExpired(now);
            var grantCount = 0;
            long grantBytes = 0;
            long storeBytes = 0;
            foreach (var held in _records)
            {
                storeBytes += held.Message.Payload.Length;
                if (!Same(held.Message.GrantPeerDid, message.GrantPeerDid))
                    continue;
                if (Same(held.Message.Id, message.Id))
                    return ValueTask.FromResult(EnqueueOutcome.DuplicateId);
                grantCount++;
                grantBytes += held.Message.Payload.Length;
            }
            if (grantCount >= _maxGrantMessages)
                return ValueTask.FromResult(EnqueueOutcome.GrantMessageLimitReached);
            if (grantBytes + size > _maxGrantBytes)
                return ValueTask.FromResult(EnqueueOutcome.GrantByteLimitReached);
            if (_records.Count >= _maxStoreMessages || storeBytes + size > _maxStoreBytes)
                return ValueTask.FromResult(EnqueueOutcome.StoreLimitReached);

            // The sequence and the mark are the store's: whatever the caller set is ignored.
            var record = new Held(message with { Sequence = ++_sequence, PushTaken = false });
            if (lease is not null)
                record.Take(lease);
            _records.Add(record);
            return ValueTask.FromResult(EnqueueOutcome.Enqueued);
        }
    }

    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.GrantPeerDid);
        ArgumentNullException.ThrowIfNull(request.Lease);
        ct.ThrowIfCancellationRequested();
        var taken = new List<QueuedMessage>();
        if (request.MaxMessages <= 0)
            return ValueTask.FromResult<IReadOnlyList<QueuedMessage>>(taken);
        var now = request.Now;
        lock (_gate)
        {
            DropExpired(now);
            long total = 0;
            foreach (var held in _records)
            {
                var m = held.Message;
                var deliverable = Same(m.GrantPeerDid, request.GrantPeerDid)
                                  && (request.RecipientDid is null || Same(m.RecipientDid, request.RecipientDid))
                                  && !(m.NotBefore > now)
                                  && !(held.LeaseExpiresAt > now && held.LeaseId is not null)
                                  && !(held.PassedOverUntil > now)
                                  && !(request.AfterSequence is { } after && m.Sequence <= after)
                                  && !(request.Lease.ForPush && m.PushTaken);
                if (!deliverable)
                    continue;
                // A prefix of what is deliverable; the first record whatever its size.
                if (taken.Count > 0 && total + m.Payload.Length > Math.Max(0, request.MaxPayloadBytes))
                    break;
                taken.Add(m); // as it was before the take
                total += m.Payload.Length;
                held.Take(request.Lease);
                if (taken.Count >= request.MaxMessages)
                    break;
            }
        }
        return ValueTask.FromResult<IReadOnlyList<QueuedMessage>>(taken);
    }

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        var ids = Ids(messageIds);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(_records.RemoveAll(h => Same(h.Message.GrantPeerDid, grantPeerDid) && ids.Contains(h.Message.Id)));
    }

    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ArgumentNullException.ThrowIfNull(leaseId);
        var ids = Ids(messageIds);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var released = 0;
            foreach (var held in Named(grantPeerDid, ids))
            {
                // Only the lease named; the push mark is never touched here.
                if (Same(held.LeaseId, leaseId))
                {
                    held.EndLease();
                    released++;
                }
            }
            return ValueTask.FromResult(released);
        }
    }

    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ArgumentNullException.ThrowIfNull(leaseId);
        var ids = Ids(messageIds);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var changed = 0;
            foreach (var held in Named(grantPeerDid, ids))
            {
                var ended = Same(held.LeaseId, leaseId);
                if (ended)
                    held.EndLease();
                // The mark this lease set, also when the lease has ended by now.
                var cleared = held.ClearMark(leaseId);
                if (ended || cleared)
                    changed++;
            }
            return ValueTask.FromResult(changed);
        }
    }

    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ArgumentNullException.ThrowIfNull(leaseId);
        var ids = Ids(messageIds);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var passed = 0;
            foreach (var held in Named(grantPeerDid, ids))
            {
                held.ClearMark(leaseId);
                if (!Same(held.LeaseId, leaseId))
                    continue;
                held.EndLease();
                held.PassedOverUntil = until;
                var count = held.Message.PassOvers;
                held.Message = held.Message with { PassOvers = count == int.MaxValue ? count : count + 1 };
                passed++;
            }
            return ValueTask.FromResult(passed);
        }
    }

    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(leaseOwner);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var ended = 0;
            foreach (var held in _records)
            {
                // A lease without an owner is never one of these.
                if (held.LeaseId is not null && held.LeaseOwner is not null && Same(held.LeaseOwner, leaseOwner))
                {
                    held.EndLease();
                    ended++;
                }
            }
            return ValueTask.FromResult(ended);
        }
    }

    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DropExpired(now);
            var visible = _records
                .Select(h => h.Message)
                .Where(m => Same(m.GrantPeerDid, grantPeerDid) && (recipientDid is null || Same(m.RecipientDid, recipientDid)) && !(m.NotBefore > now))
                .ToList();
            var status = visible.Count == 0
                ? QueueStatus.Empty
                : new QueueStatus(visible.Count, visible.Sum(m => (long)m.Payload.Length), visible.Min(m => m.ReceivedAt), visible.Max(m => m.ReceivedAt));
            return ValueTask.FromResult(status with { HighWaterSequence = _sequence });
        }
    }

    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ArgumentNullException.ThrowIfNull(recipientDid);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(_records.RemoveAll(h => Same(h.Message.GrantPeerDid, grantPeerDid) && Same(h.Message.RecipientDid, recipientDid)));
    }

    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(_records.RemoveAll(h => Same(h.Message.GrantPeerDid, grantPeerDid)));
    }

    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(DropExpired(now));
    }

    // Under _gate. A record is expired when now >= ExpiresAt; an expired record may be removed at any time.
    private int DropExpired(DateTimeOffset now) => _records.RemoveAll(h => h.Message.ExpiresAt is { } at && now >= at);

    // Under _gate. The records of the grant with these ids.
    private IEnumerable<Held> Named(string grantPeerDid, HashSet<string> ids)
        => _records.Where(h => Same(h.Message.GrantPeerDid, grantPeerDid) && ids.Contains(h.Message.Id));

    // Read once and checked before anything changes ("no throw after a partial change").
    private static HashSet<string> Ids(IReadOnlyList<string> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in messageIds.ToArray())
            ids.Add(id ?? throw new ArgumentNullException(nameof(messageIds)));
        return ids;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    // One stored record, its lease (LeaseId null: none), its pass-over instant and the id of the push
    // lease that set its push mark (null: unmarked; the mark itself is Message.PushTaken).
    private sealed class Held(QueuedMessage message)
    {
        public QueuedMessage Message { get; set; } = message;
        public string? LeaseId { get; private set; }
        public string? LeaseOwner { get; private set; }
        public DateTimeOffset LeaseExpiresAt { get; private set; }
        public DateTimeOffset PassedOverUntil { get; set; }
        public string? MarkedBy { get; private set; }

        // The lease goes on, and a push lease marks the record in the same step.
        public void Take(MessageLease lease)
        {
            LeaseId = lease.Id;
            LeaseOwner = lease.Owner;
            LeaseExpiresAt = lease.ExpiresAt;
            if (lease.ForPush)
            {
                Message = Message with { PushTaken = true };
                MarkedBy = lease.Id;
            }
        }

        public void EndLease()
        {
            LeaseId = null;
            LeaseOwner = null;
            LeaseExpiresAt = default;
        }

        public bool ClearMark(string leaseId)
        {
            if (MarkedBy is null || !Same(MarkedBy, leaseId))
                return false;
            Message = Message with { PushTaken = false };
            MarkedBy = null;
            return true;
        }
    }
}

/// <summary>The two stores every direct-push test runs against: the library's own and a host's (<see cref="ReferenceMessageStore"/>).</summary>
public enum StoreKind
{
    /// <summary><see cref="InMemoryMessageStore"/>.</summary>
    InMemory,

    /// <summary><see cref="ReferenceMessageStore"/>: written from the contract text, as a host would.</summary>
    Reference,
}

/// <summary>Builds a store of a <see cref="StoreKind"/>.</summary>
internal static class StoreKinds
{
    public static IMessageStore Create(StoreKind kind, MessagePickupOptions? options = null) => kind switch
    {
        StoreKind.InMemory => new InMemoryMessageStore(options ?? new MessagePickupOptions()),
        StoreKind.Reference => new ReferenceMessageStore(options ?? new MessagePickupOptions()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Both kinds, for a <c>[MemberData]</c> theory.</summary>
    public static IEnumerable<object[]> Both => new[] { new object[] { StoreKind.InMemory }, new object[] { StoreKind.Reference } };
}
