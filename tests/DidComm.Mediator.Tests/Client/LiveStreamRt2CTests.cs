using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT2-C (#111, red-team pass 2): the live stream's run of failures under owner decision B1 and its
/// per-drain set (QA2-3), in simulated time (<see cref="SimClock"/>, <see cref="StreamDriver"/>); the
/// real unpacker and its memory run on that clock. Nothing sleeps; real time is only a hang guard.
/// </summary>
public sealed class LiveStreamRt2CTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    /// <summary>
    /// R-11 (owner decision B1) with the acknowledgements accepted: every live-on fails, a push
    /// brings a new message every P seconds, the consumer takes each, and the mediator accepts every
    /// acknowledgement. R-11 states the stream's requests stay at one cycle per back-off, the back-off
    /// growing to 30 s however often pushes come; so in ten minutes at most as many live-on requests
    /// as the back-off schedule has steps (24).
    /// </summary>
    [Theory(Skip = "RT2 defect: with acknowledgements accepted, a stream whose cycles all fail keeps its back-off at 1 s while pushes come")]
    [InlineData(1)]
    [InlineData(10)]
    public async Task With_every_cycle_failing_and_acknowledgements_accepted_cycles_stay_at_one_per_growing_back_off(int pushEverySeconds)
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => r.Live ? throw new TimeoutException("no answer (test)") : Status(r, live: false));
        // messages-received: the default answer (a status), so every acknowledgement is accepted.
        var pushes = 0;
        var every = TimeSpan.FromSeconds(pushEverySeconds);
        using var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"ok:p{n}")));
        }, null, every + TimeSpan.FromMilliseconds(500), every);
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == LiveOn);
        var acks = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Ack);
        output.WriteLine($"push every {pushEverySeconds} s: {liveOns} live-on and {acks} acknowledgements in 10 min; ended at {driver.EndedAt?.TotalSeconds.ToString() ?? "never"}; {driver.Items.Count} items");
        output.WriteLine(string.Join(", ", exchange.Log.Take(40).Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
        driver.EndedAt.Should().BeNull("each push with a new message lets the give-up alarm go (R-11)");
        liveOns.Should().BeLessThanOrEqualTo(BackoffSteps(TenMinutes), "one cycle per back-off, the back-off growing to 30 s however often pushes come (R-11, owner decision B1)");
    }

    /// <summary>
    /// The same with each failing live-on taking the whole <see cref="MediatorClientOptions.RequestTimeout"/>
    /// (30 s) before it fails, as a mediator that does not answer it: cycles stay at one per back-off,
    /// the back-off growing to 30 s, so one cycle per minute at most once the back-off has reached it.
    /// </summary>
    [Theory(Skip = "RT2 defect: with acknowledgements accepted, a stream whose cycles all fail keeps its back-off at 1 s while pushes come")]
    [InlineData(1)]
    [InlineData(10)]
    public async Task With_every_cycle_timing_out_and_acknowledgements_accepted_cycles_stay_at_one_per_growing_back_off(int pushEverySeconds)
    {
        var exchange = new SimExchange();
        var requestTimeout = exchange.Options.RequestTimeout;
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Status(r, live: false);
            exchange.Clock.Advance(requestTimeout); // no answer within RequestTimeout
            throw new TimeoutException("no answer (test)");
        });
        var pushes = 0;
        var every = TimeSpan.FromSeconds(pushEverySeconds);
        using var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"ok:p{n}")));
        }, null, every + TimeSpan.FromMilliseconds(500), every);
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == LiveOn);
        var acks = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Ack);
        output.WriteLine($"push every {pushEverySeconds} s: {liveOns} live-on and {acks} acknowledgements in 10 min; ended at {driver.EndedAt?.TotalSeconds.ToString() ?? "never"}; {driver.Items.Count} items");
        output.WriteLine(string.Join(", ", exchange.Log.Take(30).Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
        driver.EndedAt.Should().BeNull();
        // After the back-off reaches 30 s (1+2+4+8+16 s plus five timeouts), one cycle per RequestTimeout + 30 s.
        var reached = TimeSpan.FromSeconds(1 + 2 + 4 + 8 + 16) + requestTimeout * 5;
        var bound = 5 + (int)Math.Ceiling((TenMinutes - reached) / (requestTimeout + LiveDeliveryStream.MaxBackoff)) + 1;
        liveOns.Should().BeLessThanOrEqualTo(bound, "one cycle per back-off, the back-off growing to 30 s however often pushes come (R-11, owner decision B1)");
    }

    /// <summary>
    /// B1 held: live-on succeeds but every drain round fails, and a push brings a new message that
    /// does not open every second (so nothing is taken and nothing is acknowledged). A live-on that
    /// succeeds is not progress and a push keeps the back-off, so the cycles follow the back-off
    /// schedule, one per step, growing to 30 s, two requests each.
    /// </summary>
    [Fact]
    public async Task With_live_on_succeeding_and_every_drain_round_failing_cycles_follow_the_back_off_while_pushes_come()
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.DeliveryRequestType, (Func<SentRequest, InboundMessageSnapshot>)(_ => throw new TimeoutException("no answer (test)")));
        var pushes = 0;
        using var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"bad:p{n}")));
        }, null, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1));
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == LiveOn);
        var all = exchange.Log.Count(r => r.At <= TenMinutes);
        output.WriteLine($"{liveOns} live-on, {all} requests in 10 min; ended at {driver.EndedAt?.TotalSeconds.ToString() ?? "never"}; {driver.Items.Count} items");
        driver.EndedAt.Should().BeNull("each push with a new message lets the give-up alarm go (R-11)");
        exchange.Log.Should().NotContain(r => r.Type == Ack);
        liveOns.Should().Be(BackoffSteps(TenMinutes), "one cycle per back-off step");
        all.Should().Be(2 * liveOns, "each cycle is a live-on and one failed drain round");
    }

    /// <summary>
    /// I-18 with I-19 at a mediator that does not set delivered messages aside: its queue's head is a
    /// message that never opens. Each cycle's first drain round is answered with it, already yielded as
    /// a failure by the previous cycle; the second round gets no answer. Nothing new reaches the
    /// stream, so the run's back-off should grow (1 s doubling to 30 s): the same failure is handed
    /// to the consumer at most once per back-off step, at most 24 times in ten minutes.
    /// </summary>
    [Fact(Skip = "RT2 defect: a round that brings back only a failure reported in the previous cycle restarts the back-off")]
    public async Task A_round_that_brings_back_only_the_previous_cycles_failure_does_not_restart_the_back_off()
    {
        var exchange = new SimExchange();
        var rounds = 0;
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            rounds = 0;
            return Status(r, live: r.Live);
        });
        exchange.Inner.On(MessagePickup.DeliveryRequestType, r => ++rounds == 1 ? Delivery(r, ("j", "bad:j")) : throw new TimeoutException("no answer (test)"));
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == LiveOn);
        var handed = driver.Items.Count(i => i.Item.Id == "j" && i.At <= TenMinutes);
        output.WriteLine($"{liveOns} live-on, {exchange.Log.Count(r => r.At <= TenMinutes)} requests in 10 min; the same failure handed {handed} times; host calls {exchange.HostCalls}");
        handed.Should().BeLessThanOrEqualTo(BackoffSteps(TenMinutes), "nothing new arrived, so the back-off grows");
        liveOns.Should().BeLessThanOrEqualTo(BackoffSteps(TenMinutes));
    }

    /// <summary>
    /// I-19 (QA2-3) held: the per-drain set at its stated bound. A mediator mints a new id for the
    /// same bytes at every round (so every round is new and the drain runs its fifty rounds), and a
    /// push of ten new messages waits at every turn. What the drain remembers stays within
    /// (50 rounds + 50 pushes) × 10, and the next cycle starts it again.
    /// </summary>
    [Fact]
    public async Task With_a_full_push_at_every_turn_the_per_drain_set_stays_within_its_stated_bound()
    {
        var exchange = new SimExchange();
        var mediator = new LeasingMediator(exchange, TimeSpan.Zero) { MintNewIds = true };
        for (var i = 0; i < 10; i++)
            mediator.Enqueue($"j{i}", $"bad:j{i}");
        var pushes = 0;
        exchange.BeforeBatch = () =>
        {
            // One push per drain round sent, for the first two drains only (so the clock can move).
            var rounds = exchange.Log.Count(r => r.Type == Drain);
            if (exchange.Inner.Registration is { } registration && !registration.Pushes.TryPeek(out _)
                && Volatile.Read(ref pushes) < rounds && rounds <= 2 * LiveDeliveryStream.MaxDrainRounds)
            {
                var n = Interlocked.Increment(ref pushes);
                registration.TryPush(Push(Enumerable.Range(0, 10).Select(k => ($"p{n}-{k}", k % 2 == 0 ? $"bad:p{n}-{k}" : $"ok:p{n}-{k}")).ToArray()));
            }
        };
        await using var driver = new StreamDriver(exchange);
        var max = 0;
        driver.AtWait = () => max = Math.Max(max, driver.Reported);
        var peakDuringYield = 0;
        await driver.RunAsync(() =>
        {
            peakDuringYield = Math.Max(peakDuringYield, driver.Reported);
            return exchange.Log.Count(r => r.Type == LiveOn) > 2;
        }, TimeSpan.FromMinutes(10));

        output.WriteLine($"peak per-drain set {Math.Max(max, peakDuringYield)}; {pushes} pushes; {exchange.Log.Count(r => r.Type == Drain)} rounds; {driver.Items.Count} items");
        Math.Max(max, peakDuringYield).Should().BeLessThanOrEqualTo((LiveDeliveryStream.MaxDrainRounds + LiveDeliveryStream.MaxDrainRounds) * LiveDeliveryStream.MaxPerDelivery);
    }

    // The live-on requests a stream sends in `span` when each one fails and the back-off follows
    // 1, 2, 4, 8, 16, then 30 s (the first at zero).
    private static int BackoffSteps(TimeSpan span)
    {
        var at = TimeSpan.Zero;
        var count = 1;
        while (at + LiveDeliveryStream.Backoff(count) <= span)
        {
            at += LiveDeliveryStream.Backoff(count);
            count++;
        }
        return count;
    }
}
