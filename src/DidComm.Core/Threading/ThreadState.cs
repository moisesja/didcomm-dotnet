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
    /// General-purpose mutable per-thread error counter for consumer use. <b>Note:</b> the built-in
    /// FR-PROTO-10 cascade guard no longer uses this field — it keeps its budget in a dedicated,
    /// separate store (<c>CascadeBudgetStore</c>) so a flood of cheap thids in the general store can't
    /// evict and reset it (#36). Retained for consumers who track their own per-thread counts.
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// General-purpose per-thread flag for consumer use. <b>Note:</b> as with <see cref="ErrorCount"/>,
    /// the built-in cascade guard no longer reads or writes this — see <c>CascadeBudgetStore</c> (#36).
    /// </summary>
    public bool MaxErrorsNoticeSent { get; set; }

    /// <summary>
    /// FR-THR-04 rule-3 bookkeeping: <c>true</c> once the dispatcher has emitted a reply that requests
    /// an ACK on this thread, so a subsequent pure-ACK answering it is consumed (not re-dispatched) to
    /// break an ACK loop. Set by <c>ProtocolDispatcher</c> when it produces an ACK-requesting reply and
    /// cleared when the answering pure-ACK arrives. Tracks only dispatcher-emitted ACK requests; requests
    /// sent via the facade directly are the application's responsibility (#31).
    /// </summary>
    /// <remarks>
    /// Best-effort, not a state machine: if the expected pure-ACK never arrives, the flag stays
    /// <c>true</c> until this entry is evicted from the bounded thread store (#21). While set, a later
    /// <i>unsolicited</i> pure-ACK on the same thread is dropped as a loop (a benign over-drop — pure
    /// ACKs are inert and carry no work). This is acceptable because rule-3 is only defense-in-depth:
    /// rule-2 (a pure-ACK must not itself request an ACK) is the actual loop barrier.
    /// </remarks>
    public bool AckRequested { get; set; }

    /// <summary>
    /// Monotonic last-touched stamp used by <see cref="InMemoryThreadStateStore"/>'s
    /// approximate-LRU eviction to bound the store under a flood of fresh, unauthenticated
    /// thids (issue #21). Not part of the public thread-state contract; other stores ignore it.
    /// </summary>
    internal long LastTouchedTick;

    /// <summary>Construct empty state for <paramref name="thid"/>.</summary>
    /// <param name="thid">The thread id. Must be non-empty.</param>
    public ThreadState(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        Thid = thid;
    }
}
