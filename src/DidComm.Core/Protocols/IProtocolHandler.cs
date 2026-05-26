using DidComm.Messages;

namespace DidComm.Protocols;

/// <summary>
/// A protocol handler registered with <see cref="ProtocolHandlerRegistry"/>. Implementations
/// take an inbound <see cref="Message"/>, optionally produce a reply, and the dispatcher
/// handles the protective envelope work for the reply.
/// </summary>
/// <remarks>
/// <para>
/// Per FR-PROTO-03 the registry matches handlers by Protocol Identifier URI (PIURI) —
/// <see cref="ProtocolUri"/>. The matching rule is FR-PROTO-01's case- and
/// punctuation-insensitive comparison plus FR-PROTO-02 semver compatibility (same major; the
/// older minor is the interop floor).
/// </para>
/// <para>
/// Implementations are constructed once and reused across messages (singleton lifetime in the
/// DI graph), so they MUST be thread-safe: avoid mutable fields, push per-thread state to the
/// <see cref="ProtocolContext.Thread"/> store.
/// </para>
/// <para>
/// <strong>Trust model.</strong> Handlers are on the trust path. The dispatcher invokes
/// <see cref="HandleAsync"/> only after the envelope's cryptographic guarantees are verified
/// (authcrypt sender identity, signature, FR-CONSIST-* addressing checks), so a handler can
/// rely on <see cref="ProtocolContext.Received"/> for authenticated metadata. The dispatcher
/// does NOT, however, second-guess the <c>from</c>/<c>to</c> a handler picks for its reply —
/// handlers own outgoing identity selection. Be deliberate: whatever DID a handler puts in
/// <c>reply.From</c> becomes the message's authenticated sender as far as the receiver is
/// concerned. The same-socket WebSocket reply path adds a separate transport gate (the reply
/// MUST be addressed to the inbound peer — see
/// <c>DidCommEndpointRouteBuilderExtensions.MapDidCommWebSocket</c>), but that is a delivery
/// constraint, not an identity-binding policy.
/// </para>
/// </remarks>
public interface IProtocolHandler
{
    /// <summary>
    /// The Protocol Identifier URI (PIURI) this handler serves —
    /// <c>&lt;doc-uri&gt;/&lt;protocol-name&gt;/&lt;major.minor&gt;</c> with no trailing
    /// <c>/&lt;message-type&gt;</c>. Example: <c>https://didcomm.org/trust-ping/2.0</c>.
    /// </summary>
    string ProtocolUri { get; }

    /// <summary>
    /// Handle an inbound message belonging to this protocol. Return <c>null</c> when no reply
    /// is warranted; otherwise return the reply message — the dispatcher validates it against
    /// <see cref="DidComm.Threading.AckLoopGuard.IsSafeToSend"/> (FR-THR-04 rule 2) before
    /// surfacing it to the transport layer.
    /// </summary>
    /// <param name="message">The unpacked inbound message.</param>
    /// <param name="context">The dispatcher context — wraps the unpack result, thread state, and facade.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct);
}
