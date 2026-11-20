using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The Message Pickup messages <see cref="PickupClient"/> and its live streams send and read, per
/// version (#100 DC-7): the request for each step with what it expects back, the acknowledgement,
/// and the reading of a <c>status</c> and of a <c>delivery</c>. 3.0 goes through
/// <see cref="MessagePickup"/> and <see cref="DeliveryReader"/>'s 3.0 specs, 4.0 through
/// <see cref="MessagePickup40"/>; the attachments of both are read by
/// <see cref="DeliveryReader.ReadEntries"/>.
/// </summary>
/// <remarks>
/// <para>
/// What differs (line numbers of the 4.0 page at <c>decentralized-identity/didcomm.org@a26926c</c>):
/// a 4.0 <c>delivery-request</c> carries <c>message_count_limit</c> (L38, L206) and is answered
/// only by a <c>delivery</c>, an empty one when nothing can be delivered (L29, L210, L259), never a
/// <c>status</c>; that <c>delivery</c> MUST carry an <c>attachments</c> array (L259) and no more of
/// them than the limit (L255); a 4.0 <c>status</c> gives its times in milliseconds (L39, L164),
/// which the public <see cref="PickupStatus"/> carries as seconds, rounded down, without
/// <see cref="PickupStatus.LongestWaitedSeconds"/> (L37); a 4.0 <c>messages-received</c> carries no
/// <c>return_route</c> (L35) and is not answered (L62, L294), so it is sent one way
/// (<see cref="IMediatorExchange.SendOneWayAsync"/>).
/// </para>
/// <para>
/// Every refusal is a <see cref="MediationProtocolException"/> whose text names our request id and
/// the request's message name, never a wire value.
/// </para>
/// </remarks>
internal static class PickupWire
{
    /// <summary>The name a <c>messages-received</c> goes by in log lines and exception texts.</summary>
    internal const string MessagesReceivedName = "messages-received";

    /// <summary>4.0 <c>status-request</c> → 4.0 <c>status</c>.</summary>
    internal static readonly MediatorExchangeSpec StatusRequestSpec4 = new("status-request", new[] { MessagePickup40.StatusUri }, ExpectsDelivery: false);

    /// <summary>4.0 <c>delivery-request</c> → 4.0 <c>delivery</c> only (an empty one when nothing can be delivered; a <c>status</c> does not answer it).</summary>
    internal static readonly MediatorExchangeSpec DeliveryRequestSpec4 = new("delivery-request", new[] { MessagePickup40.DeliveryUri }, ExpectsDelivery: true);

    /// <summary>4.0 <c>live-delivery-change</c> → 4.0 <c>status</c> (or a problem report, which the exchange throws).</summary>
    internal static readonly MediatorExchangeSpec LiveDeliveryChangeSpec4 = new("live-delivery-change", new[] { MessagePickup40.StatusUri }, ExpectsDelivery: false);

    // ---- requests -------------------------------------------------------------------------

    /// <summary>The <c>status-request</c> of <paramref name="version"/>, with a fresh UUID v4 id.</summary>
    internal static (Message Request, MediatorExchangeSpec Spec) StatusRequest(MessagePickupVersion version, string from, string mediatorDid, string? recipientDid)
        => IsV4(version)
            ? (MessagePickup40.CreateStatusRequest(from, mediatorDid, recipientDid, UuidV4MessageIdGenerator.Instance), StatusRequestSpec4)
            : (MessagePickup.CreateStatusRequest(from, mediatorDid, recipientDid, UuidV4MessageIdGenerator.Instance), DeliveryReader.StatusRequestSpec);

    /// <summary>The <c>delivery-request</c> of <paramref name="version"/> for up to <paramref name="limit"/> messages (<c>limit</c> in 3.0, <c>message_count_limit</c> in 4.0), with a fresh UUID v4 id.</summary>
    internal static (Message Request, MediatorExchangeSpec Spec) DeliveryRequest(MessagePickupVersion version, string from, string mediatorDid, int limit, string? recipientDid)
        => IsV4(version)
            ? (MessagePickup40.CreateDeliveryRequest(from, mediatorDid, limit, recipientDid, UuidV4MessageIdGenerator.Instance), DeliveryRequestSpec4)
            : (MessagePickup.CreateDeliveryRequest(from, mediatorDid, limit, recipientDid, UuidV4MessageIdGenerator.Instance), DeliveryReader.DeliveryRequestSpec);

