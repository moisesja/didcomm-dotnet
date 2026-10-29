using DidComm.Jose;

namespace DidComm.Resolution;

/// <summary>
/// The fully-expanded routing information needed by the sender wrapping algorithm
/// (PRD §8, FR-ROUTE-02 / FR-ROUTE-04): one or more <see cref="DidCommServiceInfo"/>
/// candidates from the recipient's DID Document, one picked, and — if its
/// <see cref="DidCommServiceInfo.Uri"/> is itself a DID (mediator-as-endpoint) — that mediator's
/// <c>DIDCommMessaging</c> service dereferenced to obtain a real transport URI plus the
/// mediator's <c>keyAgreement</c> keys prepended to the recipient's <c>routingKeys</c>.
/// </summary>
/// <remarks>
/// Per FR-ROUTE-04 a mediator's own service entry MUST publish plain transport URIs only
/// (no further DIDs) to avoid recursive endpoint resolution; the expander enforces that
/// invariant and raises if it sees a second DID-as-uri hop.
/// </remarks>
/// <param name="TransportUri">The plain transport URI the outermost packed envelope will be sent to.</param>
/// <param name="RoutingKeyJwks">Routing key public JWKs to anoncrypt-wrap for, in the **outer-to-inner** order, one forward layer per key. The sender wraps in REVERSE iteration to build the onion (FR-ROUTE-02). The facade's own <c>Forward = true</c> path groups keys per layer instead, so a mediator named by its DID gets one layer encrypted to all of its keyAgreement keys on one curve (FR-ENC-19).</param>
/// <param name="FallbackUris">Additional candidate transport URIs from the recipient's service list (FR-ROUTE-08 failover hook — Phase 5 transports iterate them).</param>
public sealed record ResolvedRoute(
    string TransportUri,
    IReadOnlyList<Jwk> RoutingKeyJwks,
    IReadOnlyList<string> FallbackUris);
