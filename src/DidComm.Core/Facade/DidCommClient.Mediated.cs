using System.Text;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Facade;

// The recipient-side mediator client's two entry points into the facade (#96), and the plain
// receiving endpoints' opening of a re-wrapped delivery (#110, OD-3). All are internal:
// DidComm.Mediator.Client (the client's package since #113) and DidComm.AspNetCore reach them through
// InternalsVisibleTo, and nothing here changes a released member.
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
    /// <remarks>
    /// The refusals are <see cref="SendAsync"/>'s, in its order and with its exception types: the
    /// recipient entry's shape; no service resolver; an unsupported DID method; no service entries;
    /// a first entry that needs forward layers (a return-route send to a routed recipient,
    /// FR-RR-07); the queue URI (FR-RR-06); a URI that is not absolute; and the SSRF guard of
    /// <see cref="DidCommOptions.OutboundEndpointPolicy"/>, which <see cref="SendAsync"/> skips for
    /// a caller's override and so is applied here, to the URI the override will carry. No text
    /// quotes the DID document's URI.
    /// </remarks>
    /// <param name="peerDid">The mediator's DID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The endpoint URI of the first service entry.</returns>
    /// <exception cref="ArgumentException"><paramref name="peerDid"/> is empty or is neither a DID nor a DID URL with a fragment.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="Resolution.IServiceEndpointResolver"/> is registered, or the first entry routes through mediators or routing keys (<see cref="RoutedReturnRouteRefusal"/>).</exception>
    /// <exception cref="UnsupportedDidMethodException"><paramref name="peerDid"/> is a <c>did:web</c> DID (DD-08).</exception>
    /// <exception cref="DidResolutionException">The DID has no <c>DIDCommMessaging</c> service entries.</exception>
    /// <exception cref="TransportException">The first entry names a queue URI, a URI that is not absolute, or a destination the outbound endpoint policy blocks.</exception>
    internal async Task<Uri> ResolveDirectEndpointAsync(string peerDid, CancellationToken ct)
    {
        // SendAsync's order: the entry's shape, then the resolver, then the DID method, then the
        // one read of the service entries (PackEncryptedCoreAsync), then what the route expansion
        // and the transport selection refuse for a direct entry.
        var entry = ParseRecipientEntry(peerDid, nameof(peerDid));
        if (_serviceResolver is null)
            throw new InvalidOperationException(ForwardRequiresServiceResolver);
        _keyService.RejectUnsupportedMethod(entry.Did);

        var entries = await _serviceResolver.ResolveAsync(entry.Did, ct).ConfigureAwait(false);
        if (entries.Count == 0)
            throw new DidResolutionException(entry.Did, "no DIDCommMessaging service entries available");

        // The selected entry is the first (MediatorEndpointExpander.ExpandAsync), judged by the
        // expander's own layer count, as SendAsync's return-route refusal judges it.
        var primary = entries[0];
        if (MediatorEndpointExpander.ForwardLayerCount(primary) > 0)
            throw new InvalidOperationException(RoutedReturnRouteRefusal);
        if (QueueTransport.IsQueueTransport(primary.Uri))
            throw new TransportException(QueueTransport.RefusalMessage, httpStatusCode: null, scheme: "didcomm");
        if (!Uri.TryCreate(primary.Uri, UriKind.Absolute, out var endpoint))
            throw new TransportException("The resolved service endpoint is not an absolute URI; it is not quoted (FR-ROUTE-03).");

        // The guard SendAsync applies to a resolved endpoint, applied to the URI the override will
        // carry: it came from the peer's DID document.
        await new OutboundEndpointGuard(_options.OutboundEndpointPolicy).ValidateAsync(endpoint, ct).ConfigureAwait(false);
        return endpoint;
    }

    /// <summary>
    /// Whether a message a peer pushes back on the connection a return-route send to
    /// <paramref name="endpoint"/> uses reaches the registered inbound consumer (PRD §8a, FR-RR-07).
    /// The mediator client asks before sending a request whose answer can only come back that way
    /// (#96). The transport asked is the one <see cref="SendAsync"/> would send through: the one this
    /// facade's own <see cref="TransportRouter"/> selects for the endpoint (L-041). Reads state only;
    /// resolves and sends nothing.
    /// </summary>
    /// <param name="endpoint">The absolute endpoint the send will carry as its override.</param>
    /// <returns>
    /// <c>true</c> or <c>false</c> when that transport says (<see cref="IInboundReceivingTransport"/>:
    /// the WebSocket transport answers <c>false</c> unless <c>ReceiveInbound</c> is on and STOMP is
    /// off); <c>null</c> when this facade cannot tell — a router of the host's own, a transport that
    /// does not say, or no router, no transport for the scheme or the queue URI, which the send then
    /// refuses itself.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <c>null</c>.</exception>
    internal bool? ReceivesPushesOn(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (_transportRouter is not TransportRouter router || QueueTransport.IsQueueTransport(endpoint))
            return null;
        return router.Select(endpoint) is IInboundReceivingTransport receiving ? receiving.ReceivesInbound : null;
    }

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
    /// <remarks>
    /// Every layer is unpacked with <see cref="UnpackAsync"/>, not with the mediator's restricted
    /// unpack, because what a layer carries may legitimately be the innermost message in any
    /// envelope (authcrypt included). The shape is judged after the unpack, on what the envelope
    /// layer established (<see cref="IsRewrapLayerForThisAgent(UnpackResult, string, IReadOnlyList{Attachment})"/>). Only the DID that decrypted a
    /// layer counts as this agent, as on the mediator (<c>ForwardHandler</c>): a forward naming
    /// another DID — another DID this agent holds included — is not opened.
    /// </remarks>
    /// <param name="packed">The delivered packed message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The innermost unpack result, the innermost packed text, and how many forward layers were opened.</returns>
    /// <exception cref="ConsistencyException">A layer is a <c>forward</c> that is not a delivery re-wrapped for this agent (another <c>next</c>, authcrypt, signed, <c>from_prior</c>, another envelope inside, or not exactly one attachment), or its attachment has no data.</exception>
    /// <exception cref="MalformedMessageException">More than the maximum number of forward layers, an attachment that is not valid base64url, decodes to nothing or is not a UTF-8 JSON object, or a layer that fails to unpack (FR-API-05 expiry and FR-API-06 size included).</exception>
    /// <exception cref="DidCommException">Any other refusal of a layer's unpack.</exception>
    internal async Task<DeliveredUnpack> UnpackDeliveredAsync(string packed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        var result = await UnpackAsync(packed, ct).ConfigureAwait(false);
        return await OpenRewrapLayersAsync(result, packed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Go on from a message a plain receiving endpoint has already unpacked (DidComm.AspNetCore,
    /// owner decision OD-3 on #110): when it is a <c>forward</c> re-wrapped for this agent, open it
    /// and every such layer inside it, with the rule and the bound of
    /// <see cref="UnpackDeliveredAsync"/> (FR-ROUTE-06, receiver side: "The outer envelope is a
    /// forward message where to is the receiver itself", routing.md "Rewrapping"); otherwise return
    /// <paramref name="received"/> as it is, with no layer opened. The received layer is not
    /// unpacked again: only the layers inside it are.
    /// </summary>
    /// <remarks>
    /// The received message is opened only when it has the shape <see cref="IsRewrapLayerForThisAgent(UnpackResult)"/>
    /// requires. Anything else — not a forward, a forward that names another DID as <c>next</c>
    /// (another DID this agent holds included), one that is signed, authcrypted or carries
    /// <c>from_prior</c>, one without exactly one attachment, or a forward too broken to read — is
    /// returned unchanged: a plain receiver is not a mediator and relays nothing. Once a layer is
    /// opened, what is inside must be the message itself or another such layer: a forward of any
    /// other shape inside, a layer past the bound, an attachment that does not decode, or a layer
    /// that fails to unpack fails the whole message, as <see cref="UnpackDeliveredAsync"/> fails it.
    /// The innermost message is the result of its own unpack, so its sender, signer and anonymity
    /// are its own envelope's; nothing is taken from a layer around it.
    /// </remarks>
    /// <param name="received">The unpack result of the received message.</param>
    /// <param name="packed">The packed text <paramref name="received"/> was unpacked from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The innermost unpack result (<paramref name="received"/> itself when no layer was opened), the packed text it came from, and how many layers were opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="received"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="packed"/> is <c>null</c> or empty.</exception>
    /// <exception cref="ConsistencyException">An opened layer holds a <c>forward</c> that is not a delivery re-wrapped for this agent, or an attachment with no data.</exception>
    /// <exception cref="MalformedMessageException">More than the maximum number of layers, an attachment that is not valid base64url, decodes to nothing or is not a UTF-8 JSON object, or a layer inside that fails to unpack (FR-API-05 expiry and FR-API-06 size included).</exception>
    /// <exception cref="DidCommException">Any other refusal of the unpack of a layer inside.</exception>
    internal Task<DeliveredUnpack> OpenReceivedRewrapAsync(UnpackResult received, string packed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentException.ThrowIfNullOrEmpty(packed);
        return IsRewrapLayerForThisAgent(received)
            ? OpenRewrapLayersAsync(received, packed, ct)
            : Task.FromResult(new DeliveredUnpack(received, packed, 0));
    }

    /// <summary>
    /// For a receiver with the routing role, a registered handler for forwards (the mediator's
    /// <see cref="ForwardHandler"/>; owner decision OD-6 on #110): when <paramref name="received"/> is
    /// a <c>forward</c> re-wrapped for this agent whose layers end in a message that is not a forward,
    /// return that message, for this agent to handle as its own. A mediator reached through a
    /// rewrapping mediator gets every message sent to it that way (routing.md "Rewrapping": "The
    /// outer envelope is a forward message where to is the receiver itself"). Returns <c>null</c> in
    /// every other case, and the forward is then left to the forward handler as it arrived: a forward
    /// for the next hop inside (relayed with every layer's expiry and delay, FR-ROUTE-09/10), and
    /// every layer that cannot be opened (the handler drops it with its typed outcome). Never throws
    /// for what it opens; only a cancellation of <paramref name="ct"/> propagates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each layer addressed to this agent is opened as the forward handler opens it: the restricted
    /// unpack of an anoncrypt envelope around a plaintext message, which looks up no DID
    /// (<see cref="UnpackAnoncryptedPlaintextAsync"/>), at most
    /// <see cref="ForwardHandler.MaxSelfAddressedLayers"/> layers, the received one included. Only the
    /// innermost layer, when it is not that shape (an authcrypt or signed message, the shapes a
    /// mediator's own protocol messages arrive in; the restricted unpack refuses it for its shape),
    /// gets the full unpack: one lookup of the sender's (or signer's) DID per request, what the same
    /// message costs when it arrives directly. If that turns out to be a forward, it is not opened
    /// further: it is the handler's to refuse. A layer that does not decrypt, or fails the receive
    /// rules, is tried once and left to the handler.
    /// </para>
    /// <para>
    /// Each layer's opening is recorded for that layer (<see cref="SelfAddressedPeels"/>). When the
    /// forward goes to the forward handler, the handler's peel takes the recorded result or refusal
    /// instead of opening the layer again. So each layer is opened once per request, and a chain
    /// left to the handler costs what it cost before this look-ahead.
    /// </para>
    /// <para>
    /// The message returned is the result of its own unpack: its sender, signer and anonymity are its
    /// own envelope's. The caller admits it as it would a message that arrived directly (replay
    /// admission, <c>created_time</c>, the id cap), with no return route (return_route.md: the
    /// connection is the mediator's, not its sender's).
    /// </para>
    /// </remarks>
    /// <param name="received">The unpack result of the received message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message found inside, the packed text it was unpacked from and how many layers were opened (at least one); <c>null</c> to leave the received forward to the forward handler.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="received"/> is <c>null</c>.</exception>
    internal async Task<DeliveredUnpack?> OpenSelfAddressedMessageAsync(UnpackResult received, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (!IsRewrapLayerForThisAgent(received))
            return null;

        var layer = received;
        for (var opened = 1; opened <= ForwardHandler.MaxSelfAddressedLayers; opened++)
        {
            // The layer's one attachment (the shape rule checked it), decoded by the relay rule, then
            // the forward handler's restricted opening (ForwardProcessor.PeelAsync does the same two
            // steps). Every outcome but a cancellation is recorded for this layer, so the forward
            // handler, if the forward goes to it, takes it instead of opening the layer again.
            string text;
            try
            {
                ForwardMessage.TryParse(layer.Message, out _, out var attachments);
                text = ForwardProcessor.ExtractAttachmentText(attachments[0], _options.MaxReceiveBytes);
            }
            catch (DidCommException ex)
            {
                SelfAddressedPeels.Refused(this, layer, ex);
                return null;
            }

            UnpackResult inner;
            try
            {
                inner = await UnpackAnoncryptedPlaintextAsync(text, ct).ConfigureAwait(false);
            }
            catch (MalformedMessageException ex) when (ex.RefusedShape)
            {
                // Not an anoncrypted plaintext message (authcrypt, signed, another envelope inside,
                // from_prior): the innermost layer, given the full unpack once.
                SelfAddressedPeels.Refused(this, layer, ex);
                return await OpenInnermostAsync(text, opened, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // It does not decrypt, parse or pass the receive rules: tried once here, it is the
                // forward handler's to drop.
                SelfAddressedPeels.Refused(this, layer, ex);
                return null;
            }
            SelfAddressedPeels.Opened(this, layer, inner);

            if (!string.Equals(inner.Message.Type, ForwardConstants.ForwardTypeUri, StringComparison.Ordinal))
                return new DeliveredUnpack(inner, text, opened);
            // A forward: another layer for this agent is opened in turn; any other is the handler's.
            if (!IsRewrapLayerForThisAgent(inner))
                return null;
            layer = inner;
        }

        // Past the bound: the forward handler drops it.
        return null;
    }

    /// <summary>
    /// The one full unpack of <see cref="OpenSelfAddressedMessageAsync"/>: an attachment the
    /// restricted unpack refused. A message that is not a forward is returned; a forward, or anything
    /// that does not unpack, leaves the received forward to the handler (<c>null</c>).
    /// </summary>
    /// <param name="text">The attachment's packed text.</param>
    /// <param name="opened">How many layers are opened once it is.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<DeliveredUnpack?> OpenInnermostAsync(string text, int opened, CancellationToken ct)
    {
        try
        {
            var full = await UnpackAsync(text, ct).ConfigureAwait(false);
            return string.Equals(full.Message.Type, ForwardConstants.ForwardTypeUri, StringComparison.Ordinal)
                ? null
                : new DeliveredUnpack(full, text, opened);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The opening loop of <see cref="UnpackDeliveredAsync"/> and <see cref="OpenReceivedRewrapAsync"/>,
    /// from an unpacked outermost layer: a message that is not a forward is the message; a forward is
    /// opened only when it is a layer re-wrapped for this agent, at most
    /// <see cref="ForwardHandler.MaxSelfAddressedLayers"/> of them, and any other forward fails.
    /// </summary>
    /// <param name="outermost">The unpack result of the outermost layer.</param>
    /// <param name="packed">The packed text it was unpacked from.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<DeliveredUnpack> OpenRewrapLayersAsync(UnpackResult outermost, string packed, CancellationToken ct)
    {
        var text = packed;
        var result = outermost;
        for (var opened = 0; ; opened++)
        {
            // Not a forward: this is the message. A structurally broken forward throws Malformed.
            if (!ForwardMessage.TryParse(result.Message, out var next, out var attachments))
                return new DeliveredUnpack(result, text, opened);

            if (!IsRewrapLayerForThisAgent(result, next, attachments))
            {
                throw new ConsistencyException(
                    "A forward that is not a delivery re-wrapped for this agent cannot be opened (FR-ROUTE-06): it must be anoncrypted plaintext, without a signature or from_prior, carry exactly one attachment, and name as next the DID that decrypted it. It is not a message for this agent.");
            }
            if (opened == ForwardHandler.MaxSelfAddressedLayers)
            {
                throw new MalformedMessageException(
                    $"The delivered message carries more than {ForwardHandler.MaxSelfAddressedLayers} forward layers re-wrapped for this agent (FR-ROUTE-06).");
            }

            text = ForwardProcessor.ExtractAttachmentText(attachments[0], _options.MaxReceiveBytes);
            result = await UnpackAsync(text, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether an unpacked <c>forward</c> is a layer a re-wrapping mediator added for this agent:
    /// exactly what <c>ForwardProcessor</c> produces in <c>RewrapMode.ReanoncryptToNext</c> and what
    /// a mediator opens out of a forward addressed to itself (<c>ForwardProcessor.PeelAsync</c> over
    /// the restricted unpack, then <c>ForwardHandler</c>'s self-addressed test) — an anoncrypt
    /// envelope directly around a plaintext message (no signature, no envelope inside, no sender key),
    /// without <c>from_prior</c>, with exactly one attachment, whose <c>next</c> has the DID subject
    /// of the key that decrypted it. Every condition is evaluated (no early exit).
    /// </summary>
    /// <param name="layer">The unpack result of the forward.</param>
    /// <param name="next">Its <c>next</c>.</param>
    /// <param name="attachments">Its attachments.</param>
    internal static bool IsRewrapLayerForThisAgent(UnpackResult layer, string next, IReadOnlyList<Attachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(attachments);
        var stack = layer.Stack;
        var anoncryptOfPlaintext = stack is { Count: 2 }
            && stack[0] == EnvelopeKind.Encrypted
            && stack[1] == EnvelopeKind.Plaintext;
        var anonymous = layer.Encrypted & layer.AnonymousSender & !layer.Authenticated & !layer.NonRepudiation
            & string.IsNullOrEmpty(layer.SenderKid) & string.IsNullOrEmpty(layer.SignerKid);
        var noFromPrior = layer.FromPrior is null & layer.Message.FromPrior is null;
        var nextSubject = DidSubject.DidSubjectOf(next);
        var decryptedBy = DidSubject.DidSubjectOf(layer.RecipientKid);
        var selfAddressed = nextSubject is not null & decryptedBy is not null
            & string.Equals(nextSubject, decryptedBy, StringComparison.Ordinal);
        return anoncryptOfPlaintext & anonymous & noFromPrior & attachments.Count == 1 & selfAddressed;
    }

    /// <summary>
    /// Whether an unpacked message is a <c>forward</c> re-wrapped for this agent
    /// (<see cref="IsRewrapLayerForThisAgent(UnpackResult, string, IReadOnlyList{Attachment})"/>).
    /// A forward too broken to read (no <c>next</c>, no attachments) is not one: it is left as it
    /// arrived.
    /// </summary>
    /// <param name="layer">The unpack result.</param>
    internal static bool IsRewrapLayerForThisAgent(UnpackResult layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        try
        {
            return ForwardMessage.TryParse(layer.Message, out var next, out var attachments)
                && IsRewrapLayerForThisAgent(layer, next, attachments);
        }
        catch (MalformedMessageException)
        {
            return false;
        }
    }

    // The pack-stage refusal of a routed operation without a service resolver (PackEncryptedAsync's text).
    private const string ForwardRequiresServiceResolver =
        "Forward = true requires an IServiceEndpointResolver. Use the (ISecretsResolver, IDidKeyService, IServiceEndpointResolver, DidCommOptions) constructor, or register the facade via AddDidComm + UseNetDidResolver.";
}

/// <summary>
/// What <see cref="DidCommClient.UnpackDeliveredAsync"/> returns for one delivered message.
/// </summary>
/// <param name="Result">The unpack result of the innermost message (every re-wrap layer opened).</param>
/// <param name="InnermostPacked">The packed text the innermost message was unpacked from: the delivered text itself when no layer was opened. The duplicate filter keys anonymous messages on it.</param>
/// <param name="LayersOpened">How many self-addressed forward layers were opened, 0 to <see cref="MediatorEndpointExpander.MaxForwardLayers"/>.</param>
internal readonly record struct DeliveredUnpack(UnpackResult Result, string InnermostPacked, int LayersOpened);
