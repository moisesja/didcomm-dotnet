namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// The outcome of <see cref="IInboundReplayGuard.TryAdmit"/> (FR-RR-08). Only
/// <see cref="Admitted"/> lets the message proceed; every other value is a drop the transport
/// answers with its normal acknowledgement (HTTP 202, nothing on a WebSocket) and logs by message
/// id and outcome name only.
/// </summary>
public enum ReplayAdmission
{
    /// <summary>First sight of this <c>(sender, message id)</c> within the retention: proceed.</summary>
    Admitted,

    /// <summary>The same <c>(sender, message id)</c> was admitted within the retention.</summary>
    Duplicate,

    /// <summary>
    /// <c>created_time</c> is older than <c>now − Window</c>; or, with a clock reading behind one the
    /// guard has already purged at (concurrent readings out of order, or a host clock that stepped
    /// back), <c>created_time + Window + ClockSkew</c> is earlier than that latest purge instant, so the
    /// message may have been admitted and forgotten. After a step back of more than
    /// <c>Window + ClockSkew</c> this refuses even a freshly stamped message until the clock catches up
    /// (<see cref="InMemoryReplayGuard"/>, FR-RR-08 (4)).
    /// </summary>
    TooOld,

    /// <summary><c>created_time</c> is later than <c>now + </c><see cref="ReplayProtectionOptions.ClockSkew"/>.</summary>
    FutureDated,

    /// <summary>No <c>created_time</c> while <see cref="ReplayProtectionOptions.RequireCreatedTime"/> is on.</summary>
    MissingCreatedTime,

    /// <summary>The guard's total or per-sender budget is full; nothing is evicted, the message is not admitted.</summary>
    Saturated,

    /// <summary>The message id is longer than <see cref="ReplayProtectionOptions.MaxMessageIdLength"/>.</summary>
    MessageIdTooLong,
}
