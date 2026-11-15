using DidComm.Exceptions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The Coordinate Mediation messages <see cref="MediationClient"/> sends and reads, per version
/// (design §4.5): the request for each operation with what it expects back, and the checks an
/// answer passes before the client returns it. 3.0 goes through <see cref="CoordinateMediation"/>'s
/// builders, 2.0 through <see cref="CoordinateMediation20"/>; both read through
/// <see cref="MediationBodies"/>, with every list bounded from its length before an entry is read
/// (L-055). Owner: engineer B.
/// </summary>
/// <remarks>
/// Every refusal is a <see cref="MediationProtocolException"/> whose text names our request id, the
/// request's message name and the rule; a shape error carries the parser's
/// <see cref="MalformedMessageException"/> (which quotes no value) as its inner exception.
/// </remarks>
internal static class CoordinateMediationWire
{
    /// <summary>The most routing DIDs a grant may name (an array counted before it is read).</summary>
    internal const int MaxRoutingDids = 16;

    /// <summary>The most updates in one <c>recipient-update</c> (the mediator's <c>MaxUpdatesPerMessage</c> default).</summary>
    internal const int MaxUpdates = 1024;

    /// <summary>
    /// The largest page a caller may ask for (<c>paginate.limit</c>), and the most DIDs one answer may
    /// carry whatever was asked for: a mediator may page larger than asked (a 2.0 one that ignores
    /// <c>paginate</c> sends its whole list), so the bound is this constant, not the request's limit.
    /// </summary>
    internal const int MaxPageLimit = 1000;

    /// <summary>The page size <see cref="MediationClient.QueryAllRecipientsAsync"/> asks for.</summary>
    internal const int WalkerPageSize = 100;

    /// <summary>The most pages <see cref="MediationClient.QueryAllRecipientsAsync"/> fetches; a walk that needs more fails.</summary>
    internal const int MaxWalkerPages = 1000;

    private static readonly MessageTypeUri Grant3 = MessageTypeUri.Parse(CoordinateMediation.MediateGrantType);
    private static readonly MessageTypeUri Deny3 = MessageTypeUri.Parse(CoordinateMediation.MediateDenyType);
    private static readonly MessageTypeUri UpdateResponse3 = MessageTypeUri.Parse(CoordinateMediation.RecipientUpdateResponseType);
    private static readonly MessageTypeUri Recipient3 = MessageTypeUri.Parse(CoordinateMediation.RecipientType);

    /// <summary>What a 3.0 <c>mediate-request</c> expects: <c>mediate-grant</c> or <c>mediate-deny</c>.</summary>
    internal static readonly MediatorExchangeSpec MediateSpec3 = Spec("mediate-request", Grant3, Deny3);

    /// <summary>What a 3.0 <c>recipient-update</c> expects: <c>recipient-update-response</c>.</summary>
    internal static readonly MediatorExchangeSpec UpdateSpec3 = Spec("recipient-update", UpdateResponse3);

    /// <summary>What a 3.0 <c>recipient-query</c> expects: <c>recipient</c>.</summary>
    internal static readonly MediatorExchangeSpec QuerySpec3 = Spec("recipient-query", Recipient3);

    /// <summary>What a 2.0 <c>mediate-request</c> expects: <c>mediate-grant</c> or <c>mediate-deny</c>.</summary>
    internal static readonly MediatorExchangeSpec MediateSpec2 = Spec("mediate-request", CoordinateMediation20.MediateGrantUri, CoordinateMediation20.MediateDenyUri);

    /// <summary>What a 2.0 <c>keylist-update</c> expects: <c>keylist-update-response</c>.</summary>
    internal static readonly MediatorExchangeSpec UpdateSpec2 = Spec("keylist-update", CoordinateMediation20.KeylistUpdateResponseUri);

    /// <summary>What a 2.0 <c>keylist-query</c> expects: <c>keylist</c>.</summary>
    internal static readonly MediatorExchangeSpec QuerySpec2 = Spec("keylist-query", CoordinateMediation20.KeylistUri);

    // ---- requests -------------------------------------------------------------------------

    /// <summary>The <c>mediate-request</c> of <paramref name="version"/>, with a fresh UUID v4 id.</summary>
    internal static (Message Request, MediatorExchangeSpec Spec) MediateRequest(CoordinateMediationVersion version, string from, string mediatorDid)
        => version == CoordinateMediationVersion.V2
            ? (CoordinateMediation20.CreateMediateRequest(from, mediatorDid, UuidV4MessageIdGenerator.Instance), MediateSpec2)
            : (CoordinateMediation.CreateMediateRequest(from, mediatorDid, UuidV4MessageIdGenerator.Instance), MediateSpec3);

