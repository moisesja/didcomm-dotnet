namespace DidComm.Protocols.Routing;

/// <summary>
/// Output of <see cref="ForwardProcessor.ProcessAsync"/> — what a mediator's transport layer
/// needs to know to relay a forward (PRD §8, FR-ROUTE-05).
/// </summary>
/// <param name="NextHop">The DID (or DID URL) the onward payload must be sent to — value of <c>body.next</c> on the inbound forward.</param>
/// <param name="OnwardPacked">The bytes to transmit to <paramref name="NextHop"/>. In pass-through mode these are the inner attachment verbatim; in rewrap mode they are a freshly-anoncrypted <c>forward</c> wrapping that attachment (FR-ROUTE-06).</param>
/// <param name="ExpiresTime">Optional <c>expires_time</c> propagated from the inbound forward. <c>null</c> when the sender did not set one.</param>
/// <param name="Delay">Optional spec-defined <c>delay_milli</c> hint resolved to a <see cref="TimeSpan"/>. Negative input values are randomised between zero and <c>|n|</c> as the spec specifies; <c>null</c> when absent.</param>
public sealed record ForwardProcessingResult(
    string NextHop,
    byte[] OnwardPacked,
    long? ExpiresTime,
    TimeSpan? Delay);
