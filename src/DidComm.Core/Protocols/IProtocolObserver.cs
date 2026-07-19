namespace DidComm.Protocols;

/// <summary>
/// A read-only side channel over inbound messages (FR-PROTO-12). The
/// <see cref="ProtocolDispatcher"/> notifies each registered observer exactly once per
/// completed dispatch — for every outcome, including <see cref="DispatchResult.NoHandler"/>
/// and loop-guard drops — AFTER the outcome is determined, so an observer can never influence
/// handler resolution, replies, or thread state. This is the seam that lets a second consumer
/// observe traffic whose PIURI is owned by a built-in handler (e.g. a higher-level state
/// machine reacting to <c>report-problem</c>, or the Discover Features initiator client
/// correlating a <c>disclose</c> to its pending query) without replacing that handler.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Trust model.</strong> Observers are host-trusted components: they can only be
/// registered by code composing the DI graph at startup (<c>AddProtocolObserver&lt;T&gt;()</c>),
/// are enumerated in an Information log line when the dispatcher is constructed (so a stowaway
/// registration is operator-visible), and run in-process on the recipient's side against
/// already-unpacked plaintext. Nothing about the seam is reachable by a remote peer, and
/// envelope cryptography is unaffected. Read-only is enforced, not assumed: each observer
/// receives its own defensive clone via <see cref="InboundObservation"/>, and exceptions an
/// observer throws are logged and swallowed — the dispatch outcome is never affected.
/// </para>
/// <para>
/// <strong>Least privilege.</strong> Prefer a narrow <see cref="ProtocolUriFilter"/> over
/// <c>null</c>: an observer should see only the protocol family it exists to watch.
/// </para>
/// </remarks>
public interface IProtocolObserver
{
    /// <summary>
    /// The PIURI this observer wants to see (e.g. <c>https://didcomm.org/report-problem/2.0</c>).
    /// Matched with the registry's rules — same protocol name and major version, any minor —
    /// so a <c>2.0</c> filter observes <c>2.1</c> traffic. <c>null</c> observes every inbound
    /// message; use it only when the observer genuinely needs the full stream.
    /// </summary>
    string? ProtocolUriFilter { get; }

    /// <summary>
    /// Called once per completed dispatch for each inbound message that matches
    /// <see cref="ProtocolUriFilter"/>. Runs after the dispatch outcome is determined.
    /// Exceptions are caught and logged by the dispatcher; they do not affect the outcome.
    /// </summary>
    /// <param name="observation">The read-only view of the inbound message (defensive clone + envelope-auth metadata).</param>
    /// <param name="ct">Cancellation token flowing from the receive pipeline.</param>
    Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct);
}
