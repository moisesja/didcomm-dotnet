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
/// RT2-C (#111, red-team pass 2, fixed in fix round 2 under owner decision F): what starts the live
/// stream's back-off again, and what its per-drain set holds, in simulated time
/// (<see cref="SimClock"/>, <see cref="StreamDriver"/>); the real unpacker and its memory run on that
/// clock. Nothing sleeps; real time is only a hang guard.
/// </summary>
public sealed class LiveStreamRt2CTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    /// <summary>
    /// P-F1 (RT2C-1), every cycle request refused at once: every live-on fails, a push brings a new
    /// message every P seconds, the consumer takes each, and the mediator accepts every
    /// acknowledgement. An accepted acknowledgement, like a push that brings something new, lets the
    /// give-up alarm go and keeps the back-off. So the live-on requests follow the back-off schedule
    /// whatever P is and whatever the refusal is (no answer, or the container's bound of waiting
    /// requests): at 0 s, after pauses of 1, 2, 4, 8 and 16 s at 1, 3, 7, 15 and 31 s, then every
    /// 30 s at 61, 91, ..., 571 s (the next would be at 601 s). That is 6 + 18 = 24 in ten minutes.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    public async Task With_every_cycle_failing_and_acknowledgements_accepted_cycles_stay_at_one_per_growing_back_off(int pushEverySeconds, bool busy)
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => !r.Live
            ? Status(r, live: false)
            : busy
                ? throw new MediatorBusyException("too many requests are waiting (test)")
                : throw new TimeoutException("no answer (test)"));
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

        var liveOns = exchange.Log.Where(r => r.At <= TenMinutes && r.Type == LiveOn).Select(r => r.At).ToArray();
        var acks = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Ack);
        output.WriteLine($"push every {pushEverySeconds} s, busy {busy}: {liveOns.Length} live-on and {acks} acknowledgements in 10 min; ended at {driver.EndedAt?.TotalSeconds.ToString() ?? "never"}; {driver.Items.Count} items");
        output.WriteLine(string.Join(", ", exchange.Log.Take(40).Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
        driver.EndedAt.Should().BeNull("each push with a new message and each accepted acknowledgement lets the give-up alarm go (R-11)");
        acks.Should().BePositive("the mediator accepted acknowledgements all along");
        exchange.Inner.AcknowledgedIds.Should().NotBeEmpty();
        liveOns.Should().Equal(BackoffSchedule(TenMinutes, TimeSpan.Zero),
            "one cycle per back-off, the back-off growing to 30 s however often pushes come and whatever acknowledgements are accepted (owner decisions B1 and F)");
        liveOns.Should().HaveCount(24);
    }

    /// <summary>
    /// P-F1 (RT2C-1), every cycle request timing out: each live-on takes the whole
    /// <see cref="MediatorClientOptions.RequestTimeout"/> (30 s) before it fails, as at a mediator
    /// that does not answer it; pushes and accepted acknowledgements as above. The pause after each
    /// failure follows the back-off, so the live-on requests are sent at 0 s, then one timeout and one
    /// pause later each time: 31, 63, 97, 135 and 181 s (pauses of 1, 2, 4, 8 and 16 s), then every
    /// 60 s (30 s of timeout, 30 s of pause) at 241, 301, ..., 541 s (the next would be at 601 s).
    /// That is 6 + 6 = 12 in ten minutes.
    /// </summary>
    [Theory]
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

        var liveOns = exchange.Log.Where(r => r.At <= TenMinutes && r.Type == LiveOn).Select(r => r.At).ToArray();
        var acks = exchange.Log.Count(r => r.At <= TenMinutes && r.Type == Ack);
        output.WriteLine($"push every {pushEverySeconds} s: {liveOns.Length} live-on and {acks} acknowledgements in 10 min; ended at {driver.EndedAt?.TotalSeconds.ToString() ?? "never"}; {driver.Items.Count} items");
        output.WriteLine(string.Join(", ", exchange.Log.Take(30).Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
        driver.EndedAt.Should().BeNull("each push with a new message and each accepted acknowledgement lets the give-up alarm go (R-11)");
        acks.Should().BePositive("the mediator accepted acknowledgements all along");
        liveOns.Should().Equal(BackoffSchedule(TenMinutes, requestTimeout),
            "one cycle per timeout and back-off, the back-off growing to 30 s however often pushes come and whatever acknowledgements are accepted (owner decisions B1 and F)");
        liveOns.Should().HaveCount(12);
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
        liveOns.Should().Be(BackoffSchedule(TenMinutes, TimeSpan.Zero).Count, "one cycle per back-off step");
        all.Should().Be(2 * liveOns, "each cycle is a live-on and one failed drain round");
    }

    /// <summary>
    /// P-F2 (RT2C-2), at a mediator that does not set delivered messages aside: its queue's head is a
    /// message that never opens. A cycle's first drain round brings it and the second gets no answer,
    /// so the stream backs off 1 s with its drain not ended. The cycle that retries gets the same
    /// message again: already reported since the last drain ended, it is not something new. It is not
    /// handed to the consumer again, and the round, bringing nothing new, ends the drain; the next
    /// cycle is one poll interval (60 s) later. So in ten minutes the cycles start at 0 and 1 s, 61
    /// and 62 s, ..., 549 and 550 s: 20 live-on requests, 50 requests in all (live-on, round, round
    /// without an answer, live-on, round; ten times), and the failure is handed over 10 times, once
    /// per drain.
    /// </summary>
    [Fact]
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

        var log = exchange.Log.Where(r => r.At <= TenMinutes).ToArray();
        var handed = driver.Items.Where(i => i.Item.Id == "j" && i.At <= TenMinutes).Select(i => i.At.TotalSeconds).ToArray();
        output.WriteLine($"{log.Count(r => r.Type == LiveOn)} live-on, {log.Length} requests in 10 min; the same failure handed {handed.Length} times");
        var drains = Enumerable.Range(0, 10).Select(k => 61.0 * k).ToArray();
        handed.Should().Equal(drains, "the failure is handed over once per drain: the cycle that retries does not report it again");
        log.Where(r => r.Type == LiveOn).Select(r => r.At.TotalSeconds).Should().Equal(drains.SelectMany(at => new[] { at, at + 1 }),
            "a cycle, its retry one second later, whose first round brings nothing new and ends the drain, then one poll interval");
        log.Select(r => r.Type).Should().Equal(Enumerable.Repeat(new[] { LiveOn, Drain, Drain, LiveOn, Drain }, 10).SelectMany(t => t));
        driver.EndedAt.Should().BeNull();
    }

    /// <summary>
    /// P-F2, what the set holds while a drain has not ended and after it has: a drain round brings a
    /// message that never opens (handed over as a failure) and the next round gets no answer, so the
    /// stream backs off with its drain not ended. A push of the same message during the back-off
    /// brings nothing new: it is not opened and not handed over again. The cycle that retries gets
    /// the message again, which ends the drain, and what the drain remembered is let go: a push of
    /// the same message after that is opened and handed over.
    /// </summary>
    [Fact]
    public async Task What_a_drain_reported_is_not_something_new_until_that_drain_ends_and_is_new_again_after_it()
    {
        var exchange = new SimExchange();
        var rounds = 0;
        exchange.Inner.On(MessagePickup.DeliveryRequestType, r => ++rounds == 2 ? throw new TimeoutException("no answer (test)") : Delivery(r, ("j", "bad:j")));
        await using var driver = new StreamDriver(exchange);
        var step = 0;
        var loadsAfterThePushInTheBackOff = -1;
        var rememberedInTheBackOff = -1;
        var rememberedAfterTheDrain = -1;
        await driver.RunAsync(() => driver.Items.Count >= 2, TimeSpan.FromMinutes(5), onWait: () =>
        {
            switch (step)
            {
                case 0: // the first wait: the second round got no answer, the stream backs off
                    step++;
                    rememberedInTheBackOff = driver.Reported;
                    return exchange.Inner.Registration!.TryPush(Push(("j", "bad:j")));
                case 1: // the push was looked at and brought nothing new: the stream waits again
                    step++;
                    loadsAfterThePushInTheBackOff = exchange.Loads.Count;
                    return false; // the clock moves: the back-off ends, the retry's round ends the drain
                case 2: // the drain has ended: the stream waits for its next cycle
                    step++;
                    rememberedAfterTheDrain = driver.Reported;
                    return exchange.Inner.Registration!.TryPush(Push(("j", "bad:j")));
                default:
                    return false;
            }
        });

        exchange.Log.Select(r => r.Type).Should().Equal(new[] { LiveOn, Drain, Drain, LiveOn, Drain }, "a cycle whose second round fails, and its retry, whose round brings nothing new");
        rememberedInTheBackOff.Should().Be(1, "the drain has not ended: what it took is kept");
        loadsAfterThePushInTheBackOff.Should().Be(1, "the push during the back-off brought only what the drain had reported: not opened");
        rememberedAfterTheDrain.Should().Be(0, "the drain ended: what it remembered is let go");
        exchange.Loads.Select(l => l.RequestId is null ? "push" : "round").Should().Equal(new[] { "round", "push" }, "only the first round and the push after the drain's end were opened");
        driver.Items.Select(i => (i.Item.Id, i.At.TotalSeconds)).Should().Equal(new[] { ("j", 0.0), ("j", 1.0) }, "handed over by the first round, then by the push after the drain ended");
    }

    /// <summary>
    /// P-F3 (I-19, QA2-3): the per-drain set at its stated bound,
    /// <see cref="LiveDeliveryStream.MaxDoneThisDrain"/> = (50 rounds + 49 pushes) × 10 = 990. A
    /// mediator gives the same bytes a new id at every round, so every round brings ten new ids, and
    /// a push of ten new messages waits at every turn. The set reaches exactly 990 when the fiftieth
    /// round is taken (49 rounds and 49 pushes before it, and its own ten), the drain ends there, and
    /// the set is let go; the second drain reaches the same peak.
    /// <para>
    /// With failing cycles, every second delivery request gets no answer: each cycle takes one round
    /// and one push, then fails and backs off, so fifty cycles pass before the drain ends, the rounds
    /// being counted until it does. During each back-off one more full push is taken, and the
    /// back-off ends while it is being opened, so the next cycle starts with that delivery still
    /// being yielded. Neither the pushes taken during back-offs nor the cycles that start add to the
    /// set: its peak is the same 990.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_a_full_push_at_every_turn_the_per_drain_set_stays_within_its_stated_bound(bool everySecondRoundFails)
    {
        var exchange = new SimExchange();
        var mediator = new LeasingMediator(exchange, TimeSpan.Zero) { MintNewIds = true };
        for (var i = 0; i < 10; i++)
            mediator.Enqueue($"j{i}", $"bad:j{i}");
        if (everySecondRoundFails)
        {
            mediator.Failure = () =>
            {
                var log = exchange.Log;
                return log[^1].Type == Drain && log.Count(r => r.Type == Drain) % 2 == 0 ? new TimeoutException("no answer (test)") : null;
            };
        }
        await using var driver = new StreamDriver(exchange);
        var peak = 0;
        var last = 0;
        var drainsEnded = 0;
        var liveOnsWhenTheFirstDrainEnded = 0;
        var pushes = 0;
        var pushesTakenInABackOff = 0;
        // The set only grows while a drain goes on and is let go when it ends: a lower count than the
        // one before is a drain that ended.
        void Observe()
        {
            var now = driver.Reported;
            peak = Math.Max(peak, now);
            if (now < last && ++drainsEnded == 1)
                liveOnsWhenTheFirstDrainEnded = exchange.Log.Count(r => r.Type == LiveOn);
            last = now;
        }
        exchange.BeforeBatch = () =>
        {
            Observe();
            var log = exchange.Log;
            var rounds = log.Count(r => r.Type == Drain);
            if (everySecondRoundFails && log[^1].Type == Drain && rounds % 2 == 0)
            {
                // The last request was a round left without an answer: this delivery is a push taken
                // during the back-off. The back-off ends now, so the next cycle starts while this
                // delivery is still to be yielded.
                pushesTakenInABackOff++;
                exchange.Clock.Advance(LiveDeliveryStream.FirstBackoff);
            }
            // One full push per drain round sent, for the first two drains only (so the clock can move).
            if (exchange.Inner.Registration is { } registration && !registration.Pushes.TryPeek(out _)
                && Volatile.Read(ref pushes) < rounds && drainsEnded < 2)
            {
                var n = Interlocked.Increment(ref pushes);
                registration.TryPush(Push(Enumerable.Range(0, 10).Select(k => ($"p{n}-{k}", k % 2 == 0 ? $"bad:p{n}-{k}" : $"ok:p{n}-{k}")).ToArray()));
            }
        };
        driver.AtWait = Observe;
        await driver.RunAsync(() =>
        {
            Observe();
            // The brake: a drain that never ends would otherwise run for the whole simulated hour.
            return drainsEnded >= 2 || exchange.Log.Count(r => r.Type == LiveOn) > 6 * LiveDeliveryStream.MaxDrainRounds;
        }, TimeSpan.FromHours(1));

        output.WriteLine($"failing cycles {everySecondRoundFails}: peak per-drain set {peak}; {drainsEnded} drains ended; {liveOnsWhenTheFirstDrainEnded} live-on when the first ended; {pushes} pushes, {pushesTakenInABackOff} taken in a back-off; {exchange.Log.Count(r => r.Type == Drain)} rounds; {driver.Items.Count} items");
        LiveDeliveryStream.MaxDoneThisDrain.Should().Be(990, "(50 rounds + 49 pushes) × 10");
        peak.Should().Be(LiveDeliveryStream.MaxDoneThisDrain, "the worst case reaches the stated bound exactly and never passes it");
        drainsEnded.Should().Be(2, "a drain ends at its fiftieth round that brought something new, and what it remembered is let go");
        if (everySecondRoundFails)
        {
            liveOnsWhenTheFirstDrainEnded.Should().Be(LiveDeliveryStream.MaxDrainRounds, "forty-nine cycles failed before the first drain ended: one round counted per cycle");
            pushesTakenInABackOff.Should().BeGreaterThanOrEqualTo(2 * (LiveDeliveryStream.MaxDrainRounds - 2), "a full push was taken in nearly every back-off, each still being yielded when the next cycle started");
        }
        else
        {
            liveOnsWhenTheFirstDrainEnded.Should().Be(1, "one cycle, fifty rounds");
        }
    }

    // When a stream whose every live-on fails sends them within `span`: the first at zero, each next
    // one `failsAfter` (how long a live-on takes to fail) plus the back-off after the one before:
    // 1, 2, 4, 8, 16, then 30 s.
    private static List<TimeSpan> BackoffSchedule(TimeSpan span, TimeSpan failsAfter)
    {
        var at = new List<TimeSpan> { TimeSpan.Zero };
        while (at[^1] + failsAfter + LiveDeliveryStream.Backoff(at.Count) <= span)
            at.Add(at[^1] + failsAfter + LiveDeliveryStream.Backoff(at.Count));
        return at;
    }
}
