using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Mediator.Internal;
using DidComm.Messages;

namespace DidComm.Mediator.Pickup;

/// <summary>The unit of the <c>…_received_time</c> members of a <c>status</c>: seconds in Message Pickup 3.0, milliseconds in 4.0.</summary>
internal enum PickupTimeUnit
{
    /// <summary>UTC epoch seconds (3.0).</summary>
    Seconds,

    /// <summary>UTC epoch milliseconds (4.0).</summary>
    Milliseconds,
}

/// <summary>
/// Reads and writes the message bodies and attachments of Message Pickup, independent of the
/// protocol version: 3.0 and 4.0 share the member shapes (<c>recipient_did</c>,
/// <c>message_count</c>, <c>message_id_list</c>, <c>live_delivery</c>, the attachment descriptor)
/// and differ in message names, in the name of the batch limit (<c>limit</c> /
/// <c>message_count_limit</c>, passed in by the caller) and in the time unit.
/// </summary>
/// <remarks>
/// Every reader throws <see cref="MalformedMessageException"/> for a wrong shape and never quotes a
/// wire value: the text names the member and the rule. A DID or id member is checked against the
/// FR-API-09 bound (<see cref="MaxIdLength"/> UTF-16 code units, one length comparison) as soon as
/// it has been read as a string, before any DID parse or store lookup sees it. Unknown members are ignored, and an explicit <c>null</c> for an
/// optional member reads as absent.
/// </remarks>
internal static class PickupBodies
{
    internal const string RecipientDid = "recipient_did";
    internal const string MessageCount = "message_count";
    internal const string LongestWaitedSeconds = "longest_waited_seconds";
    internal const string NewestReceivedTime = "newest_received_time";
    internal const string OldestReceivedTime = "oldest_received_time";
    internal const string TotalBytes = "total_bytes";
    internal const string LiveDelivery = "live_delivery";
    internal const string MessageIdList = "message_id_list";

    /// <summary>The FR-API-09 bound for a <c>recipient_did</c>, a <c>message_id_list</c> entry and an attachment id.</summary>
    internal const int MaxIdLength = IdentifierLimits.MaxDidLength;

    // ---- writers --------------------------------------------------------------------------

    /// <summary><c>{}</c>, or <c>{"recipient_did": …}</c> when a filter is given.</summary>
    public static JsonObject WriteFilter(string? recipientDid)
    {
        var body = new JsonObject();
        if (recipientDid is not null)
            body[RecipientDid] = recipientDid;
        return body;
    }

    /// <summary>A <c>status</c> body, members in the order of the protocol page; <c>null</c> members are left out.</summary>
    public static JsonObject WriteStatus(PickupStatus status)
    {
        var body = new JsonObject();
        if (status.RecipientDid is not null)
            body[RecipientDid] = status.RecipientDid;
        body[MessageCount] = status.MessageCount;
        if (status.LongestWaitedSeconds is { } waited)
            body[LongestWaitedSeconds] = waited;
        if (status.NewestReceivedTime is { } newest)
            body[NewestReceivedTime] = newest;
        if (status.OldestReceivedTime is { } oldest)
            body[OldestReceivedTime] = oldest;
        if (status.TotalBytes is { } bytes)
            body[TotalBytes] = bytes;
        if (status.LiveDelivery is { } live)
            body[LiveDelivery] = live;
        return body;
    }

    /// <summary>A <c>delivery-request</c> body: the limit under <paramref name="limitMember"/>, then the optional filter.</summary>
    public static JsonObject WriteDeliveryRequest(string limitMember, int limit, string? recipientDid)
    {
        var body = new JsonObject { [limitMember] = limit };
        if (recipientDid is not null)
            body[RecipientDid] = recipientDid;
        return body;
    }

    /// <summary>A <c>messages-received</c> body.</summary>
    public static JsonObject WriteMessageIds(IReadOnlyList<string> messageIds)
    {
        var array = new JsonArray();
        foreach (var id in messageIds)
            array.Add(id);
        return new JsonObject { [MessageIdList] = array };
    }

    /// <summary>A <c>live-delivery-change</c> body.</summary>
    public static JsonObject WriteLiveDelivery(bool liveDelivery) => new() { [LiveDelivery] = liveDelivery };

