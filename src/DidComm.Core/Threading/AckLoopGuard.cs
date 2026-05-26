using DidComm.Messages;

namespace DidComm.Threading;

/// <summary>
/// Pure helpers that enforce the FR-THR-04 ACK loop-prevention rules at the message layer.
/// Phase 6.1 ships the predicate surface; the protocol handlers in Phase 6.2 consume these
/// to decide whether to emit or honor an ACK on an incoming envelope.
/// </summary>
/// <remarks>
/// The three rules from §ACKs (DIDComm v2.1):
/// <list type="number">
///   <item><description><see cref="IsSafeToSend"/>: never send a pure ACK that also requests an ACK (rule 2).</description></item>
///   <item><description>Protocols MUST honor at most one ACK request per incoming message (rule 1) — enforced
///     in the dispatcher by replying exactly once.</description></item>
///   <item><description>Protocols MUST drop a pure ACK that arrives in response to one's own ACK request (rule 3) —
///     enforced in the dispatcher using <see cref="IsPureAck"/> + thread bookkeeping.</description></item>
/// </list>
/// </remarks>
public static class AckLoopGuard
{
    /// <summary>
    /// A "pure ACK" is a message whose only purpose is acknowledgment: it carries the
    /// <c>ack</c> header and has no body content (FR-THR-03 wording: "an <c>ack</c>-bearing
    /// message is an explicit ACK regardless of type; use Empty 1.0 when only an ACK is needed").
    /// Returns <c>true</c> when the message ACKs something AND carries no application body.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    public static bool IsPureAck(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Ack is not { Count: > 0 })
            return false;
        // An ACK is "pure" when it's an Empty 1.0 envelope or otherwise has no semantic body.
        return message.Body is null || message.Body.Count == 0;
    }

    /// <summary>
    /// Returns <c>true</c> when the message asks the recipient to acknowledge it
    /// (<c>please_ack</c> is present and non-empty).
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    public static bool RequestsAck(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.PleaseAck is { Count: > 0 };
    }

    /// <summary>
    /// FR-THR-04 rule 2: a pure ACK MUST NOT also request an ACK (otherwise both peers ACK
    /// each other's ACKs forever). Returns <c>false</c> when an outgoing message would violate
    /// the rule; callers SHOULD throw before transmitting.
    /// </summary>
    /// <param name="outgoing">The message being prepared for send.</param>
    public static bool IsSafeToSend(Message outgoing)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        return !(IsPureAck(outgoing) && RequestsAck(outgoing));
    }
}
