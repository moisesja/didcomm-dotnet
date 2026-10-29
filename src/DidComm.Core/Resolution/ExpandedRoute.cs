using DidComm.Jose;

namespace DidComm.Resolution;

/// <summary>
/// The route <see cref="MediatorEndpointExpander"/> produces for the sender's forward wrapping
/// (FR-ROUTE-02/04): the transport URI, the forward layers outermost first, and the failover URIs.
/// Internal, and grouped by layer rather than by key: one layer is one anoncrypt envelope, and when a
/// mediator is named by its DID that envelope addresses all of the mediator's keyAgreement keys
/// (FR-ENC-19), so a layer can carry several keys. The public <see cref="ResolvedRoute"/> keeps its
/// flat one-key-per-layer shape.
/// </summary>
/// <param name="TransportUri">The plain transport URI the outermost envelope is sent to.</param>
/// <param name="Layers">The forward layers in outer-to-inner order. The sender wraps in reverse to build the onion. Empty for direct delivery.</param>
/// <param name="FallbackUris">Additional candidate transport URIs from the recipient's service list (FR-ROUTE-08).</param>
internal sealed record ExpandedRoute(
    string TransportUri,
    IReadOnlyList<ForwardLayer> Layers,
    IReadOnlyList<string> FallbackUris);

/// <summary>
/// One forward layer: the mediator it is addressed to and the keys its anoncrypt envelope is
/// encrypted to. All keys share one curve and belong to <see cref="Did"/>.
/// </summary>
/// <param name="Did">The mediator's bare DID: the forward's <c>to</c>, and <c>body.next</c> of the layer outside it.</param>
/// <param name="Recipients">The keys the layer is encrypted to, at least one, in document order.</param>
internal sealed record ForwardLayer(string Did, IReadOnlyList<Jwk> Recipients);
