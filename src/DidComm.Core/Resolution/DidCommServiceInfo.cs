namespace DidComm.Resolution;

/// <summary>
/// A single <c>DIDCommMessaging</c> service entry extracted from a resolved DID Document
/// (PRD §8, FR-ROUTE-03). One DID Document can publish more than one such entry — the spec
/// orders them by preference; <see cref="IServiceEndpointResolver.ResolveAsync"/> returns the
/// list with that preference preserved.
/// </summary>
/// <remarks>
/// <para>
/// Per the v2.1 §Service Endpoint section, the <c>serviceEndpoint</c> of a
/// <c>DIDCommMessaging</c> service is a single object (or an array of such objects) carrying
/// the three pieces of information this record exposes:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Uri"/> — the destination URI. MAY be a transport URL (e.g. <c>https://…</c>) or a DID (mediator-as-DID-endpoint per FR-ROUTE-04).</description></item>
///   <item><description><see cref="RoutingKeys"/> — DID URLs identifying keys to wrap forward layers for, in reverse order, per FR-ROUTE-02.</description></item>
///   <item><description><see cref="Accept"/> — accepted DIDComm profiles (e.g. <c>didcomm/v2</c>). An empty list means "no profile filter".</description></item>
/// </list>
/// </remarks>
/// <param name="Uri">The endpoint URI (transport URL or DID). Always non-empty.</param>
/// <param name="RoutingKeys">Ordered DID-URL list of routing keys (may be empty — direct delivery).</param>
/// <param name="Accept">Ordered profile list (may be empty — caller picks its preferred profile).</param>
public sealed record DidCommServiceInfo(
    string Uri,
    IReadOnlyList<string> RoutingKeys,
    IReadOnlyList<string> Accept);
