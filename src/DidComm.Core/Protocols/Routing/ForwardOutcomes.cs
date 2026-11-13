namespace DidComm.Protocols.Routing;

/// <summary>
/// The closed vocabulary of the <c>didcomm.forward.outcomes</c> counter (see
/// <see cref="Diagnostics.DidCommDiagnostics.MeterName"/>): one value per forward, from whichever
/// stage ended it — <see cref="ForwardHandler"/> or the mediator's forward sink.
/// </summary>
internal static class ForwardOutcomes
{
    /// <summary>The counter's instrument name.</summary>
    internal const string ForwardOutcomesInstrument = "didcomm.forward.outcomes";

    /// <summary>The counter's single tag key.</summary>
    internal const string OutcomeTag = "didcomm.forward.outcome";

    /// <summary>Stored and pushed over the recipient's live connection (sink).</summary>
    internal const string DeliveredLive = "Delivered-Live";

    /// <summary>Stored for pickup (sink).</summary>
    internal const string Queued = "Queued";

    /// <summary>Not a well-formed forward, or the processor found it inconsistent or malformed (handler).</summary>
    internal const string DroppedMalformed = "Dropped-Malformed";

    /// <summary>The forward did not arrive encrypted (handler).</summary>
    internal const string DroppedNotEncrypted = "Dropped-NotEncrypted";

    /// <summary>Expired on arrival, while waiting in the handler, or when stored (handler or sink).</summary>
    internal const string DroppedExpired = "Dropped-Expired";

    /// <summary><c>next</c> is not a DID, or not a registered recipient (handler or sink); or, in rewrap mode, its DID resolves to no usable <c>keyAgreement</c> key (handler).</summary>
    internal const string DroppedUnknown = "Dropped-Unknown";

    /// <summary>The handler's backlog was full (handler).</summary>
    internal const string DroppedSaturated = "Dropped-Saturated";

    /// <summary>The transport never acknowledged the forward to its sender (handler).</summary>
    internal const string DroppedUnacknowledged = "Dropped-Unacknowledged";

    /// <summary>Discarded because the handler was shutting down (handler).</summary>
    internal const string DroppedShutdown = "Dropped-Shutdown";

    /// <summary>Larger than the pickup queue accepts (sink).</summary>
    internal const string DroppedTooLarge = "Dropped-TooLarge";

    /// <summary>The recipient's quota, or the store's, was used up (sink).</summary>
    internal const string DroppedQuota = "Dropped-Quota";

    /// <summary>A fault in screening, processing or storing (handler or sink).</summary>
    internal const string Faulted = "Faulted";
}
