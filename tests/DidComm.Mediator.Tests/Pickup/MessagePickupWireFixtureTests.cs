using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupWireSupport;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// FR-PICK-02..07 acceptance (#94): byte-exact round trips against the didcomm.org messagepickup 3.0
/// page examples. The fixtures in <c>fixtures/messagepickup-3.0/</c> are those examples normalized
/// to valid JSON in the library's canonical member order, the way #93 normalized the
/// coordinate-mediation examples: <c>typ</c> added, compact, no trailing newline, and every
/// placeholder replaced by a concrete value.
/// <list type="bullet">
/// <item><c>&lt;did for messages&gt;</c> → <c>did:example:alice</c>.</item>
/// <item><c>delivery</c>: <c>thid</c> (<c>&lt;message id of delivery-request message&gt;</c>) → <c>123456780</c>,
/// the id of the page's own <c>delivery-request</c> example; the attachment id
/// (<c>&lt;id of message&gt;</c>) → <c>123</c>, the first id the page's <c>messages-received</c> example
/// acknowledges; the data (<c>&lt;message&gt;</c>) → the base64url, without padding, of a JWE-shaped
/// text (<see cref="DeliveredPayload"/>; filler, not decryptable: a mediator never looks inside).</item>
/// <item><c>status-request</c>, <c>delivery-request</c> and <c>messages-received</c> keep the page's
/// <c>"return_route":"all"</c>; <c>live-delivery-change</c> has none because the page's example has
/// none (the page's prose still asks for it on every request).</item>
/// <item><c>status-minimal</c> is not on the page: a <c>status</c> whose body is only
/// <c>message_count</c>, the one REQUIRED member (the issue's acceptance names it).</item>
/// <item><c>live-mode-not-supported</c> is the page's problem report, its <c>pthid</c> placeholder
/// replaced by <c>123456780</c> (the thread of the page's <c>live-delivery-change</c> example).</item>
/// </list>
/// </summary>
public sealed class MessagePickupWireFixtureTests
{
    private const string ExampleId = "123456780";
    private const string ExampleDid = "did:example:alice";
    private const string From = "did:example:from";
    private const string To = "did:example:to";
    private const string StatusMinimal = "status-minimal";
    private const string LiveModeNotSupported = "live-mode-not-supported";

    /// <summary>
    /// What the <c>delivery</c> fixture's attachment decodes to. The '?' and the '~' in the kid sit
    /// where base64 turns them into its 63rd and 62nd characters ('/' and '+' in the standard
    /// alphabet, '_' and '-' in the URL-safe one), and the length (370 bytes) is not a multiple of
    /// three, so standard base64 would need padding.
    /// </summary>
    private const string DeliveredPayload =
        """{"protected":"eyJ0eXAiOiJhcHBsaWNhdGlvbi9kaWRjb21tLWVuY3J5cHRlZCtqc29uIiwiYWxnIjoiRUNESC1FUytBMjU2S1ciLCJlbmMiOiJYQzIwUCJ9","recipients":[{"header":{"kid":"did:example:alice?versionId=12#keys~1"},"encrypted_key":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"}],"iv":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYX","ciphertext":"ZGVmZ2hpamtsbW5vcHFyc3Q","tag":"yMnKy8zNzs_Q0dLT1NXW1w"}""";

    private static readonly string[] Names = MessageNames.Append(StatusMinimal).Append(LiveModeNotSupported).ToArray();

