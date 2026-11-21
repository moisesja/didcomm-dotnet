using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// Message Pickup 4.0 (<c>https://didcomm.org/message-pickup/4.0</c>, FR-PICK-12) in both
/// directions: the message type URIs, builders and strict parsers for the six messages, and the
/// 4.0 problem report. <see cref="Client.PickupClient"/> speaks 4.0 through it when a mediator
/// discloses 4.0 or the host pins it; <c>MessagePickup40Handler</c> (in the <c>DidComm.Mediator</c>
/// package) answers 4.0 recipients through it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What differs from 3.0</strong> (<see cref="MessagePickup"/>; line numbers are those of
/// the 4.0 page at <c>decentralized-identity/didcomm.org@a26926c</c>): the renamed protocol
/// (L5); <c>delivery-request</c>'s limit is <c>message_count_limit</c> (L38, L206) and a
/// <c>delivery</c> answering it carries no more attachments than that (L255); a <c>status</c>
/// has no <c>longest_waited_seconds</c> (L37) and gives its times in UTC epoch milliseconds (L39,
/// L164); a <c>status</c> always answers a request and so always carries <c>thid</c> (L33); an
/// empty <c>delivery</c> — what a <c>delivery-request</c> gets when nothing can be delivered —
/// carries an empty <c>attachments</c> array (L210, L259); <c>messages-received</c> carries no
/// <c>return_route</c> (L35) and is not answered; the live-mode problem report has the type
/// <c>https://didcomm.org/message-pickup/4.0/problem-report</c> (L362). The member shapes are 3.0's
/// and go through <see cref="PickupBodies"/>.
/// </para>
/// <para>
/// <strong>Parsers</strong> follow <see cref="MessagePickup"/>'s rules: <c>false</c> for another
/// type; <see cref="MalformedMessageException"/> naming the member and the rule, never a value, for
/// the right type with a wrong shape; DID and id members bounded (FR-API-09) before they are looked
/// at; unknown members ignored; types matched ignoring case and <c>-</c>/<c>_</c>, any 4.x minor. A
/// body that carries 3.0's <c>limit</c> instead of <c>message_count_limit</c> is malformed: no
/// version reads the other's members.
/// </para>
/// <para>
/// <strong>Builders</strong> check their arguments as <see cref="MessagePickup"/>'s do and always
/// write the canonical type strings. Answers take the thread to answer on
/// (<see cref="MessagePickup.ResponseThreadId"/>, the same rule in both versions).
/// </para>
/// <para>
/// The class is internal: <see cref="Client.PickupClient"/> and the 4.0 mediator role are its only
/// users, so 4.0 adds no public message API of its own.
/// </para>
/// </remarks>
internal static class MessagePickup40
{
    /// <summary>The Protocol Identifier URI: <c>https://didcomm.org/message-pickup/4.0</c>.</summary>
    internal const string ProtocolUri = MediatorServiceProtocols.MessagePickup40;

    /// <summary>Message type of <c>status-request</c>.</summary>
    internal const string StatusRequestType = ProtocolUri + "/status-request";

    /// <summary>Message type of <c>status</c>.</summary>
    internal const string StatusType = ProtocolUri + "/status";

    /// <summary>Message type of <c>delivery-request</c>.</summary>
    internal const string DeliveryRequestType = ProtocolUri + "/delivery-request";

    /// <summary>Message type of <c>delivery</c>.</summary>
    internal const string DeliveryType = ProtocolUri + "/delivery";

    /// <summary>Message type of <c>messages-received</c>.</summary>
    internal const string MessagesReceivedType = ProtocolUri + "/messages-received";

    /// <summary>Message type of <c>live-delivery-change</c>.</summary>
    internal const string LiveDeliveryChangeType = ProtocolUri + "/live-delivery-change";

    /// <summary>Message type of the 4.0 problem report (L362).</summary>
    internal const string ProblemReportType = ProtocolUri + "/problem-report";

    /// <summary>The name of <c>delivery-request</c>'s limit in 4.0 (3.0 calls it <c>limit</c>).</summary>
    internal const string MessageCountLimitMember = "message_count_limit";

