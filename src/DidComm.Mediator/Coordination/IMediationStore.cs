namespace DidComm.Mediator.Coordination;

/// <summary>
/// The mediator's registry of grants and of the recipient DIDs registered under them
/// (FR-MED-05/08). <see cref="CoordinateMediationHandler"/> writes it; the forward path reads it to
/// decide whether a forward's <c>next</c> names someone this mediator relays for (FR-ROUTE-11).
/// </summary>
/// <remarks>
/// <para>
/// The library ships <see cref="InMemoryMediationStore"/> only (D1): a persistent store is the
/// host's, registered before <c>AddCoordinateMediation()</c>. An implementation is a singleton and
/// MUST be thread-safe. The contract it has to keep:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Keys are bare DIDs.</strong> Callers pass DID subjects (PRD §4.3) of at most 2048 characters: a peer DID taken from an authenticated sender key, and recipient DIDs the handler has checked. The store compares them ordinally and parses nothing.</description></item>
/// <item><description><strong>One grant per peer; one grant per recipient DID.</strong> A recipient DID belongs to the first grant that registers it, and the check and the insert are one atomic step: two peers adding the same DID at once must not both succeed.</description></item>
/// <item><description><strong><see cref="UpdateRecipientsAsync"/> decides "no grant" itself</strong>, atomically with respect to <see cref="RevokeAsync"/>, and reports one outcome per update in input order. It applies updates in order and does not throw after applying some of them: a fault it cannot recover from is thrown before anything changed, or the outcomes it returns are the truth.</description></item>
/// <item><description><strong><see cref="GrantAsync"/> is get-or-add.</strong> It returns the existing grant unchanged when the peer already holds one, also when the store is full.</description></item>
/// <item><description><strong><see cref="FindGrantByRecipientDidAsync"/> does the same work whether or not the DID is registered</strong> (one keyed lookup, no second step that only a hit takes), so the forward path cannot be used to probe registrations by timing.</description></item>
/// <item><description><strong>Bounded.</strong> The number of grants, of recipients per grant and of recipients in total are limited, and the limits are reported through the outcomes below, never by throwing.</description></item>
/// </list>
/// <para>
/// A method that throws is treated as a store fault: the handler answers <c>server_error</c> for a
/// <c>recipient-update</c> and sends no reply to a <c>mediate-request</c> or <c>recipient-query</c>;
/// the exception never leaves dispatch and only its type name is logged.
/// </para>
/// </remarks>
public interface IMediationStore
{
    /// <summary>The grant <paramref name="peerDid"/> holds, or <c>null</c>.</summary>
    /// <param name="peerDid">The recipient's DID subject.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct);

    /// <summary>
    /// Record <paramref name="grant"/> for its <see cref="MediationGrant.PeerDid"/> unless that peer
    /// already holds one. Returns the grant now in effect: the existing one, unchanged and with its
    /// registrations, or <paramref name="grant"/>. Returns <c>null</c> when the peer holds none and
    /// the store has no room for another.
    /// </summary>
    /// <param name="grant">The grant to record.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct);

    /// <summary>
    /// End the grant <paramref name="peerDid"/> holds and unregister every DID under it. Returns
    /// <c>false</c> when the peer held none. Forwards for those DIDs are dropped from then on.
    /// Messages already queued for them stay in the message store (not delivered while the peer
    /// holds no grant) until purged: a host ends a mediation with
    /// <c>MessagePickupQueue.RevokeAndPurgeAsync</c>, which calls this method and then purges.
    /// </summary>
    /// <param name="peerDid">The recipient's DID subject.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct);

    /// <summary>
    /// Apply <paramref name="updates"/> to the grant <paramref name="peerDid"/> holds, in order, and
    /// report what happened to each: the result has exactly one entry per update, in the same order.
    /// When the peer holds no grant every entry is <see cref="RecipientStoreOutcome.NoGrant"/>.
    /// </summary>
    /// <param name="peerDid">The recipient's DID subject.</param>
    /// <param name="updates">The updates to apply. Every <see cref="RecipientUpdate.RecipientDid"/> is a bare DID.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct);

    /// <summary>
    /// One page of the DIDs registered under the grant <paramref name="peerDid"/> holds, in
    /// registration order, with the total. An offset at or past the end, a limit of zero, or a peer
    /// without a grant gives an empty page (the total is still reported; zero without a grant).
    /// </summary>
    /// <param name="peerDid">The recipient's DID subject.</param>
    /// <param name="offset">How many DIDs to skip; never negative.</param>
    /// <param name="limit">The largest page wanted; never negative, already clamped by the caller.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct);

    /// <summary>
    /// The grant <paramref name="recipientDid"/> is registered under, or <c>null</c> when no grant
    /// holds it. Used for every forward (FR-ROUTE-11), so it must cost the same either way.
    /// </summary>
    /// <param name="recipientDid">A bare DID: the DID subject of a forward's <c>next</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct);
}
