using DidComm.Consistency;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The version of one protocol each mediator negotiated through Discover Features (design D-10,
/// §4.4; #100 DC-6), keyed by the mediator's DID subject: at most <see cref="Capacity"/> entries
/// (no insert when full), each valid for <see cref="TimeToLive"/> on the monotonic clock of
/// <see cref="Time"/>. Held by the channel so it outlives one call, one instance per protocol:
/// Coordinate Mediation (<see cref="CoordinateMediationVersion"/>) and Message Pickup
/// (<see cref="MessagePickupVersion"/>); thread-safe. Owner: engineer B.
/// </summary>
/// <typeparam name="TVersion">The protocol's version enum, whose <c>default</c> (<c>Negotiate</c>) is not a negotiated version.</typeparam>
/// <remarks>
/// Every reading of the clock is taken inside the lock, so entries are judged against readings in
/// the order they were taken (L-060). An entry whose timestamp lies ahead of the clock (a clock that
/// stepped back) counts as expired: the cost is one more Discover Features query. When the cache is
/// full, expired entries are dropped first; a full cache of live entries takes no new one, and the
/// caller negotiates again next time. Only the version and a timestamp are retained per entry, with
/// a key no longer than a bare DID (the facade validates the DID before it gets here; L-062).
/// </remarks>
internal sealed class NegotiatedVersionCache<TVersion>
    where TVersion : struct, Enum
{
    /// <summary>The default bound on remembered mediators.</summary>
    internal const int DefaultCapacity = 64;

    /// <summary>The default lifetime of an entry.</summary>
    internal static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Create an empty cache.</summary>
    /// <param name="time">The clock whose timestamps age the entries.</param>
    /// <param name="capacity">The bound on remembered mediators.</param>
    /// <param name="timeToLive">How long an entry is used; <see cref="DefaultTimeToLive"/> when <c>null</c>.</param>
    internal NegotiatedVersionCache(TimeProvider time, int capacity = DefaultCapacity, TimeSpan? timeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Time = time;
        Capacity = capacity;
        TimeToLive = timeToLive ?? DefaultTimeToLive;
    }

    /// <summary>The clock whose timestamps age the entries.</summary>
    internal TimeProvider Time { get; }

    /// <summary>The bound on remembered mediators.</summary>
    internal int Capacity { get; }

    /// <summary>How long an entry is used.</summary>
    internal TimeSpan TimeToLive { get; }

    /// <summary>The number of entries held now, expired ones included until they are dropped.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>The version negotiated with <paramref name="mediatorDid"/>, when one is remembered and has not expired.</summary>
    /// <param name="mediatorDid">The mediator's DID (compared as a DID subject).</param>
    /// <param name="version">The negotiated version when found; <c>default</c> (<c>Negotiate</c>) otherwise.</param>
    internal bool TryGet(string mediatorDid, out TVersion version)
    {
        version = default;
        if (DidSubject.DidSubjectOf(mediatorDid) is not { } key)
            return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return false;
            if (IsExpired(entry, Time.GetTimestamp()))
            {
                _entries.Remove(key);
                return false;
            }
            version = entry.Version;
            return true;
        }
    }

    /// <summary>Remember the version negotiated with <paramref name="mediatorDid"/>; nothing is added when the cache is full.</summary>
    /// <param name="mediatorDid">The mediator's DID (keyed by its DID subject).</param>
    /// <param name="version">A negotiated version: defined, and not <c>default</c> (<c>Negotiate</c>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is <c>default</c> or not a defined value.</exception>
    internal void Set(string mediatorDid, TVersion version)
    {
        if (EqualityComparer<TVersion>.Default.Equals(version, default) || !Enum.IsDefined(version))
            throw new ArgumentOutOfRangeException(nameof(version), "Only a negotiated version is remembered.");
        if (DidSubject.DidSubjectOf(mediatorDid) is not { } key)
            return;

        lock (_gate)
        {
            var now = Time.GetTimestamp();
            if (!_entries.ContainsKey(key) && _entries.Count >= Capacity)
            {
                // Make room from what has expired; never by evicting a live entry.
                List<string>? expired = null;
                foreach (var (held, entry) in _entries)
                {
                    if (IsExpired(entry, now))
                        (expired ??= new List<string>()).Add(held);
                }
                if (expired is not null)
                {
                    foreach (var held in expired)
                        _entries.Remove(held);
                }
                if (_entries.Count >= Capacity)
                    return;
            }
            _entries[key] = new Entry(version, now);
        }
    }

    private bool IsExpired(Entry entry, long now)
    {
        // A negative age is a clock that stepped back: not trusted, expired.
        var age = Time.GetElapsedTime(entry.Stamp, now);
        return age < TimeSpan.Zero || age >= TimeToLive;
    }

    private readonly record struct Entry(TVersion Version, long Stamp);
}
