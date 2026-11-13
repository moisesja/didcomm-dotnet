using Microsoft.Extensions.Logging;

namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The default <see cref="IInboundReplayGuard"/>: process-local, bounded by
/// <see cref="ReplayProtectionOptions"/>. Registered as a singleton by <c>AddDidComm</c>; a host
/// that registers its own <see cref="IInboundReplayGuard"/> first keeps it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Rules</strong> (FR-RR-08). A timestamped message is <see cref="ReplayAdmission.TooOld"/>
/// when <c>created_time &lt; now − Window</c> and <see cref="ReplayAdmission.FutureDated"/> when
/// <c>created_time &gt; now + ClockSkew</c>; otherwise its entry expires at
/// <c>created_time + Window + ClockSkew</c>. A message without <c>created_time</c> is
/// <see cref="ReplayAdmission.MissingCreatedTime"/> unless
/// <see cref="ReplayProtectionOptions.RequireCreatedTime"/> is off, in which case its entry expires
/// at <c>now + Window</c>. The settings are validated and copied when the guard is built, so a later
/// change to the options object has no effect; <see cref="ReplayProtectionOptions.Window"/> and
/// <see cref="ReplayProtectionOptions.ClockSkew"/> count in whole seconds (floored). The guard reads
/// nothing from <c>DidCommOptions</c>: FR-API-05 expiry and its <c>ExpiresClockSkew</c> are the
/// unpack path's, judged separately.
/// </para>
/// <para>
/// <strong>Retention property.</strong> An entry is kept until its expiry inclusive (the purge
/// removes strictly earlier expiries) while age rejection starts once <c>now &gt; created_time +
/// Window</c>, so there is no instant at which a captured request is neither remembered nor too old
/// (FR-RR-08 (4)) — also for a <c>created_time</c> ahead of the clock, and whatever order the clock
/// readings arrive in: the guard remembers the latest instant it purged at, and refuses as
/// <see cref="ReplayAdmission.TooOld"/> any timestamped message whose entry would already have
/// expired by then (<c>created_time + Window + ClockSkew</c> earlier than that instant), because it
/// may have been forgotten. An entry stamped <c>now + ClockSkew</c>, the latest admitted, is kept
/// <c>Window + 2 × ClockSkew</c> from its admission.
/// </para>
/// <para>
/// <strong>Data structure.</strong> One lock over a per-sender dictionary of <c>id → expiry</c>
/// and a min-heap of <c>(sender, id)</c> by expiry. Every call first pops the expired heap entries
/// and removes them from their bucket — the only removal path, so heap and buckets never diverge
/// and each entry is popped once (amortized O(log n) per insert, serialized by the lock). A
/// duplicate is neither inserted nor refreshed. At <see cref="ReplayProtectionOptions.MaxEntries"/>,
/// or at <see cref="ReplayProtectionOptions.MaxEntriesPerPeer"/> for the sender, the message is
/// <see cref="ReplayAdmission.Saturated"/>: not admitted, nothing evicted — unexpired entries are
/// never evicted to make room, so saturation is counted (<see cref="SaturationCount"/>) and
/// recovers as entries expire. Keys are bounded: the sender subject by <c>DidSubjectOf</c>, the id
/// by <see cref="ReplayProtectionOptions.MaxMessageIdLength"/> (checked before any allocation). Each
/// sender's subject is kept once per bucket however many of its ids are remembered, so an entry
/// costs its id plus about a hundred bytes; see <see cref="ReplayProtectionOptions.MaxEntries"/> for
/// the resulting bounds.
/// </para>
/// <para>
/// <strong>Clock.</strong> Admission and retention use the instant the caller passes
/// (<c>DidCommOptions.Now()</c> at the endpoints and in the client's inbound consumer), read before
/// the guard's lock is taken, so two requests' readings can reach the lock out of order, and a host
/// clock can step back. Neither re-admits a replay (the retention property above). A reading behind
/// the latest purge instant by no more than <c>Window + ClockSkew</c> is judged normally: fresh
/// messages are admitted, replays are <see cref="ReplayAdmission.Duplicate"/>,
/// <see cref="ReplayAdmission.TooOld"/> or <see cref="ReplayAdmission.FutureDated"/>. The price of
/// that guarantee: after the host clock steps back by more than <c>Window + ClockSkew</c> (or jumps
/// forward that far and returns), in-scope messages stamped at the restored time are refused as
/// <see cref="ReplayAdmission.TooOld"/> until the clock is back within <c>Window + ClockSkew</c> of
/// the latest purge instant, or the guard is rebuilt (a process restart). Availability, never
/// integrity: keeping the host clock from stepping back is the host's part of FR-RR-08.
/// </para>
/// <para>
/// <strong>Logging.</strong> Only the flood-shaped outcomes are logged here —
/// <see cref="ReplayAdmission.Saturated"/> and <see cref="ReplayAdmission.MessageIdTooLong"/>, the
/// first occurrence and every 1000th at Warning, counts only. The transport logs each drop with the
/// message id.
/// </para>
/// </remarks>
public sealed class InMemoryReplayGuard : IInboundReplayGuard
{
    private const long LogEvery = 1000;

