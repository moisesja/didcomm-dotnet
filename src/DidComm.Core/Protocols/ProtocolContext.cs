using DidComm.Facade;
using DidComm.Threading;
using DidComm.Transports.ReturnRoute;
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

    /// <summary>
    /// The return-route session bound to this inbound, or <c>null</c> when there is none. Non-null
    /// only when the transport admitted the inbound under FR-RR-03 — encrypted with an authcrypt
    /// layer, carrying <c>return_route: all</c>, not a <c>forward</c>, FR-RR-08 replay-admitted —
    /// and bound this inbound's (peer, local DID) pair to it. A handler returns a reply or pushes
    /// follow-ups through <see cref="IReturnRouteSession.TrySendAsync"/>; <c>null</c> means reply
    /// out of band (<see cref="DidCommClient.SendAsync"/>) or not at all. Set by
    /// <see cref="ProtocolDispatcher.DispatchAsync(UnpackResult, DidCommClient?, DidCommOptions, Task, IReturnRouteSession?, CancellationToken)"/>;
    /// <c>null</c> from the other overloads.
    /// </summary>
    /// <remarks>
    /// Upgrader note: <see cref="ProtocolContext"/> is a positional record, so this member and
    /// <see cref="ReturnRoute"/> take part in value equality and <c>GetHashCode</c>. Two contexts
    /// that were equal before 1.6.0 remain equal unless they differ in these members. Binary- and
    /// source-compatible; only equality semantics changed.
    /// </remarks>
    public IReturnRouteSession? Session { get; init; }

    /// <summary>
    /// The parsed <c>return_route</c> header of THIS message (FR-RR-01), truthful for every
    /// dispatch path: the dispatcher re-reads it. A <see cref="ReturnRouteMode.Thread"/> request is
    /// surfaced here but opens no session (DD-12, PRD §15.2). On a WebSocket a header-less message
    /// shows <see cref="ReturnRouteMode.None"/> here while <see cref="Session"/> keeps its own mode
    /// (FR-RR-05). A malformed header reads as <see cref="ReturnRouteHeader.None"/>.
    /// </summary>
    public ReturnRouteHeader ReturnRoute { get; init; } = ReturnRouteHeader.None;
}
