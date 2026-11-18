namespace DidComm.Mediator.Coordination;

/// <summary>
/// Settings of the Coordinate Mediation mediator role, 3.0 and 2.0 alike: the routing DIDs it
/// grants, whether 2.0 is served too, the page size of <c>recipient-query</c> / <c>keylist-query</c>,
/// and the bounds of the in-memory store (FR-MED-06/08, FR-MED-10).
/// </summary>
/// <remarks>
/// The in-memory store is meant for samples, tests and small hosts, so its default bounds are
/// modest: 1 000 grants and 10 000 registered DIDs in all. Each key is at most 2048 characters, so
/// the store's keys take about 45 MB in the worst case at the defaults (11 000 keys of 4 KiB). A
/// host that needs more raises the bounds, or registers its own <see cref="IMediationStore"/>.
/// </remarks>
public sealed class CoordinateMediationOptions
{
    /// <summary>
    /// The mediator DIDs a <c>mediate-grant</c> carries as <c>routing_did</c>. Empty by default: the
    /// grant then carries the DID that decrypted the request. Each must be a bare DID (no path,
    /// query or fragment) of at most 2048 characters. Ignored when the host registers its own
    /// <see cref="IMediatorIdentity"/>. A complete mediator (<c>AddMediator()</c>) needs at least
    /// one, here or from the host's <see cref="IMediatorIdentity"/>.
    /// </summary>
    /// <remarks>
    /// <strong>Order matters: the first DID is the primary one.</strong> A Coordinate Mediation 3.0
    /// grant lists every DID, the first one first; a 2.0 grant has room for one DID only and carries
    /// the first (FR-MED-10); and <c>AddMediator()</c> issues its out-of-band invitation from the
    /// first. To move to a new DID, list the new one first and keep the old one, with its keys:
    /// forwards addressed to any listed routing DID keep being relayed, whichever one a grant named.
    /// A grant already issued keeps the DIDs it was issued with, so a peer that asks again gets the
    /// same answer.
    /// </remarks>
    public IList<string> RoutingDids { get; } = new List<string>();

    /// <summary>
    /// Whether <c>AddCoordinateMediation()</c> also serves Coordinate Mediation 2.0
    /// (<see cref="CoordinateMediation20Handler"/>), the version the Identus and Blocktrust mediators
    /// and their wallets speak. Default <c>true</c>: interop first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>true</c>, 2.0 is answered and disclosed by Discover Features (roles <c>mediator</c>)
    /// next to 3.0, over the same store, policy and identity, so a peer is one tenant in both
    /// versions. When <c>false</c>, no 2.0 handler is registered and nothing discloses it: a 2.0
    /// request is not answered (no handler serves its type).
    /// </para>
    /// <para>
    /// Read when <c>AddCoordinateMediation()</c> (or <c>AddMediator()</c>, which calls it) runs,
    /// because it decides what is registered; set it in that call's delegate. To serve 3.0 only, set
    /// it to <c>false</c> on every such call. A value set any other way that disagrees with what was
    /// registered (<c>services.Configure&lt;CoordinateMediationOptions&gt;</c>, configuration binding,
    /// or two calls that disagree) fails with <see cref="InvalidOperationException"/> when the
    /// Coordinate Mediation handlers are first built: at start-up in a generic host with
    /// <c>AddMediator()</c>, otherwise on the first resolution of the protocol dispatcher.
    /// </para>
    /// </remarks>
    public bool EnableV2 { get; set; } = true;

    /// <summary>
    /// The largest page a <c>recipient-query</c> (2.0: <c>keylist-query</c>) gets (FR-MED-06). A
    /// larger <c>limit</c> is clamped to it, and a query without <c>paginate</c> gets the first page
    /// of this size. Default 100.
    /// </summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>
    /// The largest number of entries one <c>recipient-update</c> (2.0: <c>keylist-update</c>) may
    /// carry. A longer list is refused
    /// as malformed from its length alone: no entry is decoded, checked or allocated, and the store
    /// and the policy are not called. What it bounds is the per-entry work (DID parsing, policy
    /// calls, store operations, the size of the reply); the message itself was already parsed once
    /// on receipt, within <c>DidCommOptions.MaxReceiveBytes</c>. The answer repeats every entry with
    /// its <c>result</c>, so it is slightly larger than the request (about 1% for long DIDs): a
    /// sender close to its own receive limit splits its list. Default 1024.
    /// </summary>
    public int MaxUpdatesPerMessage { get; set; } = 1024;

    /// <summary>
    /// The largest number of grants <see cref="InMemoryMediationStore"/> holds. A
    /// <c>mediate-request</c> from a new peer beyond it is answered <c>mediate-deny</c>.
    /// Default 1 000.
    /// </summary>
    public int MaxGrants { get; set; } = 1_000;

    /// <summary>
    /// The largest number of DIDs registered under one grant in <see cref="InMemoryMediationStore"/>.
    /// An <c>add</c> beyond it is answered <c>client_error</c>. Default 256.
    /// </summary>
    public int MaxRecipientsPerGrant { get; set; } = 256;

    /// <summary>
    /// The largest number of DIDs registered across all grants in
    /// <see cref="InMemoryMediationStore"/>. An <c>add</c> beyond it is answered
    /// <c>server_error</c>. Default 10 000.
    /// </summary>
    public int MaxRecipients { get; set; } = 10_000;

    /// <summary>
    /// Whether <c>AddCoordinateMediation()</c> also makes the store the mediator's forward
    /// recipient directory (<see cref="Protocols.Routing.IForwardRecipientRegistry"/>), so a forward
    /// is relayed exactly when its <c>next</c> is a registered recipient DID. Default <c>true</c>.
    /// Read when <c>AddCoordinateMediation()</c> runs, not later. Set it to <c>false</c> when the
    /// host registers its own directory; that directory then has to consult
    /// <see cref="IMediationStore.FindGrantByRecipientDidAsync"/> itself.
    /// </summary>
    public bool UseAsForwardRecipientRegistry { get; set; } = true;

    /// <summary>Check the settings; called when the role is registered and when it is first used.</summary>
    /// <exception cref="ArgumentException">A bound is below 1, or a routing DID is empty, over 2048 characters or not a bare DID. The DID is not quoted.</exception>
    public void Validate()
    {
        RequirePositive(MaxPageSize, nameof(MaxPageSize));
        RequirePositive(MaxUpdatesPerMessage, nameof(MaxUpdatesPerMessage));
        RequirePositive(MaxGrants, nameof(MaxGrants));
        RequirePositive(MaxRecipientsPerGrant, nameof(MaxRecipientsPerGrant));
        RequirePositive(MaxRecipients, nameof(MaxRecipients));

        foreach (var did in RoutingDids)
        {
            if (!BareDid.IsBareDid(did))
                throw new ArgumentException(
                    "A routing DID must be a bare DID (no path, query or fragment) of at most 2048 characters.", nameof(RoutingDids));
        }
    }

    private static void RequirePositive(int value, string name)
    {
        if (value < 1)
            throw new ArgumentException("The value must be at least 1.", name);
    }
}
