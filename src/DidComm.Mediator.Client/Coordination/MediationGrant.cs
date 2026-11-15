namespace DidComm.Mediator.Coordination;

/// <summary>
/// A mediation relationship: the mediator agreed to accept forwards for the DIDs that
/// <paramref name="PeerDid"/> registers (FR-MED-05). One grant exists per peer DID.
/// </summary>
/// <param name="PeerDid">The recipient that enrolled: the DID subject of the authcrypt sender key of its <c>mediate-request</c>. The key of the grant.</param>
/// <param name="RoutingDids">The mediator DIDs the grant told the recipient to publish (<c>routing_did</c>). A repeated <c>mediate-request</c> is answered with exactly these.</param>
public sealed record MediationGrant(string PeerDid, IReadOnlyList<string> RoutingDids);
