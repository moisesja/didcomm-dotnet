using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupWireSupport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// QA-1 for #100 (Message Pickup 4.0), the wire: the page's DIDComm v2 examples, taken verbatim from
/// the 4.0 page (<c>decentralized-identity/didcomm.org@a26926c</c>, L110-L120, L145-L160, L193-L204,
/// L237-L253, L281-L290, L329-L338, L359-L370) and changed only as
/// <see cref="MessagePickup40WireFixtureTests"/> records, are the checked-in fixtures, and what a
/// parser reads from each is what the builder writes back; <c>message_count_limit</c> is read by
/// exactly 3.0's rules for <c>limit</c> (#100 R-5); a 4.0 <c>status</c>'s times are UTC epoch
/// milliseconds read as exact integers (L39, L164) and floored to seconds for the public record
/// (DC-2); and the mediator role never writes a negative time.
/// </summary>
public sealed class MessagePickup40Qa1WireTests
{
    private const string P4 = "https://didcomm.org/message-pickup/4.0";
    private const string P3 = "https://didcomm.org/messagepickup/3.0";
    private const string From = "did:example:from";
    private const string To = "did:example:to";

    // ---- 1. the page's examples are the fixtures, with only the recorded changes ----------------

    /// <summary>The DIDComm v2 examples of the 4.0 page, character for character (indentation aside).</summary>
    private static readonly Dictionary<string, string> PageExamples = new(StringComparer.Ordinal)
    {
        ["status-request"] = """
            {
                "id": "123456780",
                "type": "https://didcomm.org/message-pickup/4.0/status-request",
                "body" : {
                    "recipient_did": "<did for messages>"
                },
                "return_route": "all"
            }
            """,
        ["status"] = """
            {
                "id": "123456780",
                "thid": "<message id of status-request message>",
                "type": "https://didcomm.org/message-pickup/4.0/status",
                "body": {
                    "recipient_did": "<did for messages>",
                    "message_count": 7,
                    "newest_received_time": 1739420343823,
                    "oldest_received_time": 1739420343823,
                    "total_bytes": 8096,
                    "live_delivery": false
                }
            }
            """,
        ["delivery-request"] = """
            {
                "id": "123456780",
                "type": "https://didcomm.org/message-pickup/4.0/delivery-request",
                "body": {
                    "message_count_limit": 10,
                    "recipient_did": "<did for messages>"
                },
                "return_route": "all"
            }
            """,
        ["delivery"] = """
            {
                "id": "123456780",
                "thid": "<message id of delivery-request message>",
                "type": "https://didcomm.org/message-pickup/4.0/delivery",
                "body": {
                    "recipient_did": "<did for messages>"
                },
                "attachments": [{
                    "id": "<id of message>",
                    "data": {
                        "base64": "<message>"
                    }
                }]
            }
            """,
        ["messages-received"] = """
            {
                "id": "123456780",
                "type": "https://didcomm.org/message-pickup/4.0/messages-received",
                "body": {
                    "message_id_list": ["123","456"]
                }
            }
            """,
        ["live-delivery-change"] = """
            {
                "id": "123456780",
                "type": "https://didcomm.org/message-pickup/4.0/live-delivery-change",
                "body": {
                    "live_delivery": true
                }
            }
            """,
        ["problem-report"] = """
            {
                "id": "123456780",
                "type": "https://didcomm.org/message-pickup/4.0/problem-report",
                "pthid": "<the value is the thid of the thread in which the problem occurred>",
                "body": {
                    "code": "e.m.live-mode-not-supported",
                    "comment": "Connection does not support Live Delivery"
                }
            }
            """,
    };

    public static TheoryData<string> PageNames => new() { "status-request", "status", "delivery-request", "delivery", "messages-received", "live-delivery-change", "problem-report" };

    [Theory]
    [MemberData(nameof(PageNames))]
    public void The_pages_example_with_only_the_recorded_changes_is_the_fixture_byte_for_byte(string name)
    {
        // The recorded changes (MessagePickup40WireFixtureTests' summary), and nothing else: the
        // placeholders replaced, typ added, written compactly in the serializer's member order.
        var normalized = Normalize(PageExamples[name]);

        var written = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<Message>(normalized, DidCommJson.Default)!, DidCommJson.Default);

