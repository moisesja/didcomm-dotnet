using System.Text;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupWireSupport;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The <c>Create…</c> helpers of <see cref="MessagePickup"/> and the two records they take
/// (FR-PICK-02..07; FR-API-09 on the way out; JC-8 thread ids; JC-11 attachment encoding).
/// </summary>
public sealed class MessagePickupWireBuilderTests
{
    private static readonly byte[] Payload = { 0xFB, 0xFF }; // "-_8" in base64url, "+/8=" in base64

    [Fact]
    public void Constants_are_the_didcomm_org_identifiers()
    {
        MessagePickup.ProtocolUri.Should().Be("https://didcomm.org/messagepickup/3.0");
        MessagePickup.StatusRequestType.Should().Be("https://didcomm.org/messagepickup/3.0/status-request");
        MessagePickup.StatusType.Should().Be("https://didcomm.org/messagepickup/3.0/status");
        MessagePickup.DeliveryRequestType.Should().Be("https://didcomm.org/messagepickup/3.0/delivery-request");
        MessagePickup.DeliveryType.Should().Be("https://didcomm.org/messagepickup/3.0/delivery");
        MessagePickup.MessagesReceivedType.Should().Be("https://didcomm.org/messagepickup/3.0/messages-received");
        MessagePickup.LiveDeliveryChangeType.Should().Be("https://didcomm.org/messagepickup/3.0/live-delivery-change");
        MessagePickup.MediatorRole.Should().Be("mediator");
        MessagePickup.RecipientRole.Should().Be("recipient");
        MessagePickup.LiveModeNotSupportedCode.Should().Be("e.m.live-mode-not-supported");
        MessagePickup.LiveModeNotSupportedComment.Should().Be("Connection does not support Live Delivery");
    }

    // ---- requests -------------------------------------------------------------------------

