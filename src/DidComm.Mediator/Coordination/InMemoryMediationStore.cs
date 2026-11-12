using System.Collections.Concurrent;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// Process-local <see cref="IMediationStore"/> (D1): grants and registrations live in memory and are
/// gone when the process stops. The default store of <c>AddCoordinateMediation()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bounds.</strong> At most <see cref="CoordinateMediationOptions.MaxGrants"/> grants, at
/// most <see cref="CoordinateMediationOptions.MaxRecipientsPerGrant"/> DIDs under one grant and at
/// most <see cref="CoordinateMediationOptions.MaxRecipients"/> DIDs in all. Each key is at most
/// 2048 characters (FR-API-09), so the worst case is about 4 KiB per grant and per registration.
/// Grants do not expire: with a policy that grants anyone, self-made DIDs can fill the store, after
/// which new peers are denied and new registrations fail while existing ones keep working. A
/// mediator open to the public should put an allow-list or a rate limit in its
/// <see cref="IMediationPolicy"/> and revoke grants it no longer wants
/// (<see cref="RevokeAsync"/>).
/// </para>
/// <para>
/// <strong>Concurrency.</strong> Every change happens under one lock, so "is this DID free?" and
/// "register it" are a single step and two peers can never both get the same DID.
/// <see cref="FindGrantByRecipientDidAsync"/>, which the forward path calls for every forward,
/// takes no lock: it is one probe of a concurrent index whose cost does not depend on whether the
/// DID is registered.
/// </para>
/// </remarks>
public sealed class InMemoryMediationStore : IMediationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GrantEntry> _grants = new(StringComparer.Ordinal);

    // recipient DID → the grant that holds it. Written only under _gate; read without it.
    private readonly ConcurrentDictionary<string, GrantEntry> _owners = new(StringComparer.Ordinal);

    private readonly int _maxGrants;
    private readonly int _maxRecipientsPerGrant;
    private readonly int _maxRecipients;
    private int _recipientCount;

    /// <summary>Create a store with the default bounds of <see cref="CoordinateMediationOptions"/>.</summary>
    public InMemoryMediationStore() : this(new CoordinateMediationOptions()) { }

    /// <summary>Create a store with the bounds in <paramref name="options"/> (read once, here).</summary>
    /// <param name="options">Supplies <c>MaxGrants</c>, <c>MaxRecipientsPerGrant</c> and <c>MaxRecipients</c>.</param>
    public InMemoryMediationStore(CoordinateMediationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _maxGrants = options.MaxGrants;
        _maxRecipientsPerGrant = options.MaxRecipientsPerGrant;
        _maxRecipients = options.MaxRecipients;
    }

    /// <summary>The number of grants held.</summary>
    public int GrantCount
    {
        get
        {
            lock (_gate)
                return _grants.Count;
        }
    }

    /// <summary>The number of recipient DIDs registered across all grants.</summary>
    public int RecipientCount => Volatile.Read(ref _recipientCount);

    /// <inheritdoc />
    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peerDid);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return new ValueTask<MediationGrant?>(_grants.TryGetValue(peerDid, out var entry) ? entry.Grant : null);
    }

    /// <inheritdoc />
    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(grant.PeerDid);
        ArgumentNullException.ThrowIfNull(grant.RoutingDids);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_grants.TryGetValue(grant.PeerDid, out var existing))
                return new ValueTask<MediationGrant?>(existing.Grant);
            if (_grants.Count >= _maxGrants)
                return new ValueTask<MediationGrant?>((MediationGrant?)null);

            // Keep a private copy of the list: the caller's must not be able to change a stored grant.
            var stored = grant with { RoutingDids = Array.AsReadOnly(grant.RoutingDids.ToArray()) };
            _grants.Add(stored.PeerDid, new GrantEntry(stored));
            return new ValueTask<MediationGrant?>(stored);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peerDid);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_grants.Remove(peerDid, out var entry))
                return new ValueTask<bool>(false);
            foreach (var did in entry.Order)
                _owners.TryRemove(did, out _);
            _recipientCount -= entry.Order.Count;
            entry.Clear();
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peerDid);
        ArgumentNullException.ThrowIfNull(updates);
        ct.ThrowIfCancellationRequested();

        // Everything that can throw happens before the lock: once the first update is applied the
        // rest of the batch runs to the end, so the outcomes returned are always what happened.
        var outcomes = new RecipientStoreOutcome[updates.Count];
        for (var i = 0; i < updates.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(updates[i], nameof(updates));
            ArgumentNullException.ThrowIfNull(updates[i].RecipientDid, nameof(updates));
        }

        lock (_gate)
        {
            if (!_grants.TryGetValue(peerDid, out var entry))
            {
                Array.Fill(outcomes, RecipientStoreOutcome.NoGrant);
                return new ValueTask<IReadOnlyList<RecipientStoreOutcome>>(outcomes);
            }

            for (var i = 0; i < updates.Count; i++)
            {
                var update = updates[i];
                outcomes[i] = update.Action == RecipientUpdateAction.Remove
                    ? Remove(entry, update.RecipientDid)
                    : Add(entry, update.RecipientDid);
            }
        }
        return new ValueTask<IReadOnlyList<RecipientStoreOutcome>>(outcomes);
    }

    /// <inheritdoc />
    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peerDid);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_grants.TryGetValue(peerDid, out var entry))
                return new ValueTask<RecipientPage>(new RecipientPage(Array.Empty<string>(), 0));

            var total = entry.Order.Count;
            if (limit == 0 || offset >= total)
                return new ValueTask<RecipientPage>(new RecipientPage(Array.Empty<string>(), total));

            var page = new string[Math.Min(limit, total - offset)];
            var node = entry.Order.First;
            for (var skipped = 0; skipped < offset; skipped++)
                node = node!.Next;
            for (var i = 0; i < page.Length; i++)
            {
                page[i] = node!.Value;
                node = node.Next;
            }
            return new ValueTask<RecipientPage>(new RecipientPage(page, total));
        }
    }

    /// <inheritdoc />
    public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipientDid);
        // One probe, hit or miss; nothing further is looked up for a hit (FR-MED-08, FR-ROUTE-11).
        _owners.TryGetValue(recipientDid, out var entry);
        return new ValueTask<MediationGrant?>(entry?.Grant);
    }

    // Callers hold _gate.
    private RecipientStoreOutcome Add(GrantEntry entry, string did)
    {
        if (_owners.TryGetValue(did, out var owner))
            return ReferenceEquals(owner, entry) ? RecipientStoreOutcome.AlreadyRegistered : RecipientStoreOutcome.OwnedByAnotherGrant;
        if (entry.Order.Count >= _maxRecipientsPerGrant)
            return RecipientStoreOutcome.GrantLimitReached;
        if (_recipientCount >= _maxRecipients)
            return RecipientStoreOutcome.StoreLimitReached;

        entry.Nodes.Add(did, entry.Order.AddLast(did));
        _owners[did] = entry;
        _recipientCount++;
        return RecipientStoreOutcome.Added;
    }

    // Callers hold _gate. "Someone else's" and "nobody's" get the same answer on purpose: a remove
    // must not tell the caller whether another grant holds the DID.
    private RecipientStoreOutcome Remove(GrantEntry entry, string did)
    {
        if (!entry.Nodes.Remove(did, out var node))
            return RecipientStoreOutcome.NotRegistered;
        entry.Order.Remove(node);
        _owners.TryRemove(did, out _);
        _recipientCount--;
        return RecipientStoreOutcome.Removed;
    }

    // Registration order is kept in a linked list (O(1) add and remove, stable paging); the
    // dictionary finds a DID's node. Both are touched only under the store's lock.
    private sealed class GrantEntry(MediationGrant grant)
    {
        public MediationGrant Grant { get; } = grant;
        public LinkedList<string> Order { get; } = new();
        public Dictionary<string, LinkedListNode<string>> Nodes { get; } = new(StringComparer.Ordinal);

        public void Clear()
        {
            Order.Clear();
            Nodes.Clear();
        }
    }
}