    public static TheoryData<string> Fixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names)
                data.Add(name);
            return data;
        }
    }

    public static TheoryData<string> PickupFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names.Where(n => n != LiveModeNotSupported))
                data.Add(name);
            return data;
        }
    }

    // ---- the files ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_round_trips_byte_exact(string name)
    {
        var bytes = ReadBytes(name);

        var message = JsonSerializer.Deserialize<Message>(bytes, DidCommJson.Default)!;
        var again = JsonSerializer.SerializeToUtf8Bytes(message, DidCommJson.Default);

        again.Should().Equal(bytes, "deserialize → serialize must reproduce the fixture byte for byte");
        message.Id.Should().Be(ExampleId);
        message.Type.Should().Be(TypeOf(name));
        message.Typ.Should().Be(MediaTypes.Plaintext);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_is_compact_json_without_a_trailing_newline(string name)
    {
        var bytes = ReadBytes(name);

        bytes[0].Should().Be((byte)'{', "no byte-order mark, no leading whitespace");
        bytes[^1].Should().Be((byte)'}', "no trailing newline");
        bytes.Should().NotContain((byte)'\n').And.NotContain((byte)'\r').And.NotContain((byte)'\t');
        bytes.Should().OnlyContain(b => b >= 0x20 && b < 0x7F, "the examples are plain ASCII");
        Encoding.UTF8.GetString(bytes).Should().NotContain("<", "every placeholder of the page is replaced by a concrete value");
    }

    [Fact]
    public void Fixture_directory_holds_exactly_the_six_page_examples_the_minimal_status_and_the_problem_report()
    {
        var files = Directory.GetFiles(FixtureDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        files.Should().Equal(Names.Select(n => n + ".json").Order(StringComparer.Ordinal));
        Names.Should().HaveCount(8);
    }

    // ---- each fixture against its parser and its builder ----------------------------------

    [Fact]
    public void Status_request_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("status-request");

        MessagePickup.TryParseStatusRequest(message, out var recipientDid).Should().BeTrue();
        recipientDid.Should().Be(ExampleDid);
        BodyOf(MessagePickup.CreateStatusRequest(From, To, recipientDid)).Should().Be(BodyOf(message))
            .And.Be("""{"recipient_did":"did:example:alice"}""");
        KeepsReturnRouteAll(message);
    }

    [Fact]
    public void Status_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("status");

        MessagePickup.TryParseStatus(message, out var status).Should().BeTrue();
        status.Should().Be(new PickupStatus(
            MessageCount: 7,
            RecipientDid: ExampleDid,
            LongestWaitedSeconds: 3600,
            NewestReceivedTime: 1658085169,
            OldestReceivedTime: 1658084293,
            TotalBytes: 8096,
            LiveDelivery: false));
        BodyOf(MessagePickup.CreateStatus(From, To, status!)).Should().Be(BodyOf(message), "the builder writes the members in the page's order");
        message.Thid.Should().BeNull("the page's status example carries no thid");
        HasNoReturnRoute(message);
    }

    [Fact]
    public void A_status_with_only_message_count_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read(StatusMinimal);

        MessagePickup.TryParseStatus(message, out var status).Should().BeTrue();
        status.Should().Be(new PickupStatus(3));
        status!.RecipientDid.Should().BeNull();
        status.LongestWaitedSeconds.Should().BeNull();
        status.NewestReceivedTime.Should().BeNull();
        status.OldestReceivedTime.Should().BeNull();
        status.TotalBytes.Should().BeNull();
        status.LiveDelivery.Should().BeNull("an absent live_delivery is not false");
        BodyOf(MessagePickup.CreateStatus(From, To, new PickupStatus(3))).Should().Be(BodyOf(message)).And.Be("""{"message_count":3}""");
    }

    [Fact]
    public void Delivery_request_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("delivery-request");

        MessagePickup.TryParseDeliveryRequest(message, out var limit, out var recipientDid).Should().BeTrue();
        limit.Should().Be(10);
        recipientDid.Should().Be(ExampleDid);
        BodyOf(MessagePickup.CreateDeliveryRequest(From, To, limit, recipientDid)).Should().Be(BodyOf(message))
            .And.Be("""{"limit":10,"recipient_did":"did:example:alice"}""");
        KeepsReturnRouteAll(message);
    }

    [Fact]
    public void Delivery_fixture_parses_and_the_builder_emits_the_same_body_and_attachments()
    {
        var message = Read("delivery");

        MessagePickup.TryParseDelivery(message, out var messages, out var recipientDid).Should().BeTrue();
        recipientDid.Should().Be(ExampleDid);
        var delivered = messages.Should().ContainSingle().Subject;
        delivered.Id.Should().Be("123");
        Encoding.UTF8.GetString(delivered.Payload.Span).Should().Be(DeliveredPayload);
        message.Thid.Should().Be(ExampleId);

        var built = MessagePickup.CreateDelivery(From, To, messages!, message.Thid, recipientDid);
        BodyOf(built).Should().Be(BodyOf(message)).And.Be("""{"recipient_did":"did:example:alice"}""");
        AttachmentsOf(built).Should().Be(AttachmentsOf(message), "one attachment per message: {\"id\": …, \"data\": {\"base64\": …}} and nothing else");
        built.Thid.Should().Be(message.Thid);
        HasNoReturnRoute(message);
    }

    [Fact]
    public void The_delivery_fixtures_data_is_base64url_without_padding()
    {
        // JC-11: attachment data is emitted as base64url WITHOUT padding. The payload was chosen so
        // that both halves of that sentence show in the fixture.
        var message = Read("delivery");
        var payload = Encoding.UTF8.GetBytes(DeliveredPayload);
        var encoded = message.Attachments!.Single().Data.Base64!;

        var standard = Convert.ToBase64String(payload);
        standard.Should().Contain("+").And.Contain("/").And.EndWith("==", "this payload exercises both differing characters and needs two padding characters in standard base64");

        encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        encoded.Should().Contain("-").And.Contain("_");
        (encoded.Length % 4).Should().Be(2, "a padded encoding always has a length that is a multiple of four");
        encoded.Should().Be(standard.TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        encoded.Should().Be(System.Buffers.Text.Base64Url.EncodeToString(payload));

        // What the builder writes for the same bytes is the same text.
        var built = MessagePickup.CreateDelivery(From, To, new[] { new DeliveredMessage("123", payload) });
        built.Attachments!.Single().Data.Base64.Should().Be(encoded);
    }

    [Fact]
    public void The_delivery_fixtures_payload_has_the_shape_of_an_encrypted_message()
    {
        // "The ONLY valid type of attachment for this message is a DIDComm v2 Message in encrypted
        // form." The fixture honours the shape (a JWE in general JSON serialization); nothing in
        // the mediator role depends on it.
        var jwe = JsonNode.Parse(DeliveredPayload)!.AsObject();
        jwe.Select(member => member.Key).Should().Equal("protected", "recipients", "iv", "ciphertext", "tag");
        Encoding.UTF8.GetByteCount(DeliveredPayload).Should().Be(370);
    }

    [Fact]
    public void Messages_received_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("messages-received");

        MessagePickup.TryParseMessagesReceived(message, out var messageIds).Should().BeTrue();
        messageIds.Should().Equal("123", "456");
        BodyOf(MessagePickup.CreateMessagesReceived(From, To, messageIds!)).Should().Be(BodyOf(message))
            .And.Be("""{"message_id_list":["123","456"]}""");
        KeepsReturnRouteAll(message);

        // The mediator's bounded parse reads the page's example too.
        MessagePickup.TryParseMessagesReceived(message, 2, out var bounded).Should().BeTrue();
        bounded.Should().Equal("123", "456");
    }

    [Fact]
    public void Live_delivery_change_fixture_parses_and_the_builder_emits_the_same_body()
    {
        var message = Read("live-delivery-change");

        MessagePickup.TryParseLiveDeliveryChange(message, out var liveDelivery).Should().BeTrue();
        liveDelivery.Should().BeTrue();
        BodyOf(MessagePickup.CreateLiveDeliveryChange(From, To, liveDelivery)).Should().Be(BodyOf(message))
            .And.Be("""{"live_delivery":true}""");
        HasNoReturnRoute(message); // as on the page
    }

    [Fact]
    public void Live_mode_not_supported_fixture_is_the_problem_report_the_builder_emits()
    {
        var message = Read(LiveModeNotSupported);

        message.Type.Should().Be(ProblemReports.MessageType).And.Be("https://didcomm.org/report-problem/2.0/problem-report");
        message.Pthid.Should().Be(ExampleId);
        message.Thid.Should().BeNull();
        message.Body![ProblemReports.CodeField]!.GetValue<string>().Should().Be(MessagePickup.LiveModeNotSupportedCode);
        message.Body![ProblemReports.CommentField]!.GetValue<string>().Should().Be(MessagePickup.LiveModeNotSupportedComment);

        var built = MessagePickup.CreateLiveModeNotSupported(From, To, ExampleId);
        BodyOf(built).Should().Be(BodyOf(message))
            .And.Be("""{"code":"e.m.live-mode-not-supported","comment":"Connection does not support Live Delivery"}""");
        built.Type.Should().Be(message.Type);
        built.Pthid.Should().Be(message.Pthid);

        // It belongs to another protocol: no pickup parser claims it.
        MessageNames.Count(n => Parse(n, message)).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(PickupFixtures))]
    public void Each_fixture_is_claimed_by_exactly_one_parser(string name)
    {
        var message = Read(name);
        var owner = name == StatusMinimal ? "status" : name;

        var claimedBy = MessageNames.Where(n => Parse(n, message)).ToArray();

        claimedBy.Should().Equal(new[] { owner }, "`status` must not be mistaken for `status-request`, nor `delivery` for `delivery-request`, nor the reverse");
    }

    // ---- the examples as one exchange -----------------------------------------------------

    [Fact]
    public void The_fixtures_fit_together_as_the_exchanges_of_the_protocol()
    {
        var statusRequest = Read("status-request");
        var status = Read("status");
        var deliveryRequest = Read("delivery-request");
        var delivery = Read("delivery");
        var received = Read("messages-received");
        var liveChange = Read("live-delivery-change");
        var problem = Read(LiveModeNotSupported);

        // "If a recipient_did was specified in the status-request message, the matching value MUST
        // be specified in the recipient_did attribute of the status message."
        MessagePickup.TryParseStatusRequest(statusRequest, out var asked).Should().BeTrue();
        MessagePickup.TryParseStatus(status, out var answered).Should().BeTrue();
        answered!.RecipientDid.Should().Be(asked);

        // delivery.thid is the delivery-request's thread, and its recipient_did is the request's.
        delivery.Thid.Should().Be(MessagePickup.ResponseThreadId(deliveryRequest));
        MessagePickup.TryParseDeliveryRequest(deliveryRequest, out _, out var filter).Should().BeTrue();
        MessagePickup.TryParseDelivery(delivery, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().Be(filter);

        // messages-received acknowledges the attachment ids of the delivery.
        MessagePickup.TryParseMessagesReceived(received, out var ids).Should().BeTrue();
        ids.Should().Contain(messages!.Select(m => m.Id));

        // The problem report's pthid is the thread of the live-delivery-change it answers.
        problem.Pthid.Should().Be(MessagePickup.ResponseThreadId(liveChange));
    }

    // ---- the page's own text --------------------------------------------------------------

    [Fact]
    public void The_pages_own_text_parses_without_typ_and_with_its_placeholders()
    {
        // The examples exactly as didcomm.org prints them: indented, without typ, placeholders and
        // all. A recipient_did is not checked to be a DID by the parser (that is the handler's
        // question), so a placeholder reads as a value.
        var statusRequest = Page("""
            {
                "id": "123456780",
                "type": "https://didcomm.org/messagepickup/3.0/status-request",
                "body" : {
                    "recipient_did": "<did for messages>"
                },
                "return_route": "all"
            }
            """);
        MessagePickup.TryParseStatusRequest(statusRequest, out var recipientDid).Should().BeTrue();
        recipientDid.Should().Be("<did for messages>");

        var status = Page("""
            {
                "id": "123456780",
                "type": "https://didcomm.org/messagepickup/3.0/status",
                "body": {
                        "recipient_did": "<did for messages>",
                        "message_count": 7,
                        "longest_waited_seconds": 3600,
                        "newest_received_time": 1658085169,
                        "oldest_received_time": 1658084293,
                        "total_bytes": 8096,
                        "live_delivery": false
                }
            }
            """);
        MessagePickup.TryParseStatus(status, out var parsedStatus).Should().BeTrue();
        parsedStatus.Should().Be(new PickupStatus(7, "<did for messages>", 3600, 1658085169, 1658084293, 8096, false));

        var deliveryRequest = Page("""
            {
                "id": "123456780",
                "type": "https://didcomm.org/messagepickup/3.0/delivery-request",
                "body": {
                    "limit": 10,
                    "recipient_did": "<did for messages>"
                },
                "return_route": "all"
            }
            """);
        MessagePickup.TryParseDeliveryRequest(deliveryRequest, out var limit, out recipientDid).Should().BeTrue();
        limit.Should().Be(10);
        recipientDid.Should().Be("<did for messages>");

        var received = Page("""
            {
                "id": "123456780",
                "type": "https://didcomm.org/messagepickup/3.0/messages-received",
                "body": {
                    "message_id_list": ["123","456"]
                },
                "return_route": "all"
            }
            """);
        MessagePickup.TryParseMessagesReceived(received, out var ids).Should().BeTrue();
        ids.Should().Equal("123", "456");

        var liveChange = Page("""
            {
                "id": "123456780",
                "type": "https://didcomm.org/messagepickup/3.0/live-delivery-change",
                "body": {
                    "live_delivery": true
                }
            }
            """);
        MessagePickup.TryParseLiveDeliveryChange(liveChange, out var liveDelivery).Should().BeTrue();
        liveDelivery.Should().BeTrue();
    }

    [Fact]
    public void The_pages_delivery_example_needs_its_data_placeholder_replaced()
    {
        // "<message>" is not base64 in any alphabet, which is why the fixture carries a real
        // encoding. With the placeholder the message is a delivery with undecodable data.
        const string pageShape = """
            {
                "id": "123456780",
                "thid": "<message id of delivery-request message>",
                "type": "https://didcomm.org/messagepickup/3.0/delivery",
                "body": {
                    "recipient_did": "<did for messages>"
                },
                "attachments": [{
                    "id": "<id of message>",
                    "data": {
                        "base64": "DATA"
                    }
                }]
            }
            """;

        var withPlaceholder = Page(pageShape.Replace("DATA", "<message " + Marker + ">"));
        Refused(() => MessagePickup.TryParseDelivery(withPlaceholder, out _, out _))
            .Message.Should().Be("delivery attachment 'data.base64' is not valid base64.");

        // With data in place, the page's shape parses; the other placeholders read as values.
        var withData = Page(pageShape.Replace("DATA", "-_8"));
        MessagePickup.TryParseDelivery(withData, out var messages, out var recipientDid).Should().BeTrue();
        recipientDid.Should().Be("<did for messages>");
        var delivered = messages.Should().ContainSingle().Subject;
        delivered.Id.Should().Be("<id of message>");
        delivered.Payload.ToArray().Should().Equal(0xFB, 0xFF);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "messagepickup-3.0");

    private static string TypeOf(string name) => name switch
    {
        StatusMinimal => MessagePickup.StatusType,
        LiveModeNotSupported => ProblemReports.MessageType,
        _ => MessagePickup.ProtocolUri + "/" + name,
    };

    private static byte[] ReadBytes(string name) => File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".json"));

    private static Message Read(string name) => JsonSerializer.Deserialize<Message>(ReadBytes(name), DidCommJson.Default)!;

    private static Message Page(string text) => JsonSerializer.Deserialize<Message>(text, DidCommJson.Default)!;

    // The page's request examples carry return_route: all; the round trip keeps it and the
    // library reads it as the header it is.
    private static void KeepsReturnRouteAll(Message message)
    {
        Wire(message).Should().EndWith(",\"return_route\":\"all\"}");
        ReturnRouteHeader.TryRead(message, out var header).Should().BeTrue();
        header.Should().Be(new ReturnRouteHeader(ReturnRouteMode.All, null));
    }

    private static void HasNoReturnRoute(Message message)
        => Wire(message).Should().NotContain("return_route");
}
