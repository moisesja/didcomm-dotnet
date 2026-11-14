using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// Coordinate Mediation 2.0 (<c>https://didcomm.org/coordinate-mediation/2.0</c>, FR-MED-10), the
/// recipient's direction only: the message type URIs, builders for the three requests a recipient
/// sends (<c>mediate-request</c>, <c>keylist-update</c>, <c>keylist-query</c>) and strict parsers for
/// the four answers it reads (<c>mediate-grant</c>, <c>mediate-deny</c>, <c>keylist-update-response</c>,
/// <c>keylist</c>). <see cref="Client.MediationClient"/> speaks 2.0 through it when a mediator
/// discloses no 3.0.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Same bodies as 3.0.</strong> The two versions share every entry shape
/// (<c>recipient_did</c>, <c>action</c>, <c>result</c>, <c>paginate</c>, <c>pagination</c>), so
/// this class writes through <see cref="MediationBodies"/>' writers and reads through its readers.
/// 2.0 differs in the message names (<c>keylist-*</c> for <c>recipient-*</c>), in the list a
/// query returns (<c>keys</c> for <c>dids</c>) and in <c>routing_did</c>, a single string where
/// 3.0 has an array. The grant parser accepts both shapes, as 3.0's does.
/// </para>
/// <para>
/// <strong>Parsers</strong> follow <see cref="CoordinateMediation"/>'s rules: <c>false</c> for
/// another type; <see cref="MalformedMessageException"/> naming the member and the rule, never a
/// value, for a body of the wrong shape; DID members bounded (FR-API-09) before they are looked at;
/// unknown members ignored; types matched ignoring case and <c>-</c>/<c>_</c>, any 2.x minor. Each
/// list parser takes a bound that is applied from the list's length before any entry is read
/// (L-055).
/// </para>
/// <para>
/// <strong>For the 2.0 mediator role (#99).</strong> Reusable as they are: the type constants,
/// <see cref="MediationBodies"/>' readers and writers, and the grant/deny parsers (to test what the
/// handler answers). To add: <c>CreateGrant(from, to, string routingDid, thid)</c> (a string
/// <c>routing_did</c>), <c>CreateDeny</c>, <c>CreateKeylistUpdateResponse</c>
/// (<see cref="MediationBodies.WriteUpdated"/>), <c>CreateKeylist</c>
/// (<see cref="MediationBodies.WriteRecipients"/> with <c>keys</c>), and the request parsers
/// (<c>TryParseMediateRequest</c>, <c>TryParseKeylistUpdate</c> with the
/// <see cref="MediationBodies.RequireUpdatesWithin"/> bound, <c>TryParseKeylistQuery</c>), plus a
/// decision whether the class becomes public.
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

    internal static readonly MessageTypeUri MediateGrantUri = MessageTypeUri.Parse(MediateGrantType);
    internal static readonly MessageTypeUri MediateDenyUri = MessageTypeUri.Parse(MediateDenyType);
    internal static readonly MessageTypeUri KeylistUpdateResponseUri = MessageTypeUri.Parse(KeylistUpdateResponseType);
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
            if (!Enum.IsDefined(update.Action))
                throw new ArgumentOutOfRangeException(nameof(updates), "The value is not one the protocol defines.");
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

    private static void RequireDid(string? value, string parameter)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("A DID is required.", parameter);
        if (!MediationBodies.IsWithinDidLimit(value))
            throw new ArgumentException("A DID exceeds the maximum length of 2048 characters (FR-API-09).", parameter);
    }
}
