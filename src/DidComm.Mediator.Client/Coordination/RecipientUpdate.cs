namespace DidComm.Mediator.Coordination;

/// <summary>What a <c>recipient-update</c> entry asks the mediator to do with a DID (<c>action</c>).</summary>
public enum RecipientUpdateAction
{
    /// <summary><c>add</c>: start accepting forwards for the DID.</summary>
    Add,

    /// <summary><c>remove</c>: stop accepting forwards for the DID.</summary>
    Remove,
}

/// <summary>
/// The outcome the mediator reports for one <c>recipient-update</c> entry (<c>result</c>). The
/// protocol fixes these four values.
/// </summary>
public enum RecipientUpdateOutcome
{
    /// <summary><c>client_error</c>: the request cannot be honoured as sent (not a bare DID, registered under another grant, not registered, over the per-grant limit, or the peer holds no grant).</summary>
    ClientError,

    /// <summary><c>server_error</c>: the mediator could not apply the entry (a store fault, or the mediator-wide limit is reached).</summary>
    ServerError,

    /// <summary><c>no_change</c>: the DID was already registered under the caller's grant.</summary>
    NoChange,

    /// <summary><c>success</c>: the entry was applied.</summary>
    Success,
}

/// <summary>One entry of a <c>recipient-update</c> message: <c>{"recipient_did": …, "action": …}</c>.</summary>
/// <param name="RecipientDid">The DID the update is about (<c>recipient_did</c>). At most 2048 characters (FR-API-09).</param>
/// <param name="Action">What to do with it.</param>
public sealed record RecipientUpdate(string RecipientDid, RecipientUpdateAction Action);

/// <summary>One entry of a <c>recipient-update-response</c> message: <c>{"recipient_did": …, "action": …, "result": …}</c>.</summary>
/// <param name="RecipientDid">The DID the update was about, as the request carried it.</param>
/// <param name="Action">The requested action, repeated.</param>
/// <param name="Result">What happened.</param>
public sealed record RecipientUpdateResult(string RecipientDid, RecipientUpdateAction Action, RecipientUpdateOutcome Result);

/// <summary>The optional <c>paginate</c> member of a <c>recipient-query</c>: <c>{"limit": …, "offset": …}</c>.</summary>
/// <param name="Limit">The largest number of DIDs wanted. Zero or less asks for an empty page; the mediator clamps it to its maximum page size.</param>
/// <param name="Offset">How many registered DIDs to skip. Never negative on the wire (a negative value is malformed).</param>
public sealed record RecipientQueryPaging(int Limit, int Offset);

/// <summary>The <c>pagination</c> member of a <c>recipient</c> message: <c>{"count": …, "offset": …, "remaining": …}</c>.</summary>
/// <param name="Count">The number of DIDs in this page.</param>
/// <param name="Offset">The offset this page starts at.</param>
/// <param name="Remaining">The number of registered DIDs after this page.</param>
public sealed record RecipientPagination(int Count, int Offset, int Remaining);