    internal static readonly MessageTypeUri StatusRequestUri = MessageTypeUri.Parse(StatusRequestType);
    internal static readonly MessageTypeUri StatusUri = MessageTypeUri.Parse(StatusType);
    internal static readonly MessageTypeUri DeliveryRequestUri = MessageTypeUri.Parse(DeliveryRequestType);
    internal static readonly MessageTypeUri DeliveryUri = MessageTypeUri.Parse(DeliveryType);
    internal static readonly MessageTypeUri MessagesReceivedUri = MessageTypeUri.Parse(MessagesReceivedType);
    internal static readonly MessageTypeUri LiveDeliveryChangeUri = MessageTypeUri.Parse(LiveDeliveryChangeType);
    internal static readonly MessageTypeUri ProblemReportUri = MessageTypeUri.Parse(ProblemReportType);

    private const string CodeMember = "code";
    private const string CommentMember = "comment";

    // ---- builders -------------------------------------------------------------------------

    /// <summary>Build a <c>status-request</c>. Send it with <c>return_route: all</c>.</summary>
    internal static Message CreateStatusRequest(string from, string mediator, string? recipientDid = null, IMessageIdGenerator? idGenerator = null)
    {
        MessagePickup.RequireOptionalDid(recipientDid, nameof(recipientDid));
        return MessagePickup.Start(StatusRequestType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteFilter(recipientDid))
            .Build();
    }

    /// <summary>Build a <c>status</c>. 4.0 sends one only in answer to a request (L33), so <paramref name="thid"/> is required.</summary>
    internal static Message CreateStatus(string from, string to, PickupStatus40 status, string thid, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrEmpty(thid);
        MessagePickup.RequireOptionalDid(status.RecipientDid, nameof(status));
        MessagePickup.RequireNotNegative(status.MessageCount, nameof(status));
        MessagePickup.RequireNotNegative(status.NewestReceivedTimeMs, nameof(status));
        MessagePickup.RequireNotNegative(status.OldestReceivedTimeMs, nameof(status));
        MessagePickup.RequireNotNegative(status.TotalBytes, nameof(status));

        return MessagePickup.Start(StatusType, from, to, idGenerator, thid)
            .WithBody(PickupBodies.WriteStatus40(status))
            .Build();
    }

    /// <summary>Build a <c>delivery-request</c> (<c>message_count_limit</c>). Send it with <c>return_route: all</c>.</summary>
    internal static Message CreateDeliveryRequest(string from, string mediator, int messageCountLimit, string? recipientDid = null, IMessageIdGenerator? idGenerator = null)
    {
        if (messageCountLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(messageCountLimit), "The message count limit must not be negative.");
        MessagePickup.RequireOptionalDid(recipientDid, nameof(recipientDid));
        return MessagePickup.Start(DeliveryRequestType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteDeliveryRequest(MessageCountLimitMember, messageCountLimit, recipientDid))
            .Build();
    }

