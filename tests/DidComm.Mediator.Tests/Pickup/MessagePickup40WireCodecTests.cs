using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupWireSupport;
using ProblemReports = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The internal Message Pickup 4.0 codec (<see cref="MessagePickup40"/>, FR-PICK-12, #100) over
/// hand-written bodies: <c>message_count_limit</c> read with 3.0's limit rules and never replaced by
/// 3.0's <c>limit</c> (L-074); <c>longest_waited_seconds</c> ignored whatever it holds (4.0 L37); the
/// empty <c>delivery</c> written and required with its <c>attachments</c> array (L210, L259); the 4.0
/// problem report (L359-L370); type matching across 4.x minors and spellings; and builder refusals
/// that quote no value.
/// </summary>
public sealed class MessagePickup40WireCodecTests
{
    private const string P4 = "https://didcomm.org/message-pickup/4.0";

    // ---- message_count_limit ---------------------------------------------------------------

    [Theory]
    [InlineData("""{"message_count_limit":0}""", 0)]                 // zero is valid: it asks for nothing
    [InlineData("""{"message_count_limit":1}""", 1)]
    [InlineData("""{"message_count_limit":10}""", 10)]
    [InlineData("""{"message_count_limit":10.0}""", 10)]             // JSON does not tell 10 from 10.0
    [InlineData("""{"message_count_limit":1e1}""", 10)]
    [InlineData("""{"message_count_limit":2147483648}""", int.MaxValue)] // beyond 32 bits: clamped
    [InlineData("""{"message_count_limit":1e30}""", int.MaxValue)]
    [InlineData("""{"recipient_did":"did:example:r1","limit":3,"message_count_limit":7}""", 7)] // 3.0's member is just an unknown one here
    public void Message_count_limit_is_read_as_an_integer_and_clamped(string body, int expected)
    {
        MessagePickup40.TryParseDeliveryRequest(Raw(P4 + "/delivery-request", body), out var limit, out _).Should().BeTrue();

        limit.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"message_count_limit":-1}""", "message_count_limit")]
    [InlineData("""{"message_count_limit":1.5}""", "message_count_limit")]
    [InlineData("""{"message_count_limit":"10"}""", "message_count_limit")]
    [InlineData("""{"message_count_limit":"SENTINEL"}""", "message_count_limit")]
    [InlineData("""{"message_count_limit":null}""", "message_count_limit")]
    [InlineData("""{"message_count_limit":true}""", "message_count_limit")]
    [InlineData("""{}""", "message_count_limit")]                     // REQUIRED (L206)
    [InlineData("""{"recipient_did":"did:example:SENTINEL"}""", "message_count_limit")]
    [InlineData("""{"limit":10}""", "message_count_limit")]           // 3.0's name: malformed, no fallback
    [InlineData("""{"limit":10,"recipient_did":"did:example:SENTINEL"}""", "message_count_limit")]
    public void A_delivery_request_without_a_usable_message_count_limit_is_malformed(string body, string member)
    {
        var message = Raw(P4 + "/delivery-request", body);

        var refusal = Refused(() => MessagePickup40.TryParseDeliveryRequest(message, out _, out _));

        refusal.Message.Should().Contain(member).And.StartWith("delivery-request");
    }

    [Fact]
    public void A_delivery_request_without_a_body_is_malformed()
    {
        var message = Raw(P4 + "/delivery-request", body: null);

        Refused(() => MessagePickup40.TryParseDeliveryRequest(message, out _, out _)).Message.Should().Contain("message_count_limit");
    }

    [Fact]
    public void The_builder_writes_message_count_limit_never_limit()
    {
        BodyOf(MessagePickup40.CreateDeliveryRequest(AliceDid, MediatorDid, 0)).Should().Be("""{"message_count_limit":0}""");
        BodyOf(MessagePickup40.CreateDeliveryRequest(AliceDid, MediatorDid, 3, "did:example:r1")).Should().Be("""{"message_count_limit":3,"recipient_did":"did:example:r1"}""");
    }

    // ---- status ----------------------------------------------------------------------------

    [Theory]
    [InlineData("3600")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"SENTINEL\"")]
    [InlineData("{\"SENTINEL\":[1]}")]
    [InlineData("null")]
    public void Longest_waited_seconds_is_not_a_4_0_member_and_is_ignored_whatever_it_holds(string value)
    {
        var message = Raw(P4 + "/status", $$"""{"message_count":2,"longest_waited_seconds":{{value}},"newest_received_time":1739420343823}""");

        MessagePickup40.TryParseStatus(message, out var status).Should().BeTrue();

        status.Should().Be(new PickupStatus40(2, NewestReceivedTimeMs: 1739420343823));
        status!.ToPickupStatus().LongestWaitedSeconds.Should().BeNull();
    }

    [Fact]
    public void A_4_0_status_never_writes_longest_waited_seconds_and_keeps_milliseconds()
    {
        var status = MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(1, null, 1739420343823, 1739420343001, 5, true), "thread-1");

        BodyOf(status).Should().Be("""{"message_count":1,"newest_received_time":1739420343823,"oldest_received_time":1739420343001,"total_bytes":5,"live_delivery":true}""");
        status.Thid.Should().Be("thread-1");
        MessagePickup40.TryParseStatus(RoundTrip(status), out var back).Should().BeTrue();
        back.Should().Be(new PickupStatus40(1, null, 1739420343823, 1739420343001, 5, true), "each time is read back from its own member");
    }

    [Fact]
    public void A_status_s_milliseconds_round_down_to_seconds_in_the_public_record()
    {
        new PickupStatus40(1, null, 1999, 1000).ToPickupStatus().Should().Be(new PickupStatus(1, null, null, 1, 1));
    }

    // ---- delivery --------------------------------------------------------------------------

    [Fact]
    public void An_empty_delivery_writes_an_empty_attachments_array()
    {
        var empty = MessagePickup40.CreateDelivery(MediatorDid, AliceDid, Array.Empty<DeliveredMessage>(), "thread-1");

        AttachmentsOf(empty).Should().Be("[]");
        Wire(empty).Should().Contain("\"attachments\":[]");
        BodyOf(empty).Should().Be("{}");
        MessagePickup40.TryParseDelivery(RoundTrip(empty), out var messages, out var recipientDid).Should().BeTrue();
        messages.Should().BeEmpty();
        recipientDid.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void A_4_0_delivery_without_an_attachments_array_is_malformed(string? attachments)
    {
        // L259: an empty delivery carries an empty array; one without the member says nothing.
        var message = Raw(P4 + "/delivery", """{"recipient_did":"did:example:SENTINEL"}""", attachments);

        Refused(() => MessagePickup40.TryParseDelivery(message, out _, out _)).Message.Should().Contain("attachments");

        // The 3.0 reader, for the same shape, reads no messages (3.0 has no such rule).
        MessagePickup.TryParseDelivery(Raw(MessagePickup.DeliveryType, """{}""", attachments), out var none, out _).Should().BeTrue();
        none.Should().BeEmpty();
    }

    [Fact]
    public void A_live_delivery_has_no_thread_and_no_filter()
    {
        var live = MessagePickup40.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage("id-1", new byte[] { 1 }) });

        live.Thid.Should().BeNull();
        BodyOf(live).Should().Be("{}");
        live.Attachments.Should().ContainSingle().Which.Id.Should().Be("id-1");
    }

    // ---- the 4.0 problem report ------------------------------------------------------------

    [Fact]
    public void The_live_mode_problem_report_has_the_4_0_type_pthid_code_and_comment()
    {
        var report = MessagePickup40.CreateLiveModeNotSupported(MediatorDid, AliceDid, "thread-1");

        report.Type.Should().Be(P4 + "/problem-report").And.NotBe(ProblemReports.MessageType);
        report.Pthid.Should().Be("thread-1");
        report.Thid.Should().BeNull();
        report.From.Should().Be(MediatorDid);
        report.To.Should().Equal(AliceDid);
        BodyOf(report).Should().Be("""{"code":"e.m.live-mode-not-supported","comment":"Connection does not support Live Delivery"}""");
        MessagePickup40.IsProblemReport(RoundTrip(report)).Should().BeTrue();
        MessagePickup40.IsProblemReport(MessagePickup.CreateLiveModeNotSupported(MediatorDid, AliceDid, "thread-1")).Should().BeFalse("3.0's report-problem/2.0 is not 4.0's");
    }

    // ---- type matching ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://didcomm.org/message-pickup/4.0/status-request", true)]
    [InlineData("https://didcomm.org/message-pickup/4.7/status-request", true)]  // any 4.x minor
    [InlineData("https://didcomm.org/messagepickup/4.0/status-request", true)]   // the 3.0 spelling of the name
    [InlineData("https://DIDCOMM.org/Message_Pickup/4.0/STATUS-request", true)]
    [InlineData("https://didcomm.org/message-pickup/4.0/status_request", true)]
    [InlineData("https://didcomm.org/message-pickup/3.0/status-request", false)] // another major version
    [InlineData("https://didcomm.org/messagepickup/3.0/status-request", false)]
    [InlineData("https://didcomm.org/message-pickup/5.0/status-request", false)]
    [InlineData("https://didcomm.org/message-pickup/4.0/status", false)]          // another message
    [InlineData("https://example.org/message-pickup/4.0/status-request", false)] // another organisation
    public void Types_match_across_4_x_minors_case_and_punctuation_and_nothing_else(string type, bool matches)
    {
        MessagePickup40.TryParseStatusRequest(Raw(type, "{}"), out _).Should().Be(matches, type);
        MessagePickup.TryParseStatusRequest(Raw(type, "{}"), out _).Should().Be(type.Contains("/3.0/", StringComparison.Ordinal) && type.EndsWith("/status-request", StringComparison.Ordinal), "the 3.0 parser reads 3.x only: {0}", type);
    }

    [Fact]
    public void Builders_write_the_canonical_type_strings()
    {
        MessagePickup40.CreateStatusRequest(AliceDid, MediatorDid).Type.Should().Be(P4 + "/status-request");
        MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(0), "t").Type.Should().Be(P4 + "/status");
        MessagePickup40.CreateDeliveryRequest(AliceDid, MediatorDid, 1).Type.Should().Be(P4 + "/delivery-request");
        MessagePickup40.CreateDelivery(MediatorDid, AliceDid, Array.Empty<DeliveredMessage>()).Type.Should().Be(P4 + "/delivery");
        MessagePickup40.CreateMessagesReceived(AliceDid, MediatorDid, new[] { "a" }).Type.Should().Be(P4 + "/messages-received");
        MessagePickup40.CreateLiveDeliveryChange(AliceDid, MediatorDid, true).Type.Should().Be(P4 + "/live-delivery-change");
    }

    // ---- builder refusals ------------------------------------------------------------------

    public static TheoryData<string, Func<object?>> BuilderRefusals => new()
    {
        { "status: negative count", () => MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(-1), "t") },
        { "status: negative newest time", () => MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(1, NewestReceivedTimeMs: -1), "t") },
        { "status: negative oldest time", () => MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(1, OldestReceivedTimeMs: -1), "t") },
        { "status: negative bytes", () => MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(1, TotalBytes: -1), "t") },
        { "status: recipient DID over the bound", () => MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(1, DidOverCap), "t") },
        { "status: no thread", () => MessagePickup40.CreateStatus(MediatorDid, AliceDid, new PickupStatus40(1), string.Empty) },
        { "status: sender over the bound", () => MessagePickup40.CreateStatus(DidOverCap, AliceDid, new PickupStatus40(1), "t") },
        { "status-request: filter over the bound", () => MessagePickup40.CreateStatusRequest(AliceDid, MediatorDid, DidOverCap) },
        { "delivery-request: negative limit", () => MessagePickup40.CreateDeliveryRequest(AliceDid, MediatorDid, -1) },
        { "delivery-request: filter over the bound", () => MessagePickup40.CreateDeliveryRequest(AliceDid, MediatorDid, 1, DidOverCap) },
        { "delivery: id over the bound", () => MessagePickup40.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage(IdOverCap, new byte[] { 1 }) }) },
        { "delivery: id with reserved characters", () => MessagePickup40.CreateDelivery(MediatorDid, AliceDid, new[] { new DeliveredMessage(Marker + "#/?", new byte[] { 1 }) }) },
        { "delivery: filter over the bound", () => MessagePickup40.CreateDelivery(MediatorDid, AliceDid, Array.Empty<DeliveredMessage>(), "t", DidOverCap) },
        { "delivery: recipient over the bound", () => MessagePickup40.CreateDelivery(MediatorDid, DidOverCap, Array.Empty<DeliveredMessage>()) },
        { "messages-received: id over the bound", () => MessagePickup40.CreateMessagesReceived(AliceDid, MediatorDid, new[] { IdOverCap }) },
        { "messages-received: empty id", () => MessagePickup40.CreateMessagesReceived(AliceDid, MediatorDid, new[] { string.Empty }) },
        { "live-delivery-change: mediator over the bound", () => MessagePickup40.CreateLiveDeliveryChange(AliceDid, DidOverCap, true) },
        { "problem report: no thread", () => MessagePickup40.CreateLiveModeNotSupported(MediatorDid, AliceDid, string.Empty) },
        { "problem report: recipient over the bound", () => MessagePickup40.CreateLiveModeNotSupported(MediatorDid, DidOverCap, "t") },
    };

    [Theory]
    [MemberData(nameof(BuilderRefusals))]
    public void Builders_refuse_unusable_arguments_without_quoting_them(string what, Func<object?> build)
        => ArgumentRefused(build, what);
}