        Encoding.UTF8.GetString(written).Should().Be(Encoding.UTF8.GetString(Fixture(name)),
            "the fixture differs from the page's example only by the normalizations its test records");
    }

    [Theory]
    [MemberData(nameof(PageNames))]
    public void What_a_4_0_parser_reads_from_the_pages_example_the_builder_writes_back_as_the_fixture(string name)
    {
        // The page's example (normalized) → its 4.0 parser → the values → the 4.0 builder: every member
        // the page shows survives the round, so no reader drops or renames one (from, to and
        // return_route aside: the page has no from or to, and return_route is the sender's header).
        var page = JsonSerializer.Deserialize<Message>(Normalize(PageExamples[name]), DidCommJson.Default)!;
        var ids = new MessagePickupWireFixedIds(page.Id);

        Message rebuilt;
        switch (name)
        {
            case "status-request":
                MessagePickup40.TryParseStatusRequest(page, out var statusFilter).Should().BeTrue();
                rebuilt = MessagePickup40.CreateStatusRequest(From, To, statusFilter, ids);
                break;
            case "status":
                MessagePickup40.TryParseStatus(page, out var status).Should().BeTrue();
                rebuilt = MessagePickup40.CreateStatus(From, To, status!, page.Thid!, ids);
                break;
            case "delivery-request":
                MessagePickup40.TryParseDeliveryRequest(page, out var limit, out var deliveryFilter).Should().BeTrue();
                rebuilt = MessagePickup40.CreateDeliveryRequest(From, To, limit, deliveryFilter, ids);
                break;
            case "delivery":
                MessagePickup40.TryParseDelivery(page, out var messages, out var echoed).Should().BeTrue();
                rebuilt = MessagePickup40.CreateDelivery(From, To, messages!, page.Thid, echoed, ids);
                break;
            case "messages-received":
                MessagePickup40.TryParseMessagesReceived(page, 10, out var acknowledged).Should().BeTrue();
                rebuilt = MessagePickup40.CreateMessagesReceived(From, To, acknowledged!, ids);
                break;
            case "live-delivery-change":
                MessagePickup40.TryParseLiveDeliveryChange(page, out var live).Should().BeTrue();
                rebuilt = MessagePickup40.CreateLiveDeliveryChange(From, To, live, ids);
                break;
            case "problem-report":
                MessagePickup40.IsProblemReport(page).Should().BeTrue();
                page.Body!["code"]!.GetValue<string>().Should().Be(MessagePickup.LiveModeNotSupportedCode);
                page.Body!["comment"]!.GetValue<string>().Should().Be(MessagePickup.LiveModeNotSupportedComment);
                rebuilt = MessagePickup40.CreateLiveModeNotSupported(From, To, page.Pthid!, ids);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }

        var written = JsonNode.Parse(Wire(rebuilt))!.AsObject();
        written.Remove("from");
        written.Remove("to");
        var fixture = JsonNode.Parse(Fixture(name))!.AsObject();
        fixture.Remove("return_route");
        written.ToJsonString().Should().Be(fixture.ToJsonString());
    }

    // ---- 3. message_count_limit is 3.0's limit under another name (R-5) --------------------------

    /// <summary>Every literal of 3.0's limit tables (<see cref="MessagePickupWireParserTests"/>), accepted and refused alike.</summary>
    public static TheoryData<string> LimitLiterals
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var literal in new[]
            {
                "0", "-0", "0.0", "0e5", "1", "1.0", "10", "10.0", "10.000", "1e1", "1E1", "1e+1", "100e-1", "0.1e2",
                "2147483647", "2147483647.0", "2147483648", "2147483648.0", "99999999999", "9223372036854775807",
                "9223372036854775808", "100000000000000000000000000000", "1e400", "1e99999999999999999999",
                "-1", "-10", "-2147483648", "-2147483649", "-9223372036854775809", "-1e400", "-0.5", "-1.0", "-1e0",
                "\"10\"", "\"SENTINEL\"", "\"\"",
                "1.5", "10.5", "0.1", "1e-3", "15e-1", "1e-400", "1.00000000000000000000000000001", "2147483647.5",
                "true", "false", "null", "[10]", "[]", "{}", "{\"value\":10}",
            })
            {
                data.Add(literal);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(LimitLiterals))]
    public void Message_count_limit_reads_every_literal_exactly_as_3_0_reads_limit(string literal)
    {
        var three = Read(() => (MessagePickup.TryParseDeliveryRequest(Raw(P3 + "/delivery-request", $$"""{"limit":{{literal}}}"""), out var limit, out _), limit));
        var four = Read(() => (MessagePickup40.TryParseDeliveryRequest(Raw(P4 + "/delivery-request", $$"""{"message_count_limit":{{literal}}}"""), out var limit, out _), limit));

        three.Claimed.Should().BeTrue("precondition: the 3.0 parser claims its own type");
        four.Claimed.Should().BeTrue("precondition: the 4.0 parser claims its own type");
        four.Value.Should().Be(three.Value, "an accepted literal reads as the same number in both versions");
        four.Error.Should().Be(three.Error?.Replace("'limit'", "'message_count_limit'", StringComparison.Ordinal),
            "a refused literal is refused for the same rule, naming 4.0's member");
        (four.Value is null).Should().Be(four.Error is not null, "precondition: each literal is either read or refused");
        if (literal == "1.0")
            four.Value.Should().Be(1, "an integral fraction is an integer: JSON does not tell 1 from 1.0");
    }

    // ---- 4. the times of a 4.0 status: milliseconds, exact, floored for the public record --------

    public static TheoryData<string, string, long, long> AcceptedTimes
    {
        get
        {
            var data = new TheoryData<string, string, long, long>();
            foreach (var member in new[] { "newest_received_time", "oldest_received_time" })
            {
                data.Add(member, "0", 0, 0);                                                          // the epoch itself
                data.Add(member, "-0", 0, 0);
                data.Add(member, "999", 999, 0);                                                      // floored, not rounded
                data.Add(member, "1000", 1000, 1);
                data.Add(member, "1739420343823", 1739420343823, 1739420343);                         // the page's value
                data.Add(member, "1739420343823.0", 1739420343823, 1739420343);                       // an integral fraction
                data.Add(member, "9007199254740993", 9007199254740993, 9007199254740);                // 2^53 + 1: no double holds it
                data.Add(member, "9223372036854775807", long.MaxValue, long.MaxValue / 1000);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AcceptedTimes))]
    public void A_status_time_is_read_as_an_exact_integer_of_milliseconds_and_floored_to_seconds(string member, string literal, long milliseconds, long seconds)
    {
        var message = Raw(P4 + "/status", $$"""{"message_count":1,"{{member}}":{{literal}}}""");

        MessagePickup40.TryParseStatus(message, out var status).Should().BeTrue();
        var read = member == "newest_received_time" ? status!.NewestReceivedTimeMs : status!.OldestReceivedTimeMs;
        var other = member == "newest_received_time" ? status.OldestReceivedTimeMs : status.NewestReceivedTimeMs;
        var publicStatus = status.ToPickupStatus();
        var publicRead = member == "newest_received_time" ? publicStatus.NewestReceivedTime : publicStatus.OldestReceivedTime;

        read.Should().Be(milliseconds);
        other.Should().BeNull("each time is read from its own member");
        publicRead.Should().Be(seconds, "the public record gives whole seconds, rounded down (DC-2)");
    }

    public static TheoryData<string, string> RefusedTimes
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var member in new[] { "newest_received_time", "oldest_received_time" })
            {
                foreach (var literal in new[] { "-1", "-1739420343823", "-1e400", "1739420343823.5", "0.5", "\"1739420343823\"", "true", "[1]", "{}" })
                    data.Add(member, literal);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RefusedTimes))]
    public void A_status_time_that_is_negative_a_fraction_or_not_a_number_is_malformed_naming_the_member(string member, string literal)
    {
        var message = Raw(P4 + "/status", $$"""{"message_count":1,"{{member}}":{{literal}}}""");

        var refused = Refused(() => MessagePickup40.TryParseStatus(message, out _));

        refused.Message.Should().StartWith("status '" + member + "' must ");
        refused.Message.Should().NotContain("1739420343823");
    }

    [Fact]
    public void A_time_beyond_2_to_the_53_is_written_digit_for_digit_and_read_back_unchanged()
    {
        const long beyondDoubles = 9007199254740993; // 2^53 + 1
        var status = MessagePickup40.CreateStatus(From, To, new PickupStatus40(1, null, beyondDoubles, beyondDoubles - 2), "thread-1");

        BodyOf(status).Should().Be("""{"message_count":1,"newest_received_time":9007199254740993,"oldest_received_time":9007199254740991}""");
        MessagePickup40.TryParseStatus(RoundTrip(status), out var back).Should().BeTrue();
        back!.NewestReceivedTimeMs.Should().Be(beyondDoubles);
        back.OldestReceivedTimeMs.Should().Be(beyondDoubles - 2);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static readonly string Data30 = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "messagepickup-3.0", "delivery.json")))!["attachments"]![0]!["data"]!["base64"]!.GetValue<string>();

    // The fixture test's recorded normalizations, one by one.
    private static string Normalize(string page)
    {
        var text = page
            .Replace("<did for messages>", "did:example:alice", StringComparison.Ordinal)
            .Replace("<message id of status-request message>", "123456780", StringComparison.Ordinal)
            .Replace("<message id of delivery-request message>", "123456780", StringComparison.Ordinal)
            .Replace("<id of message>", "123", StringComparison.Ordinal)
            .Replace("<message>", Data30, StringComparison.Ordinal)
            .Replace("<the value is the thid of the thread in which the problem occurred>", "123456780", StringComparison.Ordinal);
        var node = JsonNode.Parse(text)!.AsObject();
        node["typ"] = MediaTypes.Plaintext;
        return node.ToJsonString();
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "message-pickup-4.0", name + ".json"));

    // The operation alone is captured; every assertion is made by the caller, outside (L-067).
    private static (bool Claimed, int? Value, string? Error) Read(Func<(bool Claimed, int Limit)> parse)
    {
        try
        {
            var (claimed, limit) = parse();
            return (claimed, limit, null);
        }
        catch (MalformedMessageException ex)
        {
            return (true, null, ex.Message);
        }
    }
}
