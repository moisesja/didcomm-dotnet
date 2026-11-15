using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Fix round 3 (#96) regressions for the live stream, beside the ADV-3 reproductions they close
/// (<see cref="PickupClientLiveAdv3Tests"/>): once a cycle drains, its drain rounds and the pushes
/// take turns, a drain round first (ADV3-1); a host clock that steps back makes the stream's stored
/// points due instead of postponing them (ADV3-5). Driven by the fake exchange and manual clocks;
/// nothing sleeps (L-044).
/// </summary>
public sealed class PickupClientLiveFix3Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;

    /// <summary>
    /// ADV3-1: a sender whose DID never resolves keeps a push waiting at every moment, each taking
    /// the whole budget to open. Every cycle still drains, its drain round before the waiting push,
    /// and a message that only a later cycle's drain reaches (one its sender delayed) arrives during
    /// the attack.
    /// </summary>
    [Fact]
    public async Task Under_a_steady_flow_of_slow_pushes_every_cycle_drains_first_and_a_message_only_a_later_drain_reaches_arrives()
    {
        var (exchange, client) = Live(o => o.UnpackTimeout = Budget);
        exchange.UseBudget(Budget);
        // Nothing is queued at the start; one interval later a delayed message is due; then nothing.
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Status(r), r => Delivery(r, ("late", "ok:late")), r => Status(r));
        exchange.Unpacker = StallsUntilCancelled;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (r.Live && exchange.SentOf(MessagePickup.LiveDeliveryChangeType).Count == 1)
                exchange.Registration!.TryPush(Push(("s0", "stall:s0"))).Should().BeTrue();
            return Status(r, live: r.Live);
        });

        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask();
        var perInterval = (int)(exchange.Options.LivePollInterval.Ticks / Budget.Ticks);
        var items = new List<PickupItem>();
        try
        {
            for (var i = 0; i < 3 * perInterval; i++)
            {
                // A slow push is being opened (its budget and its share); the next one is waiting.
                await exchange.Clock.WaitForScheduledTimersAsync(2, PickupTest.Timeout);
                exchange.Registration!.TryPush(Push(($"s{i + 1}", $"stall:s{i + 1}"))).Should().BeTrue();
                exchange.Clock.Advance(Budget);
                (await PickupTest.Within(move)).Should().BeTrue();
                items.Add(stream.Current);
                move = stream.MoveNextAsync().AsTask();
                if (i == perInterval - 1)
                {
                    // The second cycle came due: its drain round runs before the waiting push.
                    (await PickupTest.Within(move)).Should().BeTrue();
                    items.Add(stream.Current);
                    move = stream.MoveNextAsync().AsTask();
                }
            }

            items.Where(i => i.Error is null).Select(i => i.Id).Should().Equal(new[] { "late" }, "the delayed message arrives during the attack");
            items.Where(i => i.Error is not null).Should().OnlyContain(i => i.Error is TimeoutException);
            var types = Types(exchange);
            types.Count(t => t == LiveOn).Should().BeGreaterThanOrEqualTo(3, "a cycle ran at the start and every interval");
            for (var k = 0; k < types.Count; k++)
            {
                if (types[k] == LiveOn)
                    types.ElementAtOrDefault(k + 1).Should().Be(Drain, "every cycle drains before the stream takes another push (request {0})", k);
            }
        }
        finally
        {
            await StopAsync(stream, move, cts);
        }
    }

    /// <summary>
    /// ADV3-1, the other side of the turns: a drain that keeps meeting slow messages (every round
    /// delivers another message whose sender's DID never resolves, so it never meets a status) does
    /// not keep a waiting push from its turn. The cycle's first drain round goes first, then the
    /// push, then the next drain round.
    /// </summary>
    [Fact]
    public async Task A_drain_that_keeps_meeting_slow_messages_does_not_keep_a_waiting_push_from_its_turn()
    {
        var (exchange, client) = Live(o => o.UnpackTimeout = Budget);
        exchange.UseBudget(Budget);
        var round = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
        {
            var n = Interlocked.Increment(ref round);
            return Delivery(r, ($"d{n}", $"stall:d{n}"));
        });
        exchange.Unpacker = StallsUntilCancelled;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            // An honest message is pushed as live mode goes on.
            if (r.Live && exchange.SentOf(MessagePickup.LiveDeliveryChangeType).Count == 1)
                exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();
            return Status(r, live: r.Live);
        });

        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask();
        try
        {
            await exchange.Clock.WaitForScheduledTimersAsync(2, PickupTest.Timeout); // the first round's d1 is being opened
            exchange.Clock.Advance(Budget);
            (await PickupTest.Within(move)).Should().BeTrue();
            stream.Current.Should().Match<PickupItem>(i => i.Id == "d1" && i.Error is TimeoutException, "the cycle's drain round goes before the waiting push");

            (await Next(stream)).Should().Match<PickupItem>(i => i.Id == "p1" && i.Error == null, "then the push has its turn, before the next drain round");

            move = stream.MoveNextAsync().AsTask();
            await exchange.Clock.WaitForScheduledTimersAsync(2, PickupTest.Timeout); // the next round's d2 is being opened
            Types(exchange).Should().Equal(LiveOn, Drain, Ack, Drain);
            exchange.AcknowledgedIds.Should().Equal("p1");
        }
        finally
        {
            await StopAsync(stream, move, cts);
        }
    }

    /// <summary>
    /// ADV3-5, the back-off: an acknowledgement fails and the stream backs off for one second; the
    /// host clock then steps back an hour. When the back-off's second has passed in real time, the
    /// acknowledgement is retried and the cycle runs, instead of an hour later.
    /// </summary>
    [Fact]
    public async Task A_clock_that_steps_back_during_a_back_off_does_not_stretch_it()
    {
        var clock = new SteppingTimeProvider();
        var exchange = new FakePickupExchange(time: null) { Route = WebSocketRoute };
        var acks = 0;
        exchange.On(MessagePickup.MessagesReceivedType, r => ++acks == 1
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException())
            : Task.FromResult(Status(r)));
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        var client = new PickupClient(new SteppedExchange(exchange, clock), logger: null);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await Next(stream)).Id.Should().Be("q1");

        var waiting = stream.MoveNextAsync().AsTask();
        try
        {
            await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            Types(exchange).Should().Equal(new[] { LiveOn, Drain, Ack }, "the acknowledgement failed: a back-off of one second");

            clock.StepBack(TimeSpan.FromHours(1));
            clock.Inner.Advance(TimeSpan.FromSeconds(1)); // the back-off's timer fires
            await exchange.WaitForSentAsync(6);
            await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

            Types(exchange).Should().Equal(new[] { LiveOn, Drain, Ack, Ack, LiveOn, Drain }, "the back-off has passed in real time; a clock that stepped back does not stretch it by the step");
            exchange.AcknowledgedIds.Should().Equal("q1", "q1");
        }
        finally
        {
            await StopAsync(stream, waiting, cts);
        }
    }

    /// <summary>
    /// ADV3-5, the flush age: taken messages are acknowledged once the oldest has waited ten
    /// seconds, even while pushes keep the stream busy. A host clock that steps back an hour after
    /// the first was taken does not postpone that acknowledgement by the hour.
    /// </summary>
    [Fact]
    public async Task A_clock_that_steps_back_does_not_postpone_the_ten_second_flush_of_what_was_taken()
    {
        var clock = new SteppingTimeProvider();
        var exchange = new FakePickupExchange(time: null) { Route = WebSocketRoute };
        var client = new PickupClient(new SteppedExchange(exchange, clock), logger: null);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // live-on, a drain that found nothing, the wait
        exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("q1");

        exchange.Registration!.TryPush(Push(("q2", "ok:m2"))).Should().BeTrue();
        exchange.Registration!.TryPush(Push(("q3", "ok:m3"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("q2");
        exchange.AcknowledgedIds.Should().BeEmpty("q1 was taken just now, and pushes are waiting");

        clock.StepBack(TimeSpan.FromHours(1));
        clock.Inner.Advance(LiveDeliveryStreamFlushAge); // ten seconds pass in real time
        (await Next(stream)).Id.Should().Be("q3");

        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q2" }, "q1 has waited ten seconds; a clock that stepped back does not postpone its acknowledgement by the step");
        var next = stream.MoveNextAsync().AsTask();
        await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, next, cts);
    }

    private static readonly TimeSpan LiveDeliveryStreamFlushAge = DidComm.Mediator.Client.Internal.LiveDeliveryStream.FlushAge;

    // A "stall:" message's sender DID never resolves: it waits until its token is cancelled.
    private static async Task<DidComm.Mediator.Client.Internal.DeliveredOutcome> StallsUntilCancelled(string text, CancellationToken ct)
    {
        if (text.StartsWith("stall:", StringComparison.Ordinal))
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return DefaultOutcome(text);
    }
}
