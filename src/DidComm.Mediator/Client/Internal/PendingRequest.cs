using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// One request waiting for the mediator's answer in <see cref="MediatorClientChannel"/>'s pending
/// map (design §3.2, §4.1). Its outcome is decided once, by a terminal compare-and-swap that an
/// answer (<see cref="TryComplete"/>), the deadline or the caller's cancellation
/// (<see cref="TryAbandon"/>) and the HTTP no-answer rule or the channel's disposal
/// (<see cref="TryFail"/>) compete on — the Discover Features client's <c>PendingQuery</c> pattern:
/// whoever wins, the others change nothing, so a late answer can neither replace a timeout nor be
/// parsed twice. It retains our request id (a UUID we minted), the two DID subjects of the pair
/// the answer must come from and go to (caller-supplied, bounded by the facades), the static spec
/// and a start timestamp — never a wire value (L-062).
/// </summary>
internal sealed class PendingRequest
{
    private const int Waiting = 0;
    private const int Completed = 1;
    private const int Abandoned = 2;
    private const int Failed = 3;

    private int _state;

    /// <summary>Create a waiting request.</summary>
    /// <param name="id">Our request id.</param>
    /// <param name="spec">What it expects back.</param>
    /// <param name="mediatorSubject">The DID subject of the mediator: the only sender whose answer counts.</param>
    /// <param name="localSubject">The DID subject of our DID: the only DID the answer may be for.</param>
    /// <param name="startedAt">When it was registered (<see cref="TimeProvider.GetTimestamp"/>).</param>
    internal PendingRequest(string id, MediatorExchangeSpec spec, string mediatorSubject, string localSubject, long startedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(mediatorSubject);
        ArgumentNullException.ThrowIfNull(localSubject);
        Id = id;
        Spec = spec;
        MediatorSubject = mediatorSubject;
        LocalSubject = localSubject;
        StartedAt = startedAt;
        // Continuations never run on the thread that completes it: that is the dispatcher's receive
        // path (or a timer thread), and parsing must not extend it.
        Completion = new TaskCompletionSource<InboundMessageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Our request id.</summary>
    internal string Id { get; }

    /// <summary>What the request expects back.</summary>
    internal MediatorExchangeSpec Spec { get; }

    /// <summary>The DID subject of the mediator.</summary>
    internal string MediatorSubject { get; }

    /// <summary>The DID subject of our DID.</summary>
    internal string LocalSubject { get; }

    /// <summary>When the request was registered (<see cref="TimeProvider.GetTimestamp"/>).</summary>
    internal long StartedAt { get; }

    /// <summary>Settled once: with the answer, cancelled (deadline or caller), or with the failure.</summary>
    internal TaskCompletionSource<InboundMessageSnapshot> Completion { get; }

    /// <summary>Whether no outcome has been decided yet.</summary>
    internal bool IsWaiting => Volatile.Read(ref _state) == Waiting;

    /// <summary>Whether an answer won.</summary>
    internal bool IsCompleted => Volatile.Read(ref _state) == Completed;

    /// <summary>Hand the answer over; <c>false</c> when an outcome was already decided.</summary>
    /// <param name="answer">The verified snapshot of the answer.</param>
    internal bool TryComplete(InboundMessageSnapshot answer)
    {
        if (Interlocked.CompareExchange(ref _state, Completed, Waiting) != Waiting)
            return false;
        Completion.TrySetResult(answer);
        return true;
    }

    /// <summary>Give up waiting (the deadline passed or the caller cancelled); <c>false</c> when an outcome was already decided.</summary>
    internal bool TryAbandon()
    {
        if (Interlocked.CompareExchange(ref _state, Abandoned, Waiting) != Waiting)
            return false;
        Completion.TrySetCanceled();
        return true;
    }

    /// <summary>
    /// Fail the request with <paramref name="error"/>; <c>false</c> when an outcome was already
    /// decided. The fault is marked observed at once: the exchange that wins this CAS may throw the
    /// failure itself instead of awaiting <see cref="Completion"/> (the HTTP no-answer rule, or a
    /// disposal that ends the send first), and a faulted task nobody awaits would otherwise be
    /// reported on <see cref="TaskScheduler.UnobservedTaskException"/> when collected (L-039). A
    /// caller that does await <see cref="Completion"/> still sees <paramref name="error"/>.
    /// </summary>
    /// <param name="error">What the waiting caller observes.</param>
    internal bool TryFail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(ref _state, Failed, Waiting) != Waiting)
            return false;
        Completion.TrySetException(error);
        _ = Completion.Task.Exception;
        return true;
    }
}
