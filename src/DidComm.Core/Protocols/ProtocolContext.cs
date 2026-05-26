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
/// <param name="Threads">The thread-state store, so handlers can resolve thread state by an arbitrary id (e.g. ProblemReport's <c>pthid</c>, the OOB invitation id when stitching parallel response threads).</param>
/// <param name="Client">The DIDComm facade — handlers can invoke <see cref="DidCommClient.SendAsync"/> for out-of-band sends. Nullable so dispatcher unit tests can run without spinning up the full client graph; production code always wires it in via DI.</param>
/// <param name="Options">The active <see cref="DidCommOptions"/>.</param>
public sealed record ProtocolContext(
    UnpackResult Received,
    ThreadState Thread,
    IThreadStateStore Threads,
    DidCommClient? Client,
    DidCommOptions Options);
