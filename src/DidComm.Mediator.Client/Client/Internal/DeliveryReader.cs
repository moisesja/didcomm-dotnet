using System.Text.Json;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Message Pickup 3.0 as the recipient-side client reads it (#96 design §3.3, §4.7, §4.8), and the
/// reading both versions share (<see cref="PickupWire"/> picks the version): what each 3.0 request
/// expects back, how an answer becomes a status, and how a delivery's attachments become the
/// readable messages of a delivery. A malformed answer is a <see cref="MediationProtocolException"/>
/// naming our request id and the request's name, never a wire value. A delivery is read one
/// attachment at a time (<see cref="PickupBodies.ReadAttachmentsEach"/>), so one bad attachment is
/// one failed message; so is one that is not a DIDComm message in encrypted form (#100 F-2).
/// </summary>
internal static class DeliveryReader
{
    /// <summary>The <c>status</c> type the answers are matched against.</summary>
    internal static readonly MessageTypeUri StatusUri = MessageTypeUri.Parse(MessagePickup.StatusType);

    /// <summary>The <c>delivery</c> type the answers are matched against.</summary>
    internal static readonly MessageTypeUri DeliveryUri = MessageTypeUri.Parse(MessagePickup.DeliveryType);

    /// <summary><c>status-request</c> → <c>status</c>.</summary>
    internal static readonly MediatorExchangeSpec StatusRequestSpec = new("status-request", new[] { StatusUri }, ExpectsDelivery: false);

    /// <summary><c>delivery-request</c> → <c>delivery</c>, or <c>status</c> when nothing is deliverable.</summary>
    internal static readonly MediatorExchangeSpec DeliveryRequestSpec = new("delivery-request", new[] { DeliveryUri, StatusUri }, ExpectsDelivery: true);

    /// <summary><c>messages-received</c> → <c>status</c>.</summary>
    internal static readonly MediatorExchangeSpec MessagesReceivedSpec = new("messages-received", new[] { StatusUri }, ExpectsDelivery: false);

    /// <summary><c>live-delivery-change</c> → <c>status</c> (or a problem report, which the exchange throws).</summary>
    internal static readonly MediatorExchangeSpec LiveDeliveryChangeSpec = new("live-delivery-change", new[] { StatusUri }, ExpectsDelivery: false);

    /// <summary>The message of an answer, as a fresh instance the caller may read.</summary>
    /// <param name="answer">The answer's snapshot.</param>
    /// <param name="requestId">Our request's id.</param>
    /// <param name="spec">The request's spec (its name goes into the error text).</param>
    /// <exception cref="MediationProtocolException">The answer cannot be read back as a message.</exception>
    internal static Message Open(InboundMessageSnapshot answer, string requestId, MediatorExchangeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(answer);
        try
        {
            return answer.DeserializeMessage();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // The cause is not attached: a serializer's text can quote the value it choked on.
            throw new MediationProtocolException($"The mediator's answer to request '{requestId}' ({spec.Name}) could not be read.", requestId);
        }
    }

    /// <summary>Whether <paramref name="message"/> is a Message Pickup 3.x <c>status</c>.</summary>
    /// <param name="message">The answer.</param>
    internal static bool IsStatus(Message message) => WireJson.IsType(message, StatusUri);

    /// <summary>Whether <paramref name="message"/> is a Message Pickup 3.x <c>delivery</c>.</summary>
    /// <param name="message">The answer.</param>
    internal static bool IsDelivery(Message message) => WireJson.IsType(message, DeliveryUri);

    /// <summary>
    /// Read a 3.0 <c>status</c> answer. With <paramref name="checkFilter"/>, its <c>recipient_did</c>
    /// is checked against the DID asked about (<see cref="RequireStatusFilterEcho"/>).
    /// </summary>
    /// <param name="answer">The answer.</param>
    /// <param name="requestId">Our request's id.</param>
    /// <param name="spec">The request's spec.</param>
    /// <param name="checkFilter">Whether the request carried a <c>recipient_did</c> filter (or could have).</param>
    /// <param name="filter">The filter the request carried, or <c>null</c>.</param>
    /// <exception cref="MediationProtocolException">Not a status, a malformed status, a status about another DID, or one that does not repeat the DID asked about.</exception>
    internal static PickupStatus ReadStatus(Message answer, string requestId, MediatorExchangeSpec spec, bool checkFilter, string? filter)
    {
        PickupStatus? status;
        try
        {
            if (!MessagePickup.TryParseStatus(answer, out status))
                throw Unexpected(requestId, spec);
        }
        catch (MalformedMessageException ex)
        {
            throw new MediationProtocolException($"The mediator answered request '{requestId}' ({spec.Name}) with a malformed status.", requestId, innerException: ex);
        }
        if (checkFilter)
            RequireStatusFilterEcho(status.RecipientDid, filter, requestId, spec);
        return status;
    }

    /// <summary>
    /// Check the <c>recipient_did</c> of a <c>status</c> against the DID its request asked about
    /// (#100 F-1): when the request named one (<paramref name="filter"/>), the status MUST repeat it
    /// (3.0 L116, 4.0 L168), so an absent echo is refused as well as one about another DID; when it
    /// named none, a status about one DID does not answer it. Compared as DID subjects; both
    /// conditions are evaluated, without short circuit.
    /// </summary>
    /// <param name="echo">The status's <c>recipient_did</c>, or <c>null</c>.</param>
    /// <param name="filter">The filter the request carried, or <c>null</c>.</param>
    /// <param name="requestId">Our request's id.</param>
    /// <param name="spec">The request's spec.</param>
    /// <exception cref="MediationProtocolException">The echo is absent while a DID was asked about, or names another DID.</exception>
    internal static void RequireStatusFilterEcho(string? echo, string? filter, string requestId, MediatorExchangeSpec spec)
    {
        var missing = echo is null & filter is not null;
        var another = echo is not null & !DidSubject.SameDidSubject(echo, filter);
        if (missing)
            throw new MediationProtocolException($"The mediator answered request '{requestId}' ({spec.Name}) with a status that does not repeat the recipient_did asked about.", requestId);
        if (another)
            throw AboutAnotherDid(requestId, spec);
    }

    /// <summary>
    /// Check the <c>recipient_did</c> a delivery repeats: absent, or the DID asked about
    /// (<paramref name="filter"/>). Read from the body, so a malformed member is refused too. Absent
    /// is allowed: 3.0 L174 and 4.0 L265 say when the member may appear, not that it must.
    /// </summary>
    /// <param name="delivery">The delivery.</param>
    /// <param name="requestId">Our request's id.</param>
    /// <param name="spec">The request's spec.</param>
    /// <param name="filter">The filter the request carried, or <c>null</c>.</param>
    /// <exception cref="MediationProtocolException">A malformed <c>recipient_did</c>, or one about another DID.</exception>
    internal static void RequireDeliveryFilterEcho(Message delivery, string requestId, MediatorExchangeSpec spec, string? filter)
    {
        string? echo;
        try
        {
            echo = PickupBodies.ReadRecipientDid(WireJson.ToElement(delivery.Body), "delivery");
        }
        catch (MalformedMessageException ex)
        {
            throw new MediationProtocolException($"The mediator answered request '{requestId}' ({spec.Name}) with a malformed delivery.", requestId, innerException: ex);
        }
        RequireFilterEcho(echo, filter, requestId, spec);
    }

    /// <summary>
    /// The messages of a delivery that can be handed out: of the first <paramref name="max"/>
    /// attachments (the rest are not read, L-055), those whose id is usable, each id once (the first
    /// wins). An entry carries the decoded bytes, or why it cannot be unpacked: its data did not
    /// decode, or it is not a DIDComm message in encrypted form (<see cref="IsEncryptedForm"/>).
    /// </summary>
    /// <param name="attachments">The delivery's attachments.</param>
    /// <param name="max">How many attachments to read, from the first.</param>
    internal static DeliveryContents ReadEntries(IList<Attachment>? attachments, int max)
    {
        var readings = PickupBodies.ReadAttachmentsEach(attachments, max, "delivery");
        var total = attachments?.Count ?? 0;
        var entries = new List<DeliveryEntry>(readings.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var unusable = 0;
        var repeated = 0;
        foreach (var reading in readings)
        {
            if (reading.Id is null)
            {
                // Without a usable id it cannot be acknowledged, so it is not handed out either.
                unusable++;
                continue;
            }
            if (!ids.Add(reading.Id))
            {
                repeated++;
                continue;
            }
            // #100 F-2 (3.0 L172, 4.0 L261): the only valid attachment is a DIDComm message in
            // encrypted form. Judged on the attachment as delivered, before anything unpacks it: one
            // that is not a JWE is a failed item, never handed to the unpack, never acknowledged.
            if (reading.Payload is { } payload && !IsEncryptedForm(payload))
            {
                entries.Add(new DeliveryEntry(reading.Id, Payload: null, NotEncrypted()));
                continue;
            }
            entries.Add(new DeliveryEntry(reading.Id, reading.Payload, reading.Error));
        }
        return new DeliveryContents(entries, total, total - readings.Count, unusable, repeated);
    }

    /// <summary>The refusal for an answer whose type the request does not expect.</summary>
    /// <param name="requestId">Our request's id.</param>
    /// <param name="spec">The request's spec.</param>
    internal static MediationProtocolException Unexpected(string requestId, MediatorExchangeSpec spec)
        => new($"The mediator answered request '{requestId}' ({spec.Name}) with a message of another type.", requestId);

    /// <summary>
    /// The refusal for a delivery that carried messages of which none can be handed out (every one
    /// read lacked a usable id; <see cref="DeliveryContents.Entries"/> is empty while
    /// <see cref="DeliveryContents.AttachmentCount"/> is not). Nothing in it can be acknowledged, so
    /// a mediator that keeps answering this way would keep a fetch-until-drained loop, or a live
    /// stream's drain, spinning.
    /// </summary>
    /// <param name="requestId">Our request's id.</param>
    /// <param name="spec">The request's spec.</param>
    internal static MediationProtocolException NoUsableIds(string requestId, MediatorExchangeSpec spec)
        => new($"The mediator answered request '{requestId}' ({spec.Name}) with a delivery in which no message has a usable id; nothing in it could be acknowledged.", requestId);

    /// <summary>
    /// Whether <paramref name="payload"/>, a delivered attachment's bytes, is a DIDComm message in
    /// encrypted form: a JSON object the envelope detector the unpack itself uses classifies as a JWE
    /// (it has a <c>ciphertext</c> member). Only the outermost form is judged, as delivered: a
    /// message a mediator re-wrapped for this agent is an anoncrypted <c>forward</c>, a JWE too,
    /// whatever it carries inside. Bytes that are not UTF-8 JSON, a JSON value that is not an
    /// object, a plaintext message and a JWS are not. The parse is bounded by the delivery's own size.
    /// </summary>
    /// <param name="payload">The decoded attachment data.</param>
    internal static bool IsEncryptedForm(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, DidCommJson.StrictDocument);
            return EnvelopeDetector.Detect(document.RootElement) == EnvelopeKind.Encrypted;
        }
        catch (Exception ex) when (ex is JsonException or MalformedMessageException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // A delivered attachment that is not a DIDComm message in encrypted form (F-2): the text quotes
    // no wire value.
    private static MalformedMessageException NotEncrypted()
        => new("delivery attachment is not a DIDComm message in encrypted form, the only valid type of attachment (Message Pickup 3.0 and 4.0); it was not unpacked and is not acknowledged.");

    private static void RequireFilterEcho(string? echo, string? filter, string requestId, MediatorExchangeSpec spec)
    {
        // Both values evaluated, no short circuit: present, and not the DID asked about.
        var mismatch = echo is not null & !DidSubject.SameDidSubject(echo, filter);
        if (mismatch)
            throw AboutAnotherDid(requestId, spec);
    }

    private static MediationProtocolException AboutAnotherDid(string requestId, MediatorExchangeSpec spec)
        => new($"The mediator answered request '{requestId}' ({spec.Name}) about another DID than the one asked about.", requestId);
}

/// <summary>What <see cref="DeliveryReader.ReadEntries"/> found in a delivery.</summary>
/// <param name="Entries">The messages to hand out, in delivery order.</param>
/// <param name="AttachmentCount">How many attachments the delivery carried.</param>
/// <param name="Excess">How many were beyond the bound and not read.</param>
/// <param name="Unusable">How many of those read had no usable id.</param>
/// <param name="RepeatedIds">How many of those read repeated an earlier attachment's id.</param>
internal sealed record DeliveryContents(IReadOnlyList<DeliveryEntry> Entries, int AttachmentCount, int Excess, int Unusable, int RepeatedIds);

/// <summary>One delivered message to unpack: its attachment id and its bytes, or why they did not decode.</summary>
/// <param name="Id">The attachment id (usable: it can be acknowledged).</param>
/// <param name="Payload">The decoded bytes; <c>null</c> when they did not decode.</param>
/// <param name="Error">Why the bytes did not decode; <c>null</c> when <paramref name="Payload"/> is set.</param>
internal readonly record struct DeliveryEntry(string Id, byte[]? Payload, Exception? Error);
