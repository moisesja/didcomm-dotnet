using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-5, a red-team pass of #96 scoped to the live stream's cycle scheduling after the fix for PR
/// #112 review R1-1 (<c>111dc8e</c>: a started cycle runs its drain to its end before the next one
/// is armed, one <c>LivePollInterval</c> after that end; a failure backs off). Driven by the fake
/// exchange and manual clocks (L-044: nothing sleeps). Every mediator answer yields or moves the
/// clock, and a stream that runs away is braked (a capped number of <c>live-delivery-change</c>, or
/// a capped number of clock readings), so a regression fails instead of hanging the test host.
/// </summary>
public sealed class PickupClientLiveAdv5Tests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    // ==== findings (these tests fail while the defect is there) ===================================

    /// <summary>
    /// ADV5-1 (fixed in #96, PR #112 round 1; was a defect, Low; introduced by <c>111dc8e</c>, the R1-1 fix). Property broken —
    /// <see cref="LiveDeliveryStream"/> remarks (ADV3-5): "A reading lower than that one (a host clock
    /// that stepped back: an NTP correction, a restored snapshot) counts as due, at the cost of one
    /// early cycle, retry or flush per step"; and the loop's own rule that it waits for "a push, the
    /// next cycle, or the caller's cancellation" when nothing is due.
    /// <para>
    /// Attack: since the fix, <c>_nextCycle</c> is not re-armed while a drain is in progress (it keeps
    /// the value that made the cycle due), and the loop runs no cycle while <c>_draining</c>. But
    /// <c>WaitAsync</c> still returns "something to do" at once whenever <c>_nextCycle</c> is due,
    /// whatever <c>_draining</c> says; the drain round itself is gated by <c>_notBefore</c> (the end of
    /// the last back-off), which <c>WaitAsync</c> never waits for. On a monotonic clock a draining
    /// stream always finds <c>_notBefore</c> passed, so the two never disagree. One step back of the
    /// host clock to a reading inside the last back-off's window (after a failed step and its
    /// recovery) makes the next cycle due (the reading is below the previous drain's end) while the
    /// back-off is not: the early cycle runs (as documented), then every pass finds nothing to do and
    /// <c>WaitAsync</c> returns <c>true</c> synchronously — a hot loop on the consumer's thread with
    /// no await, no request and the taken message's acknowledgement withheld, until the clock passes
    /// the old back-off's end (with a real clock up to the longest back-off, 8 s; with this frozen
    /// clock, for ever). Before the fix, <c>CycleAsync</c> re-armed <c>_nextCycle</c> at the cycle's
    /// start, so the same step left the stream waiting on a timer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADV5_1_one_clock_step_back_into_the_last_back_off_makes_a_draining_stream_spin_without_awaiting()
    {
        var stepping = new SteppingTimeProvider();
        var clock = new SpinBrakeTimeProvider(stepping);
        var exchange = new FakePickupExchange(time: null) { Route = WebSocketRoute };
        using var cts = new CancellationTokenSource();
        clock.Brake = cts;
        var liveOn = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (r.Live && Interlocked.Increment(ref liveOn) == 1)
                return Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer (test)"));
            return Task.FromResult(Status(r, live: r.Live));
        });
        var client = new PickupClient(new SteppedExchange(exchange, clock), logger: null);
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        // t0: live-on times out → back-off of 1 s (the back-off window is [t0, t0 + 1 s)).
        var first = stream.MoveNextAsync().AsTask();
        await stepping.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn);

        // t0 + 1 s: the recovery cycle turns live mode on and drains (nothing queued); its drain ended
        // at t0 + 1 s, so the next cycle is due at t0 + 61 s, and the stream waits for it.
        stepping.Inner.Advance(LiveDeliveryStream.FirstBackoff);
        await exchange.WaitForSentAsync(3);
        await stepping.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn, LiveOn, Drain);

        // The host clock steps back half a second (to t0 + 0.5 s: below the drain's end, inside the old
        // back-off). A push wakes the stream: the step's one early cycle runs, then the push is yielded.
        stepping.StepBack(TimeSpan.FromMilliseconds(500));
        exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("q1");
        // Aligned with the ADV5-1 fix: the early cycle is a whole cycle — live-on and its drain round
        // (before the fix its drain stayed gated by the old back-off, which is what made the loop spin).
        Types(exchange).Should().Equal(new[] { LiveOn, LiveOn, Drain, LiveOn, Drain }, "the step costs one early cycle (ADV3-5), its drain included");
        var sentBefore = exchange.Sent.Count;

        // The consumer asks for the next item. Nothing is due but the drain round after the back-off:
        // the stream should wait (or drain), not read the clock in a loop. The brake trips after
        // 100 000 readings with no await in between (a few dozen are a normal pass and its wait).
        var sentDuringSpin = -1;
        var ackedDuringSpin = -1;
        clock.OnTrip = () =>
        {
            sentDuringSpin = exchange.Sent.Count - sentBefore;
            ackedDuringSpin = exchange.AcknowledgedIds.Count;
        };
        clock.Arm(100_000);
        var second = Task.Run(async () => await stream.MoveNextAsync());
        await Adv5Wait.MoveOrTimerAsync(stepping.Inner, second);
        var spun = clock.Tripped;

        if (!second.IsCompleted)
            await cts.CancelAsync();
        try
        {
            await second.WaitAsync(PickupTest.Timeout);
        }
        catch (OperationCanceledException)
        {
        }
        await PickupTest.Within(stream.DisposeAsync());

        spun.Should().BeFalse(
            "a single step back of the clock costs one early cycle and nothing more (ADV3-5); instead the draining stream read the clock 100 000 times without awaiting anything, sending {0} request(s) and {1} acknowledgement id(s) meanwhile (WaitAsync returns at once on the stale due _nextCycle while the drain round waits for _notBefore)",
            sentDuringSpin, ackedDuringSpin);
    }

    /// <summary>
    /// ADV5-2 (fixed in #96, PR #112 round 1; was a defect, Medium; pre-existing in #96 since the live stream landed, unchanged by the
    /// R1-1 fix). Property broken — <see cref="LiveDeliveryStream"/> <c>Retryable</c>: the busy
    /// refusal (<see cref="MediatorBusyException"/>) is retried because it is "transient, the others
    /// end within <see cref="MediatorClientOptions.RequestTimeout"/>"; and the documented back-off
    /// "from 1 s doubling to 30 s" (class remarks, <see cref="PickupClient.ReceiveLiveAsync"/>,
    /// architecture "Failures").
    /// <para>
    /// Attack: the fifth failure in a row ends the stream before a fifth back-off is computed, so
    /// the pauses are 1, 2, 4 and 8 s and the 16 s and 30 s steps of the schedule are dead code: a
    /// stream gives up 15 s after its first failure. That is half of the default
    /// <see cref="MediatorClientOptions.RequestTimeout"/> (30 s, configurable to 10 minutes), the very
    /// time the busy rule waits for. So when the container's pending bound is full of requests that
    /// end only at their <c>RequestTimeout</c> — 64 requests to another mediator that does not
    /// answer (the bound is per container, not per mediator), or a burst of host fetches — a live
    /// stream to a healthy mediator is refused five times and ends with an
    /// <see cref="InvalidOperationException"/> at 15 s, although the bound frees up at 30 s. The
    /// same 15 s ends a stream whose mediator restarts and refuses connections for 16 s
    /// (<see cref="TransportException"/> at once on each try): "recovers from a dropped connection"
    /// holds only for outages shorter than 15 s.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("busy for one RequestTimeout")]
    [InlineData("mediator restarting for 16 s")]
    public async Task ADV5_2_the_back_off_gives_up_after_15_s_before_a_transient_condition_that_lasts_one_RequestTimeout_clears(string kind)
    {
        var (exchange, client) = Live();
        var outage = kind == "busy for one RequestTimeout" ? exchange.Options.RequestTimeout : TimeSpan.FromSeconds(16);
        var start = exchange.Clock.GetTimestamp();
        var attempts = new List<double>();
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Task.Yield();
            if (!r.Live)
                return Status(r, live: false);
            var at = exchange.Clock.GetElapsedTime(start);
            lock (attempts)
            {
                attempts.Add(at.TotalSeconds);
                if (attempts.Count > 50)
                    throw new InvalidDataException("brake: the stream kept trying"); // ends the stream at once
            }
            if (at < outage)
            {
                throw kind == "busy for one RequestTimeout"
                    ? new MediatorBusyException("64 mediator requests are already waiting for an answer; the request was not sent.")
                    : new TransportException("connection refused (test)");
            }
            return Status(r, count: 1, live: true);
        });
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var first = stream.MoveNextAsync().AsTask();
        await AdvanceUntilAsync(exchange, first, maxSeconds: 120);
        Exception? error = null;
        var moved = false;
        try
        {
            moved = await PickupTest.Within(first);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        string seen;
        lock (attempts)
            seen = string.Join(", ", attempts.Select(a => $"{a:0.#} s"));

        if (moved)
        {
            stream.Current.Id.Should().Be("q1");
            var next = stream.MoveNextAsync().AsTask();
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            await StopAsync(stream, next, cts);
        }
        else
        {
            await PickupTest.Within(stream.DisposeAsync());
        }

        error.Should().BeNull(
            "the condition is the transient kind the stream retries, and it cleared at {0} s; but the stream tried at {1} and gave up with {2} — its back-off never goes past 8 s, so five tries span 15 s",
            outage.TotalSeconds, seen, error?.GetType().Name ?? "nothing");
        moved.Should().BeTrue("the queued message arrives once the condition has cleared");
    }

    /// <summary>
    /// ADV5-3 (known limit deferred to #111 by owner decision; Low; pre-existing in #96, unchanged by the R1-1 fix). Property broken —
    /// <see cref="PickupClient.ReceiveLiveAsync"/>: "Five failures in a row end the stream with the
    /// last exception"; <c>&lt;exception cref="TimeoutException"&gt;</c>: "five steps in a row got no
    /// answer"; <see cref="LiveDeliveryStream"/> remarks: "five in a row end the stream".
    /// <para>
    /// Attack: the failure count is reset only by <c>DrainDone</c> (a drain that ran to its end), never
    /// by a step that succeeded. A drain that makes progress and then has one step time out — a long
    /// backlog over a lossy link, a mediator whose second round of a large queue is slow — fails its
    /// cycle every time, so the count climbs across cycles in which live mode was turned on, a
    /// delivery was received and yielded, and its acknowledgement was accepted. Here each cycle
    /// yields one new message and acknowledges it, then its next round times out: after five such
    /// cycles (five messages delivered, fifteen successful exchanges) the stream ends with a
    /// <see cref="TimeoutException"/>, although no two failures were in a row. With 5 % loss and a
    /// backlog of a few hundred messages a drain rarely completes, so the stream ends after about five
    /// lost requests spread over hundreds of successful ones.
    /// </para>
    /// </summary>
    // Aligned with the ADV5-2 fix (time-based give-up): the run of failures is still reset only by a
    // completed drain, so scattered failures across delivering cycles end the stream once the run is
    // older than RequestTimeout + 30 s (here at the seventh such cycle, about 61 s), not at the fifth.
    [Fact(Skip = "Known limit, deferred to #111: the run of failures is reset only when a drain completes, so failures separated by successful steps still end the stream once the run is older than RequestTimeout + 30 s (ADV5-3).")]
    public async Task ADV5_3_failures_separated_by_successful_steps_and_delivered_messages_still_end_the_stream_as_five_in_a_row()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = TimeSpan.FromSeconds(60));
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            var n = Interlocked.Increment(ref drains);
            if (n > 80)
                throw new InvalidDataException("brake"); // ends the stream at once
            return n % 2 == 1
                ? Delivery(r, ($"q{(n + 1) / 2}", $"ok:m{(n + 1) / 2}"))
                : throw new TimeoutException("no answer (test)");
        });
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var ids = new List<string>();
        Exception? error = null;
        Task<bool>? move = null;
        while (ids.Count < 12 && error is null)
        {
            move = stream.MoveNextAsync().AsTask();
            await AdvanceUntilAsync(exchange, move, maxSeconds: 60);
            try
            {
                if (!await PickupTest.Within(move))
                    break;
                ids.Add(stream.Current.Id);
                move = null;
            }
            catch (Exception ex)
            {
                error = ex;
                move = null;
            }
        }
        var types = Types(exchange);
        await StopQuietlyAsync(stream, move, cts);

        ids.Should().StartWith(new[] { "q1", "q2", "q3", "q4", "q5" }, "every cycle delivered a new message");
        exchange.AcknowledgedIds.Should().StartWith(new[] { "q1", "q2", "q3", "q4" }, "and its acknowledgement was accepted: {0}", string.Join(", ", types));
        error.Should().BeNull(
            "between every two failures the stream turned live mode on, received a delivery, yielded it and had it acknowledged — the failures were never in a row — yet it ended with {0} after {1}: {2}",
            error?.GetType().Name ?? "nothing", string.Join(", ", ids), string.Join(", ", types));
        ids.Should().Contain("q12");
    }

    /// <summary>
    /// ADV5-4 (known limit deferred to #111 by owner decision; Low; pre-existing in #96, unchanged by the R1-1 fix). Property broken —
    /// <see cref="PickupClient.ReceiveLiveAsync"/>: "A mediator that does not set delivered messages
    /// aside delivers it on every drain", and the drain is the poll that runs "one
    /// <see cref="MediatorClientOptions.LivePollInterval"/> after each such round ends" — a request
    /// rate per interval, not a burst.
    /// <para>
    /// Attack: Message Pickup 3.0 does not require a mediator to set delivered messages aside: a
    /// spec-conformant mediator answers every <c>delivery-request</c> with the head of the queue until
    /// it is acknowledged. A drain ends only at a <c>status</c>, an empty delivery, a failure or 50
    /// rounds, and a round whose messages all fail to open is none of those. So one message no
    /// agent can open (junk from anyone who can send a forward to the recipient, or a sender whose
    /// DID never resolves) at the head of such a mediator's queue turns every cycle into 50
    /// back-to-back <c>delivery-request</c>s, each answered with the same message, which the stream
    /// yields as the same failed item 50 times per cycle — 50 requests per second at the 1 s
    /// interval, for as long as the host has not discarded it. Ten such messages yield 500 failed
    /// items per cycle and block every message behind them.
    /// </para>
    /// </summary>
    [Fact(Skip = "Known limit, deferred to #111: at a mediator that does not set delivered messages aside, one message that never opens makes every drain send 50 requests and yield it 50 times (ADV5-4).")]
    public async Task ADV5_4_one_failing_message_at_a_mediator_that_does_not_lease_makes_every_drain_send_fifty_requests_and_yield_it_fifty_times()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = TimeSpan.FromSeconds(60));
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            return Delivery(r, ("junk", "bad:junk"));
        });
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var failedItems = 0;
        var move = stream.MoveNextAsync().AsTask();
        while (failedItems <= 200)
        {
            await Adv5Wait.MoveOrTimerAsync(exchange.Clock, move);
            if (!move.IsCompleted)
                break; // the stream waits for the next cycle: this cycle's drain is over
            (await move).Should().BeTrue();
            stream.Current.Error.Should().NotBeNull();
            failedItems++;
            move = stream.MoveNextAsync().AsTask();
        }
        var drainRequests = Types(exchange).Count(t => t == Drain);
        await StopQuietlyAsync(stream, move, cts);

        drainRequests.Should().BeLessThanOrEqualTo(2,
            "one poll per interval: a round that brings back only what this drain already reported is no reason for another round; the stream sent {0} delivery-request(s) and yielded the same failed message {1} times in one cycle",
            drainRequests, failedItems);
    }

    // ==== attacks that held (passing regressions) ================================================

    /// <summary>
    /// Held: a drain that runs all 50 rounds against a mediator answering each round in twice the
    /// interval sends one <c>live-delivery-change</c>, all 50 delivery requests and an acknowledgement
    /// after each round; the next cycle is due exactly one interval after the fiftieth answer, not
    /// before; nothing is acknowledged twice.
    /// </summary>
    [Fact]
    public async Task A_fifty_round_drain_slower_than_the_interval_is_never_pre_empted_and_the_next_cycle_is_due_one_interval_after_its_last_answer()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        var liveOns = 0;
        var nextCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Task.Yield();
            var n = r.Live ? Interlocked.Increment(ref liveOns) : 0;
            if (n > 5)
                await cts.CancelAsync();
            if (n == 2)
            {
                // The next cycle has started: it is held here, on the stream's token, so the stop
                // below finds the stream parked (#119: left to run on, it yields q51 first).
                nextCycle.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, r.Ct);
            }
            exchange.Clock.Advance(Interval * 2);
            return Status(r, count: 60, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            exchange.Clock.Advance(Interval * 2);
            var n = Interlocked.Increment(ref drains);
            return Delivery(r, ($"q{n}", $"ok:m{n}"));
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        for (var i = 1; i <= LiveDeliveryStream.MaxDrainRounds; i++)
            (await Next(stream)).Id.Should().Be($"q{i}");
        var mark = exchange.Clock.Mark();
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForTimerAsync(Interval, mark, PickupTest.Timeout); // the wait for the next cycle, one interval after the fiftieth answer

        var types = Types(exchange);
        types.Count(t => t == LiveOn).Should().Be(1, "a started cycle is never pre-empted: {0}", string.Join(", ", types));
        types.Count(t => t == Drain).Should().Be(LiveDeliveryStream.MaxDrainRounds);
        exchange.AcknowledgedIds.Should().OnlyHaveUniqueItems().And.HaveCount(LiveDeliveryStream.MaxDrainRounds);
        exchange.Clock.Advance(Interval - TimeSpan.FromTicks(1)).Should().Be(0, "one tick before an interval has passed since the fiftieth answer, nothing fires");
        exchange.Clock.Advance(TimeSpan.FromTicks(1)).Should().Be(1, "an interval after the fiftieth answer, the stream's wait fires");
        await PickupTest.Within(nextCycle.Task);
        Types(exchange).Skip(types.Count).Should().Equal(LiveOn);
        await StopAsync(stream, next, cts);
    }

    /// <summary>
    /// Held: pushes that arrive during every slow drain round (each answer takes three intervals)
    /// alternate with the rounds, every message — drained or pushed — is yielded once and
    /// acknowledged once, and the drain is not pre-empted by the cycles that "came due" meanwhile.
    /// </summary>
    [Fact]
    public async Task Pushes_arriving_during_every_slow_drain_round_alternate_with_it_and_each_is_acknowledged_exactly_once()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        var liveOns = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Task.Yield();
            if (r.Live && Interlocked.Increment(ref liveOns) > 5)
                await cts.CancelAsync();
            return Status(r, live: r.Live);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            var n = Interlocked.Increment(ref drains);
            exchange.Clock.Advance(Interval * 3);
            exchange.Registration!.TryPush(Push(($"p{n}", $"ok:p{n}"))).Should().BeTrue();
            return n <= 5 ? Delivery(r, ($"d{n}", $"ok:d{n}")) : Status(r);
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var seen = new List<string>();
        for (var i = 0; i < 11; i++)
            seen.Add((await Next(stream)).Id);
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        seen.Should().Equal("d1", "p1", "d2", "p2", "d3", "p3", "d4", "p4", "d5", "p5", "p6");
        var types = Types(exchange);
        types.Count(t => t == LiveOn).Should().Be(1, "{0}", string.Join(", ", types));
        types.Count(t => t == Drain).Should().Be(6);
        exchange.AcknowledgedIds.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(seen, "every item was taken when the next was asked for, and acknowledged once");
        await StopAsync(stream, next, cts);
        exchange.AcknowledgedIds.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Held: an acknowledgement that times out in the middle of a drain ends that drain (a failed
    /// step), the stream backs off, and the next cycle turns live mode on and drains again; the ids
    /// are kept, acknowledged once the mediator answers, and never sent twice after that.
    /// </summary>
    [Fact]
    public async Task An_acknowledgement_that_times_out_mid_drain_backs_off_and_the_next_cycle_drains_again_with_nothing_lost()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        var acks = 0;
        exchange.On(MessagePickup.MessagesReceivedType, async r =>
        {
            await Task.Yield();
            if (Interlocked.Increment(ref acks) == 1)
            {
                exchange.Clock.Advance(exchange.Options.RequestTimeout);
                throw new TimeoutException("no answer (test)");
            }
            return Status(r);
        });
        var drains = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            var n = Interlocked.Increment(ref drains);
            return n switch
            {
                1 => Delivery(r, ("q1", "ok:m1")),
                2 => Delivery(r, ("q2", "ok:m2")),
                _ => Status(r),
            };
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1");
        var second = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // the back-off after the failed acknowledgement
        Types(exchange).Should().Equal(LiveOn, Drain, Ack);
        exchange.Clock.Advance(LiveDeliveryStream.FirstBackoff);
        (await PickupTest.Within(second)).Should().BeTrue();
        stream.Current.Id.Should().Be("q2");
        var third = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

        Types(exchange).Should().Equal(LiveOn, Drain, Ack, Ack, LiveOn, Drain, Ack, Drain);
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q1", "q2" }, "q1 is kept after the failed attempt and sent again once; q2 follows");
        await StopAsync(stream, third, cts);
    }

    /// <summary>
    /// Held: a consumer that cancels while a live-on slower than the interval is in flight ends the
    /// stream with live mode turned off and nothing acknowledged twice; the end does not wait for the
    /// withheld answer longer than its own budget.
    /// </summary>
    [Fact]
    public async Task Cancelled_while_a_live_on_slower_than_the_interval_is_in_flight_the_end_turns_live_off_once()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        var answer = new TaskCompletionSource<InboundMessageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveOns = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Task.Yield();
            if (!r.Live)
                return Status(r, live: false);
            var n = Interlocked.Increment(ref liveOns);
            if (n == 2)
            {
                // The second cycle's answer is withheld: the consumer cancels meanwhile.
                exchange.Clock.Advance(Interval * 5);
                using var _ = r.Ct.Register(() => answer.TrySetCanceled(r.Ct));
                return await answer.Task;
            }
            return Status(r, live: true);
        });
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("q1");
        var next = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        exchange.Clock.Advance(Interval);
        await exchange.WaitForSentAsync(5);
        await StopAsync(stream, next, cts);

        Types(exchange).Should().Equal(LiveOn, Drain, Ack, Drain, LiveOn, LiveOff);
        exchange.AcknowledgedIds.Should().Equal("q1");
    }

    /// <summary>
    /// Held (FR-PICK-08): with the longest interval (24 h), pushes in between never bring the next
    /// cycle forward and it runs exactly 24 h after the start-up drain ended — not a tick before.
    /// </summary>
    [Fact]
    public async Task With_a_24_hour_interval_pushes_never_bring_the_poll_forward_and_it_runs_exactly_one_interval_after_the_drain_ended()
    {
        var day = TimeSpan.FromHours(24);
        var (exchange, client) = Live(o => o.LivePollInterval = day);
        using var cts = new CancellationTokenSource();
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            exchange.Clock.Advance(TimeSpan.FromSeconds(7));
            return Status(r);
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask();
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        Types(exchange).Should().Equal(LiveOn, Drain);
        var drainEnded = exchange.Clock.GetTimestamp();

        for (var i = 0; i < 23; i++)
        {
            exchange.Clock.Advance(TimeSpan.FromHours(1));
            exchange.Registration!.TryPush(Push(($"p{i}", $"ok:p{i}"))).Should().BeTrue();
            (await PickupTest.Within(move)).Should().BeTrue();
            stream.Current.Id.Should().Be($"p{i}");
            move = stream.MoveNextAsync().AsTask();
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        }
        Types(exchange).Count(t => t == LiveOn).Should().Be(1);
        exchange.Clock.Advance(day - exchange.Clock.GetElapsedTime(drainEnded) - TimeSpan.FromTicks(1)).Should().Be(0, "one tick before 24 h have passed since the drain ended, nothing fires");
        Types(exchange).Count(t => t == LiveOn).Should().Be(1);
        var sent = exchange.Sent.Count;
        exchange.Clock.Advance(TimeSpan.FromTicks(1)).Should().Be(1, "24 h after the drain ended, the stream's wait fires");
        await exchange.WaitForSentAsync(sent + 2);
        Types(exchange).Skip(sent).Take(2).Should().Equal(LiveOn, Drain);
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        await StopAsync(stream, move, cts);
    }

    /// <summary>
    /// Held: a mediator that answers live-on but lets every delivery request run past
    /// <see cref="MediatorClientOptions.RequestTimeout"/> gets one live-on per attempt (no storm),
    /// the documented pauses, and the stream ends once its steps have kept failing for longer than
    /// RequestTimeout + 30 s (ADV5-2's fix); live mode is turned off.
    /// </summary>
    [Fact]
    public async Task A_mediator_slower_than_RequestTimeout_on_every_drain_round_gets_one_live_on_per_attempt_and_no_storm()
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        using var cts = new CancellationTokenSource();
        var liveOns = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, async r =>
        {
            await Task.Yield();
            if (r.Live && Interlocked.Increment(ref liveOns) > 20)
                await cts.CancelAsync();
            return Status(r, count: 3, live: r.Live);
        });
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            exchange.Clock.Advance(exchange.Options.RequestTimeout);
            throw new TimeoutException("no answer (test)");
        });
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var start = exchange.Clock.GetTimestamp();
        var first = stream.MoveNextAsync().AsTask();
        // Aligned with the ADV5-2 fix (PR #112): the stream now gives up on time, once its steps have
        // kept failing for longer than RequestTimeout + 30 s, not at the fifth failure. Each failure
        // here costs a whole RequestTimeout, so fewer attempts fit in that time.
        for (var pause = 1; pause <= 64 && !first.IsCompleted; pause *= 2)
        {
            var timer = exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
            if (await Task.WhenAny(first, timer).WaitAsync(PickupTest.Timeout) == first)
                break;
            exchange.Clock.Advance(TimeSpan.FromSeconds(Math.Min(pause, 30)));
        }

        await FluentActions.Awaiting(() => PickupTest.Within(first)).Should().ThrowAsync<TimeoutException>();
        await PickupTest.Within(stream.DisposeAsync());
        var types = Types(exchange);
        types[^1].Should().Be(LiveOff);
        types.Take(types.Count - 1).Chunk(2).Should().OnlyContain(pair => pair.Length == 2 && pair[0] == LiveOn && pair[1] == Drain, "one live-on per attempt, each followed by its drain round: no storm");
        exchange.Clock.GetElapsedTime(start).Should().BeGreaterThan(exchange.Options.RequestTimeout + LiveDeliveryStream.GiveUpMargin, "the stream gave up only after its steps had kept failing for longer than RequestTimeout + 30 s");
    }

    /// <summary>
    /// Pinned (passes; the bound that "measured from the end of the previous drain" implies, which
    /// the docs do not state): a drain whose every round meets a message from a sender whose DID
    /// never resolves runs its 50 rounds, each one <see cref="MediatorClientOptions.UnpackTimeout"/>
    /// long, before the next cycle is armed — with the defaults the next <c>live-delivery-change</c>
    /// comes 50 × 20 s + 1 minute = 17 min 40 s after the first, not one minute. A connection that
    /// dropped during such a drain has live mode re-armed only then (the drain itself keeps
    /// fetching, so what is queued still arrives). <c>MaxDrainRounds</c> is what bounds it.
    /// </summary>
    [Fact]
    public async Task Pinned_a_drain_that_meets_a_slow_sender_in_every_round_holds_the_next_live_on_for_fifty_budgets_plus_one_interval()
    {
        var (exchange, client) = Live();
        var budget = exchange.Options.UnpackTimeout;
        var interval = exchange.Options.LivePollInterval;
        exchange.UseBudget(budget);
        exchange.Unpacker = StallsUntilCancelled;
        var round = 0;
        exchange.On(MessagePickup.DeliveryRequestType, async r =>
        {
            await Task.Yield();
            var n = Interlocked.Increment(ref round);
            return Delivery(r, ($"s{n}", $"stall:s{n}"));
        });
        using var cts = new CancellationTokenSource();
        var start = exchange.Clock.GetTimestamp();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var move = stream.MoveNextAsync().AsTask();
        for (var i = 1; i <= LiveDeliveryStream.MaxDrainRounds; i++)
        {
            await exchange.Clock.WaitForScheduledTimersAsync(2, PickupTest.Timeout); // this round's slow message is being opened
            exchange.Clock.Advance(budget);
            (await PickupTest.Within(move)).Should().BeTrue();
            stream.Current.Id.Should().Be($"s{i}");
            stream.Current.Error.Should().BeOfType<TimeoutException>();
            move = stream.MoveNextAsync().AsTask();
        }
        await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // the drain ended: waiting for the next cycle

        var drainTook = exchange.Clock.GetElapsedTime(start);
        drainTook.Should().Be(budget * LiveDeliveryStream.MaxDrainRounds);
        Types(exchange).Count(t => t == LiveOn).Should().Be(1, "no cycle while the drain runs, however long");
        Types(exchange).Count(t => t == Drain).Should().Be(LiveDeliveryStream.MaxDrainRounds, "the drain ends at its 50th round");
        var sent = exchange.Sent.Count;
        exchange.Clock.Advance(interval);
        await exchange.WaitForSentAsync(sent + 1);
        Types(exchange)[sent].Should().Be(LiveOn);
        exchange.Clock.GetElapsedTime(start).Should().Be(TimeSpan.FromSeconds(1060), "50 budgets of 20 s, then one interval of 1 minute");
        await StopQuietlyAsync(stream, move, cts);
    }

    // ---- helpers --------------------------------------------------------------------------------

    // A "stall:" message's sender DID never resolves: it waits until its token is cancelled.
    private static async Task<DeliveredOutcome> StallsUntilCancelled(string text, CancellationToken ct)
    {
        if (text.StartsWith("stall:", StringComparison.Ordinal))
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return DefaultOutcome(text);
    }

    // Move the clock one second at a time while the stream waits on a timer, until `move` finishes
    // (or `maxSeconds` have passed on the clock).
    private static async Task AdvanceUntilAsync(FakePickupExchange exchange, Task move, int maxSeconds)
    {
        for (var i = 0; i < maxSeconds && !move.IsCompleted; i++)
        {
            await Adv5Wait.MoveOrTimerAsync(exchange.Clock, move);
            if (move.IsCompleted)
                return;
            exchange.Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    // End a stream whatever state it is in: cancel, observe the pending move (any outcome), dispose.
    private static async Task StopQuietlyAsync(IAsyncEnumerator<PickupItem> stream, Task<bool>? pending, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        if (pending is not null)
        {
            try
            {
                await pending.WaitAsync(PickupTest.Timeout);
            }
            catch (Exception)
            {
                // The pending move's outcome is not what these tests look at.
            }
        }
        await PickupTest.Within(stream.DisposeAsync());
    }
}
