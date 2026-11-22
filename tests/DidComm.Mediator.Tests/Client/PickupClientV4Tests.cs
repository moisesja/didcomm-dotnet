using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient"/> speaking Message Pickup 4.0 (#100 DC-7, DC-8; FR-PICK-12) against the
/// fake exchange, pinned: the 4.0 request types and member names, the 4.0 answers read into the
/// public types (milliseconds rounded down to seconds, no <c>longest_waited_seconds</c>), the empty
/// <c>delivery</c> as a drained queue without a status, the strict 4.0 delivery (no
/// <c>attachments</c>, or more than <c>message_count_limit</c>, is refused whole before anything is
/// opened, L-055), and the acknowledgement sent one way, without <c>return_route</c>, never waited
/// for, with no status asked for after it (OD-6: <see cref="PickupClient.AckAsync"/> returns
/// <c>null</c>). Line numbers are those of the 4.0 page at <c>decentralized-identity/didcomm.org@a26926c</c>.
/// </summary>
public sealed class PickupClientV4Tests
{
    // The 4.0 page's own example time (L154), and the seconds it is in the public status.
    private const long ExampleMs = 1739420343823;
    private const long ExampleSeconds = 1739420343;

    private static FakePickupExchange V4() => new(o => o.MessagePickupVersion = MessagePickupVersion.V4);

    // ---- status-request -------------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_sends_a_4_0_status_request_and_reads_the_4_0_status_milliseconds_rounded_down_to_seconds()
    {
        var exchange = V4().On(MessagePickup40.StatusRequestType,
            r => Status40(r, count: 7, live: false, recipientDid: "did:example:carol", newestMs: ExampleMs, oldestMs: ExampleMs - 999, totalBytes: 8096));

        var status = await PickupTest.Client(exchange).GetStatusAsync(M, L, recipientDid: "did:example:carol");

        var sent = exchange.Sent.Should().ContainSingle().Subject;
        sent.Type.Should().Be(MessagePickup40.StatusRequestType);
        sent.Spec.Should().BeSameAs(PickupWire.StatusRequestSpec4);
        sent.RecipientDid.Should().Be("did:example:carol");
        sent.OneWay.Should().BeFalse("a status-request is answered");
        status.MessageCount.Should().Be(7);
        status.NewestReceivedTime.Should().Be(ExampleSeconds, "4.0 gives milliseconds (L164); the public status keeps seconds, rounded down");
        status.OldestReceivedTime.Should().Be(1739420342);
        status.LongestWaitedSeconds.Should().BeNull("4.0 has no longest_waited_seconds (L37)");
        status.TotalBytes.Should().Be(8096);
        status.LiveDelivery.Should().BeFalse();
        status.RecipientDid.Should().Be("did:example:carol");
    }

    [Fact]
    public async Task A_4_0_status_carrying_longest_waited_seconds_still_reports_none()
    {
        var exchange = V4().On(MessagePickup40.StatusRequestType, r =>
        {
            var status = MessagePickup40.CreateStatus(M, L, new PickupStatus40(1), r.Request.Id);
            status.Body![PickupBodies.LongestWaitedSeconds] = 99;
            return Snapshot(status);
        });

        var status = await PickupTest.Client(exchange).GetStatusAsync(M, L);

        status.LongestWaitedSeconds.Should().BeNull("an unknown member in 4.0 is ignored (L37)");
    }

