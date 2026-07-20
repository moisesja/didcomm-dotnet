namespace DidComm.Protocols;

/// <summary>
/// An internal, trusted, synchronous inbound hook the
/// <see cref="ProtocolDispatcher"/> invokes inline (guarded) before application handler code. Unlike
/// the public <see cref="IProtocolObserver"/> seam — which is for arbitrary,
/// possibly-slow application code and is therefore delivered off-path through a bounded queue that
/// may drop under load — a correlator is library-owned plumbing whose inline work is bounded to
/// immutable header/trust matching plus a <c>TaskCompletionSource.TrySetResult</c>. It performs no
/// body parsing or I/O and is never subject to observer-queue drops. A matching response therefore
/// competes directly for its pending operation instead of waiting behind the observer firehose.
/// </summary>
/// <remarks>
/// Contract: <see cref="OnInbound"/> MUST return promptly, MUST NOT block or perform I/O, and MUST
/// NOT complete any awaiter's continuation inline (use <c>RunContinuationsAsynchronously</c>). The
/// dispatcher guards the call, so a throw cannot affect the dispatch outcome — but a correlator that
/// blocked would gate the receive path, hence the contract. This interface is intentionally internal:
/// it is not an extensibility point, only the wiring for built-ins such as the Discover Features
/// initiator client.
/// </remarks>
internal interface IInboundCorrelator
{
    /// <summary>Inspect an immutable inbound snapshot and complete any matching pending operation. Fast, non-blocking, no I/O.</summary>
    /// <param name="received">The immutable plaintext/trust snapshot created at the unpack boundary.</param>
    void OnInbound(InboundMessageSnapshot received);
}