    // The validated settings, copied at construction (whole seconds for the two durations).
    private readonly long _windowS;
    private readonly long _skewS;
    private readonly bool _requireCreatedTime;
    private readonly int _maxEntries;
    private readonly int _maxEntriesPerPeer;
    private readonly int _maxMessageIdLength;
    private readonly ILogger<InMemoryReplayGuard>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(string Peer, string Id), long> _expiry = new();
    private int _count;
    // Under _gate: the latest instant (epoch seconds) the guard has purged at. Every entry whose
    // expiry is earlier is gone; nothing admitted afterwards expires earlier (see TryAdmit).
    private long _purgedAt = long.MinValue;
    private long _saturated;
    private long _tooLong;

    /// <summary>Create a guard; validates <paramref name="options"/>.</summary>
    /// <param name="options">The replay settings; <see cref="ReplayProtectionOptions.Validate"/> runs here and the values are copied, so later changes to the object have no effect.</param>
    public InMemoryReplayGuard(ReplayProtectionOptions options)
        : this(options, logger: null) { }

    /// <summary>Create a guard with a logger; validates <paramref name="options"/>.</summary>
    /// <param name="options">The replay settings; <see cref="ReplayProtectionOptions.Validate"/> runs here and the values are copied, so later changes to the object have no effect.</param>
    /// <param name="logger">Optional logger; lines carry counts only.</param>
    public InMemoryReplayGuard(ReplayProtectionOptions options, ILogger<InMemoryReplayGuard>? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Read each setting once, so the values validated are the values used (a concurrent change
        // to the options object between Validate() and the copy could otherwise slip through).
        var window = options.Window;
        var skew = options.ClockSkew;
        var snapshot = new ReplayProtectionOptions
        {
            Window = window,
            ClockSkew = skew,
            RequireCreatedTime = options.RequireCreatedTime,
            MaxEntries = options.MaxEntries,
            MaxEntriesPerPeer = options.MaxEntriesPerPeer,
            MaxMessageIdLength = options.MaxMessageIdLength,
        };
        snapshot.Validate();
        _windowS = (long)Math.Floor(window.TotalSeconds);
        _skewS = (long)Math.Floor(skew.TotalSeconds);
        _requireCreatedTime = snapshot.RequireCreatedTime;
        _maxEntries = snapshot.MaxEntries;
        _maxEntriesPerPeer = snapshot.MaxEntriesPerPeer;
        _maxMessageIdLength = snapshot.MaxMessageIdLength;
        _logger = logger;
    }

    /// <summary>Entries currently remembered (expired entries are purged on the next call).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>Senders with at least one remembered entry.</summary>
    public int PeerCount
    {
        get
        {
            lock (_gate)
                return _buckets.Count;
        }
    }

    /// <summary>How many admissions were refused as <see cref="ReplayAdmission.Saturated"/> since construction (the FR-RR-08 metric).</summary>
    public long SaturationCount => Interlocked.Read(ref _saturated);

    /// <inheritdoc />
    public ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerDidSubject);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        if (messageId.Length > _maxMessageIdLength)
        {
            RateLimited(ref _tooLong, "Replay admission refused: message id longer than MaxMessageIdLength ({MaxMessageIdLength}). Total refused: {Refused}.", _maxMessageIdLength);
            return ReplayAdmission.MessageIdTooLong;
        }

