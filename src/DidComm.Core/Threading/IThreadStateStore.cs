namespace DidComm.Threading;

/// <summary>
/// Per-thread state keyed by <c>thid</c>. Implementations MUST keep thread states isolated
/// from each other so an <c>accept-lang</c> preference learned on one thread cannot leak into
/// a concurrent thread (FR-I18N-02). Implementations MUST also be safe for concurrent use
/// from multiple threads, since the same store is shared across pack/unpack on a singleton
/// facade (NFR-03).
/// </summary>
public interface IThreadStateStore
{
    /// <summary>
    /// Return the <see cref="ThreadState"/> for <paramref name="thid"/>, creating an empty
    /// one if none exists yet. Subsequent calls with the same <paramref name="thid"/> return
    /// the same instance so callers can mutate in place.
    /// </summary>
    /// <param name="thid">The thread id whose state is requested.</param>
    /// <returns>The state record for the thread.</returns>
    ThreadState GetOrCreate(string thid);

    /// <summary>Try to retrieve existing state for <paramref name="thid"/> without creating one.</summary>
    /// <param name="thid">The thread id to look up.</param>
    /// <returns>The state record, or <c>null</c> if the thread has no recorded state yet.</returns>
    ThreadState? Get(string thid);

    /// <summary>
    /// Forget the state for <paramref name="thid"/>. Called by protocol handlers when a thread
    /// terminates (per FR-I18N-02 "until the current protocol thread (thid) ends").
    /// </summary>
    /// <param name="thid">The thread id whose state should be discarded.</param>
    /// <returns><c>true</c> if state existed and was removed; <c>false</c> if it was already absent.</returns>
    bool Remove(string thid);
}
