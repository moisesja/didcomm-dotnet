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
        new OutboundEndpointGuard(_options.OutboundEndpointPolicy).Validate(endpoint);
        return endpoint;
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
    /// layer established (<see cref="IsRewrapLayerForThisAgent"/>). Only the DID that decrypted a
    /// layer counts as this agent, as on the mediator (<c>ForwardHandler</c>): a forward naming
    /// another DID — another DID this agent holds included — is not opened.
    /// </remarks>
    /// <param name="packed">The delivered packed message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The innermost unpack result, the innermost packed text, and how many forward layers were opened.</returns>
    /// <exception cref="ConsistencyException">A layer is a <c>forward</c> that is not a delivery re-wrapped for this agent (another <c>next</c>, authcrypt, signed, <c>from_prior</c>, another envelope inside, or not exactly one attachment), or its attachment has no data.</exception>
    /// <exception cref="MalformedMessageException">More than the maximum number of forward layers, an attachment that is not valid base64url or decodes to nothing, or a layer that fails to unpack (FR-API-05 expiry and FR-API-06 size included).</exception>
    /// <exception cref="DidCommException">Any other refusal of a layer's unpack.</exception>
    internal async Task<DeliveredUnpack> UnpackDeliveredAsync(string packed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(packed);
        var text = packed;
        var result = await UnpackAsync(text, ct).ConfigureAwait(false);
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

            text = Encoding.UTF8.GetString(ForwardProcessor.ExtractAttachmentBytes(attachments[0]));
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
