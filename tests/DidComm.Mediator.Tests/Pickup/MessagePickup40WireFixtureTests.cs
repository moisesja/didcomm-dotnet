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
/// FR-PICK-12 acceptance (#100): byte-exact round trips against the didcomm.org Message Pickup 4.0
/// page (<c>decentralized-identity/didcomm.org@a26926c</c>,
/// <c>site/content/protocols/messagepickup/4.0/readme.md</c>), its DIDComm v2 examples only (L110-L120,
/// L145-L160, L193-L204, L237-L253, L281-L290, L329-L338, L359-L370). The fixtures in
/// <c>fixtures/message-pickup-4.0/</c> are those examples normalized the way the 3.0 fixtures are
/// (<see cref="MessagePickupWireFixtureTests"/>): <c>typ</c> added, compact, no trailing newline, in the
/// library's canonical member order — the order the builder writes — and every placeholder replaced
/// by a concrete value.
/// <list type="bullet">
/// <item><c>&lt;did for messages&gt;</c> → <c>did:example:alice</c>.</item>
/// <item><c>status</c>: the page writes <c>"thid"</c> before <c>"type"</c>; the canonical order puts it
/// after <c>typ</c>. Its <c>thid</c> (<c>&lt;message id of status-request message&gt;</c>) →
/// <c>123456780</c>, the id of the page's own <c>status-request</c> example. The two times stay the
/// page's <c>1739420343823</c> (both members carry the same value on the page).</item>
/// <item><c>delivery</c>: <c>thid</c> (<c>&lt;message id of delivery-request message&gt;</c>) →
/// <c>123456780</c>, the id of the page's <c>delivery-request</c> example; the attachment id
/// (<c>&lt;id of message&gt;</c>) → <c>123</c>, the first id the page's <c>messages-received</c>
/// acknowledges; the data (<c>&lt;message&gt;</c>) → the same JWE-shaped base64url text, without padding,
/// as the 3.0 <c>delivery</c> fixture (filler, not decryptable: a mediator never looks inside).</item>
/// <item><c>status-request</c> and <c>delivery-request</c> keep the page's <c>"return_route":"all"</c>;
/// <c>messages-received</c> has none because 4.0 removed it (L35, L281-L289);
/// <c>live-delivery-change</c> has none because the page's example has none (L69 still asks for it on
/// every request).</item>
/// <item><c>problem-report</c> is the page's live-mode problem report, its <c>pthid</c> placeholder
/// replaced by <c>123456780</c> (the thread of the page's <c>live-delivery-change</c> example); its
/// v2 body uses <c>comment</c> as on the page.</item>
/// <item>Not on the page: <c>status-minimal</c>, a <c>status</c> whose body is only
/// <c>message_count</c> (the one REQUIRED member, L162) and its <c>thid</c> (a 4.0 status always has
/// one, L33); <c>delivery-empty</c>, the <c>delivery</c> that answers the page's
/// <c>delivery-request</c> when nothing can be delivered (an empty <c>attachments</c> array, L210,
/// L259, with the request's thread and <c>recipient_did</c>); and <c>delivery-live</c>, a live-mode
/// <c>delivery</c> with one attachment (id <c>456</c>, the second id the page's
/// <c>messages-received</c> acknowledges), no <c>thid</c> (L34, L263) and no <c>recipient_did</c>
/// (L265: it answers no request).</item>
/// </list>
/// </summary>
public sealed class MessagePickup40WireFixtureTests
{
    private const string ExampleId = "123456780";
    private const string ExampleDid = "did:example:alice";
    private const string From = "did:example:from";
    private const string To = "did:example:to";
    private const string P4 = "https://didcomm.org/message-pickup/4.0";

    private const string StatusMinimal = "status-minimal";
    private const string DeliveryEmpty = "delivery-empty";
    private const string DeliveryLive = "delivery-live";
    private const string ProblemReport = "problem-report";

    /// <summary>The page's messages, and the parser of each.</summary>
    private static readonly string[] MessageNames40 =
    {
        "status-request", "status", "delivery-request", "delivery", "messages-received", "live-delivery-change",
    };

