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
