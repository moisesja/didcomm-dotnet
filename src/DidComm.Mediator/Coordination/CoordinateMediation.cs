using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// Coordinate Mediation 3.0 (<c>https://didcomm.org/coordinate-mediation/3.0</c>, FR-MED-01/02):
/// the protocol a recipient uses to enrol with a mediator and tell it which DIDs to accept
/// forwards for. This class holds the message type URIs, builders for the seven messages and
/// strict parsers for them.
/// </summary>
/// <remarks>
/// <para>
/// Three exchanges, all request/response: <c>mediate-request</c> → <c>mediate-grant</c> or
/// <c>mediate-deny</c>; <c>recipient-update</c> → <c>recipient-update-response</c>;
/// <c>recipient-query</c> → <c>recipient</c>. The mediator role is served by
/// <see cref="CoordinateMediationHandler"/>.
/// </para>
/// <para>
/// <strong>Parsers.</strong> A <c>TryParse…</c> method returns <c>false</c> when the message is not
/// of that type, and throws <see cref="MalformedMessageException"/> when it is of that type but its
/// body has the wrong shape (<c>mediate-request</c> and <c>mediate-deny</c> define no body members,
/// so their parsers only answer the type question). The exception text names the member and the rule and never quotes a
/// value from the message. A DID member longer than 2048 characters is refused before anything
/// parses it (FR-API-09). Members the protocol does not define are ignored. Message types are
/// matched as the handler registry matches them (FR-PROTO-01/02): ignoring case and <c>-</c>/<c>_</c>,
/// and accepting any 3.x minor version.
/// </para>
/// <para>
/// <strong>Builders.</strong> A <c>Create…</c> method checks its arguments (a DID longer than 2048
/// characters is an <see cref="ArgumentException"/>, never quoted) and returns a plaintext
/// <see cref="Message"/> to pack. Responses take the <c>thid</c> to answer on: the request's
/// <c>thid</c> if it has one, else its <c>id</c> (<see cref="ResponseThreadId"/>, FR-MED-04).
/// </para>
/// </remarks>
public static class CoordinateMediation
{
    /// <summary>The Protocol Identifier URI: <c>https://didcomm.org/coordinate-mediation/3.0</c>.</summary>
    public const string ProtocolUri = "https://didcomm.org/coordinate-mediation/3.0";

    /// <summary>Message type of <c>mediate-request</c>: a recipient asks for mediation.</summary>
    public const string MediateRequestType = ProtocolUri + "/mediate-request";

    /// <summary>Message type of <c>mediate-grant</c>: the mediator agrees and names its routing DIDs.</summary>
    public const string MediateGrantType = ProtocolUri + "/mediate-grant";

    /// <summary>Message type of <c>mediate-deny</c>: the mediator declines.</summary>
    public const string MediateDenyType = ProtocolUri + "/mediate-deny";

    /// <summary>Message type of <c>recipient-update</c>: add or remove DIDs the mediator accepts forwards for.</summary>
    public const string RecipientUpdateType = ProtocolUri + "/recipient-update";

    /// <summary>Message type of <c>recipient-update-response</c>: the per-entry results of an update.</summary>
    public const string RecipientUpdateResponseType = ProtocolUri + "/recipient-update-response";

    /// <summary>Message type of <c>recipient-query</c>: list the DIDs registered for this recipient.</summary>
    public const string RecipientQueryType = ProtocolUri + "/recipient-query";

    /// <summary>Message type of <c>recipient</c>: one page of registered DIDs.</summary>
    public const string RecipientType = ProtocolUri + "/recipient";

    /// <summary>The role name of the agent that receives forwards on behalf of others: <c>mediator</c>.</summary>
    public const string MediatorRole = "mediator";

    /// <summary>The role name of the agent the forwards are for: <c>recipient</c>.</summary>
    public const string RecipientRole = "recipient";

    private const string DidsList = "dids";

