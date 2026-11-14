using DidComm.Exceptions;
using DidComm.Resolution;

namespace DidComm.Facade;

// The recipient-side mediator client's two entry points into the facade (#96). Both are internal:
// DidComm.Mediator reaches them through InternalsVisibleTo, and nothing here changes a released
// member.
public sealed partial class DidCommClient
{
    /// <summary>
    /// The direct transport endpoint of <paramref name="peerDid"/>, selected exactly the way
    /// <see cref="SendAsync"/> selects the endpoint of a direct return-route send: one read of the
    /// DID's <c>DIDCommMessaging</c> service entries and its first entry, which must name a plain
    /// absolute transport URI the outbound endpoint guard admits. The mediator client resolves the
    /// route once per operation with it, then sends with that URI as the endpoint override, so the
    /// HTTP-or-WebSocket decision and the send use the same entry (L-041).
    /// </summary>
    /// <param name="peerDid">The mediator's DID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The endpoint URI of the first service entry.</returns>
    /// <exception cref="InvalidOperationException">No <see cref="Resolution.IServiceEndpointResolver"/> is registered, or the first entry routes through mediators or routing keys (<see cref="RoutedReturnRouteRefusal"/>).</exception>
    /// <exception cref="DidResolutionException">The DID has no <c>DIDCommMessaging</c> service entries.</exception>
    /// <exception cref="TransportException">The first entry names a queue URI, or a URI that is not absolute.</exception>
    internal Task<Uri> ResolveDirectEndpointAsync(string peerDid, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: ResolveDirectEndpointAsync (design §3.1 item 3: entries[0], forward layers / queue / relative refused, OutboundEndpointGuard).");

    /// <summary>
    /// Unpack a message a mediator delivered (Message Pickup <c>delivery</c> attachment or live
    /// push), opening the forward layers a re-wrapping mediator added for this agent (FR-ROUTE-06,
    /// receiver side). A layer is opened only when it has the shape the mediator's own acceptance
    /// rule admits — an anoncrypted plaintext <c>forward</c> with one attachment, no sender or signer
    /// key, no <c>from_prior</c>, whose <c>next</c> is the DID subject that decrypted it — and at most
    /// <see cref="MediatorEndpointExpander.MaxForwardLayers"/> layers are opened (so at most nine
    /// unpacks). Every layer is unpacked under the full receive rules. A pass-through delivery
    /// returns after the first unpack with zero layers opened.
    /// </summary>
    /// <param name="packed">The delivered packed message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The innermost unpack result, the innermost packed text, and how many forward layers were opened.</returns>
    /// <exception cref="ConsistencyException">A layer is a <c>forward</c> that is not a delivery re-wrapped for this agent (another <c>next</c>, authcrypt, signed, <c>from_prior</c>, or not exactly one attachment).</exception>
    /// <exception cref="MalformedMessageException">More than the maximum number of forward layers, or a layer that fails to unpack or decode.</exception>
    internal Task<DeliveredUnpack> UnpackDeliveredAsync(string packed, CancellationToken ct)
        => throw new NotImplementedException("#96 engineer A: UnpackDeliveredAsync (design §4.6: self-addressed rewrap layers, at most 8).");
}

/// <summary>
/// What <see cref="DidCommClient.UnpackDeliveredAsync"/> returns for one delivered message.
/// </summary>
/// <param name="Result">The unpack result of the innermost message (every re-wrap layer opened).</param>
/// <param name="InnermostPacked">The packed text the innermost message was unpacked from: the delivered text itself when no layer was opened. The duplicate filter keys anonymous messages on it.</param>
/// <param name="LayersOpened">How many self-addressed forward layers were opened, 0 to <see cref="MediatorEndpointExpander.MaxForwardLayers"/>.</param>
internal readonly record struct DeliveredUnpack(UnpackResult Result, string InnermostPacked, int LayersOpened);
