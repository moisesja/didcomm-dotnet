using DidComm.Consistency;
using DidComm.Messages;

namespace DidComm.Protocols.TrustPing;

/// <summary>
/// Handler for Trust Ping 2.0 (FR-PROTO-04). Replies to a <c>ping</c> with a
/// <c>ping-response</c> whose <c>thid</c> equals the ping's <c>id</c> when the ping's
/// <c>response_requested</c> body member is <c>true</c> (the default); replies with
/// <c>null</c> when the sender explicitly opted out.
/// </summary>
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

        var decryptingDid = DidSubject.DidSubjectOf(context.Received.RecipientKid);
        var response = decryptingDid is null
            ? TrustPing.CreateResponse(message)
            : TrustPing.CreateResponse(message, decryptingDid);
        return Task.FromResult<Message?>(response);
    }
}
