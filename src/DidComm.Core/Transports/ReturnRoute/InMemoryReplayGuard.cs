using DidComm.Facade;
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
/// <c>created_time &gt; now + ExpiresClockSkew</c>; otherwise its entry expires at
/// <c>created_time + Window + ExpiresClockSkew</c>. A message without <c>created_time</c> is
/// <see cref="ReplayAdmission.MissingCreatedTime"/> unless
/// <see cref="ReplayProtectionOptions.RequireCreatedTime"/> is off, in which case its entry expires
/// at <c>now + Window</c>. The skew is read from <see cref="DidCommOptions.ExpiresClockSkew"/> at
/// call time and clamped to zero or more; the arithmetic is the FR-API-05 expiry's (whole seconds).
/// </para>
/// <para>
/// <strong>Retention property.</strong> An entry is kept until its expiry inclusive (the purge
/// removes strictly earlier expiries) while age rejection starts at <c>now − Window</c>, so there
/// is no instant at which a captured request is neither remembered nor too old (FR-RR-08 (4)).
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
/// by <see cref="ReplayProtectionOptions.MaxMessageIdLength"/> (checked before any allocation).
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

    private readonly ReplayProtectionOptions _options;
    private readonly DidCommOptions _didCommOptions;
    private readonly ILogger<InMemoryReplayGuard>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, long>> _buckets = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(string Peer, string Id), long> _expiry = new();
    private int _count;
    private long _saturated;
    private long _tooLong;

    /// <summary>Create a guard; validates <paramref name="options"/>.</summary>
    /// <param name="options">The replay settings; <see cref="ReplayProtectionOptions.Validate"/> runs here.</param>
    /// <param name="didCommOptions">The facade options, read at call time for <see cref="DidCommOptions.ExpiresClockSkew"/>.</param>
    public InMemoryReplayGuard(ReplayProtectionOptions options, DidCommOptions didCommOptions)
        : this(options, didCommOptions, logger: null) { }

    /// <summary>Create a guard with a logger; validates <paramref name="options"/>.</summary>
    /// <param name="options">The replay settings; <see cref="ReplayProtectionOptions.Validate"/> runs here.</param>
    /// <param name="didCommOptions">The facade options, read at call time for <see cref="DidCommOptions.ExpiresClockSkew"/>.</param>
    /// <param name="logger">Optional logger; lines carry counts only.</param>
    public InMemoryReplayGuard(ReplayProtectionOptions options, DidCommOptions didCommOptions, ILogger<InMemoryReplayGuard>? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(didCommOptions);
        options.Validate();
        _options = options;
        _didCommOptions = didCommOptions;
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
        if (messageId.Length > _options.MaxMessageIdLength)
        {
            RateLimited(ref _tooLong, "Replay admission refused: message id longer than MaxMessageIdLength ({MaxMessageIdLength}). Total refused: {Refused}.", _options.MaxMessageIdLength);
            return ReplayAdmission.MessageIdTooLong;
        }

        var nowS = now.ToUnixTimeSeconds();
        var windowS = (long)Math.Floor(_options.Window.TotalSeconds);
        var skewS = (long)Math.Floor(Math.Max(0, _didCommOptions.ExpiresClockSkew.TotalSeconds));

        long expiresAt;
        if (createdTimeEpochSeconds is long created)
        {
            if (created < nowS - windowS)
                return ReplayAdmission.TooOld;
            if (created > nowS + skewS)
                return ReplayAdmission.FutureDated;
            expiresAt = created + windowS + skewS;
        }
        else if (_options.RequireCreatedTime)
        {
            return ReplayAdmission.MissingCreatedTime;
        }
        else
        {
            expiresAt = nowS + windowS;
        }

        bool saturated;
        lock (_gate)
        {
            Purge(nowS);

            var bucket = _buckets.GetValueOrDefault(peerDidSubject);
            if (bucket is not null && bucket.ContainsKey(messageId))
                return ReplayAdmission.Duplicate;

            saturated = _count >= _options.MaxEntries
                || (bucket is not null && bucket.Count >= _options.MaxEntriesPerPeer);
            if (!saturated)
            {
                if (bucket is null)
                {
                    bucket = new Dictionary<string, long>(StringComparer.Ordinal);
                    _buckets[peerDidSubject] = bucket;
                }
                bucket[messageId] = expiresAt;
                _expiry.Enqueue((peerDidSubject, messageId), expiresAt);
                _count++;
            }
        }

        if (!saturated)
            return ReplayAdmission.Admitted;

        RateLimited(ref _saturated, "Replay admission refused: the guard is saturated (MaxEntries {MaxEntries}, MaxEntriesPerPeer {MaxEntriesPerPeer}); nothing evicted. Total refused: {Refused}.", _options.MaxEntries, _options.MaxEntriesPerPeer);
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
                bucket.Remove(key.Id);
                if (bucket.Count == 0)
                    _buckets.Remove(key.Peer);
            }
            _count--;
        }
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