    [Fact]
    public async Task A_3_0_status_does_not_answer_a_4_0_status_request()
    {
        // The matcher would not complete the request with it (the spec expects only the 4.0 status);
        // a fake that hands it over anyway is refused by the reader too.
        var exchange = V4().On(MessagePickup40.StatusRequestType, r => Status(r, count: 1));

        var act = () => PickupTest.Client(exchange).GetStatusAsync(M, L);

        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.Message.Should().Contain("another type");
        PickupWire.StatusRequestSpec4.Expected.Should().Equal(MessagePickup40.StatusUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("did:example:dave")]
    public async Task GetStatus_under_4_0_refuses_a_status_that_does_not_repeat_the_DID_asked_about(string? echoed)
    {
        // #100 F-1, 4.0 L168: "the matching value MUST be specified in the recipient_did attribute".
        var exchange = V4().On(MessagePickup40.StatusRequestType, r => Status40(r, count: 1, recipientDid: echoed));

        var act = () => PickupTest.Client(exchange).GetStatusAsync(M, L, recipientDid: "did:example:carol");

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.Message.Should().NotContain("did:example");
    }

    // ---- delivery-request -----------------------------------------------------------------------

    [Fact]
    public async Task Fetch_sends_message_count_limit_and_returns_and_acknowledges_what_unpacked_one_way()
    {
        var exchange = V4().On(MessagePickup40.DeliveryRequestType, r => Delivery40(r, ("q1", "ok:m1"), ("q2", "bad:x"), ("q3", "ok:m3")));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 3, recipientDid: null);

        var request = exchange.Sent[0];
        request.Type.Should().Be(MessagePickup40.DeliveryRequestType);
        request.Spec.Should().BeSameAs(PickupWire.DeliveryRequestSpec4);
        request.MessageCountLimit.Should().Be(3, "4.0 renames limit to message_count_limit (L38)");
        request.Request.Body!.ContainsKey("limit").Should().BeFalse("3.0's member is not written in 4.0");
        batch.Items.Select(i => i.Id).Should().Equal("q1", "q2", "q3");
        batch.Items[1].Error.Should().NotBeNull();
        batch.Drained.Should().BeFalse();
        batch.Status.Should().BeNull();

        var ack = exchange.Sent.Should().HaveCount(2).And.Subject.Last();
        ack.Type.Should().Be(MessagePickup40.MessagesReceivedType);
        ack.OneWay.Should().BeTrue("nothing answers a 4.0 messages-received (L62, L294)");
        ack.Ids.Should().Equal("q1", "q3");
        ack.Request.From.Should().Be(L);
        ack.Request.To.Should().Equal(M);
        ReturnRouteHeader.Read(ack.Request).Mode.Should().Be(ReturnRouteMode.None, "the header is absent (L35): none is its default");
        HasReturnRoute(ack.Request).Should().BeFalse();
        ack.Ct.CanBeCanceled.Should().BeTrue("the acknowledgement runs on a deadline of its own");
    }

    [Fact]
    public async Task An_empty_4_0_delivery_is_a_drained_batch_without_a_status_and_nothing_is_acknowledged()
    {
        var exchange = V4().On(MessagePickup40.DeliveryRequestType, r => Delivery40(r));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);