    /// <summary>The <c>recipient-update</c> (<c>keylist-update</c> in 2.0) carrying <paramref name="updates"/>, with a fresh UUID v4 id.</summary>
    internal static (Message Request, MediatorExchangeSpec Spec) UpdateRequest(CoordinateMediationVersion version, string from, string mediatorDid, IReadOnlyList<RecipientUpdate> updates)
        => version == CoordinateMediationVersion.V2
            ? (CoordinateMediation20.CreateKeylistUpdate(from, mediatorDid, updates, UuidV4MessageIdGenerator.Instance), UpdateSpec2)
            : (CoordinateMediation.CreateRecipientUpdate(from, mediatorDid, updates, UuidV4MessageIdGenerator.Instance), UpdateSpec3);

    /// <summary>The <c>recipient-query</c> (<c>keylist-query</c> in 2.0) for <paramref name="paginate"/>, with a fresh UUID v4 id.</summary>
    internal static (Message Request, MediatorExchangeSpec Spec) QueryRequest(CoordinateMediationVersion version, string from, string mediatorDid, RecipientQueryPaging? paginate)
        => version == CoordinateMediationVersion.V2
            ? (CoordinateMediation20.CreateKeylistQuery(from, mediatorDid, paginate, UuidV4MessageIdGenerator.Instance), QuerySpec2)
            : (CoordinateMediation.CreateRecipientQuery(from, mediatorDid, paginate, UuidV4MessageIdGenerator.Instance), QuerySpec3);

    // ---- answers --------------------------------------------------------------------------

    /// <summary>
    /// The grant in <paramref name="answer"/> for <paramref name="from"/>, or <c>null</c> for a
    /// deny. <c>routing_did</c> is a string or an array of at most <see cref="MaxRoutingDids"/>
    /// (counted before any entry is read), each a bare DID of at most 2048 characters.
    /// </summary>
    internal static MediationGrant? ReadMediateAnswer(CoordinateMediationVersion version, Message answer, string requestId, string from)
    {
        var spec = version == CoordinateMediationVersion.V2 ? MediateSpec2 : MediateSpec3;
        IReadOnlyList<string>? routingDids;
        try
        {
            if (version == CoordinateMediationVersion.V2)
            {
                if (CoordinateMediation20.TryParseDeny(answer))
                    return null;
                if (!CoordinateMediation20.TryParseGrant(answer, out routingDids, MaxRoutingDids))
                    throw UnexpectedType(requestId, spec);
            }
            else
            {
                if (WireJson.IsType(answer, Deny3))
                    return null;
                if (!WireJson.IsType(answer, Grant3))
                    throw UnexpectedType(requestId, spec);
                routingDids = MediationBodies.ReadRoutingDids(MediationBodies.ToElement(answer.Body), "mediate-grant", MaxRoutingDids);
            }
        }
        catch (MalformedMessageException ex)
        {
            throw Malformed(requestId, spec, ex);
        }

        var copy = new string[routingDids.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            // Each entry is within the DID bound already (the reader checked its length first).
            if (!BareDid.IsBareDid(routingDids[i]))
                throw Mismatch(requestId, spec, "a routing DID is not a bare DID (no path, query or fragment)");
            copy[i] = routingDids[i];
        }
        return new MediationGrant(from, Array.AsReadOnly(copy));
    }

    /// <summary>
    /// The results in <paramref name="answer"/> for <paramref name="sent"/>: exactly one per update,
    /// in order, each naming the same DID (ordinal) and action as the update at its index. The list
    /// is refused from its length before any entry is read when it is longer than
    /// <paramref name="sent"/>.
    /// </summary>
    internal static IReadOnlyList<RecipientUpdateResult> ReadUpdateAnswer(CoordinateMediationVersion version, Message answer, string requestId, IReadOnlyList<RecipientUpdate> sent)
    {
        var spec = version == CoordinateMediationVersion.V2 ? UpdateSpec2 : UpdateSpec3;
        IReadOnlyList<RecipientUpdateResult> updated;
        try
        {
            if (version == CoordinateMediationVersion.V2)
            {
                if (!CoordinateMediation20.TryParseKeylistUpdateResponse(answer, out var parsed, sent.Count))
                    throw UnexpectedType(requestId, spec);
                updated = parsed;
            }
            else
            {
                if (!WireJson.IsType(answer, UpdateResponse3))
                    throw UnexpectedType(requestId, spec);
                updated = MediationBodies.ReadUpdated(MediationBodies.ToElement(answer.Body), "recipient-update-response", sent.Count);
            }
        }
        catch (MalformedMessageException ex)
        {
            throw Malformed(requestId, spec, ex);
        }

        if (updated.Count != sent.Count)
            throw Mismatch(requestId, spec, "it does not answer every update");

        // Every position is compared, without stopping at the first difference.
        var same = true;
        for (var i = 0; i < sent.Count; i++)
        {
            same &= string.Equals(updated[i].RecipientDid, sent[i].RecipientDid, StringComparison.Ordinal)
                    & updated[i].Action == sent[i].Action;
        }
        if (!same)
            throw Mismatch(requestId, spec, "an entry does not name the DID and action of the update at its position");
        return updated;
    }

