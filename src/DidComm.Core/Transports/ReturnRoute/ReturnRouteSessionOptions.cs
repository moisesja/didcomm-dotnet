namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// Bounds for the return-route session registry (PRD §8a, FR-RR-03). Configure through
/// <c>services.AddOptions&lt;ReturnRouteSessionOptions&gt;().Configure(...)</c>; the
/// <see cref="InMemoryReturnRouteSessionRegistry"/> validates the instance it is built with.
/// </summary>
public sealed class ReturnRouteSessionOptions
{
    /// <summary>
    /// Live sessions one peer DID may hold at once; registering one more evicts that peer's oldest
    /// session. Default 8: one DID with several devices or tabs (Coordinate Mediation 3.0 opens one
    /// socket per device) is covered without letting one peer hold a meaningful share of
    /// <see cref="MaxSessions"/>.
    /// </summary>
    public int MaxSessionsPerPeer { get; set; } = 8;

    /// <summary>
    /// Live sessions the registry holds in total; at the bound a new registration is refused
    /// (never evicting another peer's session). Default 4096: each live WebSocket session costs a
    /// socket plus a <see cref="MaxPendingPushes"/>-deep channel, which keeps the registry's
    /// bookkeeping to a few megabytes and stays well inside a small host's concurrent-connection
    /// range.
    /// </summary>
    public int MaxSessions { get; set; } = 4096;

    /// <summary>
    /// Pushes one WebSocket session holds at once — being snapshotted and packed, or packed and
    /// queued to its writer — before <see cref="IReturnRouteSession.TrySendAsync"/> answers
    /// <c>false</c>. A push takes its place before the snapshot, so one beyond the bound costs no
    /// snapshot, key resolution or encryption; the place is freed when the push fails or when the
    /// writer takes its frame off the queue (the frame being sent is not counted). Default 16: each
    /// queued item is one packed message, so a socket never holds more than 16 of them besides the
    /// one being sent; live delivery (message-pickup) pushes at most one unacknowledged delivery per
    /// session, so this is slack, not a working set.
    /// </summary>
    public int MaxPendingPushes { get; set; } = 16;

    /// <summary>
    /// Check the bounds: each at least 1 and <see cref="MaxSessionsPerPeer"/> no larger than
    /// <see cref="MaxSessions"/>. Throws <see cref="InvalidOperationException"/> naming the
    /// offending property.
    /// </summary>
    public void Validate()
    {
        if (MaxSessionsPerPeer < 1)
            throw new InvalidOperationException($"{nameof(ReturnRouteSessionOptions)}.{nameof(MaxSessionsPerPeer)} must be at least 1.");
        if (MaxSessions < 1)
            throw new InvalidOperationException($"{nameof(ReturnRouteSessionOptions)}.{nameof(MaxSessions)} must be at least 1.");
        if (MaxPendingPushes < 1)
            throw new InvalidOperationException($"{nameof(ReturnRouteSessionOptions)}.{nameof(MaxPendingPushes)} must be at least 1.");
        if (MaxSessionsPerPeer > MaxSessions)
            throw new InvalidOperationException($"{nameof(ReturnRouteSessionOptions)}.{nameof(MaxSessionsPerPeer)} must not exceed {nameof(MaxSessions)}.");
    }
}