    /// <summary>The <c>live-delivery-change</c> of <paramref name="version"/>, with a fresh UUID v4 id.</summary>
    internal static (Message Request, MediatorExchangeSpec Spec) LiveDeliveryChange(MessagePickupVersion version, string from, string mediatorDid, bool liveDelivery)
        => IsV4(version)
            ? (MessagePickup40.CreateLiveDeliveryChange(from, mediatorDid, liveDelivery, UuidV4MessageIdGenerator.Instance), LiveDeliveryChangeSpec4)
            : (MessagePickup.CreateLiveDeliveryChange(from, mediatorDid, liveDelivery, UuidV4MessageIdGenerator.Instance), DeliveryReader.LiveDeliveryChangeSpec);

    /// <summary>The <c>messages-received</c> of <paramref name="version"/> naming <paramref name="ids"/>, with a fresh UUID v4 id.</summary>
    internal static Message MessagesReceived(MessagePickupVersion version, string from, string mediatorDid, IReadOnlyList<string> ids)
        => IsV4(version)
            ? MessagePickup40.CreateMessagesReceived(from, mediatorDid, ids, UuidV4MessageIdGenerator.Instance)
            : MessagePickup.CreateMessagesReceived(from, mediatorDid, ids, UuidV4MessageIdGenerator.Instance);

    /// <summary>
    /// Whether the mediator answers a <c>messages-received</c> of <paramref name="version"/>: 3.0
    /// yes (it SHOULD send its <c>status</c>, 3.0 L193; <see cref="DeliveryReader.MessagesReceivedSpec"/>),
    /// 4.0 never (L62, L294), so a 4.0 one is sent one way.
    /// </summary>
    internal static bool AnswersAcknowledgement(MessagePickupVersion version) => !IsV4(version);

    // ---- answers --------------------------------------------------------------------------

    /// <summary>
    /// Read the <c>status</c> of <paramref name="version"/> that answered request
    /// <paramref name="requestId"/> as the public <see cref="PickupStatus"/> (4.0: times in seconds,
    /// rounded down, <see cref="PickupStatus.LongestWaitedSeconds"/> <c>null</c>). With
    /// <paramref name="checkFilter"/>, its <c>recipient_did</c> must repeat the DID asked about
    /// (<see cref="DeliveryReader.RequireStatusFilterEcho"/>).
    /// </summary>
    /// <exception cref="MediationProtocolException">Not a status of this version, a malformed one, or one about another DID than asked about (or not repeating it).</exception>
    internal static PickupStatus ReadStatus(MessagePickupVersion version, Message answer, string requestId, MediatorExchangeSpec spec, bool checkFilter, string? filter)
    {
        if (!IsV4(version))
            return DeliveryReader.ReadStatus(answer, requestId, spec, checkFilter, filter);

        PickupStatus40? status;
        try
        {
            if (!MessagePickup40.TryParseStatus(answer, out status))
                throw DeliveryReader.Unexpected(requestId, spec);
        }
        catch (MalformedMessageException ex)
        {
            throw new MediationProtocolException($"The mediator answered request '{requestId}' ({spec.Name}) with a malformed status.", requestId, innerException: ex);
        }
        if (checkFilter)
            DeliveryReader.RequireStatusFilterEcho(status.RecipientDid, filter, requestId, spec);
        return status.ToPickupStatus();
    }

    /// <summary>Whether <paramref name="message"/> is a <c>delivery</c> of <paramref name="version"/> (any minor of its major).</summary>
    internal static bool IsDelivery(MessagePickupVersion version, Message message)
        => IsV4(version) ? WireJson.IsType(message, MessagePickup40.DeliveryUri) : DeliveryReader.IsDelivery(message);

    /// <summary>Whether <paramref name="message"/> is a <c>delivery</c> of the other version than <paramref name="version"/>.</summary>
    internal static bool IsDeliveryOfAnotherVersion(MessagePickupVersion version, Message message)
        => IsV4(version) ? DeliveryReader.IsDelivery(message) : WireJson.IsType(message, MessagePickup40.DeliveryUri);

