using System.Diagnostics.CodeAnalysis;
using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// Message Pickup 3.0 (<c>https://didcomm.org/messagepickup/3.0</c>, FR-PICK-01..08): the protocol
/// a recipient uses to fetch the messages a mediator holds for it. This class holds the message
/// type URIs, builders for the six messages and strict parsers for them.
/// </summary>
/// <remarks>
/// <para>
/// The exchanges: <c>status-request</c> → <c>status</c>; <c>delivery-request</c> →
/// <c>delivery</c> (or <c>status</c> when nothing is waiting); <c>messages-received</c> →
/// <c>status</c>; <c>live-delivery-change</c> → <c>status</c>, or a problem report when the
/// connection cannot do live delivery. Every request is sent with <c>return_route: all</c>. The
/// mediator role is served by <see cref="MessagePickupHandler"/>.
/// </para>
/// <para>
/// <strong>Parsers.</strong> A <c>TryParse…</c> method returns <c>false</c> when the message is not
/// of that type, and throws <see cref="MalformedMessageException"/> when it is of that type but has
/// the wrong shape. The exception text names the member and the rule and never quotes a value from
/// the message. A <c>recipient_did</c>, a <c>message_id_list</c> entry or an attachment id longer
/// than 2048 characters is refused before anything else looks at it (FR-API-09). Members the
/// protocol does not define are ignored, and an explicit <c>null</c> for an optional member reads
/// as absent. Message types are matched as the handler registry matches them (FR-PROTO-01/02):
/// ignoring case and <c>-</c>/<c>_</c>, and accepting any 3.x minor version.
/// </para>
/// <para>
/// <strong>Builders.</strong> A <c>Create…</c> method checks its arguments (a DID or id longer than
/// 2048 characters is an <see cref="ArgumentException"/>, never quoted) and returns a plaintext
/// <see cref="Message"/> to pack.
/// </para>
/// </remarks>
public static class MessagePickup
{
    /// <summary>The Protocol Identifier URI: <c>https://didcomm.org/messagepickup/3.0</c>.</summary>
    public const string ProtocolUri = "https://didcomm.org/messagepickup/3.0";

    /// <summary>Message type of <c>status-request</c>: a recipient asks how many messages are waiting.</summary>
    public const string StatusRequestType = ProtocolUri + "/status-request";

    /// <summary>Message type of <c>status</c>: the state of the recipient's queue.</summary>
    public const string StatusType = ProtocolUri + "/status";

    /// <summary>Message type of <c>delivery-request</c>: a recipient asks for waiting messages.</summary>
    public const string DeliveryRequestType = ProtocolUri + "/delivery-request";

    /// <summary>Message type of <c>delivery</c>: a batch of waiting messages, one per attachment.</summary>
    public const string DeliveryType = ProtocolUri + "/delivery";

    /// <summary>Message type of <c>messages-received</c>: the recipient confirms what it received, so the mediator can remove it.</summary>
    public const string MessagesReceivedType = ProtocolUri + "/messages-received";

    /// <summary>Message type of <c>live-delivery-change</c>: turn live delivery on or off for this connection.</summary>
    public const string LiveDeliveryChangeType = ProtocolUri + "/live-delivery-change";

    /// <summary>The role name of the agent that holds messages for pickup: <c>mediator</c>.</summary>
    public const string MediatorRole = "mediator";

    /// <summary>The role name of the agent that picks messages up: <c>recipient</c>.</summary>
    public const string RecipientRole = "recipient";

    /// <summary>The problem code a mediator reports when live delivery is requested on a connection that cannot do it: <c>e.m.live-mode-not-supported</c>.</summary>
    public const string LiveModeNotSupportedCode = "e.m.live-mode-not-supported";

    /// <summary>The comment that goes with <see cref="LiveModeNotSupportedCode"/>, as the protocol page gives it.</summary>
    public const string LiveModeNotSupportedComment = "Connection does not support Live Delivery";

    private const string LimitMember = "limit";

    private static readonly MessageTypeUri StatusRequestUri = MessageTypeUri.Parse(StatusRequestType);
    private static readonly MessageTypeUri StatusUri = MessageTypeUri.Parse(StatusType);
    private static readonly MessageTypeUri DeliveryRequestUri = MessageTypeUri.Parse(DeliveryRequestType);
    private static readonly MessageTypeUri DeliveryUri = MessageTypeUri.Parse(DeliveryType);
    private static readonly MessageTypeUri MessagesReceivedUri = MessageTypeUri.Parse(MessagesReceivedType);
    private static readonly MessageTypeUri LiveDeliveryChangeUri = MessageTypeUri.Parse(LiveDeliveryChangeType);

