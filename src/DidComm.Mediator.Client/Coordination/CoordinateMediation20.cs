using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// Coordinate Mediation 2.0 (<c>https://didcomm.org/coordinate-mediation/2.0</c>, FR-MED-10) in
/// both directions: the message type URIs; builders for the three requests a recipient sends
/// (<c>mediate-request</c>, <c>keylist-update</c>, <c>keylist-query</c>) and strict parsers for
/// them; builders for the four answers a mediator sends (<c>mediate-grant</c>, <c>mediate-deny</c>,
/// <c>keylist-update-response</c>, <c>keylist</c>) and strict parsers for them.
/// <see cref="Client.MediationClient"/> speaks 2.0 through it when a mediator discloses no 3.0 or
/// the host pins 2.0; <c>CoordinateMediation20Handler</c> (in the <c>DidComm.Mediator</c> package)
/// answers 2.0 recipients through it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Same bodies as 3.0.</strong> The two versions share every entry shape
/// (<c>recipient_did</c>, <c>action</c>, <c>result</c>, <c>paginate</c>, <c>pagination</c>), so
/// this class writes through <see cref="MediationBodies"/>' writers and reads through its readers.
/// 2.0 differs in the message names (<c>keylist-*</c> for <c>recipient-*</c>), in the list a
/// query returns (<c>keys</c> for <c>dids</c>) and in <c>routing_did</c>, a single string where
/// 3.0 has an array. The grant parser accepts both shapes, as 3.0's does; the grant builder writes
/// the 2.0 shape only, a string, because both 2.0 mediators in the field and their wallets read it
/// as one.
/// </para>
/// <para>
/// <strong>Parsers</strong> follow <see cref="CoordinateMediation"/>'s rules: <c>false</c> for
/// another type; <see cref="MalformedMessageException"/> naming the member and the rule, never a
/// value, for a body of the wrong shape; DID members bounded (FR-API-09) before they are looked at;
/// unknown members ignored; types matched ignoring case and <c>-</c>/<c>_</c>, any 2.x minor. Each
/// list parser takes a bound that is applied from the list's length before any entry is read
/// (L-055). <c>mediate-request</c> and <c>mediate-deny</c> define no body members, so their parsers
/// only answer the type question.
/// </para>
/// <para>
/// <strong>Builders</strong> check their arguments as <see cref="CoordinateMediation"/>'s do (a DID
/// longer than 2048 characters is an <see cref="ArgumentException"/>, never quoted) and always
/// write the canonical type string: a 2.0 mediator in the field matches types by exact string.
/// Answers take the <c>thid</c> to answer on (<see cref="CoordinateMediation.ResponseThreadId"/>,
/// the same rule in both versions).
/// </para>
/// <para>
/// The class is internal: <see cref="Client.MediationClient"/> and the 2.0 mediator role are its
/// only users, so 2.0 adds no public message API of its own.
/// </para>
/// </remarks>
internal static class CoordinateMediation20
{
    /// <summary>The Protocol Identifier URI: <c>https://didcomm.org/coordinate-mediation/2.0</c>.</summary>
    internal const string ProtocolUri = "https://didcomm.org/coordinate-mediation/2.0";

    /// <summary>Message type of <c>mediate-request</c>: a recipient asks for mediation.</summary>
    internal const string MediateRequestType = ProtocolUri + "/mediate-request";

    /// <summary>Message type of <c>mediate-grant</c>: the mediator agrees and names its routing DID.</summary>
    internal const string MediateGrantType = ProtocolUri + "/mediate-grant";

    /// <summary>Message type of <c>mediate-deny</c>: the mediator declines.</summary>
    internal const string MediateDenyType = ProtocolUri + "/mediate-deny";

    /// <summary>Message type of <c>keylist-update</c>: add or remove DIDs the mediator accepts forwards for.</summary>
    internal const string KeylistUpdateType = ProtocolUri + "/keylist-update";

    /// <summary>Message type of <c>keylist-update-response</c>: the per-entry results of an update.</summary>
    internal const string KeylistUpdateResponseType = ProtocolUri + "/keylist-update-response";