    private static readonly MessageTypeUri MediateRequestUri = MessageTypeUri.Parse(MediateRequestType);
    private static readonly MessageTypeUri MediateGrantUri = MessageTypeUri.Parse(MediateGrantType);
    private static readonly MessageTypeUri MediateDenyUri = MessageTypeUri.Parse(MediateDenyType);
    private static readonly MessageTypeUri RecipientUpdateUri = MessageTypeUri.Parse(RecipientUpdateType);
    private static readonly MessageTypeUri RecipientUpdateResponseUri = MessageTypeUri.Parse(RecipientUpdateResponseType);
    private static readonly MessageTypeUri RecipientQueryUri = MessageTypeUri.Parse(RecipientQueryType);
    private static readonly MessageTypeUri RecipientUri = MessageTypeUri.Parse(RecipientType);

    // ---- builders -------------------------------------------------------------------------

    /// <summary>Build a <c>mediate-request</c> (<c>body: {}</c>). Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID.</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateMediateRequest(string from, string mediator, IMessageIdGenerator? idGenerator = null)
        => Start(MediateRequestType, from, mediator, idGenerator)
            .WithBody(new JsonObject())
            .Build();

    /// <summary>Build a <c>mediate-grant</c> carrying <c>routing_did</c> as an array.</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="routingDids">The mediator DIDs forwards should be sent to; at least one.</param>
    /// <param name="thid">The thread to answer on (<see cref="ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateGrant(string from, string to, IReadOnlyList<string> routingDids, string thid, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(routingDids);
        if (routingDids.Count == 0)
            throw new ArgumentException("A mediate-grant names at least one routing DID.", nameof(routingDids));
        foreach (var did in routingDids)
            RequireDid(did, nameof(routingDids));

