namespace DidComm.Threading;

/// <summary>
/// Thread-scoped cross-message state for a single DIDComm thread (identified by <c>thid</c>).
/// Currently carries the <see cref="AcceptLang"/> preference per FR-I18N-02; future phases
/// extend it with the per-thread problem-report counter (FR-PROTO-10 cascade guard) and the
/// outstanding-ACK bookkeeping FR-THR-04 needs to reject loops.
/// </summary>
/// <remarks>
/// Instances are per-thread, mutable, and reference-shared between reads and writes within
/// the same <see cref="IThreadStateStore"/> implementation. Stores are responsible for any
/// synchronization they require — the type itself is intentionally minimal.
/// </remarks>
public sealed class ThreadState
{
    /// <summary>The thread id this state belongs to.</summary>
    public string Thid { get; }

    /// <summary>
    /// The most recently observed <c>accept-lang</c> preference on this thread (FR-I18N-02).
    /// <c>null</c> when the peer has not advertised a preference yet. Order matters: the first
    /// entry is most-preferred.
    /// </summary>
    public IReadOnlyList<string>? AcceptLang { get; set; }

    /// <summary>
    /// Count of problem-reports produced on this thread. Used by the FR-PROTO-10 cascade
    /// guard (Phase 6.2c) to emit <c>e.p.req.max-errors-exceeded</c> once the per-thread
    /// threshold trips and then stop responding on the thread. Implementations / tests MAY
    /// increment this directly; concurrent callers MUST hold the per-instance lock
    /// (<see cref="ThreadState"/> exposes itself as the lock seam — see remarks).
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// Set to <c>true</c> the moment the FR-PROTO-10 cascade-stop notice has been emitted on
    /// this thread, so the handler can short-circuit subsequent reports without further work.
    /// </summary>
    public bool MaxErrorsNoticeSent { get; set; }

    /// <summary>Construct empty state for <paramref name="thid"/>.</summary>
    /// <param name="thid">The thread id. Must be non-empty.</param>
    public ThreadState(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        Thid = thid;
    }
}