    /// <summary>Message type of <c>keylist-query</c>: list the DIDs registered for this recipient.</summary>
    internal const string KeylistQueryType = ProtocolUri + "/keylist-query";

    /// <summary>Message type of <c>keylist</c>: one page of registered DIDs.</summary>
    internal const string KeylistType = ProtocolUri + "/keylist";

    /// <summary>The name of the list a <c>keylist</c> carries.</summary>
    internal const string KeysList = "keys";

    internal static readonly MessageTypeUri MediateRequestUri = MessageTypeUri.Parse(MediateRequestType);
    internal static readonly MessageTypeUri MediateGrantUri = MessageTypeUri.Parse(MediateGrantType);
    internal static readonly MessageTypeUri MediateDenyUri = MessageTypeUri.Parse(MediateDenyType);
    internal static readonly MessageTypeUri KeylistUpdateUri = MessageTypeUri.Parse(KeylistUpdateType);
    internal static readonly MessageTypeUri KeylistUpdateResponseUri = MessageTypeUri.Parse(KeylistUpdateResponseType);
    internal static readonly MessageTypeUri KeylistQueryUri = MessageTypeUri.Parse(KeylistQueryType);
    internal static readonly MessageTypeUri KeylistUri = MessageTypeUri.Parse(KeylistType);

    // ---- builders (the recipient's requests) ----------------------------------------------

    /// <summary>Build a 2.0 <c>mediate-request</c> (<c>body: {}</c>). Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID.</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateMediateRequest(string from, string mediator, IMessageIdGenerator? idGenerator = null)
        => Start(MediateRequestType, from, mediator, idGenerator)
            .WithBody(new JsonObject())
            .Build();

    /// <summary>Build a <c>keylist-update</c>. Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID (the one that holds the grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="updates">The DIDs to add or remove; the mediator answers in this order.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateKeylistUpdate(string from, string mediator, IReadOnlyList<RecipientUpdate> updates, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        foreach (var update in updates)
        {
            ArgumentNullException.ThrowIfNull(update, nameof(updates));
            RequireDid(update.RecipientDid, nameof(updates));
            RequireDefined(update.Action, nameof(updates));
        }

