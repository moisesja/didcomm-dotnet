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

    /// <summary><c>created_time</c> is older than <c>now − Window</c>.</summary>
    TooOld,

    /// <summary><c>created_time</c> is later than <c>now + ExpiresClockSkew</c>.</summary>
    FutureDated,

    /// <summary>No <c>created_time</c> while <see cref="ReplayProtectionOptions.RequireCreatedTime"/> is on.</summary>
    MissingCreatedTime,

    /// <summary>The guard's total or per-sender budget is full; nothing is evicted, the message is not admitted.</summary>
    Saturated,

    /// <summary>The message id is longer than <see cref="ReplayProtectionOptions.MaxMessageIdLength"/>.</summary>
    MessageIdTooLong,
}