    /// <summary>
    /// The attachment that carries one message: <c>{"id": …, "data": {"base64": …}}</c>. The data
    /// is base64url without padding, the encoding DIDComm v2.1 gives attachment data.
    /// </summary>
    public static Attachment ToAttachment(DeliveredMessage message) => new()
    {
        Id = message.Id,
        Data = new AttachmentData { Base64 = Base64Url.Encode(message.Payload.Span) },
    };

    // ---- readers --------------------------------------------------------------------------

    /// <summary>The optional <c>recipient_did</c> filter: <c>null</c> when absent (or the message has no body).</summary>
    public static string? ReadRecipientDid(JsonElement body, string message)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(RecipientDid, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return ReadId(value, RecipientDid, message);
    }

    /// <summary>A <c>status</c> body. <c>message_count</c> is REQUIRED; no member may be negative.</summary>
    public static PickupStatus ReadStatus(JsonElement body, string message)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(MessageCount, out var count))
            throw new MalformedMessageException($"{message} body is missing the REQUIRED '{MessageCount}' member.");
        var messageCount = WireJson.ReadInteger(count, MessageCount, message);
        if (messageCount < 0)
            throw NotNegative(MessageCount, message);

        bool? live = null;
        if (body.TryGetProperty(LiveDelivery, out var liveValue) && liveValue.ValueKind != JsonValueKind.Null)
            live = ReadBoolean(liveValue, LiveDelivery, message);

        return new PickupStatus(
            messageCount,
            ReadRecipientDid(body, message),
            ReadOptionalCount(body, LongestWaitedSeconds, message),
            ReadOptionalCount(body, NewestReceivedTime, message),
            ReadOptionalCount(body, OldestReceivedTime, message),
            ReadOptionalCount(body, TotalBytes, message),
            live);
    }

    /// <summary>
    /// The REQUIRED batch limit of a <c>delivery-request</c>, under <paramref name="limitMember"/>.
    /// An integer of any size is clamped to <see cref="int"/>; zero is valid (it asks for nothing);
    /// a negative value, a non-zero fraction or a string is malformed. Reading it does no work that
    /// depends on the number it spells (the text of a literal beyond 64 bits is copied once, within
    /// the size of the message that carried it).
    /// </summary>
    public static int ReadLimit(JsonElement body, string limitMember, string message)
    {
        var limit = WireJson.ReadRequiredInteger(body, limitMember, limitMember, "body", message);
        if (limit < 0)
            throw NotNegative(limitMember, message);
        return limit;
    }

    /// <summary>
    /// Refuse a <c>message_id_list</c> longer than <paramref name="maxIds"/> from the body as it
    /// arrived, before the body is re-read and before any entry is looked at: the entries are
    /// counted, never decoded. A body without a <c>message_id_list</c> array passes; the reader
    /// reports that.
    /// </summary>
    public static void RequireIdsWithin(JsonObject? body, int maxIds, string message)
    {
        if (body is null)
            return;

        int count;
        try
        {
            if (!body.TryGetPropertyValue(MessageIdList, out var node) || node is not JsonArray array)
                return;
            count = array.Count;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            // A body the node API cannot index (the same member name twice). Not a way round the
            // bound: ReadMessageIds applies it again from the array's length, before it allocates.
            return;
        }

        if (count > maxIds)
            throw TooManyIds(maxIds, message);
    }

    /// <summary>The REQUIRED <c>message_id_list</c>: an array of non-empty strings within the FR-API-09 bound.</summary>
    public static IReadOnlyList<string> ReadMessageIds(JsonElement body, string message, int maxIds = int.MaxValue)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(MessageIdList, out var array))
            throw new MalformedMessageException($"{message} body is missing the REQUIRED '{MessageIdList}' member.");
        if (array.ValueKind != JsonValueKind.Array)
            throw new MalformedMessageException($"{message} '{MessageIdList}' must be an array.");
        // The length is known without visiting the entries; nothing is allocated for a list that
        // is over the bound, and no entry of it is read.
        if (array.GetArrayLength() > maxIds)
            throw TooManyIds(maxIds, message);

        var ids = new List<string>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray())
            ids.Add(ReadId(entry, MessageIdList, message));
        return ids;
    }

    /// <summary>The REQUIRED boolean <c>live_delivery</c> of a <c>live-delivery-change</c>.</summary>
    public static bool ReadLiveDelivery(JsonElement body, string message)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(LiveDelivery, out var value))
            throw new MalformedMessageException($"{message} body is missing the REQUIRED '{LiveDelivery}' member.");
        return ReadBoolean(value, LiveDelivery, message);
    }

    /// <summary>
    /// The messages a <c>delivery</c> carries. Each attachment needs an <c>id</c> (a non-empty
    /// string within the FR-API-09 bound) and its data inline: <c>data.base64</c> (base64url or
    /// standard base64, padded or not) or <c>data.json</c>. The payload is decoded, never
    /// inspected: whether it is a DIDComm message is for the recipient's unpack to say, one
    /// attachment at a time. No attachments gives an empty list.
    /// </summary>
    public static IReadOnlyList<DeliveredMessage> ReadAttachments(IList<Attachment>? attachments, string message)
    {
        if (attachments is null || attachments.Count == 0)
            return Array.Empty<DeliveredMessage>();

        var messages = new List<DeliveredMessage>(attachments.Count);
        foreach (var attachment in attachments)
        {
            if (attachment is null)
                throw new MalformedMessageException($"{message} attachments must be JSON objects.");
            var id = attachment.Id;
            if (string.IsNullOrEmpty(id))
                throw new MalformedMessageException($"{message} attachment is missing the REQUIRED 'id' member.");
            if (id.Length > MaxIdLength)
                throw new MalformedMessageException($"{message} attachment 'id' exceeds the maximum length of {MaxIdLength} characters (FR-API-09).");
            messages.Add(new DeliveredMessage(id, ReadPayload(attachment.Data, message)));
        }
        return messages;
    }

    // ---- shared pieces --------------------------------------------------------------------

    /// <summary>Whether <paramref name="value"/> is acceptable where an API takes a DID or id to put on the wire: non-empty and within the FR-API-09 bound.</summary>
    public static bool IsWithinIdLimit(string? value) => !string.IsNullOrEmpty(value) && value.Length <= MaxIdLength;

    private static byte[] ReadPayload(AttachmentData? data, string message)
    {
        if (data?.Base64 is { } text)
        {
            // Present but empty is zero bytes: what the builder writes for an empty payload.
            if (text.Length == 0)
                return Array.Empty<byte>();
            try
            {
                // Lenient on receive: padding is tolerated, and so is the standard alphabet (the two
                // alphabets differ only in '+' '/' against '-' '_', so accepting both is unambiguous).
                if (text.AsSpan().IndexOfAny('+', '/') >= 0)
                    text = text.Replace('+', '-').Replace('/', '_');
                return Base64Url.DecodeRelaxed(text);
            }
            catch (FormatException)
            {
                // The cause is not attached: its text may quote the value.
                throw new MalformedMessageException($"{message} attachment 'data.base64' is not valid base64.");
            }
        }

        if (data?.Json is { } json)
        {
            try
            {
                return Encoding.UTF8.GetBytes(json.ToJsonString());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                throw new MalformedMessageException($"{message} attachment 'data.json' is not representable as JSON.");
            }
        }

        throw new MalformedMessageException($"{message} attachment is missing both 'data.base64' and 'data.json'.");
    }

    private static string ReadId(JsonElement value, string name, string message)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new MalformedMessageException($"{message} '{name}' must be a string.");
        var text = value.GetString();
        if (string.IsNullOrEmpty(text))
            throw new MalformedMessageException($"{message} '{name}' must not be empty.");
        // FR-API-09: one length comparison, before any DID parse or store lookup; never quoted.
        if (text.Length > MaxIdLength)
            throw new MalformedMessageException($"{message} '{name}' exceeds the maximum length of {MaxIdLength} characters (FR-API-09).");
        return text;
    }

    private static bool ReadBoolean(JsonElement value, string name, string message) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new MalformedMessageException($"{message} '{name}' must be a boolean."),
    };

    private static long? ReadOptionalCount(JsonElement body, string name, string message)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        var number = WireJson.ReadLong(value, name, message);
        if (number < 0)
            throw NotNegative(name, message);
        return number;
    }

    private static MalformedMessageException NotNegative(string name, string message)
        => new($"{message} '{name}' must not be negative.");

    private static MalformedMessageException TooManyIds(int maxIds, string message)
        => new($"{message} '{MessageIdList}' has more than {maxIds} entries.");
}
