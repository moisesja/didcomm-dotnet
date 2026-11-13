namespace DidComm.Mediator;

/// <summary>
/// Where the mediator may push a recipient's messages directly, when direct push is turned on
/// (<see cref="MediatorOptions.DirectPush"/>, FR-PICK-09). The host registers one implementation
/// before <c>AddMediator()</c>; without one nothing is pushed and every message waits for pickup.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint always comes from here, never from the message being relayed, so a sender cannot
/// make the mediator send anywhere. Only <c>http</c> and <c>https</c> endpoints are pushed to: an
/// HTTP push is counted as delivered only when the recipient answers with a 2xx status, and the
/// message is then removed from the queue. A <c>ws</c> or <c>wss</c> endpoint is skipped, because a
/// WebSocket write gives no receipt, so a dropped connection would lose the message silently;
/// recipients that want messages pushed over a socket use pickup live mode, which is acknowledged.
/// Any push that fails or is skipped leaves the message queued for pickup.
/// </para>
/// <para>
/// A push sends only what the queue still holds for delivery: just before it is sent, the message
/// is taken from the queue under a lease of its own, so a message that has expired, aged out, been
/// picked up or acknowledged, or is being fetched by the recipient at that moment, is not pushed.
/// </para>
/// <para>
/// The push goes out through the registered transports, under their own rules: the HTTP transport
/// sends to <c>https</c> only by default, so a plain <c>http</c> endpoint needs <c>"http"</c> in
/// <c>HttpTransportOptions.AllowedSchemes</c> (otherwise every push to it fails and the messages
/// wait for pickup). The outbound endpoint guard (<c>DidCommOptions.OutboundEndpointPolicy</c>) also
/// applies: a private or loopback address is refused unless the host allows it.
/// </para>
/// <para>
/// Implementations are host singletons and MUST be thread-safe. <see cref="Find"/> is called only
/// on the mediator's background push workers — never while a request is being answered or on the
/// forward pump — so a slow lookup delays only pushes. An exception it throws is logged by type
/// and means "skip this push".
/// </para>
/// </remarks>
public interface IRecipientEndpointDirectory
{
    /// <summary>The endpoint to push the messages of a mediation grant to.</summary>
    /// <param name="grantPeerDid">The DID of the peer the grant was made to (the recipient that asked for mediation).</param>
    /// <returns>An absolute <c>http</c> or <c>https</c> endpoint, or <c>null</c> to leave the grant's messages for pickup.</returns>
    Uri? Find(string grantPeerDid);
}
