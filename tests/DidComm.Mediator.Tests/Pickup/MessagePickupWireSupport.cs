using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using DidComm.Messages;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Shared pieces of the Message Pickup wire-format suites (the <c>MessagePickupWire…</c> files:
/// fixtures, builders, parsers, the <c>message_id_list</c> bound, the 64-bit integer reader).
/// Every hostile value below carries the word <see cref="Marker"/>, and <see cref="Refused"/> /
/// <see cref="ArgumentRefused"/> assert that no error text repeats it.
/// </summary>
internal static class MessagePickupWireSupport
{
    public const string P = MessagePickup.ProtocolUri;
    public const string Marker = "SENTINEL";
    public const string AliceDid = "did:example:alice";
    public const string MediatorDid = "did:example:mediator";

    /// <summary>The FR-API-09 bound for a DID, a <c>message_id_list</c> entry and an attachment id.</summary>
    public const int Cap = 2048;

    private const string MarkedDid = "did:example:" + Marker;

    /// <summary>A DID of exactly 2048 characters: the longest one accepted.</summary>
    public static readonly string DidAtCap = "did:example:" + new string('a', Cap - "did:example:".Length);

    /// <summary>A DID of exactly 2049 characters: the shortest one refused.</summary>
    public static readonly string DidOverCap = MarkedDid + new string('a', Cap + 1 - MarkedDid.Length);

    /// <summary>An id of exactly 2048 unreserved characters.</summary>
    public static readonly string IdAtCap = new('i', Cap);

    /// <summary>An id of exactly 2049 unreserved characters.</summary>
    public static readonly string IdOverCap = Marker + new string('i', Cap + 1 - Marker.Length);

    /// <summary>A value just under 1 MiB: what a hostile peer can fit in one default-sized message.</summary>
    public static readonly string NearMegabyte = Marker + new string('x', 1024 * 1024 - 64);

    /// <summary>
    /// The six JSON characters backslash-u-d-8-0-0: an escape for half a surrogate pair. JSON text
    /// that holds it parses, and fails only when the string is read back. Assembled from two pieces
    /// so that no tool between the keyboard and this file can turn it into something else (L-049).
    /// </summary>
    public static readonly string LoneSurrogateEscape = "\\" + "ud800";

    /// <summary>One UTF-16 code unit, U+D800 without its partner: a .NET string no JSON writer accepts.</summary>
    public static readonly string LoneSurrogate = ((char)0xD800).ToString();

    /// <summary>The six message names of the protocol.</summary>
    public static readonly string[] MessageNames =
    {
        "status-request", "status", "delivery-request", "delivery", "messages-received", "live-delivery-change",
    };

    /// <summary>The smallest body each message's parser accepts.</summary>
    public static string? ValidBody(string name) => name switch
    {
        "status-request" => "{}",
        "status" => """{"message_count":0}""",
        "delivery-request" => """{"limit":1}""",
        "delivery" => "{}",
        "messages-received" => """{"message_id_list":[]}""",
        "live-delivery-change" => """{"live_delivery":false}""",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Run the parser that owns <paramref name="name"/> over <paramref name="message"/>.</summary>
    public static bool Parse(string name, Message message) => name switch
    {
        "status-request" => MessagePickup.TryParseStatusRequest(message, out _),
        "status" => MessagePickup.TryParseStatus(message, out _),
        "delivery-request" => MessagePickup.TryParseDeliveryRequest(message, out _, out _),
        "delivery" => MessagePickup.TryParseDelivery(message, out _, out _),
        "messages-received" => MessagePickup.TryParseMessagesReceived(message, out _),
        "live-delivery-change" => MessagePickup.TryParseLiveDeliveryChange(message, out _),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// A message of <paramref name="type"/> whose body and attachments are the JSON text given (or
    /// absent), read the way the receive path reads a plaintext: one strict deserialization.
    /// </summary>
    public static Message Raw(string type, string? body, string? attachments = null)
    {
        var json = new StringBuilder();
        json.Append("{\"id\":\"m-1\",\"type\":").Append(JsonSerializer.Serialize(type));
        json.Append(",\"from\":\"").Append(MarkedDid).Append("\",\"to\":[\"").Append(MediatorDid).Append("\"]");
        if (body is not null)
            json.Append(",\"body\":").Append(body);
        if (attachments is not null)
            json.Append(",\"attachments\":").Append(attachments);
        json.Append('}');
        return JsonSerializer.Deserialize<Message>(json.ToString(), DidCommJson.Default)!;
    }

    /// <summary>
    /// A message whose body is <paramref name="body"/> parsed lazily, as <c>JsonNode.Parse</c> hands
    /// it over: nothing in it has been decoded yet. This is how a host that builds a
    /// <see cref="Message"/> from its own JSON can present text the receive path would have refused
    /// at unpack (an unpaired surrogate escape, a member named twice).
    /// </summary>
    public static Message Lazy(string type, string body)
        => InCode(type, JsonNode.Parse(body)!.AsObject());

    /// <summary>A message whose body is a <see cref="JsonObject"/> assembled in code.</summary>
    public static Message InCode(string type, JsonObject body)
        => new MessageBuilder().WithId("m-1").WithType(type).WithFrom(MarkedDid).WithTo(MediatorDid).WithBody(body).Build();

    /// <summary>
    /// Run <paramref name="act"/> and require a <see cref="MalformedMessageException"/> that quotes
    /// nothing: no <see cref="Marker"/> in its text and no inner exception that could carry what
    /// the outer text withholds.
    /// </summary>
    public static MalformedMessageException Refused(Action act)
    {
        Exception? caught = null;
        try
        {
            act();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("the message is malformed");
        caught.Should().BeOfType<MalformedMessageException>("a malformed message is never a raw JSON, format or CLR exception");
        caught!.Message.Should().NotContain(Marker, "an error never quotes a value from the message");
        caught.Message.Length.Should().BeLessThan(200, "an error names the member and the rule, whatever the size of the value");
        caught.InnerException.Should().BeNull("an inner exception could carry the value the outer text withholds");
        return (MalformedMessageException)caught;
    }

    /// <summary>
    /// Run <paramref name="build"/> and require an <see cref="ArgumentException"/> (or one of its
    /// subclasses) whose text does not repeat the argument.
    /// </summary>
    public static ArgumentException ArgumentRefused(Func<object?> build, string because = "")
    {
        Exception? caught = null;
        try
        {
            build();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("the argument is not usable: {0}", because);
        caught.Should().BeAssignableTo<ArgumentException>("a builder refuses a bad argument as an argument error: {0}", because);
        caught!.Message.Should().NotContain(Marker, "a refusal never quotes the argument: {0}", because);
        caught.Message.Length.Should().BeLessThan(200, "a refusal names the rule, whatever the size of the argument");
        caught.InnerException.Should().BeNull();
        return (ArgumentException)caught;
    }

    public static string Wire(Message message) => JsonSerializer.Serialize(message, DidCommJson.Default);

    public static Message RoundTrip(Message message) => JsonSerializer.Deserialize<Message>(Wire(message), DidCommJson.Default)!;

    /// <summary>The <c>body</c> member of the serialized message, as JSON text.</summary>
    public static string BodyOf(Message message) => JsonNode.Parse(Wire(message))!["body"]!.ToJsonString();

    /// <summary>The <c>attachments</c> member of the serialized message, as JSON text; <c>null</c> when the message has none.</summary>
    public static string? AttachmentsOf(Message message) => JsonNode.Parse(Wire(message))!["attachments"]?.ToJsonString();
}

/// <summary>An id generator that hands out one fixed id and counts how often it is asked.</summary>
internal sealed class MessagePickupWireFixedIds(string id) : IMessageIdGenerator
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public string NewId()
    {
        Interlocked.Increment(ref _calls);
        return id;
    }
}
