using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2): the live stream's cycle requests while every one of them fails, under
/// mixes <see cref="LiveStreamRt2CTests"/> does not set up: acknowledgements that are refused and
/// accepted in turn, pushes that bring only messages the stream was already handed, and a mediator
/// that answers nothing but pushes. What is checked: the cycle requests are never closer together
/// than the back-off schedule (1, 2, 4, 8, 16, then 30 s), so never more than 24 in ten minutes.
/// Simulated time (<see cref="SimClock"/>, <see cref="StreamDriver"/>); nothing sleeps.
/// </summary>
public sealed class LiveStreamBackoffRt3Tests(ITestOutputHelper output)
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Every live-on fails at once; a push brings a new message every P seconds and the consumer
    /// takes each; the mediator refuses every second acknowledgement and accepts the others. A
    /// refused acknowledgement is one more failed step: it lengthens the back-off, and no cycle
    /// request is sent before the back-off it armed has passed. The stream goes on (each new push
    /// and each accepted acknowledgement lets the give-up alarm go).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(45)]
    public async Task With_every_cycle_failing_and_acknowledgements_refused_and_accepted_in_turn_cycles_are_never_closer_than_the_back_off(int pushEverySeconds)
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => !r.Live ? Status(r, live: false) : throw new TimeoutException("no answer (test)"));
        var acks = 0;
        exchange.Inner.On(MessagePickup.MessagesReceivedType, r => Interlocked.Increment(ref acks) % 2 == 1 ? throw new TimeoutException("no answer (test)") : Status(r));
        var pushes = 0;
        var every = TimeSpan.FromSeconds(pushEverySeconds);
        using var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"ok:p{n}")));
        }, null, every + TimeSpan.FromMilliseconds(500), every);
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Where(r => r.At <= TenMinutes && r.Type == LiveOn).Select(r => r.At).ToArray();
        output.WriteLine($"push every {pushEverySeconds} s: {liveOns.Length} live-on in 10 min, {exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Ack)} acknowledgements sent, {exchange.Inner.AcknowledgedIds.Count} ids; ended at {driver.EndedAt?.ToString() ?? "never"}");
        output.WriteLine(string.Join(", ", liveOns.Select(t => t.TotalSeconds)));
        driver.EndedAt.Should().BeNull("each push with a new message lets the give-up alarm go");
        liveOns.Should().NotBeEmpty();
        liveOns.Length.Should().BeLessThanOrEqualTo(24, "the schedule allows 24 cycle requests in ten minutes");
        NeverCloserThanTheSchedule(liveOns);
    }

    /// <summary>
    /// Every live-on fails at once, and every second a push brings the same message again (the
    /// consumer was handed it once and it is not acknowledged, as the acknowledgements get no
    /// answer). Nothing lets the give-up alarm go after the first push, so the stream ends with the
    /// failure once the alarm has fired; until then its cycle requests follow the schedule.
    /// </summary>
    [Fact]
    public async Task With_every_request_failing_and_pushes_that_bring_nothing_new_cycles_follow_the_back_off_and_the_stream_gives_up()
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => !r.Live ? Status(r, live: false) : throw new TimeoutException("no answer (test)"));
        exchange.Inner.On(MessagePickup.MessagesReceivedType, r => r.OneWay ? Status(r) : throw new TimeoutException("no answer (test)"));
        using var pusher = exchange.Clock.CreateTimer(_ => exchange.Inner.Registration?.TryPush(Push(("same", "ok:same"))), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Where(r => r.Type == LiveOn).Select(r => r.At).ToArray();
        var giveUp = exchange.Options.RequestTimeout + LiveDeliveryStream.GiveUpMargin;
        output.WriteLine($"{liveOns.Length} live-on; ended at {driver.EndedAt?.ToString() ?? "never"} with {driver.EndedWith?.GetType().Name ?? "nothing"}; {driver.Items.Count} items");
        output.WriteLine(string.Join(", ", exchange.Log.Take(30).Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
        driver.Items.Count(i => i.Item.Id == "same").Should().Be(1, "a push that brings back only a message the stream holds opens nothing");
        driver.EndedWith.Should().BeOfType<TimeoutException>("the requests kept failing and nothing new came");
        driver.EndedAt.Should().NotBeNull();
        driver.EndedAt!.Value.Should().BeGreaterThanOrEqualTo(giveUp, "the stream gives up only once its give-up alarm has fired");
        driver.EndedAt.Value.Should().BeLessThanOrEqualTo(giveUp + giveUp + LiveDeliveryStream.MaxBackoff, "and at the first failed step after it, one back-off later at most (the alarm was armed again once, by the first push)");
        NeverCloserThanTheSchedule(liveOns);
    }

    /// <summary>
    /// Every cycle request is answered (live-on with a status, each drain round with "nothing
    /// queued"); a push brings a new message every P seconds and the consumer takes each; the
    /// mediator refuses every second acknowledgement and accepts the others. How many cycles the
    /// stream runs in ten minutes is measured against one per
    /// <see cref="MediatorClientOptions.LivePollInterval"/> (one minute) plus the 24 the back-off
    /// schedule allows a stream whose cycle requests all fail. No bound is stated for this case: a
    /// cycle is documented to follow each failed step once its back-off has passed, and a drain
    /// that ends starts the back-off again.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2, and the same on the head before the round: at both
    /// push rates the count the test writes is above that measure.
    /// </remarks>
    [Theory(Skip = "RT3: how many cycles a stream runs when its acknowledgements are refused and accepted in turn while its cycle requests are answered (no stated bound)")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task With_cycles_answered_and_acknowledgements_refused_and_accepted_in_turn_the_cycles_stay_near_one_per_poll_interval(int pushEverySeconds)
    {
        var exchange = new SimExchange();
        var acks = 0;
        exchange.Inner.On(MessagePickup.MessagesReceivedType, r => Interlocked.Increment(ref acks) % 2 == 1 ? throw new TimeoutException("no answer (test)") : Status(r));
        var pushes = 0;
        var every = TimeSpan.FromSeconds(pushEverySeconds);
        using var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"ok:p{n}")));
        }, null, every + TimeSpan.FromMilliseconds(500), every);
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => false, TenMinutes);

        var liveOns = exchange.Log.Where(r => r.At <= TenMinutes && r.Type == LiveOn).Select(r => r.At).ToArray();
        var drains = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Drain);
        output.WriteLine($"push every {pushEverySeconds} s: {liveOns.Length} live-on and {drains} drain rounds in 10 min (the first at {string.Join(", ", liveOns.Take(8).Select(t => t.TotalSeconds))} s); {exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Ack)} acknowledgements sent, {exchange.Inner.AcknowledgedIds.Count} ids, {driver.Stream.TakenCount} waiting; ended at {driver.EndedAt?.ToString() ?? "never"}");
        driver.EndedAt.Should().BeNull();
        var perPollInterval = (int)(TenMinutes / exchange.Options.LivePollInterval);
        liveOns.Length.Should().BeLessThanOrEqualTo(perPollInterval + 24, "one cycle per poll interval ({0}) and the 24 of the back-off schedule", perPollInterval);
    }

    // The k-th cycle request comes no sooner after the one before it than the k-th back-off of the
    // schedule: 1, 2, 4, 8, 16, then 30 s.
    private static void NeverCloserThanTheSchedule(IReadOnlyList<TimeSpan> liveOns)
    {
        for (var k = 1; k < liveOns.Count; k++)
        {
            var gap = liveOns[k] - liveOns[k - 1];
            gap.Should().BeGreaterThanOrEqualTo(LiveDeliveryStream.Backoff(k), "cycle request {0} follows the one before it by at least the back-off after {0} failures in a row (at {1} and {2})", k, liveOns[k - 1], liveOns[k]);
        }
    }
}
