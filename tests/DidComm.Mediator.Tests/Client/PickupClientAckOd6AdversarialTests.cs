using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Transports;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Red-team reproductions against owner decision OD-6 (#100, PR #143 review round 2):
/// <see cref="PickupClient.AckAsync"/> returns only the mediator's own status answering the last
/// <c>messages-received</c>, or <c>null</c>; a message that was not sent is an exception, and a status
/// that did not come is <c>null</c>, "never a <see cref="TimeoutException"/>". Each test here fails on
/// the code under review (commit 2e3b3d0). Real channel, fake send, manual clock; nothing sleeps.
/// </summary>
public sealed class PickupClientAckOd6AdversarialTests : IAsyncLifetime
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    // ---- OD6-ADV-1 --------------------------------------------------------------------------------

    [Fact]
    public async Task OD6_ADV_1_over_HTTP_a_valid_RequestTimeout_of_one_tick_makes_AckAsync_under_3_0_throw_ArgumentOutOfRangeException_for_answerWait()
    {
        // MediatorClientOptions accepts any RequestTimeout above zero. AckAsync passes
        // RequestTimeout / 2 as answerWait; one tick / 2 rounds to zero ticks (banker's rounding of
        // 0.5), and the channel refuses a zero answerWait before it looks at the route. Over HTTP,
        // where the docs say answerWait is ignored, the empty 202 should be null.
        var time = new ManualTimeProvider();
        var send = new FakeSend(); // HTTP 202, nothing returned
        await using var channel = Create(_w, send, o => o.RequestTimeout = Tick, time: time);

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(new PickupClient(channel, logger: null).AckAsync(M, L, new[] { "q1" }, serviceEndpointOverride: Https)));

        thrown.Should().BeNull("a RequestTimeout the options accept must not make AckAsync throw an undocumented ArgumentOutOfRangeException naming an internal parameter; over HTTP an empty response is null");
        send.Count.Should().Be(1, "the acknowledgement should have been sent");
    }

    [Fact]
    public async Task OD6_ADV_1b_on_a_WebSocket_a_valid_RequestTimeout_of_one_tick_makes_AckAsync_under_3_0_throw_ArgumentOutOfRangeException_for_answerWait()
    {
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        await using var channel = Create(_w, send, o => o.RequestTimeout = Tick, time: time);

        var acking = new PickupClient(channel, logger: null).AckAsync(M, L, new[] { "q1" }, serviceEndpointOverride: Wss);
        // Whatever wait the call sets, let it run out (at most a few one-tick timers).
        for (var i = 0; i < 4 && !acking.IsCompleted; i++)
        {
            await Task.WhenAny(acking, time.WaitForScheduledTimersAsync(1, PickupTest.Timeout));
            time.Advance(Tick);
        }
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        thrown.Should().NotBeOfType<ArgumentOutOfRangeException>("a RequestTimeout the options accept must not be refused as an answerWait of zero");
        send.Count.Should().Be(1, "the acknowledgement should have been sent");
    }

    // ---- OD6-ADV-2 --------------------------------------------------------------------------------

    [Fact]
    public async Task OD6_ADV_2_a_send_that_completed_before_the_deadline_but_was_resumed_after_it_is_a_TimeoutException_not_null()
    {
        // The send completes at 20 s, inside the 30 s RequestTimeout: the message was sent. The
        // channel's continuation is late (a busy thread pool): the deadline's timer fires at 30 s
        // before the channel resumes. Modelled deterministically by a continuation on the send's task
        // registered before the channel's (both run inline on SetResult, in order), which moves the
        // clock to the deadline. The docs: "On a WebSocket, the answer is waited for answerWait once
        // the send has completed, and never past the request's RequestTimeout deadline. Either end of
        // the wait gives null"; AckAsync: TimeoutException is "Never for a status that did not come:
        // that is null"; a TimeoutException means "a message was not sent within RequestTimeout".
        var time = new ManualTimeProvider();
        var send = new FakeSend();
        await using var channel = Create(_w, send, time: time);
        var timeout = channel.Options.RequestTimeout;
        var started = time.GetUtcNow();
        var sendDone = new TaskCompletionSource<SendResult>(); // continuations run inline, in order
        _ = sendDone.Task.ContinueWith(
            _ => time.Advance(timeout - (time.GetUtcNow() - started)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        send.Respond = (_, _, _) => sendDone.Task;
        var request = _w.Request(MessagePickup.MessagesReceivedType);
        var mark = time.Mark();

        var call = channel.ExchangeAllowingNoAnswerAsync(WebSocketRoute, request, DeliveryReader.MessagesReceivedSpec, M, L, timeout / 2, CancellationToken.None).Observed();
        await time.WaitForTimerAsync(timeout, mark, PickupTest.Timeout);
        await send.FirstCall.WaitAsync(PickupTest.Timeout);
        time.Advance(TimeSpan.FromSeconds(20)).Should().Be(0, "only the deadline is set while the send runs");
        sendDone.SetResult(FakeSend.Accepted(Wss)); // the send completes at 20 s
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(call));

        thrown.Should().BeNull("the message was sent at 20 s, before the deadline; no status came, which is null, never a TimeoutException that says it was not sent");
        channel.PendingCount.Should().Be(0);
    }
}