    /// <summary>
    /// The page in <paramref name="answer"/> for <paramref name="asked"/>: at most
    /// <see cref="MaxPageLimit"/> DIDs whatever <c>asked.Limit</c> was (counted from the list's length
    /// before any entry is read, L-055), and, when it reports a position, <c>count</c> equal to the
    /// page's size, <c>offset</c> equal to the one asked for (0 without a <c>paginate</c>) and
    /// <c>remaining</c> not negative. A page without a position is taken as it came.
    /// </summary>
    internal static RecipientQueryResult ReadQueryAnswer(CoordinateMediationVersion version, Message answer, string requestId, RecipientQueryPaging? asked)
    {
        var spec = version == CoordinateMediationVersion.V2 ? QuerySpec2 : QuerySpec3;
        IReadOnlyList<string> dids;
        RecipientPagination? pagination;
        try
        {
            // The bound is MaxPageLimit, not asked.Limit: a mediator may page larger than asked, and
            // the walker advances by what it got. It still comes before any entry is read.
            if (version == CoordinateMediationVersion.V2)
            {
                if (!CoordinateMediation20.TryParseKeylist(answer, out var keys, out pagination, MaxPageLimit))
                    throw UnexpectedType(requestId, spec);
                dids = keys;
            }
            else
            {
                if (!WireJson.IsType(answer, Recipient3))
                    throw UnexpectedType(requestId, spec);
                (dids, pagination) = MediationBodies.ReadRecipients(MediationBodies.ToElement(answer.Body), "dids", "recipient", MaxPageLimit);
            }
        }
        catch (MalformedMessageException ex)
        {
            throw Malformed(requestId, spec, ex);
        }

        if (pagination is not null)
        {
            var consistent = (pagination.Count == dids.Count)
                             & (pagination.Offset == (asked?.Offset ?? 0))
                             & (pagination.Remaining >= 0);
            if (!consistent)
                throw Mismatch(requestId, spec, "its pagination does not agree with the page or with the position asked for");
        }
        return new RecipientQueryResult(Array.AsReadOnly(dids.ToArray()), pagination);
    }

    /// <summary>
    /// The refusal of a recipient walk that cannot list everything, after the page of our request
    /// <paramref name="requestId"/>: the caller must not take the DIDs already yielded for the whole
    /// list. <paramref name="rule"/> is a constant text (counts at most), never a wire value.
    /// </summary>
    internal static MediationProtocolException IncompleteWalk(CoordinateMediationVersion version, string requestId, string rule)
    {
        var spec = version == CoordinateMediationVersion.V2 ? QuerySpec2 : QuerySpec3;
        return new($"The recipient listing cannot be completed after request '{requestId}' ({spec.Name}): {rule}; the DIDs listed so far are not the whole list.", requestId);
    }

    /// <summary>The answer as a message, deserialized here, on the caller's continuation.</summary>
    internal static Message Deserialize(InboundMessageSnapshot answer, string requestId, MediatorExchangeSpec spec)
    {
        try
        {
            return answer.DeserializeMessage();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // The serializer's text can quote the input; it is not attached.
            throw Malformed(requestId, spec, new MalformedMessageException("The answer is not a readable DIDComm message."));
        }
    }

    private static MediatorExchangeSpec Spec(string name, params MessageTypeUri[] expected)
        => new(name, Array.AsReadOnly(expected), ExpectsDelivery: false);

    private static MediationProtocolException UnexpectedType(string requestId, MediatorExchangeSpec spec)
        => new($"The mediator answered request '{requestId}' ({spec.Name}) with a message of another type.", requestId);

    private static MediationProtocolException Malformed(string requestId, MediatorExchangeSpec spec, MalformedMessageException inner)
        => new($"The mediator's answer to request '{requestId}' ({spec.Name}) is malformed; see the inner exception.", requestId, innerException: inner);

    private static MediationProtocolException Mismatch(string requestId, MediatorExchangeSpec spec, string rule)
        => new($"The mediator's answer to request '{requestId}' ({spec.Name}) does not match the request: {rule}.", requestId);
}
