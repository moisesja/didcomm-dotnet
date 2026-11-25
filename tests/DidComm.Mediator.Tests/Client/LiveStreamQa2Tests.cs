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
/// QA-2 for #111: the live stream's scheduling (I-15..I-20, D-11, D-12) and the stream with the slow
/// memory, in simulated time. A <see cref="SimClock"/> moves only from one timer to the next when the
/// stream waits or a delivery it opens waits on the clock (<see cref="StreamDriver"/>,
/// <see cref="SimExchange"/>); the real unpacker and its memory run on that clock. Nothing sleeps;
/// real time is only a hang guard.
/// </summary>
public sealed class LiveStreamQa2Tests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    // ---- I-15, D-11: the clock -------------------------------------------------------------------

    /// <summary>How the host clock's readings behave.</summary>
    public enum ClockMode
    {
        /// <summary>Readings follow the timers.</summary>
        Steady,

        /// <summary>Readings step back an hour and forward three, alternately, at every wait.</summary>
        StepsBackAndForth,

        /// <summary>Every reading is lower than the one before.</summary>
        ReadingsKeepFalling,
    }

    /// <summary>
    /// I-15, D-11: over ten minutes of cycles, four failed live-on requests and a push during their
    /// back-offs, every request the stream sends is at the same instant on its timers whatever the
    /// host clock's readings do — steady, stepping back and forth at every wait, or lower on every
    /// read. No storm, no postponement, no early cycle: the cycles are one interval apart, and each
    /// back-off lasts until its alarm fires.
    /// </summary>
    [Theory]
    [InlineData(ClockMode.StepsBackAndForth)]
    [InlineData(ClockMode.ReadingsKeepFalling)]
    public async Task Whatever_the_clocks_readings_do_every_request_is_sent_when_its_alarm_fires(ClockMode mode)
    {
        var baseline = await ScheduleAsync(ClockMode.Steady);
        var liveOns = baseline.Where(r => r.Type == LiveOn).Select(r => r.At.TotalSeconds).ToArray();
        liveOns.Should().Equal(new double[] { 0, 60, 120, 121, 123, 124, 126, 186, 246, 306, 366, 426, 486, 546, 606 },
            "cycles one interval after each drain ended; back-offs of 1 and 2 s after the first two failures, then (the push during the back-off is progress) 1 and 2 s again");

        var schedule = await ScheduleAsync(mode);

        schedule.Should().Equal(baseline, "the host clock's readings decide nothing ({0})", mode);
    }

    private static async Task<IReadOnlyList<(TimeSpan At, string Type)>> ScheduleAsync(ClockMode mode)
    {
        var exchange = new SimExchange();
        exchange.Clock.ReadingsKeepFalling = mode == ClockMode.ReadingsKeepFalling;
        var mediator = new LeasingMediator(exchange, TimeSpan.FromSeconds(60));
        mediator.Enqueue("q1", "ok:q1");
        mediator.Enqueue("q2", "bad:q2"); // never opens, never acknowledged: delivered again each poll
        var liveOns = 0;
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Status(r, live: false);
            var n = Interlocked.Increment(ref liveOns);
            return n is >= 3 and <= 6 ? throw new TimeoutException("no answer (test)") : Status(r, live: true);
        });
        await using var driver = new StreamDriver(exchange);
        var waits = 0;
        await driver.RunAsync(() => false, TenMinutes, onWait: () =>
        {
            waits++;
            if (mode == ClockMode.StepsBackAndForth)
                exchange.Clock.Step(waits % 2 == 1 ? TimeSpan.FromHours(-1) : TimeSpan.FromHours(3));
            return waits == 4 && exchange.Inner.Registration!.TryPush(Push(("p1", "ok:p1")));
        });
        driver.EndedAt.Should().BeNull();
        driver.Items.Select(i => i.Item.Id).Should().Contain(new[] { "q1", "p1" });
        return exchange.Log;
    }

    /// <summary>
    /// D-11: a clock whose timers never fire. After the first cycle the stream sends nothing more
    /// (no second cycle, and after a failed first attempt no retry), and still yields every push.
    /// Acknowledgements go out when nothing is ready — except while a back-off lasts.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clock_that_never_moves_sends_nothing_after_the_first_cycle_and_still_yields_every_push(bool firstAttemptFails)
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r =>
            r.Live && firstAttemptFails ? throw new TimeoutException("no answer (test)") : Status(r, live: r.Live));
        await using var driver = new StreamDriver(exchange);
        var pushed = 0;
        await driver.RunAsync(() => false, until: TimeSpan.Zero, onWait: () =>
            pushed < 3 && exchange.Inner.Registration!.TryPush(Push(($"p{++pushed}", $"ok:p{pushed}"))));

        exchange.Clock.Now.Should().Be(TimeSpan.Zero);
        driver.Items.Select(i => i.Item.Id).Should().Equal("p1", "p2", "p3");
        driver.Items.Should().OnlyContain(i => i.Item.Error == null);
        exchange.Log.Select(r => r.Type).Should().Equal(firstAttemptFails
            ? new[] { LiveOn }
            : new[] { LiveOn, Drain, Ack, Ack, Ack },
            "no timer fired: no second cycle, no retry, and nothing sent while a back-off lasts");
    }

    // ---- D-12, I-18: the run ----------------------------------------------------------------------

    /// <summary>
    /// D-12 with <see cref="MediatorClientOptions.RequestTimeout"/> at 1 tick, 30 s and 10 min (and at
    /// 30 s with the clock's readings stepping back and forth, or going lower on every read): a stream
    /// whose live-on requests all fail retries after 1, 2, 4, 8, 16 and then every 30 s, and ends
    /// with the failure of the first attempt at or after RequestTimeout + 30 s from the run's first
    /// failure — not one attempt before.
    /// </summary>
    [Theory]
    [InlineData(1L, ClockMode.Steady)]
    [InlineData(30 * TimeSpan.TicksPerSecond, ClockMode.Steady)]
    [InlineData(600 * TimeSpan.TicksPerSecond, ClockMode.Steady)]
    [InlineData(30 * TimeSpan.TicksPerSecond, ClockMode.StepsBackAndForth)]
    [InlineData(30 * TimeSpan.TicksPerSecond, ClockMode.ReadingsKeepFalling)]
    public async Task A_run_of_failures_ends_the_stream_at_the_first_attempt_from_RequestTimeout_plus_30_s_and_not_before(long requestTimeoutTicks, ClockMode mode)
    {
        var requestTimeout = TimeSpan.FromTicks(requestTimeoutTicks);
        var exchange = new SimExchange(o => o.RequestTimeout = requestTimeout);
        exchange.Clock.ReadingsKeepFalling = mode == ClockMode.ReadingsKeepFalling;
        var waits = 0;
        var attempts = new List<TimeSpan>();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Status(r, live: false);
            attempts.Add(exchange.Clock.Now);
            throw new TimeoutException($"attempt {attempts.Count}");
        });
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => false, TimeSpan.FromHours(1), onWait: () =>
        {
            if (mode == ClockMode.StepsBackAndForth)
                exchange.Clock.Step(++waits % 2 == 1 ? TimeSpan.FromHours(-1) : TimeSpan.FromHours(3));
            return false;
        });

        var giveUp = requestTimeout + LiveDeliveryStream.GiveUpMargin;
        var expected = new List<TimeSpan> { TimeSpan.Zero };
        while (expected[^1] < giveUp)
            expected.Add(expected[^1] + LiveDeliveryStream.Backoff(expected.Count));
        attempts.Should().Equal(expected, "one attempt per back-off, until the first at or after the give-up point");
        expected[^2].Should().BeLessThan(giveUp);
        driver.EndedWith.Should().BeOfType<TimeoutException>().Which.Message.Should().Be($"attempt {expected.Count}");
        driver.EndedAt.Should().Be(expected[^1]);
    }

    /// <summary>The kinds of progress (I-18), and one delivery that is not progress.</summary>
    public enum Progress
    {
        /// <summary>A live-on succeeds and its drain ends at a status.</summary>
        DrainEnd,

        /// <summary>A drain round brings a new message; the next round fails.</summary>
        DrainRoundWithNewMessage,

        /// <summary>A push brings a new message during the back-off.</summary>
        PushWithNewMessage,

        /// <summary>A failing acknowledgement is accepted on a retry.</summary>
        AcceptedAcknowledgement,

        /// <summary>A push whose only attachment has no id: nothing new.</summary>
        PushWithNothingUsable,
    }

    /// <summary>
    /// I-18 (ADV5-3): five failures (at 0, 1, 3, 7 and 15 s) and then progress at 15 or 31 s. Progress
    /// lets the run's give-up alarm go: the stream does not end at the old run's give-up point (61 s)
    /// but at the first attempt 60 s after the first failure that follows the progress. After a
    /// request that succeeded the back-off starts again at 1 s, so that attempt is at 92 s (152 s after
    /// a drain's end, whose next cycle is an interval later). After a push the back-off goes on (owner
    /// decision B1: 30 s pauses from 31 s), so it is at 91 s. A push with nothing usable is not
    /// progress: the stream ends at 61 s.
    /// </summary>
    [Theory]
    [InlineData(Progress.DrainEnd, 152)]
    [InlineData(Progress.DrainRoundWithNewMessage, 92)]
    [InlineData(Progress.PushWithNewMessage, 91)]
    [InlineData(Progress.AcceptedAcknowledgement, 92)]
    [InlineData(Progress.PushWithNothingUsable, 61)]
    public async Task Progress_lets_the_give_up_alarm_go_and_the_next_failure_starts_a_new_run(Progress progress, int endsAtSeconds)
    {
        var exchange = new SimExchange();
        var at31 = TimeSpan.FromSeconds(31);
        var rounds = 0;
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Status(r, live: false);
            var now = exchange.Clock.Now;
            var succeeds = progress switch
            {
                Progress.DrainEnd or Progress.DrainRoundWithNewMessage => now == at31,
                Progress.AcceptedAcknowledgement => now == TimeSpan.Zero,
                _ => false,
            };
            return succeeds ? Status(r, live: true) : throw new TimeoutException("no answer (test)");
        });
        exchange.Inner.On(MessagePickup.DeliveryRequestType, r => progress switch
        {
            Progress.DrainRoundWithNewMessage => Interlocked.Increment(ref rounds) == 1 ? Delivery(r, ("n1", "bad:n1")) : throw new TimeoutException("no answer (test)"),
            Progress.AcceptedAcknowledgement => Interlocked.Increment(ref rounds) == 1 ? Delivery(r, ("q1", "ok:q1")) : Status(r),
            _ => Status(r),
        });
        exchange.Inner.On(MessagePickup.MessagesReceivedType, r => exchange.Clock.Now < at31 ? throw new TimeoutException("no answer (test)") : Status(r));
        await using var driver = new StreamDriver(exchange);
        var pushed = false;
        await driver.RunAsync(() => false, TimeSpan.FromHours(1), onWait: () =>
        {
            if (pushed || exchange.Clock.Now != TimeSpan.FromSeconds(15))
                return false;
            pushed = true;
            return progress switch
            {
                Progress.PushWithNewMessage => exchange.Inner.Registration!.TryPush(Push(("p1", "bad:p1"))),
                Progress.PushWithNothingUsable => exchange.Inner.Registration!.TryPush(RawDelivery(null, new[] { Entry(null, "bad:x") })),
                _ => false,
            };
        });

        driver.EndedWith.Should().BeOfType<TimeoutException>();
        driver.EndedAt.Should().Be(TimeSpan.FromSeconds(endsAtSeconds), "{0}: {1}", progress, string.Join(", ", exchange.Log.Select(l => $"{l.At.TotalSeconds}:{l.Type}")));
    }

    /// <summary>
    /// R-11 (owner decision B1), pinned and measured: every request fails, and a push brings a new
    /// message every P seconds. A push lets the give-up alarm go but keeps the back-off, so while
    /// pushes come at least once a minute the stream never gives up, and its requests follow the
    /// back-off whatever P is: after 1, 2, 4, 8 and 16 s, then one every 30 s (24 in ten minutes). With
    /// P = 61 s it gives up at 61 s, as a stream without pushes does.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(59, false)]
    [InlineData(61, true)]
    public async Task With_every_request_failing_new_pushes_keep_the_stream_from_giving_up_and_its_requests_stay_bounded(int pushEverySeconds, bool givesUp)
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => r.Live ? throw new TimeoutException("no answer (test)") : Status(r, live: false));
        exchange.Inner.On(MessagePickup.MessagesReceivedType, (Func<SentRequest, InboundMessageSnapshot>)(_ => throw new TimeoutException("no answer (test)")));
        var pushes = 0;
        var every = TimeSpan.FromSeconds(pushEverySeconds);
        using var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"ok:p{n}")));
        }, null, every + TimeSpan.FromMilliseconds(500), every);
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => false, TenMinutes);

        var sent = exchange.Log.Count(r => r.At <= TenMinutes);
        output.WriteLine($"push every {pushEverySeconds} s: {sent} requests in 10 min ({sent / 10.0:F1} per minute); ended at {driver.EndedAt?.TotalSeconds.ToString() ?? "never"}; {driver.Items.Count} items");
        if (givesUp)
        {
            driver.EndedWith.Should().BeOfType<TimeoutException>();
            driver.EndedAt.Should().Be(TimeSpan.FromSeconds(61));
            return;
        }
        driver.EndedAt.Should().BeNull("each push with a new message lets the give-up alarm go (R-11)");
        var expected = new List<TimeSpan> { TimeSpan.Zero };
        while (expected[^1] + LiveDeliveryStream.Backoff(expected.Count) <= TenMinutes)
            expected.Add(expected[^1] + LiveDeliveryStream.Backoff(expected.Count));
        exchange.Log.Where(r => r.At <= TenMinutes).Select(r => r.At).Should().Equal(expected,
            "one request per back-off, and the back-off grows to 30 s however often pushes come (owner decision B1)");
        sent.Should().Be(24);
        driver.Stream.TakenCount.Should().BeLessThanOrEqualTo(LiveDeliveryStream.MaxTaken);
        driver.Stream.HeldCount.Should().BeLessThanOrEqualTo(LiveDeliveryStream.MaxTaken + LiveRegistration.PushCapacity + LiveDeliveryStream.MaxPerDelivery);
    }

    // ---- I-19: the drain's end --------------------------------------------------------------------

    /// <summary>
    /// I-19 (ADV5-4) with the slow memory, at a mediator that does not set delivered messages aside:
    /// ten messages whose senders' DIDs never resolve, a full round of ten. Each poll sends one live-on
    /// and two delivery requests and yields each message once as a failure. Only the first poll opens
    /// them (each until the budget ends); later polls hold them back without opening, at once.
    /// Nothing is acknowledged.
    /// </summary>
    [Fact]
    public async Task At_a_mediator_that_does_not_lease_each_poll_yields_each_slow_message_once_with_two_delivery_requests_and_opens_them_only_once()
    {
        var exchange = new SimExchange();
        var mediator = new LeasingMediator(exchange, TimeSpan.Zero);
        for (var i = 0; i < 10; i++)
            mediator.Enqueue($"s{i}", $"stall:s{i}");
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => exchange.Log.Count(r => r.Type == LiveOn) > 3, TimeSpan.FromMinutes(5));

        var polls = exchange.Log.Where(r => r.Type == LiveOn).Select(r => r.At).ToArray();
        polls.Should().HaveCount(4);
        for (var p = 0; p < 3; p++)
        {
            exchange.Log.Where(r => r.At >= polls[p] && r.At < polls[p + 1]).Select(r => r.Type).Should().Equal(new[] { LiveOn, Drain, Drain }, "poll {0}", p + 1);
            var items = driver.Items.Where(i => i.At >= polls[p] && i.At < polls[p + 1]).Select(i => i.Item).ToArray();
            items.Select(i => i.Id).Should().Equal(Enumerable.Range(0, 10).Select(i => $"s{i}"), "each message once per poll");
            items.Should().OnlyContain(i => i.Error is TimeoutException);
            if (p > 0)
                items.Should().OnlyContain(i => i.Error!.Message.Contains("did not open when it was delivered before"), "remembered from the first poll");
        }
        exchange.HostCalls.Should().Be(10, "only the first poll opened them");
        (polls[1] - polls[0]).Should().Be(Budget + exchange.Options.LivePollInterval, "the first poll's drain took the budget");
        (polls[2] - polls[1]).Should().Be(exchange.Options.LivePollInterval, "later polls take no time");
        exchange.Log.Should().NotContain(r => r.Type == Ack);
    }

    /// <summary>
    /// R-12, with a <see cref="MediatorClientOptions.LivePollInterval"/> (1 s) shorter than a drain
    /// round (the 20 s budget): at a 10 s lease, twenty-five messages whose senders never resolve and
    /// an honest message behind them. Each drain ends when the failures it reported come back from an
    /// expired lease; the next cycle passes the remembered ones at once and learns the next ten; the
    /// honest message is yielded in the third cycle. No cycle starts while a drain runs, and nothing
    /// but the honest message is acknowledged.
    /// </summary>
    [Fact]
    public async Task A_drain_that_outlasts_the_lease_ends_on_its_own_failures_and_the_next_cycles_reach_the_honest_message()
    {
        var exchange = new SimExchange(o => o.LivePollInterval = TimeSpan.FromSeconds(1));
        var mediator = new LeasingMediator(exchange, TimeSpan.FromSeconds(10));
        for (var i = 0; i < 25; i++)
            mediator.Enqueue($"s{i}", $"stall:s{i}");
        mediator.Enqueue("h", "ok:h");
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => driver.Items.Any(i => i.Item.Id == "h"), TimeSpan.FromMinutes(5));

        var honest = driver.Items.Single(i => i.Item.Id == "h");
        honest.Item.Error.Should().BeNull();
        exchange.Log.Count(r => r.Type == LiveOn).Should().Be(3, "two cycles learn ten each and end on their own failures; the third reaches it: {0}", Trace(exchange));
        honest.At.Should().Be(TimeSpan.FromSeconds(20 + 1 + 20 + 1 + 20), "three rounds that each take the budget, one per cycle, and two intervals");
        exchange.HostCalls.Should().Be(26, "every slow message was opened once, the honest one once");
        NoCycleWhileDraining(exchange);
        await driver.DisposeAsync();
        mediator.Queued.Should().HaveCount(25).And.NotContain("h");
    }

    /// <summary>
    /// I-19 against a mediator that gives the same bytes a new id at every delivery: every round is
    /// new, so the drain runs its fifty rounds and ends there. What it remembers of the failures it
    /// yielded is bounded (500 here, at most (50 + 50) × 10), and it starts again empty at the next
    /// cycle.
    /// </summary>
    [Fact]
    public async Task A_mediator_minting_new_ids_for_the_same_bytes_gets_fifty_rounds_per_drain_and_the_reported_set_stays_bounded()
    {
        var exchange = new SimExchange();
        var mediator = new LeasingMediator(exchange, TimeSpan.Zero) { MintNewIds = true };
        for (var i = 0; i < 10; i++)
            mediator.Enqueue($"j{i}", $"bad:j{i}");
        await using var driver = new StreamDriver(exchange);
        var reportedAtEachWait = new List<int>();
        await driver.RunAsync(() => exchange.Log.Count(r => r.Type == LiveOn) > 2, TimeSpan.FromMinutes(5), onWait: () =>
        {
            reportedAtEachWait.Add(driver.Reported);
            return false;
        });

        var cycles = exchange.Log.Select((r, i) => (r, i)).Where(x => x.r.Type == LiveOn).Select(x => x.i).ToArray();
        cycles.Should().HaveCount(3);
        for (var c = 0; c < 2; c++)
            exchange.Log.Skip(cycles[c] + 1).Take(cycles[c + 1] - cycles[c] - 1).Count(r => r.Type == Drain).Should().Be(LiveDeliveryStream.MaxDrainRounds);
        driver.Items.Count(i => i.At < exchange.Log[cycles[1]].At).Should().Be(500, "the first drain yields its fifty rounds of ten, each once");
        driver.Items.Count(i => i.At >= exchange.Log[cycles[1]].At && i.At < exchange.Log[cycles[2]].At).Should().Be(500, "and so does the second");
        driver.Items.Should().OnlyContain(i => i.Item.Error != null);
        reportedAtEachWait.Should().NotBeEmpty();
        reportedAtEachWait.Max().Should().BeInRange(490, 500, "bounded by the drain's rounds (the last round's failures are yielded once the drain has ended)");
        driver.Reported.Should().BeLessThanOrEqualTo(10, "cleared at the third cycle's start");
    }

    /// <summary>
    /// I-19 and the owner's direction "end a drain round that brings nothing new": at a mediator that
    /// keeps acknowledged messages (it answers every round with the same ten, already opened, yielded
    /// and acknowledged in this drain), a round of them brings nothing new. Each poll should open each
    /// message at most once and send at most two delivery requests; nothing new is yielded either way.
    /// </summary>
    [Fact]
    public async Task At_a_mediator_that_keeps_acknowledged_messages_each_poll_opens_each_message_at_most_once()
    {
        var exchange = new SimExchange();
        var head = Enumerable.Range(0, 10).Select(i => ($"g{i}", $"ok:g{i}")).ToArray();
        exchange.Inner.On(MessagePickup.DeliveryRequestType, r => Delivery(r, head)); // acknowledged messages stay queued
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => exchange.Log.Count(r => r.Type == LiveOn) > 1, TimeSpan.FromMinutes(5));

        var firstPoll = exchange.Log.TakeWhile((r, i) => i == 0 || r.Type != LiveOn).ToArray();
        driver.Items.Select(i => i.Item.Id).Should().Equal(head.Select(h => h.Item1), "each message is yielded once");
        firstPoll.Count(r => r.Type == Drain).Should().BeLessThanOrEqualTo(2, "a round of messages already acknowledged in this drain brings nothing new: {0}", Trace(exchange));
        exchange.HostCalls.Should().BeLessThanOrEqualTo(20, "each poll opens each message at most once");
    }

    // ---- ADV3-1: the turns ------------------------------------------------------------------------

    /// <summary>
    /// ADV3-1 held: a push is always waiting, every push and every drain round brings one new message
    /// whose sender never resolves (each delivery takes the whole budget), at a mediator that gives
    /// it a new id each round. While the drain runs, its rounds and the pushes strictly take turns:
    /// neither keeps the other from its turn.
    /// </summary>
    [Fact]
    public async Task Slow_pushes_and_slow_drain_rounds_strictly_take_turns()
    {
        var exchange = new SimExchange();
        var mediator = new LeasingMediator(exchange, TimeSpan.Zero) { MintNewIds = true };
        mediator.Enqueue("d", "stall:d");
        var pushes = 0;
        exchange.BeforeBatch = () =>
        {
            if (exchange.Inner.Registration is { } registration && !registration.Pushes.TryPeek(out _))
            {
                var n = Interlocked.Increment(ref pushes);
                registration.TryPush(Push(($"p{n}", $"stall:p{n}")));
            }
        };
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => exchange.Loads.Count >= 21, TimeSpan.FromHours(1));

        var kinds = exchange.Loads.Take(20).Select(l => l.RequestId is null ? "push" : "drain").ToArray();
        kinds.Should().Equal(Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? "drain" : "push"), "drain rounds and pushes take turns");
        exchange.Loads.Take(20).Select(l => l.At).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("each delivery took its budget");
    }

    // ---- the stream with the slow memory ----------------------------------------------------------

    /// <summary>
    /// I-13 through the stream: a push of a message remembered as slow fails at once — no host call,
    /// no time passes, not acknowledged — and once its pause has ended a push of it is opened and
    /// acknowledged. Attachment ids compare exactly in the memory: a push of the same id in another
    /// case is a message of its own and is opened.
    /// </summary>
    [Fact]
    public async Task A_push_of_a_message_remembered_as_slow_fails_at_once_and_after_its_pause_is_opened()
    {
        var exchange = new SimExchange();
        exchange.Unpacker.Memory.Strike(SimExchange.Scope, "P1");
        await using var driver = new StreamDriver(exchange);
        var step = 0;
        await driver.RunAsync(() => driver.Items.Count >= 3, TimeSpan.FromMinutes(15), onWait: () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    return exchange.Inner.Registration!.TryPush(Push(("P1", "ok:m-upper")));
                case 1 when driver.Items.Count == 1:
                    step++;
                    return exchange.Inner.Registration!.TryPush(Push(("p1", "ok:m-lower")));
                case 2 when exchange.Clock.Now >= exchange.Unpacker.Memory.FirstPause:
                    step++;
                    return exchange.Inner.Registration!.TryPush(Push(("P1", "ok:m-upper")));
                default:
                    return false;
            }
        });

        driver.Items.Select(i => i.Item.Id).Should().Equal("P1", "p1", "P1");
        driver.Items[0].Item.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("did not open when it was delivered before");
        driver.Items[0].At.Should().Be(TimeSpan.Zero, "no time passed");
        driver.Items[1].Item.Error.Should().BeNull("an id in another case is another message");
        driver.Items[2].Item.Error.Should().BeNull("its pause ended");
        exchange.Opened.Should().Equal("ok:m-lower", "ok:m-upper");
        exchange.Inner.AcknowledgedIds.Should().Equal("p1", "P1");
    }

    /// <summary>
    /// I-13 in a drain round: five messages remembered as slow and five honest ones in one round of
    /// ten. The remembered ones fail at once without a host call, the honest ones open, no time
    /// passes, the items keep delivery order, and only the honest ones are acknowledged.
    /// </summary>
    [Fact]
    public async Task Remembered_messages_in_a_drain_round_fail_at_once_and_the_honest_ones_in_it_open()
    {
        var exchange = new SimExchange();
        var mediator = new LeasingMediator(exchange, TimeSpan.FromSeconds(60));
        for (var i = 0; i < 5; i++)
        {
            mediator.Enqueue($"k{i}", $"stall:k{i}");
            exchange.Unpacker.Memory.Strike(SimExchange.Scope, $"k{i}");
        }
        for (var i = 0; i < 5; i++)
            mediator.Enqueue($"h{i}", $"ok:h{i}");
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => driver.Items.Count >= 10, TimeSpan.FromMinutes(1));

        driver.Items.Select(i => i.Item.Id).Should().Equal("k0", "k1", "k2", "k3", "k4", "h0", "h1", "h2", "h3", "h4");
        driver.Items.Take(5).Should().OnlyContain(i => i.Item.Error is TimeoutException && i.Item.Error.Message.Contains("did not open when it was delivered before"));
        driver.Items.Skip(5).Should().OnlyContain(i => i.Item.Error == null);
        driver.Items.Should().OnlyContain(i => i.At == TimeSpan.Zero, "no time passed");
        exchange.Opened.Should().BeEquivalentTo(Enumerable.Range(0, 5).Select(i => $"ok:h{i}"));
        await driver.DisposeAsync();
        exchange.Inner.AcknowledgedIds.Should().BeEquivalentTo(Enumerable.Range(0, 5).Select(i => $"h{i}"));
    }

    /// <summary>
    /// The stream's acceptance edges (S7 is engineer D's; these probe around it): an honest message
    /// behind N messages whose senders never resolve, at a leasing mediator, with a
    /// <see cref="MediatorClientOptions.LivePollInterval"/> of 1 s (shorter than a drain round) and
    /// rounds of ten. It is reached within ceil(N / 10) + 1 cycles, and the time it takes is at most
    /// one budget per round that met a new message (ceil(N / 10) plus one per cycle) plus one
    /// interval per cycle. N = 256 is the memory's bound per mailbox.
    /// </summary>
    [Theory]
    [InlineData(1, 60)]
    [InlineData(16, 60)]
    [InlineData(31, 60)]
    [InlineData(100, 60)]
    [InlineData(256, 60)]
    [InlineData(1, 10)]
    [InlineData(16, 10)]
    [InlineData(31, 10)]
    [InlineData(100, 10)]
    [InlineData(256, 10)]
    public async Task An_honest_message_behind_N_slow_ones_is_reached_within_the_stated_cycles_and_time(int n, int leaseSeconds)
    {
        var exchange = new SimExchange(o => o.LivePollInterval = TimeSpan.FromSeconds(1));
        var mediator = new LeasingMediator(exchange, TimeSpan.FromSeconds(leaseSeconds));
        for (var i = 0; i < n; i++)
            mediator.Enqueue($"s{i}", $"stall:s{i}");
        mediator.Enqueue("h", "ok:h");
        await using var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => driver.Items.Any(i => i.Item.Id == "h"), TimeSpan.FromHours(1));

        var reached = driver.Items.Where(i => i.Item.Id == "h").Select(i => (TimeSpan?)i.At).FirstOrDefault();
        var cycles = exchange.Log.Count(r => r.Type == LiveOn);
        output.WriteLine($"N = {n}, lease {leaseSeconds} s: reached at {reached?.TotalSeconds} s in cycle {cycles}; {exchange.HostCalls} host calls; {exchange.Log.Count(r => r.Type == Drain)} delivery requests");
        reached.Should().NotBeNull("the honest message is reached: {0}", Trace(exchange));
        driver.Items.Single(i => i.Item.Id == "h").Item.Error.Should().BeNull();
        var learningRounds = (n + 9) / 10;
        cycles.Should().BeLessThanOrEqualTo(learningRounds + 1);
        reached!.Value.Should().BeLessThanOrEqualTo(Budget * (learningRounds + cycles) + exchange.Options.LivePollInterval * cycles);
        exchange.HostCalls.Should().Be(n + 1, "each slow message is opened once: its pause outlasts the whole run");
        NoCycleWhileDraining(exchange);
    }

    // ---- I-16, I-20: the wake and the alarms -----------------------------------------------------

    /// <summary>
    /// I-20: a run of failures whose back-off alarm is replaced at every failure, with a push every
    /// second resetting the run (so the give-up alarm is let go and armed again each time), for five
    /// minutes. Whenever the stream waits it holds at most its three alarms — every replaced one was
    /// disposed — and when it ends it holds none.
    /// </summary>
    [Fact]
    public async Task Alarms_replaced_over_and_over_leave_at_most_three_timers_and_none_after_the_end()
    {
        var exchange = new SimExchange();
        exchange.Inner.On(MessagePickup.LiveDeliveryChangeType, r => r.Live ? throw new TimeoutException("no answer (test)") : Status(r, live: false));
        exchange.Inner.On(MessagePickup.MessagesReceivedType, (Func<SentRequest, InboundMessageSnapshot>)(_ => throw new TimeoutException("no answer (test)")));
        var pushes = 0;
        var pusher = exchange.Clock.CreateTimer(_ =>
        {
            var n = Interlocked.Increment(ref pushes);
            exchange.Inner.Registration?.TryPush(Push(($"p{n}", $"ok:p{n}")));
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
        var driver = new StreamDriver(exchange);
        var most = 0;
        await driver.RunAsync(() => false, TimeSpan.FromMinutes(5), onWait: () =>
        {
            most = Math.Max(most, exchange.Clock.LiveTimers - 1); // the pusher is the test's
            return false;
        });
        pusher.Dispose();
        await driver.DisposeAsync();

        most.Should().BeInRange(1, 3, "the cycle, flush-age and give-up alarms at most");
        exchange.Clock.LiveTimers.Should().Be(0, "the end lets every alarm go");
        (exchange.Clock.Created - 1).Should().BeGreaterThan(300, "alarms were armed again and again");
    }

    /// <summary>
    /// I-20: the stream is disposed while its next cycle's alarm fires on another thread, many times
    /// over. The disposal never throws, the alarm runs no cycle (no live-on after the end's
    /// live-off), and nothing is left: no timer and no open mailbox.
    /// </summary>
    [Fact]
    public async Task Disposing_the_stream_while_its_alarm_fires_never_throws_and_runs_no_cycle()
    {
        for (var i = 0; i < 100; i++)
        {
            var exchange = new SimExchange();
            var driver = new StreamDriver(exchange);
            await driver.RunAsync(() => driver.Items.Count >= 1, TimeSpan.Zero, onWait: () =>
                driver.Items.Count == 0 && exchange.Inner.Registration!.TryPush(Push(("p1", "ok:p1"))));
            driver.Items.Should().HaveCount(1);

            using var go = new Barrier(2);
            var fire = Task.Run(() =>
            {
                go.SignalAndWait();
                return exchange.Clock.Advance(exchange.Options.LivePollInterval);
            });
            go.SignalAndWait();
            var disposing = driver.DisposeAsync().AsTask();
            await fire.WaitAsync(TimeSpan.FromSeconds(20));
            var thrown = await Record.ExceptionAsync(() => disposing.WaitAsync(TimeSpan.FromSeconds(20)));

            thrown.Should().BeNull("iteration {0}", i);
            var types = exchange.Log.Select(r => r.Type).ToArray();
            types.Count(t => t == LiveOn).Should().BeLessThanOrEqualTo(2, "iteration {0}: {1}", i, string.Join(", ", types));
            types.Last().Should().Be(LiveOff, "iteration {0}: nothing is sent after the end's live-off", i);
            exchange.Clock.LiveTimers.Should().Be(0, "iteration {0}", i);
            exchange.Inner.Registration.Should().BeNull();
        }
    }

    /// <summary>
    /// I-16: the caller cancels at the moment the stream's wait is published, as the next cycle's
    /// alarm fires. The stream ends with the cancellation; no cycle runs after it, and the end turns
    /// live mode off once.
    /// </summary>
    [Fact]
    public async Task A_cancellation_together_with_a_firing_alarm_ends_the_stream_without_running_the_cycle()
    {
        var exchange = new SimExchange();
        var driver = new StreamDriver(exchange);
        var armed = 1;
        driver.AtWait = () =>
        {
            // The first wait, after the first drain: the caller cancels as the next cycle's alarm fires.
            if (Interlocked.Exchange(ref armed, 0) != 1)
                return;
            driver.Caller.Cancel();
            exchange.Clock.Advance(exchange.Options.LivePollInterval);
        };
        await driver.RunAsync(() => false, TimeSpan.FromMinutes(5));

        driver.EndedWith.Should().BeAssignableTo<OperationCanceledException>();
        await driver.DisposeAsync();
        exchange.Log.Select(r => r.Type).Should().Equal(LiveOn, Drain, LiveOff);
        exchange.Clock.LiveTimers.Should().Be(0);
    }

    /// <summary>
    /// The container is disposed while the stream opens a delivery (its shutdown ends the delivery's
    /// budget): the consumer sees <see cref="ObjectDisposedException"/>, every reservation comes back,
    /// and the stream leaves no timer and no open mailbox.
    /// </summary>
    [Fact]
    public async Task The_container_disposed_while_the_stream_opens_a_delivery_ends_it_with_the_disposal_and_leaves_nothing()
    {
        using var shutdown = new CancellationTokenSource();
        var exchange = new SimExchange(shutdown: shutdown.Token);
        var mediator = new LeasingMediator(exchange, TimeSpan.FromSeconds(60));
        mediator.Enqueue("s0", "stall:s0");
        mediator.Enqueue("h", "ok:h");
        exchange.OnSettled = () =>
        {
            exchange.MarkDisposed();
            shutdown.Cancel();
            return true;
        };
        var driver = new StreamDriver(exchange);
        await driver.RunAsync(() => false, TimeSpan.FromMinutes(1));
        await driver.DisposeAsync();

        driver.EndedWith.Should().BeOfType<ObjectDisposedException>();
        driver.Items.Should().BeEmpty();
        SpinWait.SpinUntil(() => exchange.Unpacker.Reserved == 0 && exchange.Unpacker.Opening == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();
        SpinWait.SpinUntil(() => exchange.Clock.LiveTimers == 0, TimeSpan.FromSeconds(10)).Should().BeTrue("the delivery's timers and the stream's alarms are all let go");
        exchange.Inner.Registration.Should().BeNull();
    }

    // ---- attachment ids compare exactly ----------------------------------------------------------

    /// <summary>
    /// D-8 in the stream's own sets: ids that differ only in letter case are different messages. A
    /// drain round bringing "id-1" after "ID-1" was reported in the same drain is new (the drain goes
    /// on), and the drain ends only at a round of ids it has reported exactly. A push of "a" while
    /// "A" is held is opened, and both are acknowledged by their exact ids.
    /// </summary>
    [Fact]
    public async Task Ids_that_differ_only_in_case_are_distinct_in_the_reported_and_held_sets()
    {
        var exchange = new SimExchange();
        exchange.Inner.OnSequence(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("ID-1", "bad:upper")),
            r => Delivery(r, ("id-1", "bad:lower")),
            r => Delivery(r, ("ID-1", "bad:upper"), ("id-1", "bad:lower")));
        await using var driver = new StreamDriver(exchange);
        var pushed = false;
        await driver.RunAsync(() => driver.Items.Count >= 4, TimeSpan.FromMinutes(1), onWait: () =>
        {
            if (pushed)
                return false;
            pushed = true;
            return exchange.Inner.Registration!.TryPush(Push(("A", "ok:m-upper"))) & exchange.Inner.Registration!.TryPush(Push(("a", "ok:m-lower")));
        });

        exchange.Log.Take(4).Select(r => r.Type).Should().Equal(new[] { LiveOn, Drain, Drain, Drain }, "a round of 'id-1' after 'ID-1' is new; the round of both ends the drain");
        driver.Items.Select(i => i.Item.Id).Should().Equal("ID-1", "id-1", "A", "a");
        driver.Items.Skip(2).Should().OnlyContain(i => i.Item.Error == null);
        exchange.Opened.Should().Contain(new[] { "ok:m-upper", "ok:m-lower" });
        exchange.Inner.AcknowledgedIds.Should().Equal("A", "a");
    }

    // ---- helpers ----------------------------------------------------------------------------------

    // R1-1, I-17: between two live-on requests there is a delivery request (a cycle that started
    // drains before the next is armed), unless the first live-on failed.
    private static void NoCycleWhileDraining(SimExchange exchange)
    {
        var log = exchange.Log.Select(r => r.Type).ToArray();
        var liveOns = log.Select((t, i) => (t, i)).Where(x => x.t == LiveOn).Select(x => x.i).ToArray();
        for (var k = 0; k + 1 < liveOns.Length; k++)
            log.Skip(liveOns[k] + 1).Take(liveOns[k + 1] - liveOns[k] - 1).Should().Contain(Drain, "no cycle starts while a drain runs: {0}", Trace(exchange));
    }

    private static string Trace(SimExchange exchange)
        => string.Join(", ", exchange.Log.Select(r => $"{r.At.TotalSeconds}:{r.Type}"));
}
