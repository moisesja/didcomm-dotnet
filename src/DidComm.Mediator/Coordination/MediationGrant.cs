namespace DidComm.Mediator.Coordination;

/// <summary>
/// A mediation relationship: the mediator agreed to accept forwards for the DIDs that
/// <paramref name="PeerDid"/> registers (FR-MED-05). One grant exists per peer DID.
/// </summary>
/// <param name="PeerDid">The recipient that enrolled: the DID subject of the authcrypt sender key of its <c>mediate-request</c>. The key of the grant.</param>
/// <param name="RoutingDids">The mediator DIDs the grant told the recipient to publish (<c>routing_did</c>). A repeated <c>mediate-request</c> is answered with exactly these.</param>
public sealed record MediationGrant(string PeerDid, IReadOnlyList<string> RoutingDids);

/// <summary>One page of the DIDs registered under a grant, as <see cref="IMediationStore.ListRecipientsAsync"/> returns it.</summary>
/// <param name="Dids">The DIDs in this page, in registration order.</param>
/// <param name="Total">The number of DIDs registered under the grant in all, at the moment the page was read.</param>
public sealed record RecipientPage(IReadOnlyList<string> Dids, int Total);

/// <summary>
/// What a store did with one <see cref="RecipientUpdate"/>. The store reports the fact; the handler
/// maps it to the protocol's <c>result</c> value, so a store never has to know the wire vocabulary.
/// </summary>
public enum RecipientStoreOutcome
{
    /// <summary>The DID is now registered under the caller's grant (<c>success</c>).</summary>
    Added,

    /// <summary>The DID was registered under the caller's grant and no longer is (<c>success</c>).</summary>
    Removed,

    /// <summary><c>add</c> of a DID the caller's grant already holds (<c>no_change</c>).</summary>
    AlreadyRegistered,

    /// <summary><c>add</c> of a DID another grant holds; a DID belongs to the first grant that registers it (<c>client_error</c>).</summary>
    OwnedByAnotherGrant,

    /// <summary><c>remove</c> of a DID the caller's grant does not hold, whether another grant holds it or nobody does (<c>client_error</c>).</summary>
    NotRegistered,

    /// <summary><c>add</c> refused: the caller's grant is at its recipient limit (<c>client_error</c>).</summary>
    GrantLimitReached,

    /// <summary><c>add</c> refused: the store is at its total recipient limit (<c>server_error</c>).</summary>
    StoreLimitReached,

    /// <summary>The caller holds no grant, so nothing was applied (<c>client_error</c>).</summary>
    NoGrant,
}
