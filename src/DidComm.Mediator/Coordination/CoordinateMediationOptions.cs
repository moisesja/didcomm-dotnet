using DidComm.Consistency;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// Settings of the Coordinate Mediation mediator role: the routing DIDs it grants, the page size of
/// <c>recipient-query</c>, and the bounds of the in-memory store (FR-MED-06/08).
/// </summary>
public sealed class CoordinateMediationOptions
{
    /// <summary>
    /// The mediator DIDs a <c>mediate-grant</c> carries as <c>routing_did</c>. Empty by default: the
    /// grant then carries the DID that decrypted the request. Each must be a bare DID (no path,
    /// query or fragment) of at most 2048 characters. Ignored when the host registers its own
    /// <see cref="IMediatorIdentity"/>.
    /// </summary>
    public IList<string> RoutingDids { get; } = new List<string>();

    /// <summary>
    /// The largest page a <c>recipient-query</c> gets (FR-MED-06). A larger <c>limit</c> is clamped
    /// to it, and a query without <c>paginate</c> gets the first page of this size. Default 100.
    /// </summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>
    /// The largest number of entries one <c>recipient-update</c> may carry. A longer list is refused
    /// as malformed from its length alone: no entry is decoded, checked or allocated, and the store
    /// and the policy are not called. What it bounds is the per-entry work (DID parsing, policy
    /// calls, store operations, the size of the reply); the message itself was already parsed once
    /// on receipt, within <c>DidCommOptions.MaxReceiveBytes</c>. Default 1024.
    /// </summary>
    public int MaxUpdatesPerMessage { get; set; } = 1024;

    /// <summary>
    /// The largest number of grants <see cref="InMemoryMediationStore"/> holds. A
    /// <c>mediate-request</c> from a new peer beyond it is answered <c>mediate-deny</c>.
    /// Default 10 000.
    /// </summary>
    public int MaxGrants { get; set; } = 10_000;

    /// <summary>
    /// The largest number of DIDs registered under one grant in <see cref="InMemoryMediationStore"/>.
    /// An <c>add</c> beyond it is answered <c>client_error</c>. Default 256.
    /// </summary>
    public int MaxRecipientsPerGrant { get; set; } = 256;

    /// <summary>
    /// The largest number of DIDs registered across all grants in
    /// <see cref="InMemoryMediationStore"/>. An <c>add</c> beyond it is answered
    /// <c>server_error</c>. Default 100 000.
    /// </summary>
    public int MaxRecipients { get; set; } = 100_000;

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

/// <summary>The "bare DID" rule of FR-MED-05: a DID with no path, query or fragment.</summary>
internal static class BareDid
{
    /// <summary>
    /// Whether <paramref name="value"/> is a DID and nothing more: non-empty, at most
    /// <see cref="IdentifierLimits.MaxDidLength"/> characters (checked first, so an over-long value
    /// is never parsed), and equal to its own DID subject (PRD §4.3). A DID URL is not normalized
    /// into its DID; it is simply not a bare DID.
    /// </summary>
    public static bool IsBareDid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > IdentifierLimits.MaxDidLength)
            return false;
        return string.Equals(DidSubject.DidSubjectOf(value), value, StringComparison.Ordinal);
    }
}
