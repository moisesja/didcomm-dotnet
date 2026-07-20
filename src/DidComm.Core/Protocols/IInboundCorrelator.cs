using DidComm.Facade;

namespace DidComm.Protocols;

/// <summary>
/// An internal, trusted, <strong>synchronous and lossless</strong> inbound hook the
/// <see cref="ProtocolDispatcher"/> invokes inline (guarded) after it has computed the dispatch
/// outcome. Unlike the public <see cref="IProtocolObserver"/> seam — which is for arbitrary,
/// possibly-slow application code and is therefore delivered off-path through a bounded queue that
/// may drop under load — a correlator is library-owned plumbing whose work is O(1) and non-blocking
/// (a dictionary lookup + a <c>TaskCompletionSource.TrySetResult</c>), so it runs inline and is
/// never subject to queue drops. That is what lets an initiator's genuine, authenticated response
/// complete even while an attacker floods the same PIURI with unsolicited traffic.
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
    /// <summary>Inspect a dispatched inbound message and complete any matching pending operation. Fast, non-blocking, no I/O.</summary>
    /// <param name="received">The unpack result for the inbound message. Read-only in practice; do not mutate.</param>
    void OnInbound(UnpackResult received);
}
