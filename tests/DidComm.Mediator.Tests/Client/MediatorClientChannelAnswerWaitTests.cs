using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="MediatorClientChannel.ExchangeAllowingNoAnswerAsync"/> and its <c>answerWait</c>
/// (#100, PR #143 review round 2, OD-6): a request the mediator may leave unanswered (a Message
/// Pickup 3.0 <c>messages-received</c>). On a WebSocket, silence cannot be told from a slow answer,
/// so the channel waits <c>answerWait</c> for the answer, counted from when the send completed, not
/// from the call, and inside the request's own <c>RequestTimeout</c>; nothing by then is
/// <c>null</c>. A send that does not complete, a transport failure, a problem report, the caller's
/// cancellation and the channel's disposal are never <c>null</c>. Over HTTP the response shows at
/// once whether an answer came, and <c>answerWait</c> plays no part. Real channel, fake send,
/// manual clock; nothing here sleeps.
/// </summary>
public sealed class MediatorClientChannelAnswerWaitTests : IAsyncLifetime
{
    // The window under test is shorter than half the request's deadline (30 s by default), so the
    // window and the deadline never come due at the same instant.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // A send that takes longer than the window and still finishes well inside the deadline.
    private static readonly TimeSpan SlowSend = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private readonly ManualTimeProvider _time = new();
    private readonly FakeSend _send = new();
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    // ---- on a WebSocket: the window ----------------------------------------------------------