        return Start(KeylistUpdateType, from, mediator, idGenerator)
            .WithBody(MediationBodies.WriteUpdates(updates))
            .Build();
    }

    /// <summary>Build a <c>keylist-query</c>. Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID (the one that holds the grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="paginate">The page wanted, or <c>null</c> for the mediator's first page.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateKeylistQuery(string from, string mediator, RecipientQueryPaging? paginate = null, IMessageIdGenerator? idGenerator = null)
    {
        if (paginate is { Offset: < 0 })
            throw new ArgumentOutOfRangeException(nameof(paginate), "The offset must not be negative.");

        return Start(KeylistQueryType, from, mediator, idGenerator)
            .WithBody(MediationBodies.WritePaginate(paginate))
            .Build();
    }

    // ---- builders (the mediator's answers) ------------------------------------------------

    /// <summary>
    /// Build a 2.0 <c>mediate-grant</c> carrying <c>routing_did</c> as a single string, the 2.0
    /// shape.
    /// </summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="routingDid">The mediator DID forwards should be sent to.</param>
    /// <param name="thid">The thread to answer on (<see cref="CoordinateMediation.ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateGrant(string from, string to, string routingDid, string thid, IMessageIdGenerator? idGenerator = null)
    {
        RequireDid(routingDid, nameof(routingDid));
        return Start(MediateGrantType, from, to, idGenerator, thid)
            .WithBody(MediationBodies.WriteRoutingDid(routingDid))
            .Build();
    }

    /// <summary>Build a 2.0 <c>mediate-deny</c> (<c>body: {}</c>).</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="thid">The thread to answer on (<see cref="CoordinateMediation.ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateDeny(string from, string to, string thid, IMessageIdGenerator? idGenerator = null)
        => Start(MediateDenyType, from, to, idGenerator, thid)
            .WithBody(new JsonObject())
            .Build();

    /// <summary>Build a <c>keylist-update-response</c>.</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="updated">One result per update, in the order the request listed them.</param>
    /// <param name="thid">The thread to answer on (<see cref="CoordinateMediation.ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateKeylistUpdateResponse(string from, string to, IReadOnlyList<RecipientUpdateResult> updated, string thid, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(updated);
        foreach (var entry in updated)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(updated));
            RequireDid(entry.RecipientDid, nameof(updated));
            RequireDefined(entry.Action, nameof(updated));
            RequireDefined(entry.Result, nameof(updated));
        }

        return Start(KeylistUpdateResponseType, from, to, idGenerator, thid)
            .WithBody(MediationBodies.WriteUpdated(updated))
            .Build();
    }

    /// <summary>Build a <c>keylist</c> message: one page of registered DIDs, listed as <c>keys</c>.</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="keys">The registered DIDs in this page.</param>
    /// <param name="pagination">Where this page sits in the whole list, or <c>null</c> to omit <c>pagination</c>.</param>
    /// <param name="thid">The thread to answer on (<see cref="CoordinateMediation.ResponseThreadId"/>).</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateKeylist(string from, string to, IReadOnlyList<string> keys, RecipientPagination? pagination, string thid, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var did in keys)
            RequireDid(did, nameof(keys));

        return Start(KeylistType, from, to, idGenerator, thid)
            .WithBody(MediationBodies.WriteRecipients(KeysList, keys, pagination))
            .Build();
    }

    // ---- parsers (the recipient's requests) -----------------------------------------------

    /// <summary>
    /// Whether <paramref name="message"/> is a 2.0 <c>mediate-request</c>. The message defines no
    /// body members, so there is nothing to refuse: an absent <c>body</c> (as in the protocol page's
    /// example) and a <c>body</c> with members this version does not know are both accepted.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    internal static bool TryParseMediateRequest(Message message) => WireJson.IsType(message, MediateRequestUri);

    /// <summary>
    /// Read a <c>keylist-update</c>, with a bound on the number of entries as the mediator applies it
    /// (FR-MED-08). A longer list is malformed, and it is refused from its length alone: before the
    /// body is re-read and before any entry is decoded or allocated, so what sits beyond the bound is
    /// never looked at (L-055).
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="maxUpdates">The longest <c>updates</c> list accepted.</param>
    /// <param name="updates">The entries in wire order. Each <c>recipient_did</c> is a non-empty string of at most 2048 characters; whether it is a DID has not been checked.</param>
    /// <exception cref="MalformedMessageException">It is a <c>keylist-update</c> whose <c>updates</c> is missing, not an array or over <paramref name="maxUpdates"/>, or an entry lacks a member, has an <c>action</c> other than <c>add</c>/<c>remove</c>, or a <c>recipient_did</c> that is not a string, is empty or is over the bound.</exception>
    internal static bool TryParseKeylistUpdate(Message message, int maxUpdates, [NotNullWhen(true)] out IReadOnlyList<RecipientUpdate>? updates)
    {
        updates = null;
        if (!WireJson.IsType(message, KeylistUpdateUri))
            return false;
        MediationBodies.RequireUpdatesWithin(message.Body, maxUpdates, "keylist-update");
        updates = MediationBodies.ReadUpdates(MediationBodies.ToElement(message.Body), "keylist-update", maxUpdates);
        return true;
    }

    /// <summary>Read a <c>keylist-query</c>. An absent <c>body</c> or an absent <c>paginate</c> gives a <c>null</c> <paramref name="paginate"/>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="paginate">The requested page, or <c>null</c> when the query names none. Values beyond the 32-bit range are clamped.</param>
    /// <exception cref="MalformedMessageException">It is a <c>keylist-query</c> whose <c>paginate</c> is not an object, lacks <c>limit</c> or <c>offset</c>, has a non-integer value, or a negative <c>offset</c>.</exception>
    internal static bool TryParseKeylistQuery(Message message, out RecipientQueryPaging? paginate)
    {
        paginate = null;
        if (!WireJson.IsType(message, KeylistQueryUri))
            return false;
        paginate = MediationBodies.ReadPaginate(MediationBodies.ToElement(message.Body), "keylist-query");
        return true;
    }

    // ---- parsers (the mediator's answers) -------------------------------------------------

    /// <summary>
    /// Read a 2.0 <c>mediate-grant</c>. <c>routing_did</c> is a string in 2.0; an array (the 3.0
    /// shape) is accepted too. Either way <paramref name="routingDids"/> is a non-empty list.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="routingDids">The mediator's routing DIDs, each at most 2048 characters; none parsed or resolved.</param>
    /// <param name="maxRoutingDids">The longest array accepted, refused from its length before any entry is read.</param>
    /// <exception cref="MalformedMessageException">It is a <c>mediate-grant</c> with a missing, empty, mistyped or over-long <c>routing_did</c>, or an array over <paramref name="maxRoutingDids"/>.</exception>
    internal static bool TryParseGrant(Message message, [NotNullWhen(true)] out IReadOnlyList<string>? routingDids, int maxRoutingDids = int.MaxValue)
    {
        routingDids = null;
        if (!WireJson.IsType(message, MediateGrantUri))
            return false;
        routingDids = MediationBodies.ReadRoutingDids(MediationBodies.ToElement(message.Body), "mediate-grant", maxRoutingDids);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="message"/> is a 2.0 <c>mediate-deny</c>. The message defines no body
    /// members: an absent <c>body</c> (as in the protocol page's example) and one with unknown members
    /// are both accepted.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    internal static bool TryParseDeny(Message message) => WireJson.IsType(message, MediateDenyUri);

    /// <summary>Read a <c>keylist-update-response</c>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="updated">The results in wire order.</param>
    /// <param name="maxEntries">The longest <c>updated</c> list accepted, refused from its length before any entry is read.</param>
    /// <exception cref="MalformedMessageException">It is a <c>keylist-update-response</c> whose <c>updated</c> is missing, not an array or over <paramref name="maxEntries"/>, or an entry lacks a member or carries an unknown <c>action</c> or <c>result</c>, or an over-long <c>recipient_did</c>.</exception>
    internal static bool TryParseKeylistUpdateResponse(Message message, [NotNullWhen(true)] out IReadOnlyList<RecipientUpdateResult>? updated, int maxEntries = int.MaxValue)
    {
        updated = null;
        if (!WireJson.IsType(message, KeylistUpdateResponseUri))
            return false;
        updated = MediationBodies.ReadUpdated(MediationBodies.ToElement(message.Body), "keylist-update-response", maxEntries);
        return true;
    }

    /// <summary>Read a <c>keylist</c> message.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="keys">The registered DIDs in this page, in wire order.</param>
    /// <param name="pagination">The page position, or <c>null</c> when the message carries no <c>pagination</c>.</param>
    /// <param name="maxEntries">The longest <c>keys</c> list accepted, refused from its length before any entry is read.</param>
    /// <exception cref="MalformedMessageException">It is a <c>keylist</c> whose <c>keys</c> is missing, over <paramref name="maxEntries"/> or not an array of <c>{"recipient_did"}</c> objects, or whose <c>pagination</c> lacks <c>count</c>, <c>offset</c> or <c>remaining</c> or has a non-integer value.</exception>
    internal static bool TryParseKeylist(Message message, [NotNullWhen(true)] out IReadOnlyList<string>? keys, out RecipientPagination? pagination, int maxEntries = int.MaxValue)
    {
        keys = null;
        pagination = null;
        if (!WireJson.IsType(message, KeylistUri))
            return false;
        (keys, pagination) = MediationBodies.ReadRecipients(MediationBodies.ToElement(message.Body), KeysList, "keylist", maxEntries);
        return true;
    }

    // ---- shared pieces --------------------------------------------------------------------

    private static MessageBuilder Start(string type, string from, string to, IMessageIdGenerator? idGenerator)
    {
        RequireDid(from, nameof(from));
        RequireDid(to, nameof(to));
        return new MessageBuilder(idGenerator ?? UuidV4MessageIdGenerator.Instance)
            .WithType(type)
            .WithFrom(from)
            .WithTo(to);
    }

    // An answer: the same, plus the REQUIRED thread id (FR-MED-04).
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
