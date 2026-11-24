using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
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
/// <para>
/// Since #111 the stream schedules by one rule over a phase and three alarms it arms (the next
/// cycle, the flush age, the give-up point). The second half of this class checks that rule: the
/// wake hand-over between the wait and an alarm (I-16), a replaced alarm's late callback (I-20), the
/// drain's end at a mediator that does not set delivered messages aside (I-19), what ends a run of
/// failures (I-18), and the give-up point (D-12). Where a test needs the moment the stream waits, it
/// builds the stream directly with the wait's seam, which runs right after the wait has published
/// its wake source.
/// </para>
/// </summary>
public sealed class PickupClientLiveSchedulingTests
{
    // Seven seconds: no back-off step (1, 2, 4, 8, 16, 30 s), no RequestTimeout and no budget has
    // this length, so a timer of exactly one interval is the stream's wait for its next cycle and
    // nothing else (#119 F3: at 1 s the first back-off met the same rendezvous).
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);

    [Theory]
    [InlineData(3500)]   // faster than the interval
    [InlineData(7000)]   // exactly the interval
    [InlineData(14000)]  // slower than the interval (the review's reproduction: twice the interval)
    [InlineData(35000)]  // much slower, still well inside RequestTimeout (2 min here): LivePollInterval < RequestTimeout
    public async Task Whatever_the_mediators_latency_a_started_cycle_drains_and_the_queued_message_is_yielded(int latencyMs)
    {
        var latency = TimeSpan.FromMilliseconds(latencyMs);
        var (exchange, client) = Live(o =>
        {
            o.LivePollInterval = Interval;
            o.RequestTimeout = TimeSpan.FromMinutes(2);
        });
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

        // Run on: the drain ends with a status, and the stream waits for the next cycle, one interval
        // after that end (#119: the rendezvous is that timer, and the baseline is read before the
        // clock moves, since the stream runs the cycle on its own once it does).
        var mark = exchange.Clock.Mark();
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForTimerAsync(Interval, mark, PickupTest.Timeout);
        var before = exchange.Sent.Count;
        mark = exchange.Clock.Mark();
        exchange.Clock.Advance(Interval).Should().Be(1, "one interval after the drain ended, the stream's wait fires");
        await exchange.WaitForSentAsync(before + 2);
        Types(exchange).Skip(before).Take(2).Should().Equal(new[] { LiveOn, Drain }, "the cycle that came due sends its delivery request");
        await exchange.Clock.WaitForTimerAsync(Interval, mark, PickupTest.Timeout); // its drain ended: the stream waits again
        await StopAsync(stream, next, cts);

        NoCycleWithoutItsDrain(Types(exchange));
    }

    [Fact]
    public async Task The_reviews_reproduction_twenty_slow_answers_yield_the_queued_message_instead_of_twenty_live_on_requests()
    {
        // The review's numbers scaled to this class's interval: LivePollInterval 7 s, default
        // RequestTimeout 30 s; every live-on answer succeeds with message_count 1 after two intervals.
        // Before the fix: 20 live-on, 0 delivery requests, 0 items.
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Brake(cts);
            exchange.Clock.Advance(Interval * 2);
            return Status(r, count: 1, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
            Interlocked.Increment(ref drains) == 1 ? Delivery(r, ("q1", "ok:m1")) : Status(r));
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1");
        var mark = exchange.Clock.Mark();
        var next = stream.MoveNextAsync().AsTask();
        // Eight more cycles, one per interval: twenty requests in all. Each time the stream waits for
        // its next cycle (that timer is the rendezvous), the baseline is read before the clock moves.
        for (var i = 0; i < 8; i++)
        {
            await exchange.Clock.WaitForTimerAsync(Interval, mark, PickupTest.Timeout);
            var before = exchange.Sent.Count;
            mark = exchange.Clock.Mark();
            exchange.Clock.Advance(Interval).Should().Be(1);
            await exchange.WaitForSentAsync(before + 2);
            Types(exchange).Skip(before).Take(2).Should().Equal(LiveOn, Drain);
        }
        await exchange.Clock.WaitForTimerAsync(Interval, mark, PickupTest.Timeout);
        exchange.Sent.Count.Should().Be(20);
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

        // The drain's second round answers status at t = 11 s; the next cycle is due at 21 s: the
        // stream waits on a timer of exactly one interval (measured from the end, not from the start
        // at 0 s, which would leave nothing to wait for).
        var mark = exchange.Clock.Mark();
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForTimerAsync(interval, mark, PickupTest.Timeout);
        Types(exchange).Count(t => t == LiveOn).Should().Be(1, "the drain ended at 11 s and the next cycle is not due before 21 s");
        exchange.Clock.Advance(interval - TimeSpan.FromTicks(1)).Should().Be(0, "one tick before an interval has passed since the drain ended, nothing fires");
        var before = exchange.Sent.Count;
        mark = exchange.Clock.Mark();
        exchange.Clock.Advance(TimeSpan.FromTicks(1)).Should().Be(1, "an interval after the drain ended, the stream's wait fires");
        await exchange.WaitForSentAsync(before + 2);
        Types(exchange).Skip(before).Take(2).Should().Equal(LiveOn, Drain);
        await exchange.Clock.WaitForTimerAsync(interval, mark, PickupTest.Timeout);
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
            exchange.Clock.Advance(Interval * 2);
            if (failing == "live-on" && r.Live && Interlocked.Increment(ref liveOns) == 1)
                throw new TimeoutException("no answer (test)");
            return Status(r, count: 1, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
        {
            exchange.Clock.Advance(Interval * 2);
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

    // ---- the scheduler (#111) -----------------------------------------------------------------

    /// <summary>
    /// S4 (I-16, the wake hand-over): an alarm that fires after the wait has published its wake
    /// source and before it looks again at what is due still wakes the stream. The wait's seam fires
    /// the next cycle's alarm at exactly that point; the cycle runs with no further move of the clock
    /// and no push. (Were the look taken before the wake source is published, the alarm would cancel
    /// a source nobody waits on, and the stream would sleep through it.)
    /// </summary>
    [Fact]
    public async Task An_alarm_that_fires_after_the_wait_published_its_wake_source_and_before_it_looks_again_still_runs_the_cycle()
    {
        var (exchange, _) = Live(o => o.LivePollInterval = Interval);
        var waits = new WaitSeam();
        var fired = -1;
        // The first wait follows the first drain, whose end armed the next cycle's alarm: fire it now.
        waits.At = n =>
        {
            if (n == 1)
                fired = exchange.Clock.Advance(Interval);
        };
        using var cts = new CancellationTokenSource();
        var stream = Direct(exchange, waits, cts.Token);
        var mark = exchange.Clock.Mark();
        var move = stream.MoveNextAsync().AsTask();

        await exchange.WaitForSentAsync(4); // the hang guard: a wait that sleeps through the alarm sends nothing more
        await waits.ReachedAsync(2);
        fired.Should().Be(1, "the seam fired the next cycle's alarm, and nothing else");
        Types(exchange).Should().Equal(new[] { LiveOn, Drain, LiveOn, Drain }, "the alarm that fired inside the wait ran the cycle");
        exchange.Clock.ScheduledTimersSince(mark).Should().Be(1, "the second drain's end armed the next alarm; the first one fired");
        move.IsCompleted.Should().BeFalse();
        await StopAsync(stream, move, cts);
    }

    /// <summary>
    /// S5 (I-20): an alarm replaced before it fired, whose callback still runs afterwards (a timer
    /// callback already dispatched when the timer was disposed), changes no decision. The next
    /// cycle's alarm is replaced by a back-off's when an acknowledgement fails; its callback then
    /// runs late. The stream wakes once for nothing — no acknowledgement and no cycle before the
    /// back-off's own alarm fires — and then runs both when it does.
    /// </summary>
    [Fact]
    public async Task A_replaced_alarms_late_callback_runs_no_cycle_and_no_acknowledgement_before_the_back_off_ends()
    {
        var clock = new LateCallbacks(new ManualTimeProvider());
        var inner = new FakePickupExchange(o => o.LivePollInterval = Interval, clock.Inner) { Route = WebSocketRoute };
        var acks = 0;
        inner.On(MessagePickup.MessagesReceivedType, r => Interlocked.Increment(ref acks) == 1
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer (test)"))
            : Task.FromResult(Status(r)));
        var waits = new WaitSeam();
        using var cts = new CancellationTokenSource();
        var stream = Direct(new SteppedExchange(inner, clock), waits, cts.Token);

        // The first drain ends (the next cycle's alarm, of one interval, is armed); a push arrives.
        var first = stream.MoveNextAsync().AsTask();
        await waits.ReachedAsync(1);
        inner.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("p1");

        // p1 is taken and its acknowledgement fails: the back-off's alarm replaces the next cycle's.
        var mark = clock.Inner.Mark();
        var move = stream.MoveNextAsync().AsTask();
        await clock.Inner.WaitForTimerAsync(LiveDeliveryStream.FirstBackoff, mark, PickupTest.Timeout);
        await waits.ReachedAsync(2);
        Types(inner).Should().Equal(LiveOn, Drain, Ack);

        // The replaced alarm's callback runs late: one wake for nothing, then the stream waits again.
        clock.RunLate(Interval);
        await waits.ReachedAsync(3);
        Types(inner).Should().Equal(new[] { LiveOn, Drain, Ack }, "a replaced alarm's late callback makes nothing due: the back-off still lasts");
        move.IsCompleted.Should().BeFalse();

        // The back-off's own alarm fires: the acknowledgement is retried, then the cycle runs.
        clock.Inner.Advance(LiveDeliveryStream.FirstBackoff).Should().Be(1);
        await inner.WaitForSentAsync(6);
        await waits.ReachedAsync(4);
        Types(inner).Should().Equal(LiveOn, Drain, Ack, Ack, LiveOn, Drain);
        inner.AcknowledgedIds.Should().Equal("p1", "p1");
        await StopAsync(stream, move, cts);
    }

    /// <summary>
    /// S6 (I-19, ADV5-4): a mediator that does not set delivered messages aside answers every round
    /// with the same messages at the head of its queue, and none of them opens. Each poll yields them
    /// once, with one round of failures and two delivery requests (the second brings nothing new and
    /// ends the drain); the next poll yields the same failures again.
    /// </summary>
    [Fact]
    public async Task At_a_mediator_that_does_not_set_delivered_messages_aside_each_poll_yields_the_failures_at_the_head_once_with_two_delivery_requests()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        var head = new[] { ("j1", "bad:j1"), ("j2", "bad:j2"), ("j3", "bad:j3") };
        exchange.On(MessagePickup.DeliveryRequestType, r => Delivery(r, head));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var move = stream.MoveNextAsync().AsTask();
        for (var poll = 1; poll <= 3; poll++)
        {
            var yielded = new List<PickupItem>();
            long mark = 0;
            foreach (var _ in head)
            {
                (await PickupTest.Within(move)).Should().BeTrue();
                yielded.Add(stream.Current);
                mark = exchange.Clock.Mark(); // the round after the last failure ends the drain and arms the next alarm
                move = stream.MoveNextAsync().AsTask();
            }
            await exchange.Clock.WaitForTimerAsync(Interval, mark, PickupTest.Timeout); // the drain ended: the next cycle's alarm

            yielded.Select(i => i.Id).Should().Equal(new[] { "j1", "j2", "j3" }, "poll {0} yields the failures at the head once", poll);
            yielded.Should().OnlyContain(i => i.Error != null);
            Types(exchange).Should().Equal(Enumerable.Repeat(new[] { LiveOn, Drain, Drain }, poll).SelectMany(t => t),
                "each poll sends live-on and two delivery requests: the second brings nothing new and ends the drain");
            exchange.Unpacked.Should().HaveCount(3 * poll, "the head is opened once per poll");
            move.IsCompleted.Should().BeFalse();
            if (poll < 3)
                exchange.Clock.Advance(Interval).Should().Be(1, "the next poll's alarm fires");
        }
        exchange.AcknowledgedIds.Should().BeEmpty("failures are never acknowledged");
        await StopAsync(stream, move, cts);
    }

    /// <summary>
    /// I-18 (ADV5-3, owner decision B1): each case has one failure, then one kind of progress, then a
    /// failure. After an acknowledgement the mediator accepted (the failure before it was that
    /// acknowledgement's first attempt) — a request that succeeded — the next failure backs off one
    /// second again. After a push that brings a new message during the back-off, the back-off goes on:
    /// the next failure backs off two seconds, the run's next step.
    /// </summary>
    [Theory]
    [InlineData("an acknowledgement the mediator accepted")]
    [InlineData("a push that brings a new message")]
    public async Task After_a_successful_request_the_next_failure_backs_off_one_second_again_and_after_a_push_the_back_off_goes_on(string progress)
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        var liveOns = 0;
        var byAck = progress == "an acknowledgement the mediator accepted";
        // By acknowledgement: the first live-on succeeds and its drain brings q1, whose acknowledgement
        // fails once; every later live-on fails. By push: every live-on fails.
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Task.FromResult(Status(r, live: false));
            return byAck && Interlocked.Increment(ref liveOns) == 1
                ? Task.FromResult(Status(r, live: true))
                : Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer (test)"));
        });
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        var acks = 0;
        exchange.On(MessagePickup.MessagesReceivedType, r => Interlocked.Increment(ref acks) == 1
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer (test)"))
            : Task.FromResult(Status(r)));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        Task<bool> move;
        var mark = exchange.Clock.Mark();
        if (byAck)
        {
            (await Next(stream)).Id.Should().Be("q1");
            mark = exchange.Clock.Mark();
            move = stream.MoveNextAsync().AsTask(); // q1 is taken; its acknowledgement fails: the run's first failure
            await exchange.Clock.WaitForTimerAsync(LiveDeliveryStream.FirstBackoff, mark, PickupTest.Timeout);
            Types(exchange).Should().Equal(LiveOn, Drain, Ack);
        }
        else
        {
            move = stream.MoveNextAsync().AsTask(); // live-on fails: the run's first failure
            await exchange.Clock.WaitForTimerAsync(LiveDeliveryStream.FirstBackoff, mark, PickupTest.Timeout);
            exchange.Registration!.TryPush(Push(("f1", "bad:f1"))).Should().BeTrue(); // new, and it never opens: nothing to acknowledge
            (await PickupTest.Within(move)).Should().BeTrue();
            stream.Current.Id.Should().Be("f1");
            move = stream.MoveNextAsync().AsTask();
            Types(exchange).Should().Equal(LiveOn);
        }

        // The back-off ends. By acknowledgement: the acknowledgement is accepted, then live-on fails.
        // By push: live-on fails.
        mark = exchange.Clock.Mark();
        exchange.Clock.Advance(LiveDeliveryStream.FirstBackoff).Should().Be(1);
        var next = await Adv5Wait.BackoffAlarmAsync(exchange.Clock, move);
        Types(exchange).Should().Equal(byAck ? new[] { LiveOn, Drain, Ack, Ack, LiveOn } : new[] { LiveOn, LiveOn });
        if (byAck)
            next.Should().Be(LiveDeliveryStream.FirstBackoff, "{0} ended the run, so the failure after it is the first of a new run", progress);
        else
            next.Should().Be(LiveDeliveryStream.Backoff(2), "{0} lets the give-up alarm go but keeps the back-off", progress);
        await StopAsync(stream, move, cts);
    }

    /// <summary>
    /// D-12 (ADV5-2 held): a stream whose steps all fail ends with the last failure's exception once
    /// its run is RequestTimeout + 30 s old (60 s here), and not before. The sixth attempt, after
    /// pauses of 1, 2, 4, 8 and 16 s, takes until one tick before that point to fail (it is backed off
    /// 30 s and retried, and the seventh failure ends the stream), or until exactly that point (it ends
    /// the stream).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_of_failures_ends_the_stream_once_it_is_RequestTimeout_plus_30_s_old_and_not_a_tick_before(bool sixthFailsAtThePoint)
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        var giveUp = exchange.Options.RequestTimeout + LiveDeliveryStream.GiveUpMargin;
        var start = exchange.Clock.GetTimestamp();
        var failures = new List<TimeoutException>();
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Task.FromResult(Status(r, live: false));
            TimeoutException error;
            lock (failures)
            {
                error = new TimeoutException($"attempt {failures.Count + 1}");
                failures.Add(error);
                if (failures.Count == 6)
                    exchange.Clock.Advance(giveUp - exchange.Clock.GetElapsedTime(start) - (sixthFailsAtThePoint ? TimeSpan.Zero : TimeSpan.FromTicks(1)));
            }
            return Task.FromException<InboundMessageSnapshot>(error);
        });
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var mark = exchange.Clock.Mark();
        var move = stream.MoveNextAsync().AsTask();

        foreach (var pause in new[] { 1, 2, 4, 8, 16 })
        {
            await exchange.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(pause), mark, PickupTest.Timeout);
            move.IsCompleted.Should().BeFalse("a run younger than RequestTimeout + 30 s is retried");
            mark = exchange.Clock.Mark();
            exchange.Clock.Advance(TimeSpan.FromSeconds(pause));
        }
        if (!sixthFailsAtThePoint)
        {
            await exchange.Clock.WaitForTimerAsync(LiveDeliveryStream.MaxBackoff, mark, PickupTest.Timeout);
            move.IsCompleted.Should().BeFalse("a failure one tick before the give-up point is backed off and retried");
            exchange.Clock.GetElapsedTime(start).Should().Be(giveUp - TimeSpan.FromTicks(1));
            exchange.Clock.Advance(LiveDeliveryStream.MaxBackoff);
        }

        // Only the move is captured (L-067); a stream that did not end is stopped before it is disposed.
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(move));
        await cts.CancelAsync();
        _ = await Record.ExceptionAsync(() => move.WaitAsync(PickupTest.Timeout));
        await PickupTest.Within(stream.DisposeAsync());
        TimeoutException expected;
        lock (failures)
            expected = failures[sixthFailsAtThePoint ? 5 : 6];
        thrown.Should().BeSameAs(expected, "the first failure at or after the give-up point ends the stream with its own exception");
        Types(exchange).Should().Equal(Enumerable.Repeat(LiveOn, sixthFailsAtThePoint ? 6 : 7).Append(LiveOff));
    }

    // A stream built directly, not through PickupClient, so the test can pass the wait's seam.
    private static IAsyncEnumerator<PickupItem> Direct(IMediatorExchange exchange, WaitSeam waits, CancellationToken ct)
        => LiveDeliveryStream.ReceiveAsync(exchange, new PickupLog(logger: null), M, L, serviceEndpointOverride: null,
            static (_, _, _, _) => Task.FromResult(MessagePickupVersion.V3), ct, waits.Published).GetAsyncEnumerator(ct);

    /// <summary>
    /// The live stream's wait seam: counts the waits (each one has published its wake source and is
    /// about to look again at what is due), runs <see cref="At"/> there on the stream's thread, and
    /// lets a test wait for the n-th. Everything the stream did before a wait has happened when that
    /// wait is reached.
    /// </summary>
    private sealed class WaitSeam
    {
        private readonly object _gate = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        /// <summary>Runs at each wait, with its number (1-based), before the wait looks again.</summary>
        public Action<int>? At { get; set; }

        /// <summary>The seam the stream calls.</summary>
        public void Published()
        {
            int n;
            lock (_gate)
                n = ++_count;
            At?.Invoke(n);
            TaskCompletionSource signal;
            lock (_gate)
            {
                signal = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
        }

        /// <summary>Wait (bounded, in real time) until the stream has reached its <paramref name="count"/>-th wait.</summary>
        public async Task ReachedAsync(int count)
        {
            using var cts = new CancellationTokenSource(PickupTest.Timeout);
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_count >= count)
                        return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(cts.Token);
            }
        }
    }

    /// <summary>
    /// A clock over a <see cref="ManualTimeProvider"/> that keeps every timer's callback, so a test
    /// can run one late — after its timer was disposed, as a callback already dispatched when Dispose
    /// ran would be. Timers otherwise are the inner clock's.
    /// </summary>
    private sealed class LateCallbacks(ManualTimeProvider inner) : TimeProvider
    {
        private readonly List<(TimeSpan Due, TimerCallback Callback, object? State)> _created = new();

        public ManualTimeProvider Inner => inner;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_created)
                _created.Add((dueTime, callback, state));
            return inner.CreateTimer(callback, state, dueTime, period);
        }

        /// <summary>Run, now, the callback of the first timer created with <paramref name="due"/>.</summary>
        public void RunLate(TimeSpan due)
        {
            (TimeSpan Due, TimerCallback Callback, object? State) timer;
            lock (_created)
                timer = _created.First(t => t.Due == due);
            timer.Callback(timer.State);
        }
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
