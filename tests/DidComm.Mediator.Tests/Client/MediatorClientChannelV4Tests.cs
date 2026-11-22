using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The channel and Message Pickup 4.0 (#100 DC-7, DC-8): the one-way send a 4.0
/// <c>messages-received</c> goes through (no <c>return_route</c> header, nothing registered, bounded
/// by <see cref="MediatorClientOptions.RequestTimeout"/> and counted against
/// <see cref="MediatorClientOptions.MaxPendingRequests"/>, <c>created_time</c> stamped by the send
/// because the type is a mediator-service protocol's, FR-RR-09), and the matcher's 4.0 answer types:
/// a 4.0 <c>status</c> completes the request its <c>thid</c> names (ignoring letter case), a 4.0
/// <c>problem-report</c> the one its <c>pthid</c> names with its code, and a thread-less 4.0
/// <c>delivery</c> is a live push, never an answer.
/// </summary>
public sealed class MediatorClientChannelV4Tests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    private Message Ack(IMessageIdGenerator? ids = null)
        => MessagePickup40.CreateMessagesReceived(L, M, new[] { "q1" }, ids ?? UuidV4MessageIdGenerator.Instance);

    // ---- the one-way send -----------------------------------------------------------------------

    [Fact]
    public async Task A_one_way_message_is_sent_once_without_return_route_to_the_route_endpoint_authcrypted_from_the_local_DID()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = Ack();

        await PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, request, "messages-received", M, L, default));

        var call = _send.Calls.Should().ContainSingle().Which;
        call.Message.Should().BeSameAs(request, "the channel never stamps or rewrites the message (SendAsync stamps a copy)");
        call.Options.ReturnRoute.Should().BeNull("no header: none is the extension's default when it is omitted (return_route main.md L25; 4.0 L35)");
        call.Options.Recipients.Should().Equal(M);
        call.Options.From.Should().Be(L);
        call.Options.ServiceEndpointOverride.Should().Be(Wss);
        call.Token.CanBeCanceled.Should().BeTrue("the send runs under a deadline");
        channel.PendingCount.Should().Be(0, "the slot is given back when the send completes");
        _time.ScheduledTimers.Should().Be(0, "the deadline was released");
    }

    [Fact]
    public async Task The_real_send_writes_no_return_route_and_stamps_created_time_because_message_pickup_4_0_is_a_mediator_service_protocol()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_456);
        await using var world = await ClientWorld.CreateAsync(o => o.Clock = () => now);
        var router = new RecordingRouter();
        var client = new DidCommClient(world.Secrets, world.Services.GetRequiredService<Resolution.IDidKeyService>(), world.Services.GetRequiredService<Resolution.IServiceEndpointResolver>(), router,
            new DidCommOptions { Clock = () => now });
        using var channel = new MediatorClientChannel(client, new MediatorClientOptions(), receivesInbound: false, _time, loggerFactory: null);

        await PickupTest.Within(channel.SendOneWayAsync(HttpRoute, MessagePickup40.CreateMessagesReceived(world.Local.Did, world.Mediator.Did, new[] { "q1" }), "messages-received", world.Mediator.Did, world.Local.Did, default));

        var request = router.Requests.Should().ContainSingle().Subject;
        request.ReturnRoute.Should().BeNull("the transport is not asked for a return route either");
        request.Endpoint.Should().Be(Https);
        var sent = (await world.Client.UnpackAsync(Encoding.UTF8.GetString(request.Payload.Span))).Message;
        sent.Type.Should().Be(MessagePickup40.MessagesReceivedType);
        (sent.AdditionalHeaders?.ContainsKey("return_route") ?? false).Should().BeFalse("the header is absent from what was sent");
        ReturnRouteHeader.Read(sent).Mode.Should().Be(ReturnRouteMode.None);
        sent.CreatedTime.Should().Be(now.ToUnixTimeSeconds(), "FR-RR-09: a message of a mediator-service protocol carries created_time whatever its header");
    }

    [Fact]
    public async Task A_send_that_stalls_ends_with_a_TimeoutException_naming_its_id_at_exactly_one_RequestTimeout()
    {
        await using var channel = Create(_w, _send, time: _time);
        _send.Respond = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };
        var request = Ack();
        var mark = _time.Mark();

        var sending = channel.SendOneWayAsync(WebSocketRoute, request, "messages-received", M, L, default);
        await _time.WaitForTimerAsync(channel.Options.RequestTimeout, mark, PickupTest.Timeout);
        sending.IsCompleted.Should().BeFalse("nothing ends it before its deadline");
        _time.Advance(channel.Options.RequestTimeout).Should().Be(1, "the send's deadline is the only timer");
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(sending));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(request.Id).And.Contain("messages-received").And.NotContain(M).And.NotContain(L);
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_send_in_flight_counts_against_MaxPendingRequests_and_its_slot_frees_when_it_completes()
    {
        await using var channel = Create(_w, _send, o => o.MaxPendingRequests = 1, time: _time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = async (_, options, _) =>
        {
            await release.Task;
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        var first = channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default);
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var refused = Ack();

        Exception? oneWay;
        Exception? exchange;
        try
        {
            oneWay = await Record.ExceptionAsync(() => PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, refused, "messages-received", M, L, default)));
            exchange = await Record.ExceptionAsync(() => PickupTest.Within(channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, M, L, default)));
        }
        finally
        {
            release.TrySetResult();
        }

        oneWay.Should().BeOfType<MediatorBusyException>().Which.Message.Should().Contain("MaxPendingRequests").And.Contain(refused.Id);
        exchange.Should().BeOfType<MediatorBusyException>("a one-way send holds a slot like a request waiting for its answer");
        _send.Count.Should().Be(1, "neither refused message was sent");
        await PickupTest.Within(first);
        channel.PendingCount.Should().Be(0);
        await PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default));
    }

    [Fact]
    public async Task Nothing_is_registered_so_a_status_naming_the_message_while_it_is_sent_completes_nothing()
    {
        await using var channel = Create(_w, _send, time: _time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _send.Respond = async (_, options, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                await release.Task; // the one-way send, held while the status arrives
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        // Something else waits, so the matcher is not idle and looks the status's thread up.
        var waiting = channel.ExchangeAsync(WebSocketRoute, _w.Request(), ClientSpecs.StatusRequest, M, L, default).Observed();
        var request = Ack();
        var sending = channel.SendOneWayAsync(WebSocketRoute, request, "messages-received", M, L, default);
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, PickupTest.Timeout).Should().BeTrue();
        var lookupsBefore = channel.Lookups;

        var claimed = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(StatusFor(request.Id)));

        claimed.Should().BeFalse("nothing waits for an answer to a one-way message");
        channel.Lookups.Should().Be(lookupsBefore + 1, "the status was looked up, and nothing waits under its thread");
        sending.IsCompleted.Should().BeFalse("the send is still held");
        waiting.IsCompleted.Should().BeFalse();
        release.SetResult();
        await PickupTest.Within(sending);
    }

    [Fact]
    public async Task An_HTTP_response_that_carries_a_message_is_ignored()
    {
        await using var channel = Create(_w, _send, time: _time);
        _send.ReturnWith(async m => await _w.ReceiveAsync(StatusFor(m.Id)));

        await PickupTest.Within(channel.SendOneWayAsync(HttpRoute, Ack(), "messages-received", M, L, default));

        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_transport_failure_passes_through_as_it_is()
    {
        await using var channel = Create(_w, _send, time: _time);
        var failure = new TransportException("the connection closed");
        _send.Respond = (_, _, _) => Task.FromException<SendResult>(failure);

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default)));

        thrown.Should().BeSameAs(failure);
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task The_callers_cancellation_ends_a_stalled_send_as_a_cancellation()
    {
        await using var channel = Create(_w, _send, time: _time);
        _send.Respond = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };
        using var cts = new CancellationTokenSource();
        var sending = channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, cts.Token);
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);

        cts.Cancel();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(sending));

        thrown.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task A_disposed_channel_refuses_and_a_disposal_ends_a_stalled_send_with_ObjectDisposedException()
    {
        var channel = Create(_w, _send, time: _time);
        _send.Respond = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };
        var sending = channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default);
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);

        await channel.DisposeAsync();
        var stalled = await Record.ExceptionAsync(() => PickupTest.Within(sending));
        var after = await Record.ExceptionAsync(() => PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, Ack(), "messages-received", M, L, default)));

        stalled.Should().BeOfType<ObjectDisposedException>();
        after.Should().BeOfType<ObjectDisposedException>();
        _send.Count.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task A_message_id_outside_1_to_64_characters_is_refused_before_sending(int length)
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = Ack();
        request.Id = new string('a', length);

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(channel.SendOneWayAsync(WebSocketRoute, request, "messages-received", M, L, default)));

        thrown.Should().BeOfType<ArgumentException>();
        _send.Count.Should().Be(0);
    }

    // ---- a pin the mediator does not speak --------------------------------------------------------

    [Fact]
    public async Task A_pinned_V4_against_a_mediator_that_does_not_speak_it_times_out_over_HTTP_and_nothing_falls_back_to_3_0()
    {
        // A 3.0-only mediator does not answer a 4.0 status-request: over HTTP, a 202 with nothing.
        await using var channel = Create(_w, _send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: _time);
        var pickup = new PickupClient(channel, logger: null);

        var thrown = await Record.ExceptionAsync(() => pickup.GetStatusAsync(M, L, serviceEndpointOverride: Https));

        var sent = _send.Calls.Should().ContainSingle("a pinned version is never second-guessed").Which.Message;
        sent.Type.Should().Be(MessagePickup40.StatusRequestType);
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(sent.Id);
    }

    [Fact]
    public async Task A_pinned_V4_against_a_mediator_that_does_not_speak_it_times_out_over_a_WebSocket_at_one_RequestTimeout()
    {
        await using var channel = Create(_w, _send, o => o.MessagePickupVersion = MessagePickupVersion.V4, time: _time);
        var pickup = new PickupClient(channel, logger: null);
        var mark = _time.Mark();

        var fetching = pickup.FetchAsync(M, L, limit: 5, serviceEndpointOverride: Wss);
        await _time.WaitForTimerAsync(channel.Options.RequestTimeout, mark, PickupTest.Timeout);
        _time.Advance(channel.Options.RequestTimeout).Should().Be(1);
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(fetching));

        var sent = _send.Calls.Should().ContainSingle().Which.Message;
        sent.Type.Should().Be(MessagePickup40.DeliveryRequestType);
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(sent.Id);
    }

    // ---- the 4.0 answer types -------------------------------------------------------------------

    [Fact]
    public async Task A_4_0_status_whose_thid_is_the_request_id_in_upper_case_completes_a_4_0_status_request()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = MessagePickup40.CreateStatusRequest(L, M, idGenerator: new LetterIds("request")); // an id certain to hold letters
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, PickupWire.StatusRequestSpec4, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var upper = request.Id.ToUpperInvariant();
        upper.Should().NotBe(request.Id, "precondition: the id has letters to re-case");

        var claimed = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(StatusFor(upper)));
        var answer = await PickupTest.Within(exchange);

        claimed.Should().BeTrue();
        answer.Type.Should().Be(MessagePickup40.StatusType);
    }

    [Fact]
    public async Task A_4_0_status_without_thid_completes_nothing()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = MessagePickup40.CreateStatusRequest(L, M);
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, PickupWire.StatusRequestSpec4, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var status = StatusFor(request.Id);
        status.Thid = null;

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(status)).Should().BeFalse("a reply without thid answers no request (owner answer 2, #98)");
        exchange.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_4_0_problem_report_naming_the_request_in_pthid_fails_it_with_its_code()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = MessagePickup40.CreateLiveDeliveryChange(L, M, liveDelivery: true);
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, PickupWire.LiveDeliveryChangeSpec4, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);

        var claimed = channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(MessagePickup40.CreateLiveModeNotSupported(M, L, request.Id)));
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(exchange));

        claimed.Should().BeTrue();
        var report = thrown.Should().BeOfType<MediationProtocolException>().Subject;
        report.ProblemCode.Should().Be(MessagePickup.LiveModeNotSupportedCode);
        report.RequestId.Should().Be(request.Id);
    }

    [Fact]
    public async Task A_thread_less_4_0_delivery_is_a_live_push_and_never_answers_a_waiting_delivery_request()
    {
        await using var channel = Create(_w, _send, time: _time);
        using var stream = channel.RegisterLiveStream(M, L);
        var request = MessagePickup40.CreateDeliveryRequest(L, M, 10);
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, PickupWire.DeliveryRequestSpec4, M, L, default).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        var push = MessagePickup40.CreateDelivery(M, L, new[] { new DeliveredMessage("q1", Encoding.UTF8.GetBytes(FakePickupExchange.Jwe("x"))) });

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(push)).Should().BeTrue();

        stream.Pushes.TryRead(out var pushed).Should().BeTrue();
        pushed!.Type.Should().Be(MessagePickup40.DeliveryType);
        exchange.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void The_4_0_answer_types_are_classified_by_name_and_kind()
    {
        Classify(MessagePickup40.StatusType).Should().Be(("status (4.0)", MediatorAnswerKind.Answer));
        Classify(MessagePickup40.DeliveryType).Should().Be(("delivery (4.0)", MediatorAnswerKind.Delivery));
        Classify(MessagePickup40.ProblemReportType).Should().Be(("problem-report (4.0)", MediatorAnswerKind.ProblemReport));
        Classify(MessagePickup.StatusType).Should().Be(("status", MediatorAnswerKind.Answer), "3.0's entries are unchanged");
        Classify(MessagePickup.DeliveryType).Should().Be(("delivery", MediatorAnswerKind.Delivery));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private Message StatusFor(string thid) => MessagePickup40.CreateStatus(M, L, new PickupStatus40(0, LiveDelivery: true), thid);

    private static (string, MediatorAnswerKind) Classify(string type)
    {
        MediatorAnswerTypes.TryClassify(DidComm.Protocols.MessageTypeUri.Parse(type), out var name, out var kind).Should().BeTrue();
        return (name, kind);
    }

    private sealed class RecordingRouter : ITransportRouter
    {
        public System.Collections.Concurrent.ConcurrentQueue<TransportRequest> Requests { get; } = new();

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }
}