    [Fact]
    public void Requests_carry_from_to_no_thid_and_the_expected_bodies()
    {
        var statusRequest = MessagePickup.CreateStatusRequest(AliceDid, MediatorDid);
        IsRequest(statusRequest, MessagePickup.StatusRequestType);
        Wire(statusRequest).Should().Contain("\"body\":{}", "a status-request without a filter emits an empty body object, never omits it");
        BodyOf(MessagePickup.CreateStatusRequest(AliceDid, MediatorDid, "did:example:r1")).Should().Be("""{"recipient_did":"did:example:r1"}""");

        var deliveryRequest = MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, 3);
        IsRequest(deliveryRequest, MessagePickup.DeliveryRequestType);
        BodyOf(deliveryRequest).Should().Be("""{"limit":3}""");
        BodyOf(MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, 3, "did:example:r1")).Should().Be("""{"limit":3,"recipient_did":"did:example:r1"}""", "limit first, as on the page");
        BodyOf(MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, 0)).Should().Be("""{"limit":0}""", "zero is a legal limit: it asks for nothing");
        BodyOf(MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, int.MaxValue)).Should().Be("""{"limit":2147483647}""");

        var received = MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, new[] { "id-2", "id-1", "id-2" });
        IsRequest(received, MessagePickup.MessagesReceivedType);
        BodyOf(received).Should().Be("""{"message_id_list":["id-2","id-1","id-2"]}""", "the ids are written in the order given, repeats included");
        BodyOf(MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, Array.Empty<string>())).Should().Be("""{"message_id_list":[]}""");

        var liveOn = MessagePickup.CreateLiveDeliveryChange(AliceDid, MediatorDid, liveDelivery: true);
        IsRequest(liveOn, MessagePickup.LiveDeliveryChangeType);
        BodyOf(liveOn).Should().Be("""{"live_delivery":true}""");
        BodyOf(MessagePickup.CreateLiveDeliveryChange(AliceDid, MediatorDid, liveDelivery: false)).Should().Be("""{"live_delivery":false}""");
    }

    [Fact]
    public void Message_ids_to_acknowledge_are_opaque_only_emptiness_and_length_are_checked()
    {
        // The recipient sends back what the delivery carried; it reads nothing into an id.
        var ids = new[] { "has space", "a/b#c?d", "caf" + (char)0xE9, "AZaz09-._~", IdAtCap };

        var message = MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, ids);

        MessagePickup.TryParseMessagesReceived(RoundTrip(message), out var parsed).Should().BeTrue();
        parsed.Should().Equal(ids);
    }

    // ---- status ---------------------------------------------------------------------------

    [Fact]
    public void A_status_writes_the_members_it_is_given_in_page_order_and_leaves_out_the_rest()
    {
        // All 64 combinations of the six optional members.
        for (var mask = 0; mask < 64; mask++)
        {
            var status = new PickupStatus(
                MessageCount: 7,
                RecipientDid: (mask & 1) != 0 ? "did:example:r1" : null,
                LongestWaitedSeconds: (mask & 2) != 0 ? 3600L : null,
                NewestReceivedTime: (mask & 4) != 0 ? 1658085169L : null,
                OldestReceivedTime: (mask & 8) != 0 ? 1658084293L : null,
                TotalBytes: (mask & 16) != 0 ? 8096L : null,
                LiveDelivery: (mask & 32) != 0 ? false : null);

            var expected = new StringBuilder("{");
            if ((mask & 1) != 0)
                expected.Append("\"recipient_did\":\"did:example:r1\",");
            expected.Append("\"message_count\":7");
            if ((mask & 2) != 0)
                expected.Append(",\"longest_waited_seconds\":3600");
            if ((mask & 4) != 0)
                expected.Append(",\"newest_received_time\":1658085169");
            if ((mask & 8) != 0)
                expected.Append(",\"oldest_received_time\":1658084293");
            if ((mask & 16) != 0)
                expected.Append(",\"total_bytes\":8096");
            if ((mask & 32) != 0)
                expected.Append(",\"live_delivery\":false");
            expected.Append('}');

            var message = MessagePickup.CreateStatus(MediatorDid, AliceDid, status);

            BodyOf(message).Should().Be(expected.ToString(), "combination {0}", mask);
            MessagePickup.TryParseStatus(RoundTrip(message), out var parsed).Should().BeTrue();
            parsed.Should().Be(status, "combination {0} reads back as it was written", mask);
        }
    }

    [Fact]
    public void Zero_and_false_are_values_and_the_largest_numbers_are_written_as_numbers()
    {
        BodyOf(MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(0, null, 0, 0, 0, 0, false))).Should().Be(
            """{"message_count":0,"longest_waited_seconds":0,"newest_received_time":0,"oldest_received_time":0,"total_bytes":0,"live_delivery":false}""",
            "only null is left out");

        BodyOf(MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(int.MaxValue, null, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, true))).Should().Be(
            """{"message_count":2147483647,"longest_waited_seconds":9223372036854775807,"newest_received_time":9223372036854775807,"oldest_received_time":9223372036854775807,"total_bytes":9223372036854775807,"live_delivery":true}""");
    }

    [Fact]
    public void A_status_is_addressed_mediator_to_recipient_and_carries_the_thid_only_when_one_is_given()
    {
        var bare = MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1));
        bare.Type.Should().Be(MessagePickup.StatusType);
        bare.From.Should().Be(MediatorDid);
        bare.To.Should().Equal(AliceDid);
        bare.Thid.Should().BeNull();
        bare.Pthid.Should().BeNull();
        bare.Attachments.Should().BeNull();
        Wire(bare).Should().NotContain("thid");

        var threaded = MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1), "thread-1");
        threaded.Thid.Should().Be("thread-1");
        threaded.Pthid.Should().BeNull();
        Wire(threaded).Should().Contain("\"thid\":\"thread-1\"");
    }

    [Fact]
    public void A_status_record_defaults_every_optional_member_to_absent()
    {
        var status = new PickupStatus(3);

        status.MessageCount.Should().Be(3);
        status.RecipientDid.Should().BeNull();
        status.LongestWaitedSeconds.Should().BeNull();
        status.NewestReceivedTime.Should().BeNull();
        status.OldestReceivedTime.Should().BeNull();
        status.TotalBytes.Should().BeNull();
        status.LiveDelivery.Should().BeNull();
        status.Should().Be(new PickupStatus(3)).And.NotBe(new PickupStatus(3, LiveDelivery: false), "absent and false are different answers");
    }

    // ---- delivery -------------------------------------------------------------------------

    [Fact]
    public void A_delivery_carries_one_attachment_per_message_in_the_order_given()
    {
        var messages = new[]
        {
            new DeliveredMessage("id-2", new byte[] { 1 }),
            new DeliveredMessage("id-1", new byte[] { 1, 2 }),
            new DeliveredMessage("id-3", new byte[] { 1, 2, 3 }),
        };

        var delivery = MessagePickup.CreateDelivery(MediatorDid, AliceDid, messages, "thread-1", "did:example:r1");

        delivery.Type.Should().Be(MessagePickup.DeliveryType);
        delivery.From.Should().Be(MediatorDid);
        delivery.To.Should().Equal(AliceDid);
        delivery.Thid.Should().Be("thread-1");
        delivery.Pthid.Should().BeNull();
        BodyOf(delivery).Should().Be("""{"recipient_did":"did:example:r1"}""");
        AttachmentsOf(delivery).Should().Be(
            """[{"id":"id-2","data":{"base64":"AQ"}},{"id":"id-1","data":{"base64":"AQI"}},{"id":"id-3","data":{"base64":"AQID"}}]""",
            "an attachment is {id, data: {base64}} and nothing else: no media type, no byte count, no padding");
    }

    [Fact]
    public void A_delivery_carries_thid_and_recipient_did_only_when_they_are_given()
    {
        // A live delivery answers no request: no thid, and no filter to repeat.
        var live = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("id-1", Payload) });
        live.Thid.Should().BeNull();
        Wire(live).Should().NotContain("thid").And.Contain("\"body\":{}", "without a filter the body is an empty object, never omitted");

        var threaded = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("id-1", Payload) }, thid: "thread-1");
        threaded.Thid.Should().Be("thread-1");
        BodyOf(threaded).Should().Be("{}");

        var filtered = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("id-1", Payload) }, recipientDid: "did:example:r1");
        filtered.Thid.Should().BeNull();
        BodyOf(filtered).Should().Be("""{"recipient_did":"did:example:r1"}""");
    }

    [Fact]
    public void A_delivery_without_messages_has_no_attachments_member()
    {
        var empty = MessagePickup.CreateDelivery(MediatorDid, AliceDid, Array.Empty<DeliveredMessage>(), "thread-1");

        empty.Attachments.Should().BeNull();
        Wire(empty).Should().NotContain("attachments");
        MessagePickup.TryParseDelivery(RoundTrip(empty), out var messages, out _).Should().BeTrue();
        messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(new byte[] { 0xFB }, "-w")]                         // "+w==" in base64
    [InlineData(new byte[] { 0xFB, 0xFF }, "-_8")]                  // "+/8="
    [InlineData(new byte[] { 0xFB, 0xFF, 0xBF }, "-_-_")]           // "+/+/"
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "_____w")]   // "/////w=="
    [InlineData(new byte[] { 0x00 }, "AA")]                         // "AA=="
    [InlineData(new byte[] { 0x00, 0x00 }, "AAA")]                  // "AAA="
    [InlineData(new byte[] { 0x3E, 0x3F, 0x7E }, "Pj9-")]           // ">?~": text that ends up on the differing characters
    public void Attachment_data_is_base64url_without_padding(byte[] payload, string expected)
    {
        // JC-11 / DIDComm v2.1 attachments: the URL-safe alphabet, no '='.
        var delivery = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("id-1", payload) });

        var encoded = delivery.Attachments!.Single().Data.Base64;

        encoded.Should().Be(expected);
        encoded.Should().Be(Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        encoded.Should().NotContain("=").And.NotContain("+").And.NotContain("/");
    }

    [Fact]
    public void Only_the_bytes_of_the_payload_are_encoded_when_it_is_a_slice_of_a_larger_buffer()
    {
        var buffer = new byte[] { 0xAA, 0xFB, 0xFF, 0xAA };

        var delivery = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("id-1", buffer.AsMemory(1, 2)) });

        delivery.Attachments!.Single().Data.Base64.Should().Be("-_8");
    }

    // ---- round trips ----------------------------------------------------------------------

    [Fact]
    public void Everything_a_builder_emits_parses_back_to_the_same_values()
    {
        MessagePickup.TryParseStatusRequest(RoundTrip(MessagePickup.CreateStatusRequest(AliceDid, MediatorDid)), out var noFilter).Should().BeTrue();
        noFilter.Should().BeNull();
        MessagePickup.TryParseStatusRequest(RoundTrip(MessagePickup.CreateStatusRequest(AliceDid, MediatorDid, DidAtCap)), out var filter).Should().BeTrue();
        filter.Should().Be(DidAtCap);

        var status = new PickupStatus(5, DidAtCap, 50, 1_800_000_040, 1_800_000_000, 510, true);
        MessagePickup.TryParseStatus(RoundTrip(MessagePickup.CreateStatus(MediatorDid, AliceDid, status, "thread-1")), out var parsedStatus).Should().BeTrue();
        parsedStatus.Should().Be(status);

        foreach (var (limit, did) in new (int, string?)[] { (0, null), (1, "did:example:r1"), (int.MaxValue, DidAtCap) })
        {
            MessagePickup.TryParseDeliveryRequest(RoundTrip(MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, limit, did)), out var parsedLimit, out var parsedDid).Should().BeTrue();
            parsedLimit.Should().Be(limit);
            parsedDid.Should().Be(did);
        }

        var large = new byte[100_000];
        new Random(94).NextBytes(large);
        var sent = new[] { new DeliveredMessage(IdAtCap, large), new DeliveredMessage("AZaz09-._~", Payload), new DeliveredMessage("id-3", new byte[] { 0 }) };
        MessagePickup.TryParseDelivery(RoundTrip(MessagePickup.CreateDelivery(MediatorDid, AliceDid, sent, "thread-1", "did:example:r1")), out var delivered, out var echoed).Should().BeTrue();
        echoed.Should().Be("did:example:r1");
        delivered!.Select(m => m.Id).Should().Equal(sent.Select(m => m.Id));
        for (var i = 0; i < sent.Length; i++)
            delivered![i].Payload.ToArray().Should().Equal(sent[i].Payload.ToArray(), "payload {0} arrives byte for byte", i);

        var ids = new[] { "id-1", IdAtCap, "id-1" };
        MessagePickup.TryParseMessagesReceived(RoundTrip(MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, ids)), out var parsedIds).Should().BeTrue();
        parsedIds.Should().Equal(ids);

        foreach (var live in new[] { true, false })
        {
            MessagePickup.TryParseLiveDeliveryChange(RoundTrip(MessagePickup.CreateLiveDeliveryChange(AliceDid, MediatorDid, live)), out var parsedLive).Should().BeTrue();
            parsedLive.Should().Be(live);
        }
    }

    [Fact]
    public void The_builder_and_the_parser_agree_about_an_empty_payload()
    {
        // Either answer is consistent: the builder refuses a message without bytes (as
        // MessagePickupQueue.DeliverAsync does), or it writes one and the parser reads it back as
        // zero bytes. What must not happen is a delivery the library builds and then calls
        // malformed: one such attachment would cost the recipient the whole batch.
        Message built;
        try
        {
            built = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[]
            {
                new DeliveredMessage("id-1", Payload),
                new DeliveredMessage("id-2", ReadOnlyMemory<byte>.Empty),
            });
        }
        catch (ArgumentException)
        {
            return;
        }

        var parse = () => MessagePickup.TryParseDelivery(RoundTrip(built), out _, out _);
        parse.Should().NotThrow("what CreateDelivery emits, TryParseDelivery reads");
        MessagePickup.TryParseDelivery(RoundTrip(built), out var messages, out _).Should().BeTrue();
        messages!.Select(m => m.Payload.Length).Should().Equal(2, 0);
    }

    // ---- thread ids, problem report, id generator -----------------------------------------

    [Fact]
    public void ResponseThreadId_is_the_request_thid_else_its_id()
    {
        var plain = new MessageBuilder().WithId("request-id").WithType(MessagePickup.StatusRequestType).Build();
        MessagePickup.ResponseThreadId(plain).Should().Be("request-id");

        var threaded = new MessageBuilder().WithId("request-id").WithThid("existing-thread").WithType(MessagePickup.DeliveryRequestType).Build();
        MessagePickup.ResponseThreadId(threaded).Should().Be("existing-thread");

        plain.Thid = string.Empty;
        MessagePickup.ResponseThreadId(plain).Should().Be("request-id", "an empty thid is no thid");

        // It reads the two headers and nothing else: the message need not be a pickup message.
        var other = new MessageBuilder().WithId("ping-id").WithType("https://didcomm.org/trust-ping/2.0/ping").Build();
        MessagePickup.ResponseThreadId(other).Should().Be("ping-id");

        var act = () => MessagePickup.ResponseThreadId(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Live_mode_not_supported_is_the_problem_report_of_the_protocol_page()
    {
        var report = MessagePickup.CreateLiveModeNotSupported(MediatorDid, AliceDid, "thread-1");

        report.Type.Should().Be("https://didcomm.org/report-problem/2.0/problem-report").And.Be(ProblemReports.MessageType);
        report.From.Should().Be(MediatorDid);
        report.To.Should().Equal(AliceDid);
        report.Pthid.Should().Be("thread-1", "pthid is the thread in which the problem occurred");
        report.Thid.Should().BeNull();
        report.Attachments.Should().BeNull();
        BodyOf(report).Should().Be("""{"code":"e.m.live-mode-not-supported","comment":"Connection does not support Live Delivery"}""");
        report.Id.Should().NotBeNullOrEmpty().And.NotBe(MessagePickup.CreateLiveModeNotSupported(MediatorDid, AliceDid, "thread-1").Id);

        // It is not a message of the pickup protocol.
        MessageNames.Count(name => Parse(name, RoundTrip(report))).Should().Be(0);
    }

    [Fact]
    public void A_custom_id_generator_is_honoured_once_by_every_builder_that_takes_one()
    {
        var builders = new Func<IMessageIdGenerator?, Message>[]
        {
            ids => MessagePickup.CreateStatusRequest(AliceDid, MediatorDid, null, ids),
            ids => MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1), "thread-1", ids),
            ids => MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, 1, null, ids),
            ids => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("attachment-id", Payload) }, "thread-1", null, ids),
            ids => MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, new[] { "attachment-id" }, ids),
            ids => MessagePickup.CreateLiveDeliveryChange(AliceDid, MediatorDid, true, ids),
        };

        foreach (var build in builders)
        {
            var ids = new MessagePickupWireFixedIds("fixed-id");
            build(ids).Id.Should().Be("fixed-id");
            ids.Calls.Should().Be(1, "one message, one id");

            // The default generates distinct ids.
            build(null).Id.Should().NotBe(build(null).Id).And.NotBe("fixed-id");
        }
    }

    [Fact]
    public void A_generated_id_never_becomes_an_attachment_id()
    {
        // The attachment ids are the store's, given by the caller; the generator is for the message.
        var delivery = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("attachment-id", Payload) }, idGenerator: new MessagePickupWireFixedIds("message-id"));

        delivery.Id.Should().Be("message-id");
        delivery.Attachments!.Single().Id.Should().Be("attachment-id");
    }

    // ---- argument validation --------------------------------------------------------------

    [Fact]
    public void A_did_or_id_of_exactly_2048_characters_is_accepted_everywhere()
    {
        DidAtCap.Length.Should().Be(2048);
        IdAtCap.Length.Should().Be(2048);

        MessagePickup.CreateStatusRequest(DidAtCap, DidAtCap, DidAtCap).From.Should().Be(DidAtCap);
        MessagePickup.CreateStatus(DidAtCap, DidAtCap, new PickupStatus(1, DidAtCap), "thread-1").To.Should().Equal(DidAtCap);
        MessagePickup.CreateDeliveryRequest(DidAtCap, DidAtCap, 1, DidAtCap).Should().NotBeNull();
        MessagePickup.CreateDelivery(DidAtCap, DidAtCap, new[] { new DeliveredMessage(IdAtCap, Payload) }, "thread-1", DidAtCap).Attachments!.Single().Id.Should().Be(IdAtCap);
        MessagePickup.CreateMessagesReceived(DidAtCap, DidAtCap, new[] { IdAtCap }).Should().NotBeNull();
        MessagePickup.CreateLiveDeliveryChange(DidAtCap, DidAtCap, true).Should().NotBeNull();
        MessagePickup.CreateLiveModeNotSupported(DidAtCap, DidAtCap, "thread-1").Should().NotBeNull();
    }

    public static TheoryData<string, Func<object?>> OverLongCases
    {
        get
        {
            var ok = new DeliveredMessage("id-1", Payload);
            var data = new TheoryData<string, Func<object?>>();
            foreach (var (label, value) in new[] { ("2049 characters", DidOverCap), ("almost a megabyte", NearMegabyte) })
            {
                data.Add($"status-request from, {label}", () => MessagePickup.CreateStatusRequest(value, MediatorDid));
                data.Add($"status-request mediator, {label}", () => MessagePickup.CreateStatusRequest(AliceDid, value));
                data.Add($"status-request recipient_did, {label}", () => MessagePickup.CreateStatusRequest(AliceDid, MediatorDid, value));
                data.Add($"status from, {label}", () => MessagePickup.CreateStatus(value, AliceDid, new PickupStatus(1)));
                data.Add($"status to, {label}", () => MessagePickup.CreateStatus(MediatorDid, value, new PickupStatus(1)));
                data.Add($"status recipient_did, {label}", () => MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1, value)));
                data.Add($"delivery-request from, {label}", () => MessagePickup.CreateDeliveryRequest(value, MediatorDid, 1));
                data.Add($"delivery-request mediator, {label}", () => MessagePickup.CreateDeliveryRequest(AliceDid, value, 1));
                data.Add($"delivery-request recipient_did, {label}", () => MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, 1, value));
                data.Add($"delivery from, {label}", () => MessagePickup.CreateDelivery(value, AliceDid, new[] { ok }));
                data.Add($"delivery to, {label}", () => MessagePickup.CreateDelivery(MediatorDid, value, new[] { ok }));
                data.Add($"delivery recipient_did, {label}", () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { ok }, recipientDid: value));
                data.Add($"messages-received from, {label}", () => MessagePickup.CreateMessagesReceived(value, MediatorDid, new[] { "id-1" }));
                data.Add($"messages-received mediator, {label}", () => MessagePickup.CreateMessagesReceived(AliceDid, value, new[] { "id-1" }));
                data.Add($"live-delivery-change from, {label}", () => MessagePickup.CreateLiveDeliveryChange(value, MediatorDid, true));
                data.Add($"live-delivery-change mediator, {label}", () => MessagePickup.CreateLiveDeliveryChange(AliceDid, value, true));
                data.Add($"problem report from, {label}", () => MessagePickup.CreateLiveModeNotSupported(value, AliceDid, "thread-1"));
                data.Add($"problem report to, {label}", () => MessagePickup.CreateLiveModeNotSupported(MediatorDid, value, "thread-1"));
            }

            foreach (var (label, value) in new[] { ("2049 characters", IdOverCap), ("almost a megabyte", NearMegabyte) })
            {
                data.Add($"delivery message id, {label}", () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { ok, new DeliveredMessage(value, Payload) }));
                data.Add($"messages-received entry, {label}", () => MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, new[] { "id-1", value }));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(OverLongCases))]
    public void An_over_long_did_or_id_is_refused_without_being_quoted(string what, Func<object?> build)
    {
        DidOverCap.Length.Should().Be(2049);
        IdOverCap.Length.Should().Be(2049);

        var ex = ArgumentRefused(build, what);

        ex.Message.Should().Contain("2048", "the refusal names the bound ({0})", what);
    }

    public static TheoryData<string, Func<object?>> MissingCases
    {
        get
        {
            var ok = new DeliveredMessage("id-1", Payload);
            var data = new TheoryData<string, Func<object?>>();
            foreach (var (label, missing) in new[] { ("null", (string?)null), ("empty", string.Empty) })
            {
                data.Add($"status-request from {label}", () => MessagePickup.CreateStatusRequest(missing!, MediatorDid));
                data.Add($"status-request mediator {label}", () => MessagePickup.CreateStatusRequest(AliceDid, missing!));
                data.Add($"status from {label}", () => MessagePickup.CreateStatus(missing!, AliceDid, new PickupStatus(1)));
                data.Add($"status to {label}", () => MessagePickup.CreateStatus(MediatorDid, missing!, new PickupStatus(1)));
                data.Add($"delivery-request from {label}", () => MessagePickup.CreateDeliveryRequest(missing!, MediatorDid, 1));
                data.Add($"delivery-request mediator {label}", () => MessagePickup.CreateDeliveryRequest(AliceDid, missing!, 1));
                data.Add($"delivery from {label}", () => MessagePickup.CreateDelivery(missing!, AliceDid, new[] { ok }));
                data.Add($"delivery to {label}", () => MessagePickup.CreateDelivery(MediatorDid, missing!, new[] { ok }));
                data.Add($"delivery message id {label}", () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { ok, new DeliveredMessage(missing!, Payload) }));
                data.Add($"messages-received from {label}", () => MessagePickup.CreateMessagesReceived(missing!, MediatorDid, new[] { "id-1" }));
                data.Add($"messages-received mediator {label}", () => MessagePickup.CreateMessagesReceived(AliceDid, missing!, new[] { "id-1" }));
                data.Add($"messages-received entry {label}", () => MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, new[] { "id-1", missing! }));
                data.Add($"live-delivery-change from {label}", () => MessagePickup.CreateLiveDeliveryChange(missing!, MediatorDid, true));
                data.Add($"live-delivery-change mediator {label}", () => MessagePickup.CreateLiveDeliveryChange(AliceDid, missing!, true));
                data.Add($"problem report from {label}", () => MessagePickup.CreateLiveModeNotSupported(missing!, AliceDid, "thread-1"));
                data.Add($"problem report to {label}", () => MessagePickup.CreateLiveModeNotSupported(MediatorDid, missing!, "thread-1"));
                data.Add($"problem report pthid {label}", () => MessagePickup.CreateLiveModeNotSupported(MediatorDid, AliceDid, missing!));
            }

            // An empty string is not "no filter" and not "no thread": only null is.
            data.Add("status-request recipient_did empty", () => MessagePickup.CreateStatusRequest(AliceDid, MediatorDid, string.Empty));
            data.Add("delivery-request recipient_did empty", () => MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, 1, string.Empty));
            data.Add("delivery recipient_did empty", () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { ok }, recipientDid: string.Empty));
            data.Add("status recipient_did empty", () => MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1, string.Empty)));
            data.Add("status thid empty", () => MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1), string.Empty));
            data.Add("delivery thid empty", () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { ok }, thid: string.Empty));

            data.Add("status null", () => MessagePickup.CreateStatus(MediatorDid, AliceDid, null!));
            data.Add("delivery null list", () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, null!));
            data.Add("delivery null entry", () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new DeliveredMessage[] { ok, null! }));
            data.Add("messages-received null list", () => MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, null!));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void Missing_arguments_are_argument_errors(string what, Func<object?> build)
        => ArgumentRefused(build, what); // ArgumentNullException derives from ArgumentException

    [Fact]
    public void A_refusal_names_the_parameter_at_fault()
    {
        var ok = new DeliveredMessage("id-1", Payload);

        ArgumentRefused(() => MessagePickup.CreateStatusRequest(DidOverCap, MediatorDid)).ParamName.Should().Be("from");
        ArgumentRefused(() => MessagePickup.CreateStatusRequest(AliceDid, MediatorDid, DidOverCap)).ParamName.Should().Be("recipientDid");
        ArgumentRefused(() => MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, 1, DidOverCap)).ParamName.Should().Be("recipientDid");
        ArgumentRefused(() => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { ok }, recipientDid: DidOverCap)).ParamName.Should().Be("recipientDid");
        ArgumentRefused(() => MessagePickup.CreateStatus(MediatorDid, AliceDid, null!)).ParamName.Should().Be("status");
        ArgumentRefused(() => MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1, DidOverCap))).ParamName.Should().Be("status");
        ArgumentRefused(() => MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1), string.Empty)).ParamName.Should().Be("thid");
        ArgumentRefused(() => MessagePickup.CreateDelivery(MediatorDid, AliceDid, null!)).ParamName.Should().Be("messages");
        ArgumentRefused(() => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { ok, new DeliveredMessage(IdOverCap, Payload) })).ParamName.Should().Be("messages");
        ArgumentRefused(() => MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, null!)).ParamName.Should().Be("messageIds");
        ArgumentRefused(() => MessagePickup.CreateMessagesReceived(AliceDid, MediatorDid, new[] { IdOverCap })).ParamName.Should().Be("messageIds");
        ArgumentRefused(() => MessagePickup.CreateLiveModeNotSupported(MediatorDid, AliceDid, string.Empty)).ParamName.Should().Be("pthid");
    }

    public static TheoryData<string, PickupStatus> NegativeStatuses => new()
    {
        { "message_count", new PickupStatus(-7654321) },
        { "message_count, the smallest", new PickupStatus(int.MinValue) },
        { "longest_waited_seconds", new PickupStatus(1, LongestWaitedSeconds: -7654321) },
        { "newest_received_time", new PickupStatus(1, NewestReceivedTime: -7654321) },
        { "oldest_received_time", new PickupStatus(1, OldestReceivedTime: -7654321) },
        { "total_bytes", new PickupStatus(1, TotalBytes: -7654321) },
        { "total_bytes, the smallest", new PickupStatus(1, TotalBytes: long.MinValue) },
        { "the last of several", new PickupStatus(1, null, 1, 1, 1, -7654321, true) },
    };

    [Theory]
    [MemberData(nameof(NegativeStatuses))]
    public void A_negative_status_member_is_refused_without_being_quoted(string what, PickupStatus status)
    {
        var ex = ArgumentRefused(() => MessagePickup.CreateStatus(MediatorDid, AliceDid, status), what);

        ex.Should().BeOfType<ArgumentOutOfRangeException>(what);
        ex.ParamName.Should().Be("status");
        ex.Message.Should().NotContain("7654321").And.NotContain("2147483648").And.NotContain("9223372036854775808");
        ((ArgumentOutOfRangeException)ex).ActualValue.Should().BeNull("the exception carries the rule, not the number");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-7654321)]
    [InlineData(int.MinValue)]
    public void A_negative_limit_is_an_argument_error(int limit)
    {
        var act = () => MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, limit);

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("limit");
    }

    [Fact]
    public void A_negative_limit_is_refused_without_being_quoted()
    {
        // The rule of every other refusal in this class (and of #93's negative paginate offset): the
        // text names the argument and the rule, not the value.
        var act = () => MessagePickup.CreateDeliveryRequest(AliceDid, MediatorDid, -7654321);

        var ex = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.Message.Should().NotContain("7654321", "a refusal never quotes the argument");
    }

    public static TheoryData<string, string> ReservedIds => new()
    {
        { "a space", Marker + " id" },
        { "a slash", Marker + "/id" },
        { "a fragment", Marker + "#id" },
        { "a colon", Marker + ":id" },
        { "a plus", Marker + "+id" },
        { "a percent escape", Marker + "%20id" },
        { "an equals sign", Marker + "=id" },
        { "a query", Marker + "?id" },
        { "a quote", Marker + "\"id" },
        { "a brace", "{" + Marker + "}" },
        { "a non-ASCII letter", Marker + (char)0xE9 },
    };

    [Fact]
    public void A_message_id_with_a_control_character_or_half_a_surrogate_pair_is_refused_too()
    {
        // Kept out of the theory data above: these characters do not survive every test reporter.
        foreach (var id in new[] { Marker + "\nid", Marker + "\0id", Marker + "\tid", Marker + LoneSurrogate, Marker + (char)0x7F })
        {
            var ex = ArgumentRefused(() => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage(id, Payload) }));
            ex.Message.Should().Contain("unreserved");
        }
    }

    [Theory]
    [MemberData(nameof(ReservedIds))]
    public void A_message_id_with_a_reserved_character_is_refused_by_the_delivery_builder_without_being_quoted(string what, string id)
    {
        // FR-ATT-04: an attachment id is unreserved URI characters. The message model's own check
        // quotes the id; the builder's runs first and does not.
        var ex = ArgumentRefused(() => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[]
        {
            new DeliveredMessage("id-1", Payload),
            new DeliveredMessage(id, Payload),
        }), what);

        ex.Should().BeOfType<ArgumentException>(what);
        ex.ParamName.Should().Be("messages");
        ex.Message.Should().Contain("unreserved");
    }

    [Fact]
    public void Every_unreserved_character_is_accepted_in_a_message_id()
    {
        const string unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

        var delivery = MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage(unreserved, Payload) });

        delivery.Attachments!.Single().Id.Should().Be(unreserved);
    }

    [Theory]
    [InlineData("a thid with a space")]
    [InlineData("a/thid")]
    public void A_thid_the_message_model_refuses_never_yields_a_message(string thid)
    {
        // The check is the message model's (Message.Validate, FR-MSG-11), reached through Build():
        // the builders add no rule of their own for the characters of a thread id. Asserted here:
        // no message comes out. Which exception says so, and in what words, is the model's affair.
        var status = () => MessagePickup.CreateStatus(MediatorDid, AliceDid, new PickupStatus(1), thid);
        var delivery = () => MessagePickup.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("id-1", Payload) }, thid);
        var report = () => MessagePickup.CreateLiveModeNotSupported(MediatorDid, AliceDid, thid);

        status.Should().Throw<Exception>();
        delivery.Should().Throw<Exception>();
        report.Should().Throw<Exception>();
    }

    // ---- DeliveredMessage -----------------------------------------------------------------

    [Fact]
    public void A_delivered_message_prints_neither_its_id_nor_its_payload()
    {
        var payload = Encoding.UTF8.GetBytes(Marker + "-payload-0123456789");
        var message = new DeliveredMessage(Marker + "-id", payload);

        foreach (var text in new[] { message.ToString(), $"{message}", string.Format("{0}", message), string.Join(", ", new object[] { message, message }) })
        {
            text.Should().NotContain(Marker, "neither the id nor a text payload is printed");
            text.Should().NotContain("-id").And.NotContain("0123456789");
            text.Should().NotContain(Convert.ToBase64String(payload)).And.NotContain(System.Buffers.Text.Base64Url.EncodeToString(payload));
            text.Should().NotContain(Convert.ToHexString(payload)).And.NotContain(Convert.ToHexString(payload).ToLowerInvariant());
            text.Should().Contain(payload.Length + " bytes", "the length is all a log line needs");
        }

        new DeliveredMessage(Marker, ReadOnlyMemory<byte>.Empty).ToString().Should().Contain("0 bytes").And.NotContain(Marker);
    }

    [Fact]
    public void A_delivered_message_exposes_exactly_what_it_was_given()
    {
        var payload = new byte[] { 1, 2, 3 };
        var message = new DeliveredMessage("id-1", payload);

        message.Id.Should().Be("id-1");
        message.Payload.ToArray().Should().Equal(1, 2, 3);
        var (id, bytes) = message;
        id.Should().Be("id-1");
        bytes.Length.Should().Be(3);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static void IsRequest(Message message, string type)
    {
        message.Type.Should().Be(type);
        message.Typ.Should().Be(MediaTypes.Plaintext);
        message.From.Should().Be(AliceDid, "a request is sent by the recipient");
        message.To.Should().Equal(MediatorDid);
        message.Thid.Should().BeNull("a request starts its own thread");
        message.Pthid.Should().BeNull();
        message.Attachments.Should().BeNull();
        message.Id.Should().NotBeNullOrEmpty();
    }
}
