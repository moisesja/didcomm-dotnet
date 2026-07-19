namespace DidComm.Protocols;

/// <summary>
/// A read-only side channel over inbound messages (FR-PROTO-12). For each registered observer, the
/// <see cref="ProtocolDispatcher"/> attempts to deliver every inbound message that matches
/// <see cref="ProtocolUriFilter"/> — for every outcome, including <see cref="DispatchResult.NoHandler"/>
/// and loop-guard drops — so a second consumer can observe traffic whose PIURI is owned by a
/// built-in handler (e.g. a higher-level state machine reacting to <c>report-problem</c>, or the
/// Discover Features initiator client correlating a <c>disclose</c> to its pending query) without
/// replacing that handler.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Delivery is best-effort (at-most-once), not guaranteed.</strong> Delivery is <em>fully
/// decoupled</em> from dispatch: the dispatcher enqueues the message onto a per-observer bounded
/// background queue and returns the outcome immediately, so an observer can never gate reply delivery
/// or change the outcome — even a hung or faulting observer only backs up (and eventually drops from)
/// its own queue. When an observer falls behind, its newest observations are <strong>dropped</strong>
/// (and logged) rather than buffered without bound — so do not rely on this seam for lossless capture;
/// hand off to your own durable store quickly. Because each observer has its own pump, an observer
/// <strong>may be invoked concurrently</strong> with other observers and across inbound messages;
/// a single observer's own invocations are serialized in arrival order. Implementations MUST be safe
/// under concurrent invocation.
/// </para>
/// <para>
/// <strong>Trust model.</strong> Observers are host-trusted components: they can only be
/// registered by code composing the DI graph at startup (<c>AddProtocolObserver&lt;T&gt;()</c>),
/// are enumerated in an Information log line when the dispatcher is constructed (so a stowaway
/// registration is operator-visible), and run in-process on the recipient's side against
/// already-unpacked plaintext. Nothing about the seam is reachable by a remote peer, and
/// envelope cryptography is unaffected. Read-only is enforced, not assumed: each observer
/// receives its own defensive clone via <see cref="InboundObservation"/>, and exceptions an
/// observer throws are logged and isolated — the dispatch outcome is never affected.
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
    /// Delivered at most once per matching inbound message, on the observer's background pump (off the
    /// dispatch/receive path). Exceptions are caught, logged, and isolated by the dispatcher; they
    /// never affect the outcome or other observers.
    /// </summary>
    /// <remarks>
    /// Delivery is off the critical path, so a slow observer does not block message handling — but a
    /// persistently slow observer will fall behind and its bounded queue will drop the newest
    /// observations (logged). For lossless capture of a high-volume stream, hand off quickly here
    /// (e.g. to your own durable queue) rather than doing slow work inline.
    /// </remarks>
    /// <param name="observation">The read-only view of the inbound message (defensive clone + envelope-auth metadata).</param>
    /// <param name="ct">Cancellation token for the delivery pump.</param>
    Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct);
}