    // ---- builders -------------------------------------------------------------------------

    /// <summary>Build a <c>status-request</c>. Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID (the one that holds the mediation grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="recipientDid">Ask about one registered DID only, or <c>null</c> for everything the mediator holds for this recipient.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateStatusRequest(string from, string mediator, string? recipientDid = null, IMessageIdGenerator? idGenerator = null)
    {
        RequireOptionalDid(recipientDid, nameof(recipientDid));
        return Start(StatusRequestType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteFilter(recipientDid))
            .Build();
    }

    /// <summary>Build a <c>status</c>.</summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="status">The queue state to report; <c>null</c> members are left out.</param>
    /// <param name="thid">The thread to answer on (<see cref="ResponseThreadId"/>), or <c>null</c> for none.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateStatus(string from, string to, PickupStatus status, string? thid = null, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        RequireOptionalDid(status.RecipientDid, nameof(status));
        RequireNotNegative(status.MessageCount, nameof(status));
        RequireNotNegative(status.LongestWaitedSeconds, nameof(status));
        RequireNotNegative(status.NewestReceivedTime, nameof(status));
        RequireNotNegative(status.OldestReceivedTime, nameof(status));
        RequireNotNegative(status.TotalBytes, nameof(status));

        return Start(StatusType, from, to, idGenerator, thid)
            .WithBody(PickupBodies.WriteStatus(status))
            .Build();
    }

    /// <summary>Build a <c>delivery-request</c>. Send it with <c>return_route: all</c>.</summary>
    /// <param name="from">The recipient's DID (the one that holds the mediation grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="limit">The largest number of messages wanted; zero or more. The mediator may deliver fewer.</param>
    /// <param name="recipientDid">Ask for messages of one registered DID only, or <c>null</c> for any.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateDeliveryRequest(string from, string mediator, int limit, string? recipientDid = null, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        RequireOptionalDid(recipientDid, nameof(recipientDid));
        return Start(DeliveryRequestType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteDeliveryRequest(LimitMember, limit, recipientDid))
            .Build();
    }

    /// <summary>
    /// Build a <c>delivery</c>: one attachment per message, <c>{"id": …, "data": {"base64": …}}</c>,
    /// the data being the message bytes in base64url.
    /// </summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="messages">The messages to deliver, in delivery order. Each id is unreserved URI characters (FR-ATT-04), at most 2048.</param>
    /// <param name="thid">The thread of the <c>delivery-request</c> being answered (<see cref="ResponseThreadId"/>), or <c>null</c> for a live delivery, which answers no request.</param>
    /// <param name="recipientDid">The filter the <c>delivery-request</c> carried, repeated; <c>null</c> when it had none.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateDelivery(string from, string to, IReadOnlyList<DeliveredMessage> messages, string? thid = null, string? recipientDid = null, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        RequireOptionalDid(recipientDid, nameof(recipientDid));
        var builder = Start(DeliveryType, from, to, idGenerator, thid)
            .WithBody(PickupBodies.WriteFilter(recipientDid));
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
            RequireId(message.Id, nameof(messages));
            // Checked here so the refusal never quotes the id (the message model's own check does).
            if (!UnreservedUriChars.IsUnreserved(message.Id))
                throw new ArgumentException("A message id must consist of unreserved URI characters (FR-ATT-04).", nameof(messages));
            builder.WithAttachment(PickupBodies.ToAttachment(message));
        }
        return builder.Build();
    }

    /// <summary>Build a <c>messages-received</c>. Send it with <c>return_route: all</c>; the mediator answers with a <c>status</c>.</summary>
    /// <param name="from">The recipient's DID (the one that holds the mediation grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="messageIds">The attachment ids of the messages received, as the <c>delivery</c> carried them.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateMessagesReceived(string from, string mediator, IReadOnlyList<string> messageIds, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        foreach (var id in messageIds)
            RequireId(id, nameof(messageIds));
        return Start(MessagesReceivedType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteMessageIds(messageIds))
            .Build();
    }

    /// <summary>Build a <c>live-delivery-change</c>. Live delivery needs a connection that stays open (a WebSocket).</summary>
    /// <param name="from">The recipient's DID (the one that holds the mediation grant).</param>
    /// <param name="mediator">The mediator's DID.</param>
    /// <param name="liveDelivery"><c>true</c> to have new messages pushed over this connection as they arrive; <c>false</c> to have them queued.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    public static Message CreateLiveDeliveryChange(string from, string mediator, bool liveDelivery, IMessageIdGenerator? idGenerator = null)
        => Start(LiveDeliveryChangeType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteLiveDelivery(liveDelivery))
            .Build();

    /// <summary>
    /// Build the problem report a mediator sends when live delivery is requested on a connection
    /// that cannot do it (FR-PICK-07): type <c>https://didcomm.org/report-problem/2.0/problem-report</c>,
    /// code <see cref="LiveModeNotSupportedCode"/>, comment <see cref="LiveModeNotSupportedComment"/>.
    /// </summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="pthid">The thread the problem occurred in: the <c>live-delivery-change</c>'s thread (<see cref="ResponseThreadId"/>).</param>
    public static Message CreateLiveModeNotSupported(string from, string to, string pthid)
    {
        RequireDid(from, nameof(from));
        RequireDid(to, nameof(to));
        ArgumentException.ThrowIfNullOrEmpty(pthid);
        return ProblemReports.Create(from, to, LiveModeNotSupportedCode, pthid, LiveModeNotSupportedComment);
    }

    /// <summary>
    /// The thread a response to <paramref name="request"/> belongs to (DD-11): the request's
    /// <c>thid</c> when it has one, else its <c>id</c>. A <c>status</c> and a <c>delivery</c> carry
    /// it as <c>thid</c>, a problem report as <c>pthid</c>.
    /// </summary>
    /// <param name="request">The request being answered.</param>
    public static string ResponseThreadId(Message request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrEmpty(request.Thid) ? request.Id : request.Thid;
    }

    // ---- parsers --------------------------------------------------------------------------

    /// <summary>Read a <c>status-request</c>. An absent <c>body</c> or an absent <c>recipient_did</c> gives a <c>null</c> <paramref name="recipientDid"/>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="recipientDid">The DID the request is about, or <c>null</c> for all. A non-empty string of at most 2048 characters; whether it is a DID has not been checked.</param>
    /// <exception cref="MalformedMessageException">It is a <c>status-request</c> whose <c>recipient_did</c> is not a string, is empty or is over the bound.</exception>
    public static bool TryParseStatusRequest(Message message, out string? recipientDid)
    {
        recipientDid = null;
        if (!WireJson.IsType(message, StatusRequestUri))
            return false;
        recipientDid = PickupBodies.ReadRecipientDid(WireJson.ToElement(message.Body), "status-request");
        return true;
    }

    /// <summary>Read a <c>status</c>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="status">The reported queue state. Integers beyond the 64-bit range are clamped.</param>
    /// <exception cref="MalformedMessageException">It is a <c>status</c> without <c>message_count</c>, or a member has the wrong JSON type, is negative, or (for <c>recipient_did</c>) is empty or over the bound.</exception>
    public static bool TryParseStatus(Message message, [NotNullWhen(true)] out PickupStatus? status)
    {
        status = null;
        if (!WireJson.IsType(message, StatusUri))
            return false;
        status = PickupBodies.ReadStatus(WireJson.ToElement(message.Body), "status");
        return true;
    }

    /// <summary>Read a <c>delivery-request</c>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="limit">The largest number of messages wanted: zero or more; a value beyond the 32-bit range is clamped.</param>
    /// <param name="recipientDid">The DID the request is about, or <c>null</c> for any. A non-empty string of at most 2048 characters; whether it is a DID has not been checked.</param>
    /// <exception cref="MalformedMessageException">It is a <c>delivery-request</c> whose <c>limit</c> is missing, not a number, negative or has a fractional part, or whose <c>recipient_did</c> is not a string, is empty or is over the bound.</exception>
    public static bool TryParseDeliveryRequest(Message message, out int limit, out string? recipientDid)
    {
        limit = 0;
        recipientDid = null;
        if (!WireJson.IsType(message, DeliveryRequestUri))
            return false;
        var body = WireJson.ToElement(message.Body);
        limit = PickupBodies.ReadLimit(body, LimitMember, "delivery-request");
        recipientDid = PickupBodies.ReadRecipientDid(body, "delivery-request");
        return true;
    }

    /// <summary>
    /// Read a <c>delivery</c>. The payloads are decoded (<c>data.base64</c> in base64url or standard
    /// base64, padded or not; or <c>data.json</c>) and not inspected: whether each is a DIDComm
    /// message is for the recipient's own unpack to say, one attachment at a time.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="messages">The delivered messages in wire order; empty when the message has no attachments.</param>
    /// <param name="recipientDid">The filter the mediator repeated, or <c>null</c>.</param>
    /// <exception cref="MalformedMessageException">It is a <c>delivery</c> with an attachment that has no <c>id</c>, an <c>id</c> over the bound, no inline data, or data that does not decode; or a <c>recipient_did</c> that is not a string, is empty or is over the bound.</exception>
    public static bool TryParseDelivery(Message message, [NotNullWhen(true)] out IReadOnlyList<DeliveredMessage>? messages, out string? recipientDid)
    {
        messages = null;
        recipientDid = null;
        if (!WireJson.IsType(message, DeliveryUri))
            return false;
        recipientDid = PickupBodies.ReadRecipientDid(WireJson.ToElement(message.Body), "delivery");
        messages = PickupBodies.ReadAttachments(message.Attachments, "delivery");
        return true;
    }

    /// <summary>Read a <c>messages-received</c>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="messageIds">The acknowledged ids in wire order. Each is a non-empty string of at most 2048 characters.</param>
    /// <exception cref="MalformedMessageException">It is a <c>messages-received</c> whose <c>message_id_list</c> is missing or not an array, or an entry is not a string, is empty or is over the bound.</exception>
    public static bool TryParseMessagesReceived(Message message, [NotNullWhen(true)] out IReadOnlyList<string>? messageIds)
        => TryParseMessagesReceived(message, int.MaxValue, out messageIds);

    /// <summary>
    /// <see cref="TryParseMessagesReceived(Message, out IReadOnlyList{string}?)"/> with a bound on
    /// the number of ids, as the mediator applies it (FR-PICK-10). A longer list is malformed, and
    /// it is refused from its length alone: before the body is re-read and before any entry is
    /// decoded or allocated, so what sits beyond the bound is never looked at.
    /// </summary>
    internal static bool TryParseMessagesReceived(Message message, int maxIds, [NotNullWhen(true)] out IReadOnlyList<string>? messageIds)
    {
        messageIds = null;
        if (!WireJson.IsType(message, MessagesReceivedUri))
            return false;
        PickupBodies.RequireIdsWithin(message.Body, maxIds, "messages-received");
        messageIds = PickupBodies.ReadMessageIds(WireJson.ToElement(message.Body), "messages-received", maxIds);
        return true;
    }

    /// <summary>Read a <c>live-delivery-change</c>.</summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="liveDelivery">The requested state.</param>
    /// <exception cref="MalformedMessageException">It is a <c>live-delivery-change</c> whose <c>live_delivery</c> is missing or not a boolean.</exception>
    public static bool TryParseLiveDeliveryChange(Message message, out bool liveDelivery)
    {
        liveDelivery = false;
        if (!WireJson.IsType(message, LiveDeliveryChangeUri))
            return false;
        liveDelivery = PickupBodies.ReadLiveDelivery(WireJson.ToElement(message.Body), "live-delivery-change");
        return true;
    }

    // ---- shared pieces --------------------------------------------------------------------

    private static MessageBuilder Start(string type, string from, string to, IMessageIdGenerator? idGenerator, string? thid = null)
    {
        RequireDid(from, nameof(from));
        RequireDid(to, nameof(to));
        var builder = new MessageBuilder(idGenerator ?? UuidV4MessageIdGenerator.Instance)
            .WithType(type)
            .WithFrom(from)
            .WithTo(to);
        if (thid is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(thid);
            builder.WithThid(thid);
        }
        return builder;
    }

    private static void RequireDid(string? value, string parameter)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("A DID is required.", parameter);
        if (!PickupBodies.IsWithinIdLimit(value))
            throw new ArgumentException("A DID exceeds the maximum length of 2048 characters (FR-API-09).", parameter);
    }

    private static void RequireOptionalDid(string? value, string parameter)
    {
        if (value is not null)
            RequireDid(value, parameter);
    }

    private static void RequireId(string? value, string parameter)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("A message id is required.", parameter);
        if (!PickupBodies.IsWithinIdLimit(value))
            throw new ArgumentException("A message id exceeds the maximum length of 2048 characters (FR-API-09).", parameter);
    }

    private static void RequireNotNegative(long? value, string parameter)
    {
        if (value is < 0)
            throw new ArgumentOutOfRangeException(parameter, "A status member must not be negative.");
    }
}