        batch.Drained.Should().BeTrue("an empty attachments array is 4.0's \"nothing to deliver\" (L29, L259)");
        batch.Status.Should().BeNull("4.0 answers a drained queue with no status");
        batch.Items.Should().BeEmpty();
        exchange.Sent.Should().ContainSingle("nothing to acknowledge");
    }

    [Fact]
    public async Task A_4_0_delivery_with_more_messages_than_its_limit_is_refused_whole_before_anything_is_opened()
    {
        // L255: MUST NOT exceed message_count_limit. Poison beyond the limit (L-055): the third
        // attachment's data is not base64 and the fourth has no id — a reader that looked at them
        // first would report those, not the count.
        var exchange = V4().On(MessagePickup40.DeliveryRequestType, r => RawDelivery40(r, Entry("q1", "ok:m1"), Entry("q2", "ok:m2"), PoisonedAttachment("q3"), Entry(null, "ok:m4")));

        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, limit: 2);

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.Message.Should().Contain("message_count_limit").And.Contain(exchange.Sent[0].Request.Id);
        exchange.Batches.Should().BeEmpty("nothing of it was opened");
        exchange.Unpacked.Should().BeEmpty();
        exchange.AcknowledgedIds.Should().BeEmpty("nothing of it is acknowledged");
    }

    [Fact]
    public async Task A_4_0_delivery_with_exactly_its_limit_is_read()
    {
        // Control for the test above: the bound is "more than", not "as many as".
        var exchange = V4().On(MessagePickup40.DeliveryRequestType, r => Delivery40(r, ("q1", "ok:m1"), ("q2", "ok:m2")));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 2);

        batch.Items.Should().HaveCount(2);
    }

    [Fact]
    public void The_4_0_count_is_refused_from_the_list_length_before_any_attachment_is_read()
    {
        // L-055 at the reader: three attachments for a limit of two, every one of which throws when
        // read. Refused by the count alone, never by touching an entry.
        var request = MessagePickup40.CreateDeliveryRequest(L, M, 2);
        var delivery = MessagePickup40.CreateDelivery(M, L, Array.Empty<DeliveredMessage>(), thid: request.Id);
        delivery.Attachments = new PoisonEvery(count: 3);

        var act = () => PickupWire.ReadDeliveryAnswer(MessagePickupVersion.V4, delivery, request.Id, PickupWire.DeliveryRequestSpec4, limit: 2, checkFilter: true, filter: null);

        act.Should().Throw<MediationProtocolException>().Which.Message.Should().Contain("message_count_limit");
    }

    [Fact]
    public async Task A_4_0_delivery_without_an_attachments_array_is_refused()
    {
        // L259: "the delivery message MUST contain an empty array of attachments" when nothing is
        // queued; without one it says neither "these" nor "nothing".
        var exchange = V4().On(MessagePickup40.DeliveryRequestType, r => Snapshot(new MessageBuilder()
            .WithType(MessagePickup40.DeliveryType).WithFrom(M).WithTo(L).WithThid(r.Request.Id).WithBody(new JsonObject()).Build()));

        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, limit: 5);

        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.Message.Should().Contain("no attachments array");
        exchange.Sent.Should().ContainSingle("nothing is acknowledged");
    }

    [Fact]
    public async Task A_status_does_not_answer_a_4_0_delivery_request()
    {
        // 4.0 answers a delivery-request with a delivery only (L210): the spec does not expect a
        // status, and the reader refuses one a fake hands over anyway.
        var exchange = V4().On(MessagePickup40.DeliveryRequestType, r => Status40(r, count: 0));

        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, limit: 5);

        await act.Should().ThrowAsync<MediationProtocolException>();
        PickupWire.DeliveryRequestSpec4.Expected.Should().Equal(MessagePickup40.DeliveryUri);
        PickupWire.DeliveryRequestSpec4.ExpectsDelivery.Should().BeTrue();
        MediatorAnswerTypes.Validate(PickupWire.DeliveryRequestSpec4);
    }

    [Fact]
    public async Task A_4_0_delivery_may_leave_the_filter_out_but_not_name_another_DID()
    {
        // L265 says when recipient_did is included, not that it must be: absent is allowed for a
        // delivery (unlike a status, F-1); another DID is refused.
        var leaves = V4().On(MessagePickup40.DeliveryRequestType, r => Delivery40(r, ("q1", "ok:m1")));
        (await PickupTest.Client(leaves).FetchAsync(M, L, limit: 5, recipientDid: L)).Items.Should().ContainSingle();

        var other = V4().On(MessagePickup40.DeliveryRequestType, r => RawDelivery40(r, new[] { Entry("q1", "ok:m1") }, recipientDid: "did:example:dave"));
        var act = () => PickupTest.Client(other).FetchAsync(M, L, limit: 5, recipientDid: L);
        await act.Should().ThrowAsync<MediationProtocolException>();
        other.AcknowledgedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_4_0_acknowledgement_is_logged_and_the_batch_is_still_returned()
    {
        var exchange = V4().On(MessagePickup40.DeliveryRequestType, r => Delivery40(r, ("q1", "ok:m1")));
        exchange.OneWay = _ => Task.FromException(new TransportException("the socket closed"));
        var logs = new PickupTestLogs();

        var batch = await PickupTest.Client(exchange, logs).FetchAsync(M, L, limit: 5);

        batch.Items.Should().ContainSingle().Which.Unpacked.Should().NotBeNull();
        logs.Entries.Should().Contain(e => e.Message.Contains("failed (TransportException)", StringComparison.Ordinal));
    }

    // ---- messages-received ----------------------------------------------------------------------

    [Fact]
    public async Task Ack_under_4_0_sends_each_100_id_chunk_one_way_asks_for_no_status_and_returns_null()
    {
        // OD-6: nothing answers a 4.0 messages-received (L62, L294), and the call does not ask for a
        // status of its own, even from a mediator that would answer one.
        var exchange = V4().On(MessagePickup40.StatusRequestType, r => Status40(r, count: 2, newestMs: ExampleMs));
        var ids = Enumerable.Range(0, 150).Select(i => $"q{i}").ToArray();

        var status = await PickupTest.Client(exchange).AckAsync(M, L, ids);

        status.Should().BeNull("under 4.0 the mediator sends no status for an acknowledgement");
        exchange.Sent.Select(s => (s.Type, s.OneWay)).Should().Equal(
            (MessagePickup40.MessagesReceivedType, true), (MessagePickup40.MessagesReceivedType, true));
        exchange.Sent[0].Ids.Should().Equal(ids[..100]);
        exchange.Sent[1].Ids.Should().Equal(ids[100..]);
        exchange.Sent.Should().OnlyContain(s => !HasReturnRoute(s.Request));
    }

    [Fact]
    public async Task Ack_under_4_0_asks_for_nothing_after_a_send_that_takes_most_of_the_RequestTimeout()
    {
        // The one-way send takes 20 of its 30 seconds; nothing follows it, and the call ends there.
        var exchange = V4().On(MessagePickup40.StatusRequestType, Unanswered);
        var clock = exchange.Clock;
        var started = clock.GetUtcNow();
        exchange.OneWay = _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        };

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }));

        status.Should().BeNull();
        exchange.Sent.Should().ContainSingle().Which.OneWay.Should().BeTrue();
        (clock.GetUtcNow() - started).Should().Be(TimeSpan.FromSeconds(20), "only the send's own time");
        clock.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task Ack_under_4_0_passes_a_failed_send_through()
    {
        var exchange = V4();
        exchange.OneWay = _ => Task.FromException(new TransportException("refused"));

        var act = () => PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1", "q2" });

        await act.Should().ThrowAsync<TransportException>();
        exchange.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Ack_under_4_0_a_send_that_was_not_completed_in_time_is_a_TimeoutException_never_null()
    {
        // The channel throws this when the send did not complete within RequestTimeout.
        var timeout = new TimeoutException("not sent within the RequestTimeout");
        var exchange = V4();
        exchange.OneWay = _ => Task.FromException(timeout);

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, Enumerable.Range(0, 150).Select(i => $"q{i}").ToArray())));

        thrown.Should().BeSameAs(timeout);
        exchange.Sent.Should().ContainSingle("the call stops at the message that was not sent");
    }

    // ---- live-delivery-change -------------------------------------------------------------------

    [Fact]
    public async Task SetLiveDelivery_under_4_0_sends_a_4_0_live_delivery_change_and_reads_the_4_0_status()
    {
        var exchange = V4();
        exchange.Route = WebSocketRoute;

        var status = await PickupTest.Client(exchange).SetLiveDeliveryAsync(M, L, liveDelivery: true);

        var sent = exchange.Sent.Should().ContainSingle().Subject;
        sent.Type.Should().Be(MessagePickup40.LiveDeliveryChangeType);
        sent.Spec.Should().BeSameAs(PickupWire.LiveDeliveryChangeSpec4);
        sent.Live.Should().BeTrue();
        status.LiveDelivery.Should().BeTrue();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static InboundMessageSnapshot RawDelivery40(SentRequest request, params Attachment[] attachments) => RawDelivery40(request, attachments, recipientDid: null);

    private static InboundMessageSnapshot RawDelivery40(SentRequest request, Attachment[] attachments, string? recipientDid)
    {
        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            Type = MessagePickup40.DeliveryType,
            From = M,
            To = new List<string> { L },
            Thid = request.Request.Id,
            Body = recipientDid is null ? new JsonObject() : new JsonObject { [PickupBodies.RecipientDid] = recipientDid },
            Attachments = attachments.ToList(),
        };
        return Snapshot(message);
    }

    private static async Task<InboundMessageSnapshot> Unanswered(SentRequest request)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, request.Ct);
        throw new InvalidOperationException("A delay without end ended.");
    }

    private static bool HasReturnRoute(Message message) => message.AdditionalHeaders?.ContainsKey("return_route") ?? false;

    // A list of `count` attachments every one of which throws when read.
    private sealed class PoisonEvery(int count) : List<Attachment>, IList<Attachment>
    {
        Attachment IList<Attachment>.this[int index]
        {
            get => throw new InvalidOperationException("an attachment was read");
            set => throw new InvalidOperationException("an attachment was written");
        }

        int ICollection<Attachment>.Count => count;
    }
}