    /// <summary>
    /// Build a <c>delivery</c>: one attachment per message, as <see cref="MessagePickup.CreateDelivery"/>
    /// writes them. With no messages the <c>attachments</c> array is written empty (L210, L259), never
    /// left out.
    /// </summary>
    /// <param name="from">The mediator's DID.</param>
    /// <param name="to">The recipient's DID.</param>
    /// <param name="messages">The messages to deliver, in delivery order; may be empty.</param>
    /// <param name="thid">The thread of the <c>delivery-request</c> being answered, or <c>null</c> for a live delivery (L34, L263).</param>
    /// <param name="recipientDid">The filter the <c>delivery-request</c> carried, repeated (L265); <c>null</c> when it had none.</param>
    /// <param name="idGenerator">Optional id generator; the default is a UUID v4.</param>
    internal static Message CreateDelivery(string from, string to, IReadOnlyList<DeliveredMessage> messages, string? thid = null, string? recipientDid = null, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        MessagePickup.RequireOptionalDid(recipientDid, nameof(recipientDid));
        var builder = MessagePickup.Start(DeliveryType, from, to, idGenerator, thid)
            .WithBody(PickupBodies.WriteFilter(recipientDid));
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
            MessagePickup.RequireId(message.Id, nameof(messages));
            if (!UnreservedUriChars.IsUnreserved(message.Id))
                throw new ArgumentException("A message id must consist of unreserved URI characters (FR-ATT-04).", nameof(messages));
            builder.WithAttachment(PickupBodies.ToAttachment(message));
        }
        var delivery = builder.Build();
        // The builder creates the list on the first attachment; an empty delivery still writes "attachments": [].
        delivery.Attachments ??= new List<Attachment>();
        return delivery;
    }

    /// <summary>Build a <c>messages-received</c>. Sent without <c>return_route</c> (L35); the mediator does not answer it.</summary>
    internal static Message CreateMessagesReceived(string from, string mediator, IReadOnlyList<string> messageIds, IMessageIdGenerator? idGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        foreach (var id in messageIds)
            MessagePickup.RequireId(id, nameof(messageIds));
        return MessagePickup.Start(MessagesReceivedType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteMessageIds(messageIds))
            .Build();
    }

    /// <summary>Build a <c>live-delivery-change</c>.</summary>
    internal static Message CreateLiveDeliveryChange(string from, string mediator, bool liveDelivery, IMessageIdGenerator? idGenerator = null)
        => MessagePickup.Start(LiveDeliveryChangeType, from, mediator, idGenerator)
            .WithBody(PickupBodies.WriteLiveDelivery(liveDelivery))
            .Build();

    /// <summary>
    /// Build the 4.0 problem report for live delivery asked for on a connection that cannot do it
    /// (L342-L369): type <see cref="ProblemReportType"/>, <c>pthid</c> = the thread of the
    /// <c>live-delivery-change</c>, code <see cref="MessagePickup.LiveModeNotSupportedCode"/> and
    /// comment <see cref="MessagePickup.LiveModeNotSupportedComment"/>.
    /// </summary>
    internal static Message CreateLiveModeNotSupported(string from, string to, string pthid, IMessageIdGenerator? idGenerator = null)
    {
        MessagePickup.RequireDid(from, nameof(from));
        MessagePickup.RequireDid(to, nameof(to));
        ArgumentException.ThrowIfNullOrEmpty(pthid);
        var body = new JsonObject
        {
            [CodeMember] = MessagePickup.LiveModeNotSupportedCode,
            [CommentMember] = MessagePickup.LiveModeNotSupportedComment,
        };
        return new MessageBuilder(idGenerator ?? UuidV4MessageIdGenerator.Instance)
            .WithType(ProblemReportType)
            .WithFrom(from)
            .WithTo(to)
            .WithPthid(pthid)
            .WithBody(body)
            .Build();
    }

    // ---- parsers --------------------------------------------------------------------------

    /// <summary>Read a <c>status-request</c>; see <see cref="MessagePickup.TryParseStatusRequest"/>.</summary>
    internal static bool TryParseStatusRequest(Message message, out string? recipientDid)
    {
        recipientDid = null;
        if (!WireJson.IsType(message, StatusRequestUri))
            return false;
        recipientDid = PickupBodies.ReadRecipientDid(WireJson.ToElement(message.Body), "status-request");
        return true;
    }

    /// <summary>Read a <c>status</c>: <c>message_count</c> REQUIRED, times in milliseconds, <c>longest_waited_seconds</c> ignored.</summary>
    internal static bool TryParseStatus(Message message, [NotNullWhen(true)] out PickupStatus40? status)
    {
        status = null;
        if (!WireJson.IsType(message, StatusUri))
            return false;
        status = PickupBodies.ReadStatus40(WireJson.ToElement(message.Body), "status");
        return true;
    }

    /// <summary>Read a <c>delivery-request</c>: <c>message_count_limit</c> REQUIRED, with 3.0's <c>limit</c> rules (zero or more; a fraction, a negative value or a non-number is malformed; beyond 32 bits clamped).</summary>
    internal static bool TryParseDeliveryRequest(Message message, out int messageCountLimit, out string? recipientDid)
    {
        messageCountLimit = 0;
        recipientDid = null;
        if (!WireJson.IsType(message, DeliveryRequestUri))
            return false;
        var body = WireJson.ToElement(message.Body);
        messageCountLimit = PickupBodies.ReadLimit(body, MessageCountLimitMember, "delivery-request");
        recipientDid = PickupBodies.ReadRecipientDid(body, "delivery-request");
        return true;
    }

    /// <summary>
    /// Read a <c>delivery</c> as a whole, as <see cref="MessagePickup.TryParseDelivery"/> does, and
    /// also require the <c>attachments</c> array (L259: an empty delivery carries an empty one).
    /// </summary>
    internal static bool TryParseDelivery(Message message, [NotNullWhen(true)] out IReadOnlyList<DeliveredMessage>? messages, out string? recipientDid)
    {
        messages = null;
        recipientDid = null;
        if (!WireJson.IsType(message, DeliveryUri))
            return false;
        recipientDid = PickupBodies.ReadRecipientDid(WireJson.ToElement(message.Body), "delivery");
        if (message.Attachments is null)
            throw new MalformedMessageException("delivery is missing the REQUIRED 'attachments' array (Message Pickup 4.0).");
        messages = PickupBodies.ReadAttachments(message.Attachments, "delivery");
        return true;
    }

    /// <summary>Read a <c>messages-received</c>, the id list bounded from its length before any entry is read (see <see cref="MessagePickup.TryParseMessagesReceived(Message, int, out IReadOnlyList{string}?)"/>).</summary>
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
    internal static bool TryParseLiveDeliveryChange(Message message, out bool liveDelivery)
    {
        liveDelivery = false;
        if (!WireJson.IsType(message, LiveDeliveryChangeUri))
            return false;
        liveDelivery = PickupBodies.ReadLiveDelivery(WireJson.ToElement(message.Body), "live-delivery-change");
        return true;
    }

    /// <summary>Whether <paramref name="message"/> is a 4.0 problem report (any 4.x minor).</summary>
    internal static bool IsProblemReport(Message message) => WireJson.IsType(message, ProblemReportUri);
}

