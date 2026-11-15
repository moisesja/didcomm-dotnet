using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The consumer's cancellation against a live stream whose mediator (or unpack step) ignores the
/// token it is handed (#119). Whichever step of a cycle is in progress when the token is cancelled —
/// the wait for the next cycle, an acknowledgement, the <c>live-delivery-change</c>, a drain round,
/// the opening of a delivered message — the pending <c>MoveNextAsync</c> ends with an
/// <see cref="OperationCanceledException"/> once that step returns, no item is yielded after the
/// cancellation, nothing more is asked of the mediator but the end's <c>live-delivery-change{false}</c>,
/// and what was acknowledged is acknowledged once. Each step is held at a rendezvous until the token
/// has been cancelled, then released (L-044: no sleeps).
/// </summary>
public sealed class PickupClientLiveCancellationTests
{
    // Seven seconds: no back-off step (1, 2, 4, 8, 16, 30 s), no RequestTimeout and no budget has
    // this length, so a timer of exactly one interval is the stream's wait for its next cycle (#119 F3).
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);

    [Theory]
    [InlineData("waiting for the next cycle")]
    [InlineData("acknowledging before the wait")]
    [InlineData("turning live delivery on")]
    [InlineData("in a drain round")]
    [InlineData("opening a delivered message")]
    [InlineData("acknowledging after an item")]
    public async Task A_cancellation_that_lands_while_any_step_of_a_cycle_ignores_the_token_ends_the_move_with_OperationCanceledException_and_yields_nothing_after_it(string step)
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        var hold = new Hold();
        // Cycle 1: live-on, a drain round that brings q1, one that ends the drain. Cycle 2 (one
        // interval later): live-on, a drain round that brings q2, one that ends the drain.
        var liveOns = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Task.Yield();
            var n = r.Live ? Interlocked.Increment(ref liveOns) : 0;
            if (n > 5)
                await cts.CancelAsync(); // a regression that loops must fail, not hang
            if (n == 2 && step == "turning live delivery on")
                await hold.AtAsync(r.Ct);
            return Status(r, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            var n = Interlocked.Increment(ref drains);
            if (n == 3 && step == "in a drain round")
                await hold.AtAsync(r.Ct);
            return n switch
            {
                1 => Delivery(r, ("q1", "ok:m1")),
                3 => Delivery(r, ("q2", "ok:m2")),
                _ => Status(r),
            };
        });
        exchange.On(MessagePickup.MessagesReceivedType, async r =>
        {
            if ((step == "acknowledging before the wait" && r.Ids.Contains("q1")) || (step == "acknowledging after an item" && r.Ids.Contains("q2")))
                await hold.AtAsync(r.Ct);
            return Status(r);
        });
        exchange.Unpacker = async (text, ct) =>
        {
            if (text == "ok:m2" && step == "opening a delivered message")
                await hold.AtAsync(ct);
            return DefaultOutcome(text);
        };
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await Next(stream)).Id.Should().Be("q1");
        var sent = exchange.Sent.Count;

        // Asking for the next item takes q1: its acknowledgement, the drain's last round, the wait.
        var mark = exchange.Clock.Mark();
        var pending = stream.MoveNextAsync().AsTask();
        if (step == "acknowledging before the wait")
        {
            await PickupTest.Within(hold.Entered);
        }
        else
        {
            await exchange.Clock.WaitForTimerAsync(Interval, mark, PickupTest.Timeout);
            if (step != "waiting for the next cycle")
            {
                exchange.Clock.Advance(Interval).Should().Be(1, "the wait for the next cycle fires");
                if (step == "acknowledging after an item")
                {
                    (await PickupTest.Within(pending)).Should().BeTrue();
                    stream.Current.Id.Should().Be("q2");
                    pending = stream.MoveNextAsync().AsTask(); // takes q2: its acknowledgement is held
                }
                await PickupTest.Within(hold.Entered);
            }
        }

        await cts.CancelAsync();
        hold.Release();

        await FluentActions.Awaiting(() => PickupTest.Within(pending)).Should().ThrowAsync<OperationCanceledException>(
            "the consumer's cancellation ends the move once the step in progress ({0}) returns, whatever that step did with the token", step);
        if (step != "waiting for the next cycle")
            hold.CancelledInside.Should().BeTrue("the token was cancelled while the step was held");
        await stream.DisposeAsync();

        var after = Types(exchange).Skip(sent).ToArray();
        switch (step)
        {
            case "waiting for the next cycle":
                after.Should().Equal(Ack, Drain, LiveOff);
                break;
            case "acknowledging before the wait":
                after.Should().Equal(new[] { Ack, LiveOff }, "nothing more is asked after the cancelled acknowledgement");
                break;
            case "turning live delivery on":
                after.Should().Equal(new[] { Ack, Drain, LiveOn, LiveOff }, "no drain round after the cancelled live-on");
                break;
            case "in a drain round":
                after.Should().Equal(Ack, Drain, LiveOn, Drain, LiveOff);
                exchange.Unpacked.Should().NotContain("ok:m2", "the delivery that answered the cancelled round is not opened");
                break;
            case "opening a delivered message":
                after.Should().Equal(Ack, Drain, LiveOn, Drain, LiveOff);
                exchange.AcknowledgedIds.Should().NotContain("q2", "q2 was opened after the cancellation and never yielded");
                break;
            case "acknowledging after an item":
                after.Should().Equal(new[] { Ack, Drain, LiveOn, Drain, Ack, LiveOff }, "nothing more is asked after the cancelled acknowledgement");
                exchange.AcknowledgedIds.Count(id => id == "q2").Should().Be(1, "q2 was taken before the cancellation and is acknowledged once");
                break;
        }
        exchange.AcknowledgedIds.Should().OnlyHaveUniqueItems();
        exchange.Registrations.Should().BeEmpty("the mailbox is closed at the end");
    }

    /// <summary>A step that waits for its release whatever happens to the token it was handed.</summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelledInside;

        public Task Entered => _entered.Task;

        public bool CancelledInside => Volatile.Read(ref _cancelledInside) != 0;

        public void Release() => _release.TrySetResult();

        public async Task AtAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task;
            if (ct.IsCancellationRequested)
                Interlocked.Exchange(ref _cancelledInside, 1);
        }
    }
}
