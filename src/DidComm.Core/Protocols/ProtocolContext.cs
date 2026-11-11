using DidComm.Facade;
using DidComm.Threading;
using ThreadState = DidComm.Threading.ThreadState; // disambiguate from System.Threading.ThreadState

namespace DidComm.Protocols;

/// <summary>
/// The execution context passed to an <see cref="IProtocolHandler"/>. Wraps everything a
/// handler needs that isn't the message itself: the full unpack result (so the handler can
/// see envelope metadata + sender DID), the per-thread mutable state (FR-I18N-02 accept-lang,
/// FR-PROTO-10 cascade-guard counter), the facade for out-of-band sends (e.g. a handler that
/// wants to push a follow-up), and the <see cref="DidCommOptions"/> in effect.
/// </summary>
/// <param name="Received">The full unpack outcome for the inbound message.</param>
/// <param name="Thread">The mutable state for the current thread (resolved from <c>thid</c> or <c>id</c>). For protocols whose semantically-meaningful thread is a different message's thread — most notably ProblemReport, whose <c>pthid</c> names the failing thread — use <see cref="Threads"/> to look up the right one.</param>
/// <param name="Client">The DIDComm facade — handlers can invoke <see cref="DidCommClient.SendAsync"/> for out-of-band sends. Nullable so dispatcher unit tests can run without spinning up the full client graph; production code always wires it in via DI.</param>
/// <param name="Options">The active <see cref="DidCommOptions"/>.</param>
/// <param name="Threads">The thread-state store, so handlers can resolve thread state by an arbitrary id (e.g. ProblemReport's <c>pthid</c>, the OOB invitation id when stitching parallel response threads). Appended at the end of the positional parameter list so existing positional callers (<c>received, thread, client, options</c>) are not broken.</param>
public sealed record ProtocolContext(
    UnpackResult Received,
    ThreadState Thread,
    DidCommClient? Client,
    DidCommOptions Options,
    IThreadStateStore Threads)
{
    /// <summary>
    /// Completes once the transport has acknowledged the inbound message on the wire — for HTTP,
    /// when the response (the 202) has been committed. A handler that defers work past the
    /// acknowledgement (the mediator's <see cref="Routing.ForwardHandler"/>: nothing registration-
    /// dependent may run before the 202 is in the sender's hands, FR-ROUTE-11) awaits it before
    /// starting that work. <see cref="Task.CompletedTask"/> when the transport provides no such
    /// ordering — dispatcher-only use (tests, samples, the WebSocket endpoints), where the handler
    /// proceeds immediately. A cancelled or faulted task means the inbound was never acknowledged
    /// (the client aborted, or the response turned into a rejection after the handler ran); a
    /// handler that was waiting on it must drop the deferred work. Set by
    /// <see cref="ProtocolDispatcher.DispatchAsync(UnpackResult, DidCommClient?, DidCommOptions, Task, CancellationToken)"/>.
    /// </summary>
    public Task Acknowledged { get; init; } = Task.CompletedTask;
}