    /// <summary>
    /// Read the answer to a <c>delivery-request</c> of <paramref name="version"/> for up to
    /// <paramref name="limit"/> messages. 3.0: a <c>status</c> (nothing deliverable; its
    /// <c>recipient_did</c> must repeat <paramref name="filter"/>), or a <c>delivery</c> whose first
    /// <paramref name="limit"/> attachments are read and whose others are left unread. 4.0: a
    /// <c>delivery</c> only, which must carry an <c>attachments</c> array (L259; an empty one is a
    /// drained queue) of at most <paramref name="limit"/> entries (L255), refused from the count
    /// before any attachment is read (L-055). With <paramref name="checkFilter"/>, in both, the
    /// <c>recipient_did</c> of the answer is checked against <paramref name="filter"/> (a status must
    /// repeat it, a delivery may leave it out); a live stream's drain asks with no filter and checks
    /// none, as it always has.
    /// </summary>
    /// <param name="version">The version spoken.</param>
    /// <param name="message">The answer.</param>
    /// <param name="requestId">Our request's id.</param>
    /// <param name="spec">The request's spec.</param>
    /// <param name="limit">The limit the request carried.</param>
    /// <param name="checkFilter">Whether to check the answer's <c>recipient_did</c> against <paramref name="filter"/>.</param>
    /// <param name="filter">The filter the request carried, or <c>null</c>.</param>
    /// <exception cref="MediationProtocolException">An answer of another type; a malformed one; a status or delivery about another DID than asked about; under 4.0 a delivery without <c>attachments</c> or with more than <paramref name="limit"/>.</exception>
    internal static DeliveryAnswer ReadDeliveryAnswer(MessagePickupVersion version, Message message, string requestId, MediatorExchangeSpec spec, int limit, bool checkFilter, string? filter)
    {
        if (!IsV4(version))
        {
            // Nothing deliverable: a 3.0 mediator answers with its status (3.0 L143).
            if (DeliveryReader.IsStatus(message))
                return new DeliveryAnswer(DeliveryReader.ReadStatus(message, requestId, spec, checkFilter, filter), Contents: null);
            if (!DeliveryReader.IsDelivery(message))
                throw DeliveryReader.Unexpected(requestId, spec);
            if (checkFilter)
                DeliveryReader.RequireDeliveryFilterEcho(message, requestId, spec, filter);
            // Only the first `limit` attachments are read (L-055): what the mediator sent beyond the
            // request is not decoded, not unpacked and not acknowledged, so it stays queued.
            return new DeliveryAnswer(Status: null, DeliveryReader.ReadEntries(message.Attachments, limit));
        }

        if (!WireJson.IsType(message, MessagePickup40.DeliveryUri))
            throw DeliveryReader.Unexpected(requestId, spec);
        if (checkFilter)
            DeliveryReader.RequireDeliveryFilterEcho(message, requestId, spec, filter);
        // L259: an answer to a delivery-request carries an attachments array, empty when nothing
        // could be delivered; without one it says neither "these messages" nor "nothing".
        if (message.Attachments is not { } attachments)
        {
            throw new MediationProtocolException(
                $"The mediator answered request '{requestId}' ({spec.Name}) with a delivery that has no attachments array (Message Pickup 4.0 requires one, empty when nothing is delivered).", requestId);
        }
        // L255: MUST NOT exceed the request's message_count_limit. Refused from the count alone,
        // before any attachment is decoded or unpacked (L-055), and nothing in it is acknowledged.
        if (attachments.Count > limit)
        {
            throw new MediationProtocolException(
                $"The mediator answered request '{requestId}' ({spec.Name}) with a delivery of more messages than its message_count_limit (Message Pickup 4.0 forbids it); nothing in it was opened or acknowledged.", requestId);
        }
        return new DeliveryAnswer(Status: null, DeliveryReader.ReadEntries(attachments, limit));
    }

    private static bool IsV4(MessagePickupVersion version) => version switch
    {
        MessagePickupVersion.V4 => true,
        MessagePickupVersion.V3 => false,
        // A resolved version only; Negotiate or an undefined value is a programming error.
        _ => throw new ArgumentOutOfRangeException(nameof(version), "A resolved Message Pickup version (V4 or V3) is needed."),
    };
}

/// <summary>What answered a <c>delivery-request</c> (<see cref="PickupWire.ReadDeliveryAnswer"/>): a 3.0 <c>status</c> (nothing deliverable), or the messages of a <c>delivery</c>.</summary>
/// <param name="Status">The 3.0 status that answered it; <c>null</c> for a delivery.</param>
/// <param name="Contents">The delivery's readable messages; <c>null</c> for a status.</param>
internal readonly record struct DeliveryAnswer(PickupStatus? Status, DeliveryContents? Contents);