    private static readonly string[] Names = MessageNames40.Concat(new[] { StatusMinimal, DeliveryEmpty, DeliveryLive, ProblemReport }).ToArray();

    public static TheoryData<string> Fixtures => Data(Names);

    /// <summary>Every 4.0 fixture but the problem report: each belongs to one of the six messages.</summary>
    public static TheoryData<string> PickupFixtures => Data(Names.Where(n => n != ProblemReport));

    /// <summary>The 3.0 fixtures (<see cref="MessagePickupWireFixtureTests"/>).</summary>
    public static TheoryData<string> Fixtures30 => Data(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "messagepickup-3.0")).Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal));

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
    public void Fixture_is_compact_ascii_json_without_placeholders(string name)
    {
        var bytes = ReadBytes(name);

        bytes[0].Should().Be((byte)'{', "no byte-order mark, no leading whitespace");
        bytes[^1].Should().Be((byte)'}', "no trailing newline");
        bytes.Should().OnlyContain(b => b >= 0x20 && b < 0x7F, "the examples are plain ASCII, without line breaks or tabs");
        Encoding.UTF8.GetString(bytes).Should().NotContain("<", "every placeholder of the page is replaced by a concrete value");
    }

    [Fact]
    public void Fixture_directory_holds_the_seven_page_examples_and_the_three_additions()
    {
        var files = Directory.GetFiles(FixtureDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        files.Should().Equal(Names.Select(n => n + ".json").Order(StringComparer.Ordinal));
        Names.Should().HaveCount(10);
    }

    // ---- the builder writes each fixture ----------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_builder_writes_each_fixture_byte_for_byte_but_for_from_to_and_return_route(string name)
    {
        // The page's examples carry neither from nor to, and the builder requires both; return_route
        // is a transport header the sender adds, not part of what the builder makes. Everything
        // else — member order included, which is why the status fixture's thid sits after typ — is
        // what the builder writes.
        var ids = new MessagePickupWireFixedIds(ExampleId);
        var delivered = new DeliveredMessage("123", DecodeBase64Url(Read("delivery").Attachments!.Single().Data.Base64!));
        var live = new DeliveredMessage("456", delivered.Payload);
        var built = name switch
        {
            "status-request" => MessagePickup40.CreateStatusRequest(From, To, ExampleDid, ids),
            "status" => MessagePickup40.CreateStatus(From, To, new PickupStatus40(7, ExampleDid, 1739420343823, 1739420343823, 8096, false), ExampleId, ids),
            StatusMinimal => MessagePickup40.CreateStatus(From, To, new PickupStatus40(3), ExampleId, ids),
            "delivery-request" => MessagePickup40.CreateDeliveryRequest(From, To, 10, ExampleDid, ids),
            "delivery" => MessagePickup40.CreateDelivery(From, To, new[] { delivered }, ExampleId, ExampleDid, ids),
            DeliveryEmpty => MessagePickup40.CreateDelivery(From, To, Array.Empty<DeliveredMessage>(), ExampleId, ExampleDid, ids),
            DeliveryLive => MessagePickup40.CreateDelivery(From, To, new[] { live }, idGenerator: ids),
            "messages-received" => MessagePickup40.CreateMessagesReceived(From, To, new[] { "123", "456" }, ids),
            "live-delivery-change" => MessagePickup40.CreateLiveDeliveryChange(From, To, true, ids),
            ProblemReport => MessagePickup40.CreateLiveModeNotSupported(From, To, ExampleId, ids),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        var written = JsonNode.Parse(Wire(built))!.AsObject();
        written["from"]!.GetValue<string>().Should().Be(From);
        written["to"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(To);
        written.Remove("from");
        written.Remove("to");
        var fixture = JsonNode.Parse(ReadBytes(name))!.AsObject();
        fixture.Remove("return_route");

        written.ToJsonString().Should().Be(fixture.ToJsonString());
        ids.Calls.Should().Be(1);
    }

    // ---- each fixture against its parser --------------------------------------------------

    [Fact]
    public void Status_request_fixture_parses()
    {
        var message = Read("status-request");

        MessagePickup40.TryParseStatusRequest(message, out var recipientDid).Should().BeTrue();
        recipientDid.Should().Be(ExampleDid);
        KeepsReturnRouteAll(message);
    }

    [Fact]
    public void Status_fixture_parses_with_its_times_in_milliseconds_and_maps_to_seconds_for_the_public_record()
    {
        var message = Read("status");

        MessagePickup40.TryParseStatus(message, out var status).Should().BeTrue();
        status.Should().Be(new PickupStatus40(
            MessageCount: 7,
            RecipientDid: ExampleDid,
            NewestReceivedTimeMs: 1739420343823,
            OldestReceivedTimeMs: 1739420343823,
            TotalBytes: 8096,
            LiveDelivery: false));
        message.Thid.Should().Be(ExampleId, "a 4.0 status always answers a request (L33)");
        status!.ToPickupStatus().Should().Be(new PickupStatus(7, ExampleDid, null, 1739420343, 1739420343, 8096, false));
        HasNoReturnRoute(message);
    }

    [Fact]
    public void A_status_with_only_message_count_parses_to_nothing_else()
    {
        var message = Read(StatusMinimal);

        MessagePickup40.TryParseStatus(message, out var status).Should().BeTrue();
        status.Should().Be(new PickupStatus40(3));
        status!.LiveDelivery.Should().BeNull("an absent live_delivery is not false");
        message.Thid.Should().Be(ExampleId);
    }

    [Fact]
    public void Delivery_request_fixture_parses_message_count_limit()
    {
        var message = Read("delivery-request");

        MessagePickup40.TryParseDeliveryRequest(message, out var limit, out var recipientDid).Should().BeTrue();
        limit.Should().Be(10);
        recipientDid.Should().Be(ExampleDid);
        KeepsReturnRouteAll(message);
    }

    [Fact]
    public void Delivery_fixture_parses_to_its_one_message()
    {
        var message = Read("delivery");

        MessagePickup40.TryParseDelivery(message, out var messages, out var recipientDid).Should().BeTrue();
        recipientDid.Should().Be(ExampleDid);
        var delivered = messages.Should().ContainSingle().Subject;
        delivered.Id.Should().Be("123");
        delivered.Payload.ToArray().Should().Equal(Read30Payload(), "the same payload as the 3.0 fixture");
        message.Thid.Should().Be(ExampleId);
        HasNoReturnRoute(message);
    }

    [Fact]
    public void The_empty_delivery_fixture_parses_to_no_messages_and_keeps_the_request_s_thread_and_filter()
    {
        var message = Read(DeliveryEmpty);

        Wire(message).Should().Contain("\"attachments\":[]");
        MessagePickup40.TryParseDelivery(message, out var messages, out var recipientDid).Should().BeTrue();
        messages.Should().BeEmpty();
        recipientDid.Should().Be(ExampleDid);
        message.Thid.Should().Be(ExampleId);
    }

    [Fact]
    public void The_live_delivery_fixture_parses_and_carries_neither_a_thread_nor_a_filter()
    {
        var message = Read(DeliveryLive);

        MessagePickup40.TryParseDelivery(message, out var messages, out var recipientDid).Should().BeTrue();
        messages.Should().ContainSingle().Which.Id.Should().Be("456");
        recipientDid.Should().BeNull();
        message.Thid.Should().BeNull("a live delivery answers no request (L34, L263)");
        message.Pthid.Should().BeNull();
    }

    [Fact]
    public void Messages_received_fixture_parses_and_carries_no_return_route()
    {
        var message = Read("messages-received");

        MessagePickup40.TryParseMessagesReceived(message, 2, out var messageIds).Should().BeTrue("the mediator's bounded parse reads the page's example");
        messageIds.Should().Equal("123", "456");
        HasNoReturnRoute(message); // 4.0 L35
    }

    [Fact]
    public void Live_delivery_change_fixture_parses()
    {
        var message = Read("live-delivery-change");

        MessagePickup40.TryParseLiveDeliveryChange(message, out var liveDelivery).Should().BeTrue();
        liveDelivery.Should().BeTrue();
        HasNoReturnRoute(message); // as on the page
    }

    [Fact]
    public void Problem_report_fixture_is_the_4_0_type_with_pthid_code_and_comment()
    {
        var message = Read(ProblemReport);

        MessagePickup40.IsProblemReport(message).Should().BeTrue();
        message.Type.Should().Be(P4 + "/problem-report").And.NotBe(ProblemReports.MessageType);
        message.Pthid.Should().Be(ExampleId);
        message.Thid.Should().BeNull();
        message.Body![ProblemReports.CodeField]!.GetValue<string>().Should().Be(MessagePickup.LiveModeNotSupportedCode);
        message.Body![ProblemReports.CommentField]!.GetValue<string>().Should().Be(MessagePickup.LiveModeNotSupportedComment);
        MessageNames40.Count(n => Parse40(n, message)).Should().Be(0, "no 4.0 message parser claims the problem report");
        MessageNames.Count(n => Parse(n, message)).Should().Be(0, "nor does a 3.0 one");
    }

    // ---- which parser claims which fixture --------------------------------------------------

    [Theory]
    [MemberData(nameof(PickupFixtures))]
    public void Each_4_0_fixture_is_claimed_by_exactly_one_4_0_parser_and_by_no_3_0_parser(string name)
    {
        var message = Read(name);
        var owner = name switch
        {
            StatusMinimal => "status",
            DeliveryEmpty or DeliveryLive => "delivery",
            _ => name,
        };

        MessageNames40.Where(n => Parse40(n, message)).Should().Equal(new[] { owner }, "`status` must not be mistaken for `status-request`, nor `delivery` for `delivery-request`, nor the reverse");
        MessageNames.Where(n => Parse(n, message)).Should().BeEmpty("a 3.0 parser never reads a 4.0 message (another major version)");
        MessagePickup40.IsProblemReport(message).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Fixtures30))]
    public void No_4_0_parser_claims_a_3_0_fixture(string name)
    {
        var message = JsonSerializer.Deserialize<Message>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "messagepickup-3.0", name + ".json")), DidCommJson.Default)!;

        MessageNames40.Where(n => Parse40(n, message)).Should().BeEmpty("a 4.0 parser never reads a 3.0 message ({0})", name);
        MessagePickup40.IsProblemReport(message).Should().BeFalse("the 3.0 problem report is report-problem/2.0, not 4.0's");
    }

    // ---- the examples as one exchange -----------------------------------------------------

    [Fact]
    public void The_fixtures_fit_together_as_the_exchanges_of_the_protocol()
    {
        var statusRequest = Read("status-request");
        var status = Read("status");
        var deliveryRequest = Read("delivery-request");
        var delivery = Read("delivery");
        var empty = Read(DeliveryEmpty);
        var live = Read(DeliveryLive);
        var received = Read("messages-received");
        var liveChange = Read("live-delivery-change");
        var problem = Read(ProblemReport);

        // L168: the status repeats the filter; L33: and answers on the request's thread.
        MessagePickup40.TryParseStatusRequest(statusRequest, out var asked).Should().BeTrue();
        MessagePickup40.TryParseStatus(status, out var answered).Should().BeTrue();
        answered!.RecipientDid.Should().Be(asked);
        status.Thid.Should().Be(MessagePickup.ResponseThreadId(statusRequest));

        // A delivery, full or empty, answers on the delivery-request's thread with its recipient_did (L265).
        MessagePickup40.TryParseDeliveryRequest(deliveryRequest, out var limit, out var filter).Should().BeTrue();
        foreach (var answer in new[] { delivery, empty })
        {
            answer.Thid.Should().Be(MessagePickup.ResponseThreadId(deliveryRequest));
            MessagePickup40.TryParseDelivery(answer, out var batch, out var echoed).Should().BeTrue();
            echoed.Should().Be(filter);
            batch!.Count.Should().BeLessThanOrEqualTo(limit, "L255: never more attachments than message_count_limit");
        }

        // messages-received acknowledges what the delivery and the live push carried.
        MessagePickup40.TryParseDelivery(delivery, out var fetched, out _).Should().BeTrue();
        MessagePickup40.TryParseDelivery(live, out var pushed, out _).Should().BeTrue();
        MessagePickup40.TryParseMessagesReceived(received, 10, out var ids).Should().BeTrue();
        ids.Should().Equal(fetched!.Concat(pushed!).Select(m => m.Id));

        // The problem report's pthid is the thread of the live-delivery-change it answers.
        problem.Pthid.Should().Be(MessagePickup.ResponseThreadId(liveChange));
    }

    // ---- the page's own text --------------------------------------------------------------

    [Fact]
    public void The_pages_status_example_parses_as_printed_with_thid_before_type()
    {
        var status = Page("""
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
            """);
        MessagePickup40.TryParseStatus(status, out var parsed).Should().BeTrue();
        parsed.Should().Be(new PickupStatus40(7, "<did for messages>", 1739420343823, 1739420343823, 8096, false));

        // Written again, thid moves behind typ: the canonical order of the fixture.
        Wire(status).IndexOf("\"thid\"", StringComparison.Ordinal).Should().BeGreaterThan(Wire(status).IndexOf("\"type\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pages_delivery_request_example_parses_as_printed()
    {
        var request = Page("""
            {
                "id": "123456780",
                "type": "https://didcomm.org/message-pickup/4.0/delivery-request",
                "body": {
                    "message_count_limit": 10,
                    "recipient_did": "<did for messages>"
                },
                "return_route": "all"
            }
            """);
        MessagePickup40.TryParseDeliveryRequest(request, out var limit, out var recipientDid).Should().BeTrue();
        limit.Should().Be(10);
        recipientDid.Should().Be("<did for messages>");
    }

    // ---- helpers --------------------------------------------------------------------------

    private static TheoryData<string> Data(IEnumerable<string> names)
    {
        var data = new TheoryData<string>();
        foreach (var name in names)
            data.Add(name);
        return data;
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "message-pickup-4.0");

    private static string TypeOf(string name) => name switch
    {
        StatusMinimal => P4 + "/status",
        DeliveryEmpty or DeliveryLive => P4 + "/delivery",
        _ => P4 + "/" + name,
    };

    private static byte[] ReadBytes(string name) => File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".json"));

    private static Message Read(string name) => JsonSerializer.Deserialize<Message>(ReadBytes(name), DidCommJson.Default)!;

    private static Message Page(string text) => JsonSerializer.Deserialize<Message>(text, DidCommJson.Default)!;

    private static byte[] DecodeBase64Url(string text) => System.Buffers.Text.Base64Url.DecodeFromChars(text);

    private static byte[] Read30Payload()
    {
        var delivery = JsonSerializer.Deserialize<Message>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "messagepickup-3.0", "delivery.json")), DidCommJson.Default)!;
        MessagePickup.TryParseDelivery(delivery, out var messages, out _).Should().BeTrue();
        return messages!.Single().Payload.ToArray();
    }

    /// <summary>Run the 4.0 parser that owns <paramref name="name"/> over <paramref name="message"/>.</summary>
    private static bool Parse40(string name, Message message) => name switch
    {
        "status-request" => MessagePickup40.TryParseStatusRequest(message, out _),
        "status" => MessagePickup40.TryParseStatus(message, out _),
        "delivery-request" => MessagePickup40.TryParseDeliveryRequest(message, out _, out _),
        "delivery" => MessagePickup40.TryParseDelivery(message, out _, out _),
        "messages-received" => MessagePickup40.TryParseMessagesReceived(message, int.MaxValue, out _),
        "live-delivery-change" => MessagePickup40.TryParseLiveDeliveryChange(message, out _),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static void KeepsReturnRouteAll(Message message)
    {
        Wire(message).Should().EndWith(",\"return_route\":\"all\"}");
        ReturnRouteHeader.TryRead(message, out var header).Should().BeTrue();
        header.Should().Be(new ReturnRouteHeader(ReturnRouteMode.All, null));
    }

    private static void HasNoReturnRoute(Message message)
        => Wire(message).Should().NotContain("return_route");
}
