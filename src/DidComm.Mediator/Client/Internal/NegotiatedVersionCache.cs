namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The Coordinate Mediation version each mediator negotiated through Discover Features
/// (design D-10, §4.4), keyed by the mediator's DID subject: at most <see cref="Capacity"/> entries
/// (no insert when full), each valid for <see cref="TimeToLive"/> on the monotonic clock of
/// <see cref="Time"/>. Held by the channel so it outlives one call; thread-safe. Owner: engineer B.
/// </summary>
internal sealed class NegotiatedVersionCache
{
    /// <summary>The default bound on remembered mediators.</summary>
    internal const int DefaultCapacity = 64;

    /// <summary>The default lifetime of an entry.</summary>
    internal static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);

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

    /// <summary>The version negotiated with <paramref name="mediatorDid"/>, when one is remembered and has not expired.</summary>
    /// <param name="mediatorDid">The mediator's DID (compared as a DID subject).</param>
    /// <param name="version"><see cref="CoordinateMediationVersion.V3"/> or <see cref="CoordinateMediationVersion.V2"/> when found.</param>
    internal bool TryGet(string mediatorDid, out CoordinateMediationVersion version)
        => throw new NotImplementedException("#96 engineer B: NegotiatedVersionCache.TryGet (design D-10).");

    /// <summary>Remember the version negotiated with <paramref name="mediatorDid"/>; nothing is added when the cache is full.</summary>
    /// <param name="mediatorDid">The mediator's DID (keyed by its DID subject).</param>
    /// <param name="version"><see cref="CoordinateMediationVersion.V3"/> or <see cref="CoordinateMediationVersion.V2"/>.</param>
    internal void Set(string mediatorDid, CoordinateMediationVersion version)
        => throw new NotImplementedException("#96 engineer B: NegotiatedVersionCache.Set (design D-10).");
}
