using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// PR #112 review R1-1: the live stream's cycle schedule against the mediator's latency. Before the
/// fix the next cycle was scheduled when a cycle started, before its <c>live-delivery-change</c>
/// was answered, and the loop checks a due cycle before anything else — so a mediator whose answers
/// took at least <c>LivePollInterval</c> found the next cycle due on every return, and the stream
/// sent <c>live-delivery-change</c> for ever with no delivery request and nothing yielded. The fix:
/// a cycle that has started runs its drain to its end before the next cycle is armed, and the next
/// cycle is scheduled one interval after that end. Latency is simulated by moving the injected
/// clock inside the fake mediator's answers (L-044: no sleeps).
/// </summary>
public sealed class PickupClientLiveSchedulingTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(500)]   // faster than the interval
    [InlineData(1000)]  // exactly the interval
    [InlineData(2000)]  // slower than the interval (the review's reproduction)
    [InlineData(5000)]  // much slower, still well inside RequestTimeout (30 s): LivePollInterval < RequestTimeout
    public async Task Whatever_the_mediators_latency_a_started_cycle_drains_and_the_queued_message_is_yielded(int latencyMs)
    {
        var latency = TimeSpan.FromMilliseconds(latencyMs);
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Brake(cts);
            exchange.Clock.Advance(latency);
            return Status(r, count: 1, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            exchange.Clock.Advance(latency);
            return Interlocked.Increment(ref drains) == 1 ? Delivery(r, ("q1", "ok:m1")) : Status(r);
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1", "the queued message is fetched and yielded although every answer takes {0}", latency);
        Types(exchange).Should().StartWith(new[] { LiveOn, Drain }, "a started cycle sends its delivery request before anything else");

        // Run on: the drain ends with a status, then the next cycle comes due one interval later.
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Clock.Advance(Interval);
        await exchange.WaitForSentAsync(Types(exchange).Count + 2);
        await StopAsync(stream, next, cts);

        NoCycleWithoutItsDrain(Types(exchange));
    }

    [Fact]
    public async Task The_reviews_reproduction_twenty_slow_answers_yield_the_queued_message_instead_of_twenty_live_on_requests()
    {
        // LivePollInterval 1 s, default RequestTimeout 30 s; every live-on answer succeeds with
        // message_count 1 after 2 s. Before the fix: 20 live-on, 0 delivery requests, 0 items.
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Brake(cts);
            exchange.Clock.Advance(TimeSpan.FromSeconds(2));
            return Status(r, count: 1, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
            Interlocked.Increment(ref drains) == 1 ? Delivery(r, ("q1", "ok:m1")) : Status(r));
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1");
        var next = stream.MoveNextAsync().AsTask();
        for (var i = 0; i < 20 && exchange.Sent.Count < 20; i++)
        {
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            exchange.Clock.Advance(Interval);
            await exchange.WaitForSentAsync(Math.Min(20, exchange.Sent.Count + 1));
        }
        await StopAsync(stream, next, cts);

        var types = Types(exchange);
        types.Count(t => t == Drain).Should().BeGreaterThan(0);
        NoCycleWithoutItsDrain(types);
    }

    [Fact]
    public async Task The_next_cycle_is_due_one_interval_after_the_previous_cycles_drain_ended_not_after_it_started()
    {
        var interval = TimeSpan.FromSeconds(10);
        var (exchange, client) = Live(o => o.LivePollInterval = interval);
        using var cts = new CancellationTokenSource();
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Brake(cts);
            exchange.Clock.Advance(TimeSpan.FromSeconds(3));
            return Status(r, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
        {
            exchange.Clock.Advance(TimeSpan.FromSeconds(4));
            return Interlocked.Increment(ref drains) == 1 ? Delivery(r, ("q1", "ok:m1")) : Status(r);
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        (await Next(stream)).Id.Should().Be("q1");

        // The drain's second round answers status at t = 11 s; the next cycle is due at 21 s.
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        var before = Types(exchange).Count(t => t == LiveOn);
        before.Should().Be(1, "the drain ended at 11 s and the next cycle is not due before 21 s (measured from the end, not from the start at 0 s)");
        exchange.Clock.Advance(interval - TimeSpan.FromTicks(1));
        Types(exchange).Count(t => t == LiveOn).Should().Be(before, "one tick before an interval has passed since the drain ended");
        exchange.Clock.Advance(TimeSpan.FromTicks(1));
        await exchange.WaitForSentAsync(exchange.Sent.Count + 1);
        Types(exchange).Count(t => t == LiveOn).Should().Be(before + 1);
        await StopAsync(stream, next, cts);
    }

    [Theory]
    [InlineData("live-on")]
    [InlineData("drain")]
    public async Task A_timed_out_step_slower_than_the_interval_backs_off_and_the_next_cycle_still_drains(string failing)
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        var liveOns = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Brake(cts);
            exchange.Clock.Advance(TimeSpan.FromSeconds(2));
            if (failing == "live-on" && r.Live && Interlocked.Increment(ref liveOns) == 1)
                throw new TimeoutException("no answer (test)");
            return Status(r, count: 1, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
        {
            exchange.Clock.Advance(TimeSpan.FromSeconds(2));
            var n = Interlocked.Increment(ref drains);
            if (failing == "drain" && n == 1)
                return Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer (test)"));
            return Task.FromResult(n <= 2 ? Delivery(r, ("q1", "ok:m1")) : Status(r));
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();

        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // the back-off
        first.IsCompleted.Should().BeFalse("the stream backs off after the failed step");
        exchange.Clock.Advance(LiveDeliveryStream.FirstBackoff);

        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("q1", "after the back-off the next cycle re-arms live mode and drains");
        var types = Types(exchange);
        types.Should().Equal(failing == "live-on"
            ? new[] { LiveOn, LiveOn, Drain }
            : new[] { LiveOn, Drain, LiveOn, Drain });
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, next, cts);
        NoCycleWithoutItsDrain(Types(exchange), allowFailedLiveOn: failing == "live-on");
    }

    // A regression must fail, not hang the test host: every answer yields (so MoveNextAsync returns a
    // task the test can bound), and a stream that keeps sending live-on is cancelled after 50.
    private int _liveOns;

    private async Task Brake(CancellationTokenSource cts)
    {
        await Task.Yield();
        if (Interlocked.Increment(ref _liveOns) > 50)
            await cts.CancelAsync();
    }

    // Every successful live-on is followed by at least one delivery request before the next
    // live-on (a failed live-on, when the test makes one fail, is followed by a back-off instead).
    private static void NoCycleWithoutItsDrain(IReadOnlyList<string> types, bool allowFailedLiveOn = false)
    {
        var liveOns = types.Select((t, i) => (t, i)).Where(x => x.t == LiveOn).Select(x => x.i).ToArray();
        var skipped = allowFailedLiveOn ? 1 : 0;
        for (var k = skipped; k + 1 < liveOns.Length; k++)
        {
            types.Skip(liveOns[k] + 1).Take(liveOns[k + 1] - liveOns[k] - 1).Should().Contain(Drain,
                "a cycle that has started sends its delivery request before the next cycle is armed (R1-1): {0}", string.Join(", ", types));
        }
    }
}