        return Start(MediateGrantType, from, to, idGenerator, thid)
            .WithBody(MediationBodies.WriteRoutingDids(routingDids))
            .Build();
    }

    /// <summary>Build a <c>mediate-deny</c> (<c>body: {}</c>).</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="thid">The thread to answer on (<see cref="ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateDeny(string from, string to, string thid, IMessageIdGenerator? idGenerator = null)
        => Start(MediateDenyType, from, to, idGenerator, thid)
            .WithBody(new JsonObject())
            .Build();

    /// <summary>Build a <c>recipient-update</c>. Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID (the one that holds the grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="updates">The DIDs to add or remove; the mediator answers in this order.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateRecipientUpdate(string from, string mediator, IReadOnlyList<RecipientUpdate> updates, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        foreach (var update in updates)
        {
            ArgumentNullException.ThrowIfNull(update, nameof(updates));
            RequireDid(update.RecipientDid, nameof(updates));
            RequireDefined(update.Action, nameof(updates));
        }

        return Start(RecipientUpdateType, from, mediator, idGenerator)
            .WithBody(MediationBodies.WriteUpdates(updates))
            .Build();
    }

    /// <summary>Build a <c>recipient-update-response</c>.</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="updated">One result per update, in the order the request listed them.</param>
    /// <param name="thid">The thread to answer on (<see cref="ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateRecipientUpdateResponse(string from, string to, IReadOnlyList<RecipientUpdateResult> updated, string thid, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(updated);
        foreach (var entry in updated)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(updated));
            RequireDid(entry.RecipientDid, nameof(updated));
            RequireDefined(entry.Action, nameof(updated));
            RequireDefined(entry.Result, nameof(updated));
        }

        return Start(RecipientUpdateResponseType, from, to, idGenerator, thid)
            .WithBody(MediationBodies.WriteUpdated(updated))
            .Build();
    }

    /// <summary>Build a <c>recipient-query</c>. Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID (the one that holds the grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="paginate">The page wanted, or <c>null</c> for the mediator's first page.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateRecipientQuery(string from, string mediator, RecipientQueryPaging? paginate = null, IMessageIdGenerator? idGenerator = null)
    {
        if (paginate is { Offset: < 0 })
            throw new ArgumentOutOfRangeException(nameof(paginate), "The offset must not be negative.");

        return Start(RecipientQueryType, from, mediator, idGenerator)
            .WithBody(MediationBodies.WritePaginate(paginate))
            .Build();
    }

    /// <summary>Build a <c>recipient</c> message: one page of registered DIDs.</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="dids">The registered DIDs in this page.</param>
    /// <param name="pagination">Where this page sits in the whole list, or <c>null</c> to omit <c>pagination</c>.</param>
    /// <param name="thid">The thread to answer on (<see cref="ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateRecipient(string from, string to, IReadOnlyList<string> dids, RecipientPagination? pagination, string thid, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(dids);
        foreach (var did in dids)
            RequireDid(did, nameof(dids));

        return Start(RecipientType, from, to, idGenerator, thid)
            .WithBody(MediationBodies.WriteRecipients(DidsList, dids, pagination))
            .Build();
    }

    /// <summary>
    /// The <c>thid</c> a response to <paramref name="request"/> carries (FR-MED-04, DD-11): the
    /// request's <c>thid</c> when it has one, else its <c>id</c>.
    /// </summary>
    /// <param name="request">The request being answered.</param>
    public static string ResponseThreadId(Message request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrEmpty(request.Thid) ? request.Id : request.Thid;
    }

    // ---- parsers --------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="message"/> is a <c>mediate-request</c>. The message defines no body
    /// members, so there is nothing to refuse: an absent <c>body</c> (as in the protocol page's
    /// example) and a <c>body</c> with members this version does not know are both accepted.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    public static bool TryParseMediateRequest(Message message) => IsType(message, MediateRequestUri);

    /// <summary>
    /// Read a <c>mediate-grant</c>. <c>routing_did</c> may be an array (3.0) or a single string
    /// (the 2.0 shape); either way <paramref name="routingDids"/> is a non-empty list.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="routingDids">The mediator's routing DIDs. Each is at most 2048 characters; none has been parsed or resolved.</param>
    /// <exception cref="MalformedMessageException">It is a <c>mediate-grant</c> with a missing, empty, mistyped or over-long <c>routing_did</c>.</exception>
    public static bool TryParseGrant(Message message, [NotNullWhen(true)] out IReadOnlyList<string>? routingDids)
    {
        routingDids = null;
        if (!IsType(message, MediateGrantUri))
            return false;
        routingDids = MediationBodies.ReadRoutingDids(MediationBodies.ToElement(message.Body), "mediate-grant");
        return true;
    }

    /// <summary>
    /// Whether <paramref name="message"/> is a <c>mediate-deny</c>. The message defines no body
    /// members: an absent <c>body</c> and one with unknown members are both accepted.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    public static bool TryParseDeny(Message message) => IsType(message, MediateDenyUri);

    /// <summary>Read a <c>recipient-update</c>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="updates">The entries in wire order. Each <c>recipient_did</c> is a non-empty string of at most 2048 characters; whether it is a DID has not been checked.</param>
    /// <exception cref="MalformedMessageException">It is a <c>recipient-update</c> whose <c>updates</c> is missing or not an array, or an entry lacks a member, has an <c>action</c> other than <c>add</c>/<c>remove</c>, or a <c>recipient_did</c> that is not a string, is empty or is over the bound.</exception>
    public static bool TryParseRecipientUpdate(Message message, [NotNullWhen(true)] out IReadOnlyList<RecipientUpdate>? updates)
        => TryParseRecipientUpdate(message, int.MaxValue, out updates);

    /// <summary>
    /// <see cref="TryParseRecipientUpdate(Message, out IReadOnlyList{RecipientUpdate}?)"/> with a
    /// bound on the number of entries, as the mediator applies it (FR-MED-08). A longer list is
    /// malformed, and it is refused from its length alone: before the body is re-read and before
    /// any entry is decoded or allocated, so what sits beyond the bound is never looked at.
    /// </summary>
    internal static bool TryParseRecipientUpdate(Message message, int maxUpdates, [NotNullWhen(true)] out IReadOnlyList<RecipientUpdate>? updates)
    {
        updates = null;
        if (!IsType(message, RecipientUpdateUri))
            return false;
        MediationBodies.RequireUpdatesWithin(message.Body, maxUpdates, "recipient-update");
        updates = MediationBodies.ReadUpdates(MediationBodies.ToElement(message.Body), "recipient-update", maxUpdates);
        return true;
    }

    /// <summary>Read a <c>recipient-update-response</c>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="updated">The results in wire order.</param>
    /// <exception cref="MalformedMessageException">It is a <c>recipient-update-response</c> whose <c>updated</c> is missing or not an array, or an entry lacks a member or carries an unknown <c>action</c> or <c>result</c>, or an over-long <c>recipient_did</c>.</exception>
    public static bool TryParseRecipientUpdateResponse(Message message, [NotNullWhen(true)] out IReadOnlyList<RecipientUpdateResult>? updated)
    {
        updated = null;
        if (!IsType(message, RecipientUpdateResponseUri))
            return false;
        updated = MediationBodies.ReadUpdated(MediationBodies.ToElement(message.Body), "recipient-update-response");
        return true;
    }

    /// <summary>Read a <c>recipient-query</c>. An absent <c>body</c> or an absent <c>paginate</c> gives a <c>null</c> <paramref name="paginate"/>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="paginate">The requested page, or <c>null</c> when the query names none. Values beyond the 32-bit range are clamped.</param>
    /// <exception cref="MalformedMessageException">It is a <c>recipient-query</c> whose <c>paginate</c> is not an object, lacks <c>limit</c> or <c>offset</c>, has a non-integer value, or a negative <c>offset</c>.</exception>
    public static bool TryParseRecipientQuery(Message message, out RecipientQueryPaging? paginate)
    {
        paginate = null;
        if (!IsType(message, RecipientQueryUri))
            return false;
        paginate = MediationBodies.ReadPaginate(MediationBodies.ToElement(message.Body), "recipient-query");
        return true;
    }

    /// <summary>Read a <c>recipient</c> message.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="dids">The registered DIDs in this page, in wire order.</param>
    /// <param name="pagination">The page position, or <c>null</c> when the message carries no <c>pagination</c>.</param>
    /// <exception cref="MalformedMessageException">It is a <c>recipient</c> message whose <c>dids</c> is missing or not an array of <c>{"recipient_did"}</c> objects, or whose <c>pagination</c> lacks <c>count</c>, <c>offset</c> or <c>remaining</c> or has a non-integer value.</exception>
    public static bool TryParseRecipient(Message message, [NotNullWhen(true)] out IReadOnlyList<string>? dids, out RecipientPagination? pagination)
    {
        dids = null;
        pagination = null;
        if (!IsType(message, RecipientUri))
            return false;
        (dids, pagination) = MediationBodies.ReadRecipients(MediationBodies.ToElement(message.Body), DidsList, "recipient");
        return true;
    }

    // ---- shared pieces --------------------------------------------------------------------

    internal static bool IsType(Message message, MessageTypeUri expected) => WireJson.IsType(message, expected);

    private static MessageBuilder Start(string type, string from, string to, IMessageIdGenerator? idGenerator)
    {
        RequireDid(from, nameof(from));
        RequireDid(to, nameof(to));
        return new MessageBuilder(idGenerator ?? UuidV4MessageIdGenerator.Instance)
            .WithType(type)
            .WithFrom(from)
            .WithTo(to);
    }

    // A response: the same, plus the REQUIRED thread id (FR-MED-04).
    private static MessageBuilder Start(string type, string from, string to, IMessageIdGenerator? idGenerator, string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        return Start(type, from, to, idGenerator).WithThid(thid);
    }

    private static void RequireDid(string? value, string parameter)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("A DID is required.", parameter);
        if (!MediationBodies.IsWithinDidLimit(value))
            throw new ArgumentException("A DID exceeds the maximum length of 2048 characters (FR-API-09).", parameter);
    }

    private static void RequireDefined<TEnum>(TEnum value, string parameter) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameter, "The value is not one the protocol defines.");
    }
}