        var nowS = now.ToUnixTimeSeconds();
        if (createdTimeEpochSeconds is long created)
        {
            if (created < nowS - _windowS)
                return ReplayAdmission.TooOld;
            if (created > nowS + _skewS)
                return ReplayAdmission.FutureDated;
        }
        else if (_requireCreatedTime)
        {
            return ReplayAdmission.MissingCreatedTime;
        }

        bool saturated;
        lock (_gate)
        {
            // Purging at an instant earlier than one already purged at would remove nothing.
            if (nowS > _purgedAt)
            {
                Purge(nowS);
                _purgedAt = nowS;
            }

            long expiresAt;
            if (createdTimeEpochSeconds is long stamped)
            {
                expiresAt = stamped + _windowS + _skewS;
                // Its entry would already have been purged: it may have been admitted and forgotten.
                // Reachable only with a reading behind _purgedAt (another request read the clock
                // later but took the lock first, or the host clock stepped back); refusing it keeps
                // the retention property whatever order the readings arrive in.
                if (expiresAt < _purgedAt)
                    return ReplayAdmission.TooOld;
            }
            else
            {
                // Timestamp-free (RequireCreatedTime off): remembered for Window from the latest
                // purge instant, never from an earlier reading that the purge has overtaken.
                expiresAt = _purgedAt + _windowS;
            }

            var bucket = _buckets.GetValueOrDefault(peerDidSubject);
            if (bucket is not null && bucket.Ids.ContainsKey(messageId))
                return ReplayAdmission.Duplicate;

            saturated = _count >= _maxEntries
                || (bucket is not null && bucket.Ids.Count >= _maxEntriesPerPeer);
            if (!saturated)
            {
                if (bucket is null)
                {
                    bucket = new Bucket(peerDidSubject);
                    _buckets[peerDidSubject] = bucket;
                }
                bucket.Ids[messageId] = expiresAt;
                // The bucket's own copy of the subject, not the caller's: the caller passes a fresh
                // string per message (the endpoint parses it from the key id each time), and keeping
                // that instance per entry would retain up to 4 KB more per entry than the id itself.
                _expiry.Enqueue((bucket.Peer, messageId), expiresAt);
                _count++;
            }
        }

        if (!saturated)
            return ReplayAdmission.Admitted;

        RateLimited(ref _saturated, "Replay admission refused: the guard is saturated (MaxEntries {MaxEntries}, MaxEntriesPerPeer {MaxEntriesPerPeer}); nothing evicted. Total refused: {Refused}.", _maxEntries, _maxEntriesPerPeer);
        return ReplayAdmission.Saturated;
    }

    // Caller holds _gate. The only removal path: each heap entry is popped exactly once, and the
    // bucket entry it names is removed with it, so the two structures never diverge.
    private void Purge(long nowS)
    {
        while (_expiry.TryPeek(out var key, out var expiresAt) && expiresAt < nowS)
        {
            _expiry.Dequeue();
            if (_buckets.TryGetValue(key.Peer, out var bucket))
            {
                bucket.Ids.Remove(key.Id);
                if (bucket.Ids.Count == 0)
                    _buckets.Remove(key.Peer);
            }
            _count--;
        }
    }

    // One sender's entries. Peer is the single retained copy of the sender subject: the dictionary
    // key and every heap entry of this bucket share it.
    private sealed class Bucket
    {
        internal Bucket(string peer) => Peer = peer;

        internal string Peer { get; }

        internal Dictionary<string, long> Ids { get; } = new(StringComparer.Ordinal);
    }

    private void RateLimited(ref long counter, string message, params object?[] args)
    {
        var n = Interlocked.Increment(ref counter);
        if (n != 1 && n % LogEvery != 0)
            return;
        try
        {
            var withCount = new object?[args.Length + 1];
            args.CopyTo(withCount, 0);
            withCount[args.Length] = n;
            _logger?.LogWarning(message, withCount);
        }
        catch
        {
            // Logging providers are host code; they must not change admission.
        }
    }
}
