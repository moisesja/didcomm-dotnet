namespace DidComm.Mediator.Pickup;

/// <summary>
/// Process-local <see cref="IMessageStore"/> (D1): queued messages live in memory and are gone when
/// the process stops. The default store of <c>AddMessagePickup()</c>, meant for samples, tests and
/// small hosts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bounds.</strong> At most <see cref="MessagePickupOptions.MaxQueueMessages"/> records and
/// <see cref="MessagePickupOptions.MaxQueueBytes"/> payload bytes per grant, and at most
/// <see cref="MessagePickupOptions.MaxStoreMessages"/> records and
/// <see cref="MessagePickupOptions.MaxStoreBytes"/> payload bytes in all. Every retained record
/// counts, whatever its state. Worst-case memory is the store-wide byte bound plus the keys of the
/// store-wide record bound (two DIDs of up to 2048 characters and an id per record, about 8 KiB):
/// roughly 170 MiB at the defaults. Records without an expiry stay until they are acknowledged or
/// purged; with a mediation policy that grants anyone, self-made grants can fill the store-wide
/// bounds, after which new messages are refused for everyone while what is already held stays
/// deliverable.
/// </para>
/// <para>
/// <strong>Concurrency.</strong> One lock guards everything, so the bound check and the insert, and
/// the selection and the lease mark, are each a single step. No operation waits, calls out or hands
/// anything out to lock on.
/// </para>
/// <para>
/// <strong>Expiry</strong> is applied when a grant is next touched (enqueue, lease, status) and by
/// <see cref="PurgeExpiredAsync"/>; there is no timer. A lease that has run out is simply no longer
/// honoured by the next <see cref="LeaseAsync"/>.
/// </para>
/// </remarks>
public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GrantQueue> _grants = new(StringComparer.Ordinal);

    // lease owner token → the records currently marked with it. Touched only under _gate.
    private readonly Dictionary<string, HashSet<Entry>> _owners = new(StringComparer.Ordinal);

    private readonly int _maxQueueMessages;
    private readonly long _maxQueueBytes;
    private readonly int _maxStoreMessages;
    private readonly long _maxStoreBytes;
    private int _count;
    private long _bytes;

    /// <summary>Create a store with the default bounds of <see cref="MessagePickupOptions"/>.</summary>
    public InMemoryMessageStore() : this(new MessagePickupOptions()) { }

    /// <summary>Create a store with the bounds in <paramref name="options"/> (read once, here).</summary>
    /// <param name="options">Supplies <c>MaxQueueMessages</c>, <c>MaxQueueBytes</c>, <c>MaxStoreMessages</c> and <c>MaxStoreBytes</c>.</param>
    public InMemoryMessageStore(MessagePickupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _maxQueueMessages = options.MaxQueueMessages;
        _maxQueueBytes = options.MaxQueueBytes;
        _maxStoreMessages = options.MaxStoreMessages;
        _maxStoreBytes = options.MaxStoreBytes;
    }

    /// <summary>The number of records held, across all grants and in any state.</summary>
    public int MessageCount
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>The payload total of the records held, in bytes.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_gate)
                return _bytes;
        }
    }

    /// <inheritdoc />
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
            _grants.TryGetValue(message.GrantPeerDid, out var queue);
            if (queue is not null)
                PurgeExpired(queue, now);

            var refusal = Refusal(queue, message.Id, size);
            if (refusal is { } outcome)
            {
                if (queue is not null)
                    DropIfEmpty(message.GrantPeerDid, queue);
                return new ValueTask<EnqueueOutcome>(outcome);
            }

            if (queue is null)
            {
                queue = new GrantQueue();
                _grants.Add(message.GrantPeerDid, queue);
            }

            var entry = new Entry(message);
            entry.Node = queue.Order.AddLast(entry);
            queue.ById.Add(message.Id, entry);
            queue.Bytes += size;
            _count++;
            _bytes += size;
            if (message.ExpiresAt is { } expires && expires < queue.NextExpiry)
                queue.NextExpiry = expires;
            if (lease is not null)
                Mark(entry, lease);
            return new ValueTask<EnqueueOutcome>(EnqueueOutcome.Enqueued);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.GrantPeerDid);
        ArgumentNullException.ThrowIfNull(request.Lease);
        ct.ThrowIfCancellationRequested();

        if (request.MaxMessages <= 0)
            return new ValueTask<IReadOnlyList<QueuedMessage>>(Array.Empty<QueuedMessage>());

        lock (_gate)
        {
            if (!_grants.TryGetValue(request.GrantPeerDid, out var queue))
                return new ValueTask<IReadOnlyList<QueuedMessage>>(Array.Empty<QueuedMessage>());
            PurgeExpired(queue, request.Now);
            DropIfEmpty(request.GrantPeerDid, queue);

            List<QueuedMessage>? taken = null;
            long total = 0;
            for (var node = queue.Order.First; node is not null; node = node.Next)
            {
                var entry = node.Value;
                var message = entry.Message;
                if (request.RecipientDid is not null && !string.Equals(message.RecipientDid, request.RecipientDid, StringComparison.Ordinal))
                    continue;
                if (message.NotBefore is { } notBefore && notBefore > request.Now)
                    continue;
                if (entry.LeaseExpiresAt > request.Now)
                    continue;

                // A prefix of what is deliverable: stop at the first record that does not fit, never
                // skip to a smaller one. The first record is taken whatever its size.
                long size = message.Payload.Length;
                if (taken is not null && size > request.MaxPayloadBytes - total)
                    break;

                Mark(entry, request.Lease);
                (taken ??= new List<QueuedMessage>(Math.Min(request.MaxMessages, queue.ById.Count))).Add(message);
                total += size;
                if (taken.Count >= request.MaxMessages)
                    break;
            }
            return new ValueTask<IReadOnlyList<QueuedMessage>>(taken is null ? Array.Empty<QueuedMessage>() : taken);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        var ids = Copy(messageIds);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_grants.TryGetValue(grantPeerDid, out var queue))
                return new ValueTask<int>(0);
            var removed = 0;
            foreach (var id in ids)
            {
                if (queue.ById.TryGetValue(id, out var entry))
                {
                    Remove(queue, entry);
                    removed++;
                }
            }
            DropIfEmpty(grantPeerDid, queue);
            return new ValueTask<int>(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        var ids = Copy(messageIds);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_grants.TryGetValue(grantPeerDid, out var queue))
                return new ValueTask<int>(0);
            var released = 0;
            foreach (var id in ids)
            {
                if (queue.ById.TryGetValue(id, out var entry) && entry.LeaseExpiresAt != default)
                {
                    Unmark(entry);
                    released++;
                }
            }
            return new ValueTask<int>(released);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(leaseOwner);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_owners.Remove(leaseOwner, out var entries))
                return new ValueTask<int>(0);
            foreach (var entry in entries)
            {
                entry.LeaseOwner = null;
                entry.LeaseExpiresAt = default;
            }
            return new ValueTask<int>(entries.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_grants.TryGetValue(grantPeerDid, out var queue))
                return new ValueTask<QueueStatus>(QueueStatus.Empty);
            PurgeExpired(queue, now);
            DropIfEmpty(grantPeerDid, queue);

            var count = 0;
            long bytes = 0;
            DateTimeOffset? oldest = null;
            DateTimeOffset? newest = null;
            foreach (var entry in queue.Order)
            {
                var message = entry.Message;
                if (recipientDid is not null && !string.Equals(message.RecipientDid, recipientDid, StringComparison.Ordinal))
                    continue;
                if (message.NotBefore is { } notBefore && notBefore > now)
                    continue;
                count++;
                bytes += message.Payload.Length;
                if (oldest is null || message.ReceivedAt < oldest)
                    oldest = message.ReceivedAt;
                if (newest is null || message.ReceivedAt > newest)
                    newest = message.ReceivedAt;
            }
            return new ValueTask<QueueStatus>(count == 0 ? QueueStatus.Empty : new QueueStatus(count, bytes, oldest, newest));
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ArgumentNullException.ThrowIfNull(recipientDid);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_grants.TryGetValue(grantPeerDid, out var queue))
                return new ValueTask<int>(0);
            var removed = RemoveWhere(queue, entry => string.Equals(entry.Message.RecipientDid, recipientDid, StringComparison.Ordinal));
            DropIfEmpty(grantPeerDid, queue);
            return new ValueTask<int>(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_grants.TryGetValue(grantPeerDid, out var queue))
                return new ValueTask<int>(0);
            var removed = RemoveWhere(queue, static _ => true);
            DropIfEmpty(grantPeerDid, queue);
            return new ValueTask<int>(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var removed = 0;
            List<string>? emptied = null;
            foreach (var (grantPeerDid, queue) in _grants)
            {
                removed += PurgeExpired(queue, now);
                if (queue.ById.Count == 0)
                    (emptied ??= new List<string>()).Add(grantPeerDid);
            }
            if (emptied is not null)
            {
                foreach (var grantPeerDid in emptied)
                    _grants.Remove(grantPeerDid);
            }
            return new ValueTask<int>(removed);
        }
    }

    // Callers hold _gate. Why a record cannot be admitted, or null when it can: the id first, then
    // the grant's bounds, then the store's.
    private EnqueueOutcome? Refusal(GrantQueue? queue, string id, long size)
    {
        if (queue is not null && queue.ById.ContainsKey(id))
            return EnqueueOutcome.DuplicateId;
        if ((queue?.ById.Count ?? 0) >= _maxQueueMessages)
            return EnqueueOutcome.GrantMessageLimitReached;
        if (size > _maxQueueBytes - (queue?.Bytes ?? 0))
            return EnqueueOutcome.GrantByteLimitReached;
        if (_count >= _maxStoreMessages || size > _maxStoreBytes - _bytes)
            return EnqueueOutcome.StoreLimitReached;
        return null;
    }

    // Everything that can throw happens before the lock (the "no throw after a partial change" rule).
    private static string[] Copy(IReadOnlyList<string> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var ids = new string[messageIds.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(messageIds[i], nameof(messageIds));
            ids[i] = messageIds[i];
        }
        return ids;
    }

    // Callers hold _gate. Removes the grant's expired records when one can exist; the bucket's
    // NextExpiry (the earliest expiry it has seen) keeps the common case to one comparison.
    private int PurgeExpired(GrantQueue queue, DateTimeOffset now)
    {
        if (now < queue.NextExpiry)
            return 0;

        var next = DateTimeOffset.MaxValue;
        var removed = 0;
        for (var node = queue.Order.First; node is not null;)
        {
            var following = node.Next;
            var expires = node.Value.Message.ExpiresAt;
            if (expires is { } at && now >= at)
            {
                Remove(queue, node.Value);
                removed++;
            }
            else if (expires is { } later && later < next)
            {
                next = later;
            }
            node = following;
        }
        queue.NextExpiry = next;
        return removed;
    }

    // Callers hold _gate.
    private int RemoveWhere(GrantQueue queue, Func<Entry, bool> match)
    {
        var removed = 0;
        for (var node = queue.Order.First; node is not null;)
        {
            var following = node.Next;
            if (match(node.Value))
            {
                Remove(queue, node.Value);
                removed++;
            }
            node = following;
        }
        return removed;
    }

    // Callers hold _gate.
    private void Remove(GrantQueue queue, Entry entry)
    {
        Unmark(entry);
        queue.Order.Remove(entry.Node!);
        queue.ById.Remove(entry.Message.Id);
        long size = entry.Message.Payload.Length;
        queue.Bytes -= size;
        _count--;
        _bytes -= size;
    }

    // Callers hold _gate.
    private void DropIfEmpty(string grantPeerDid, GrantQueue queue)
    {
        if (queue.ById.Count == 0)
            _grants.Remove(grantPeerDid);
    }

    // Callers hold _gate. Puts the lease on the record, replacing whatever (expired) lease it had.
    private void Mark(Entry entry, MessageLease lease)
    {
        Unmark(entry);
        entry.LeaseExpiresAt = lease.ExpiresAt;
        entry.LeaseOwner = lease.Owner;
        if (lease.Owner is { } owner)
        {
            if (!_owners.TryGetValue(owner, out var entries))
                _owners[owner] = entries = new HashSet<Entry>();
            entries.Add(entry);
        }
    }

    // Callers hold _gate.
    private void Unmark(Entry entry)
    {
        if (entry.LeaseOwner is { } owner && _owners.TryGetValue(owner, out var entries))
        {
            entries.Remove(entry);
            if (entries.Count == 0)
                _owners.Remove(owner);
        }
        entry.LeaseOwner = null;
        entry.LeaseExpiresAt = default;
    }

    // One stored record with its lease mark. LeaseExpiresAt == default means "not leased"; a lease
    // whose expiry has passed is ignored by the readers and overwritten by the next Mark.
    private sealed class Entry(QueuedMessage message)
    {
        public QueuedMessage Message { get; } = message;
        public LinkedListNode<Entry>? Node { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTimeOffset LeaseExpiresAt { get; set; }
    }

    // A grant's records in arrival order (O(1) append and remove, stable position across leases)
    // with an id index. Touched only under the store's lock.
    private sealed class GrantQueue
    {
        public LinkedList<Entry> Order { get; } = new();
        public Dictionary<string, Entry> ById { get; } = new(StringComparer.Ordinal);
        public long Bytes { get; set; }
        public DateTimeOffset NextExpiry { get; set; } = DateTimeOffset.MaxValue;
    }
}
