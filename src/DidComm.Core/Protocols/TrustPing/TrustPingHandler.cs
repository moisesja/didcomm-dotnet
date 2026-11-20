using DidComm.Consistency;
using DidComm.Messages;

namespace DidComm.Protocols.TrustPing;

/// <summary>
/// Handler for Trust Ping 2.0 (FR-PROTO-04). Replies to a <c>ping</c> with a
/// <c>ping-response</c> in the ping's thread (its <c>thid</c> is the ping's <c>thid</c> when the
/// ping declares one, else the ping's <c>id</c>) when the ping's <c>response_requested</c> body
/// member is <c>true</c> (the default); replies with <c>null</c> when the sender explicitly opted out.
/// A ping passed in directly (not through unpack) that declares a <c>thid</c> that is not a valid
/// thread id is refused with <see cref="Exceptions.MalformedMessageException"/>, never answered on
/// another thread. The reply is sent from the DID whose key decrypted the ping, else the first
/// <c>to</c> entry; a ping without <c>from</c>, or a plaintext one without <c>to</c>, gets no reply.
/// </summary>
/// <example>
/// Registered in <c>samples/02-Cookbook</c> (section S) and <c>samples/05-WebSocketChat</c>.
/// </example>
public sealed class TrustPingHandler : IProtocolHandler
{
    /// <inheritdoc />
    public string ProtocolUri => TrustPing.ProtocolUri;

    /// <inheritdoc />
    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        // The registry only invokes us for the trust-ping PIURI, but the protocol covers both
        // `ping` and `ping-response`. We auto-reply ONLY to a ping; a ping-response is the
        // terminating leaf and never warrants a further reply.
        if (!string.Equals(message.Type, TrustPing.PingType, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Message?>(null);

        if (!TrustPing.IsResponseRequested(message))
            return Task.FromResult<Message?>(null);

        // A reply needs both ends: the pinger's `from` (an anonymous ping has nobody to answer),
        // and our DID: the one whose key decrypted the ping, else (plaintext, a direct call) the
        // first `to` entry. A decrypted ping without `to` is still ours: "If the header is omitted,
        // each recipient SHOULD assume they are the only recipient" (DIDComm v2.1
        // message_structure.md, `to`).
        if (string.IsNullOrEmpty(message.From))
            return Task.FromResult<Message?>(null);
        var responder = DidSubject.DidSubjectOf(context.Received.RecipientKid)
            ?? (message.To is { Count: > 0 } && !string.IsNullOrEmpty(message.To[0]) ? message.To[0] : null);
        if (responder is null)
            return Task.FromResult<Message?>(null);

        return Task.FromResult<Message?>(TrustPing.CreateResponse(message, responder));
    }
}