/// <summary>
/// The body of a Message Pickup 4.0 <c>status</c> (L145-L170): <c>message_count</c> REQUIRED, the
/// times in UTC epoch milliseconds, no <c>longest_waited_seconds</c>. Internal: the client hands
/// callers a <see cref="PickupStatus"/> (<see cref="ToPickupStatus"/>).
/// </summary>
/// <param name="MessageCount">The number of messages waiting. Never negative.</param>
/// <param name="RecipientDid">The DID the status is about, present exactly when the request named one.</param>
/// <param name="NewestReceivedTimeMs">When the newest waiting message arrived, UTC epoch milliseconds.</param>
/// <param name="OldestReceivedTimeMs">When the oldest waiting message arrived, UTC epoch milliseconds.</param>
/// <param name="TotalBytes">The total size of the waiting messages in bytes.</param>
/// <param name="LiveDelivery">Whether live delivery is on for the connection the status travels on.</param>
internal sealed record PickupStatus40(
    int MessageCount,
    string? RecipientDid = null,
    long? NewestReceivedTimeMs = null,
    long? OldestReceivedTimeMs = null,
    long? TotalBytes = null,
    bool? LiveDelivery = null)
{
    /// <summary>
    /// The public form: the times in UTC epoch seconds, rounded down, as <see cref="PickupStatus"/>
    /// documents them whatever the version; <see cref="PickupStatus.LongestWaitedSeconds"/> is
    /// <c>null</c>, because 4.0 has no such member.
    /// </summary>
    internal PickupStatus ToPickupStatus()
        => new(MessageCount, RecipientDid, LongestWaitedSeconds: null, NewestReceivedTimeMs / 1000, OldestReceivedTimeMs / 1000, TotalBytes, LiveDelivery);

    /// <summary>The numbers and the flag only: the recipient DID is not printed.</summary>
    public override string ToString()
        => $"PickupStatus40 {{ MessageCount = {MessageCount}, NewestReceivedTimeMs = {NewestReceivedTimeMs}, OldestReceivedTimeMs = {OldestReceivedTimeMs}, TotalBytes = {TotalBytes}, LiveDelivery = {LiveDelivery} }}";
}