    [Fact]
    public async Task On_a_WebSocket_the_wait_starts_when_the_send_completes_and_no_answer_by_its_end_is_null()
    {
        // (a) The send takes 15 s, longer than the 10 s window, and completes inside the 30 s
        // deadline. A window counted from the call would already be over when the send returns.
        await using var channel = Create(_w, _send, time: _time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = async (_, options, _) =>
        {
            await release.Task;
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        var request = Acknowledgement();
        var started = _time.GetUtcNow();

        var call = Exchange(channel, WebSocketRoute, request, Wait).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        _time.Advance(SlowSend).Should().Be(0, "while the send runs only the request's deadline (30 s) is set");
        call.IsCompleted.Should().BeFalse("the message is still being sent");
        var afterSend = _time.Mark();
        release.SetResult();
        await _time.WaitForTimerAsync(Wait, afterSend, PickupTest.Timeout);
        _time.Advance(Wait - Tick).Should().Be(0);
        call.IsCompleted.Should().BeFalse("the window has a tick left");
        _time.Advance(Tick).Should().Be(1, "the window, counted from the end of the send");
        var answer = await PickupTest.Within(call);

        answer.Should().BeNull("no answer came within the window after the send");
        (_time.GetUtcNow() - started).Should().Be(SlowSend + Wait);
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0, "the window and the deadline were both released");
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id))).Should().BeFalse("a status after the window answers nothing");
    }

    [Fact]
    public async Task On_a_WebSocket_a_window_the_request_deadline_cuts_short_is_null_at_the_deadline_not_a_timeout()
    {
        // (k) The send completes at 25 s, so the 10 s window would end at 35 s, after the 30 s
        // deadline. The message was sent: no answer by the deadline is null, not a TimeoutException.
        await using var channel = Create(_w, _send, time: _time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = async (_, options, _) =>
        {
            await release.Task;
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        var timeout = channel.Options.RequestTimeout;
        var lateSend = timeout - TimeSpan.FromSeconds(5);
        var started = _time.GetUtcNow();

        var call = Exchange(channel, WebSocketRoute, Acknowledgement(), Wait).Observed();
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        _time.Advance(lateSend).Should().Be(0, "the send takes 25 s and nothing ends it");
        var afterSend = _time.Mark();
        release.SetResult();
        await _time.WaitForTimerAsync(Wait, afterSend, PickupTest.Timeout);
        _time.Advance(timeout - lateSend - Tick).Should().Be(0, "nothing comes due before the deadline");
        call.IsCompleted.Should().BeFalse();
        _time.Advance(Tick).Should().Be(1, "the request's deadline, before the window would end");
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeNull("a request that was sent and got no answer is not a timeout");
        (await call).Should().BeNull("the message was sent and no answer came");
        (_time.GetUtcNow() - started).Should().Be(timeout);
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0, "the window and the deadline were both released");
    }

    [Fact]
    public async Task On_a_WebSocket_an_answer_that_arrives_just_before_the_deadline_cuts_the_window_short_still_holds()
    {
        // (l) As in (k), the deadline ends the window; here the answer arrives a tick before it.
        // Whichever runs first, the wait seeing the answer or the deadline ending the wait, the
        // answer that arrived first is returned: the deadline gives up only on a request still
        // waiting. Repeated so that the order in which those two run is varied.
        for (var round = 0; round < 20; round++)
        {
            var time = new ManualTimeProvider();
            var send = new FakeSend();
            await using var channel = Create(_w, send, time: time);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            send.Respond = async (_, options, _) =>
            {
                await release.Task;
                return FakeSend.Accepted(options.ServiceEndpointOverride!);
            };
            var timeout = channel.Options.RequestTimeout;
            var lateSend = timeout - TimeSpan.FromSeconds(5);
            var request = Acknowledgement();

            var call = Exchange(channel, WebSocketRoute, request, Wait).Observed();
            await send.FirstCall.WaitAsync(PickupTest.Timeout);
            time.Advance(lateSend).Should().Be(0);
            var afterSend = time.Mark();
            release.SetResult();
            await time.WaitForTimerAsync(Wait, afterSend, PickupTest.Timeout);
            time.Advance(timeout - lateSend - Tick).Should().Be(0);
            var status = await _w.SnapshotAsync(_w.Status(request.Id, count: 4));
            // The answer and then the deadline, back to back.
            var taken = channel.AsCorrelator().OnInbound(status);
            time.Advance(Tick);
            var answer = await PickupTest.Within(call);

            taken.Should().BeTrue("round {0}: the request was still waiting", round);
            answer.Should().NotBeNull("round {0}: the answer came before the deadline", round);
            answer!.Thid.Should().Be(request.Id);
            channel.PendingCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task On_a_WebSocket_a_send_that_never_completes_is_a_TimeoutException_at_the_RequestTimeout_never_null()
    {
        // (b)
        await using var channel = Create(_w, _send, time: _time);
        _send.Respond = (_, _, _) => new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var request = Acknowledgement();
        var timeout = channel.Options.RequestTimeout;
        var started = _time.GetUtcNow();
        var mark = _time.Mark();

        var call = Exchange(channel, WebSocketRoute, request, Wait).Observed();
        await _time.WaitForTimerAsync(timeout, mark, PickupTest.Timeout);
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        _time.Advance(timeout - Tick).Should().Be(0, "nothing ends a send before its deadline");
        call.IsCompleted.Should().BeFalse();
        _time.Advance(Tick).Should().Be(1, "the request's deadline");
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeOfType<TimeoutException>("a message that was not sent did not go unanswered")
            .Which.Message.Should().Contain(request.Id).And.NotContain(_w.Mediator.Did).And.NotContain(_w.Local.Did);
        (_time.GetUtcNow() - started).Should().Be(timeout);
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task On_a_WebSocket_a_send_that_fails_propagates_its_TransportException()
    {
        // (c)
        await using var channel = Create(_w, _send, time: _time);
        var failure = new TransportException("the socket closed");
        _send.Respond = (_, _, _) => Task.FromException<SendResult>(failure);

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(Exchange(channel, WebSocketRoute, Acknowledgement(), Wait)));

        thrown.Should().BeSameAs(failure);
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0, "no window was opened for a message that was not sent");
    }

    [Fact]
    public async Task On_a_WebSocket_an_answer_within_the_window_is_returned()
    {
        // (d)
        await using var channel = Create(_w, _send, time: _time);
        var request = Acknowledgement();
        var mark = _time.Mark();

        var call = Exchange(channel, WebSocketRoute, request, Wait).Observed();
        await _time.WaitForTimerAsync(Wait, mark, PickupTest.Timeout);
        _time.Advance(Wait - TimeSpan.FromSeconds(1)).Should().Be(0);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(request.Id, count: 3))).Should().BeTrue();
        var answer = await PickupTest.Within(call);

        answer.Should().NotBeNull();
        answer!.Thid.Should().Be(request.Id);
        answer.Type.Should().Be(MessagePickup.StatusType);
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task On_a_WebSocket_an_answer_that_arrives_before_the_send_returns_is_returned_even_when_the_send_outlasts_the_window()
    {
        // (e) Over a WebSocket the answer can come before the send returns. The send then takes
        // longer than the window; the answer already won and holds.
        await using var channel = Create(_w, _send, time: _time);
        var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.Respond = async (message, options, _) =>
        {
            answered.SetResult(channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Status(message.Id, count: 2))));
            await release.Task;
            return FakeSend.Accepted(options.ServiceEndpointOverride!);
        };
        var request = Acknowledgement();

        var call = Exchange(channel, WebSocketRoute, request, Wait).Observed();
        (await answered.Task.WaitAsync(PickupTest.Timeout)).Should().BeTrue("the request is registered before it is sent");
        _time.Advance(Wait + TimeSpan.FromSeconds(2)).Should().Be(0, "no window runs while the message is being sent");
        release.SetResult();
        var answer = await PickupTest.Within(call);

        answer.Should().NotBeNull();
        answer!.Thid.Should().Be(request.Id);
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task On_a_WebSocket_a_problem_report_within_the_window_is_a_MediationProtocolException_never_null()
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = Acknowledgement();
        var mark = _time.Mark();

        var call = Exchange(channel, WebSocketRoute, request, Wait).Observed();
        await _time.WaitForTimerAsync(Wait, mark, PickupTest.Timeout);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(_w.Problem(request.Id))).Should().BeTrue();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeOfType<MediationProtocolException>().Which.RequestId.Should().Be(request.Id);
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task On_a_WebSocket_the_callers_cancellation_during_the_window_is_an_OperationCanceledException_with_the_callers_token()
    {
        // (g)
        await using var channel = Create(_w, _send, time: _time);
        using var cts = new CancellationTokenSource();
        var mark = _time.Mark();

        var call = Exchange(channel, WebSocketRoute, Acknowledgement(), Wait, cts.Token).Observed();
        await _time.WaitForTimerAsync(Wait, mark, PickupTest.Timeout);
        await cts.CancelAsync();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(cts.Token);
        channel.PendingCount.Should().Be(0);
        _time.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task On_a_WebSocket_disposing_the_channel_during_the_window_is_an_ObjectDisposedException()
    {
        // (h)
        await using var channel = Create(_w, _send, time: _time);
        var mark = _time.Mark();

        var call = Exchange(channel, WebSocketRoute, Acknowledgement(), Wait).Observed();
        await _time.WaitForTimerAsync(Wait, mark, PickupTest.Timeout);
        channel.Dispose();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeOfType<ObjectDisposedException>();
        channel.PendingCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(-1L, true)]
    [InlineData(-10_000_000L, true)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    public async Task An_answerWait_of_zero_or_less_is_refused_before_anything_is_sent(long ticks, bool webSocket)
    {
        // (f) Over HTTP too: the argument is checked before the route is looked at.
        await using var channel = Create(_w, _send, time: _time);
        var mark = _time.Mark();

        var thrown = await Record.ExceptionAsync(() => Exchange(channel, webSocket ? WebSocketRoute : HttpRoute, Acknowledgement(), TimeSpan.FromTicks(ticks)));

        thrown.Should().BeOfType<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("answerWait");
        _send.Count.Should().Be(0, "nothing was sent");
        channel.PendingCount.Should().Be(0);
        _time.Mark().Should().Be(mark, "not even a deadline was started");
    }

    [Fact]
    public async Task On_a_WebSocket_without_an_answerWait_the_request_waits_its_whole_RequestTimeout_and_then_times_out()
    {
        // (i) As before OD-6: with no window, silence on a WebSocket is a timeout, never null.
        await using var channel = Create(_w, _send, time: _time);
        var request = Acknowledgement();
        var timeout = channel.Options.RequestTimeout;
        var started = _time.GetUtcNow();
        var mark = _time.Mark();

        var call = Exchange(channel, WebSocketRoute, request, answerWait: null).Observed();
        await _time.WaitForTimerAsync(timeout, mark, PickupTest.Timeout);
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        _time.Advance(timeout - Tick).Should().Be(0, "no shorter wait is set");
        call.IsCompleted.Should().BeFalse();
        _time.Advance(Tick).Should().Be(1);
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(request.Id);
        (_time.GetUtcNow() - started).Should().Be(timeout);
        channel.PendingCount.Should().Be(0);
    }

    // ---- over HTTP: answerWait plays no part ---------------------------------------------------

    [Theory]
    [InlineData(202)]
    [InlineData(200)]
    public async Task Over_HTTP_a_response_without_an_answer_is_null_at_once_whatever_the_answerWait(int status)
    {
        // (j)
        await using var channel = Create(_w, _send, time: _time);
        _send.Respond = (_, options, _) => Task.FromResult(FakeSend.Accepted(options.ServiceEndpointOverride!, returned: null, status));
        var started = _time.GetUtcNow();
        var mark = _time.Mark();

        var answer = await PickupTest.Within(Exchange(channel, HttpRoute, Acknowledgement(), Wait));

        answer.Should().BeNull();
        _time.GetUtcNow().Should().Be(started, "nothing waited on the clock");
        (_time.Mark() - mark).Should().Be(1, "only the request's own deadline was started, no window");
        _time.ScheduledTimers.Should().Be(0);
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_the_answer_in_the_response_is_returned()
    {
        await using var channel = Create(_w, _send, time: _time);
        _send.ReturnWith(async message => await _w.ReceiveAsync(_w.Status(message.Id, count: 1)));
        var request = Acknowledgement();

        var answer = await PickupTest.Within(Exchange(channel, HttpRoute, request, Wait));

        answer.Should().NotBeNull();
        answer!.Thid.Should().Be(request.Id);
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_a_response_that_does_not_come_within_the_RequestTimeout_is_a_TimeoutException_never_null()
    {
        await using var channel = Create(_w, _send, time: _time);
        _send.Respond = (_, _, _) => new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var timeout = channel.Options.RequestTimeout;
        var mark = _time.Mark();

        var call = Exchange(channel, HttpRoute, Acknowledgement(), Wait).Observed();
        await _time.WaitForTimerAsync(timeout, mark, PickupTest.Timeout);
        await _send.FirstCall.WaitAsync(PickupTest.Timeout);
        _time.Advance(timeout - Tick).Should().Be(0);
        call.IsCompleted.Should().BeFalse();
        _time.Advance(Tick).Should().Be(1);
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeOfType<TimeoutException>();
        channel.PendingCount.Should().Be(0);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // A Message Pickup 3.0 acknowledgement: the request a mediator may leave without an answer.
    private Message Acknowledgement() => _w.Request(MessagePickup.MessagesReceivedType);

    private Task<InboundMessageSnapshot?> Exchange(MediatorClientChannel channel, MediatorRoute route, Message request, TimeSpan? answerWait, CancellationToken ct = default)
        => channel.ExchangeAllowingNoAnswerAsync(route, request, DeliveryReader.MessagesReceivedSpec, _w.Mediator.Did, _w.Local.Did, answerWait, ct);
}
