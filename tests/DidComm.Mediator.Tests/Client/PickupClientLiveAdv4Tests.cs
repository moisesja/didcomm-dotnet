using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-4, the last red-team pass of #96, scoped to the live-stream fixes of fix round 3
/// (<c>b87c85e</c>): drain rounds and pushes take turns (ADV3-1), and how the stream's clock decides
/// what is due (ADV3-5; since #111 every "due" is an alarm the stream arms, and no clock reading is
/// compared). Driven by the fake exchange and manual clocks; nothing sleeps (L-044).
/// </summary>
public sealed class PickupClientLiveAdv4Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;

    // ---- ADV3-1: the turns ---------------------------------------------------------------------

    /// <summary>
    /// ADV4-1 (defect, Low; resolved in #96 by the fix for PR #112 review R1-1 — no cycle can come
    /// due while a drain is in progress — so this reproduction runs again and passes). Property broken — <see cref="LiveDeliveryStream"/> remarks and
    /// <see cref="PickupClient.ReceiveLiveAsync"/>: "a drain that keeps meeting slow messages cannot
    /// keep the pushes from theirs"; "a waiting push gets its turn after that round".
    /// <para>
    /// Attack: the push's turn is a flag (<c>_pushTurn</c>) that the drain round sets and that
    /// <c>CycleAsync</c> clears ("the cycle's first drain round goes before any waiting push"). The
    /// loop looks for a due cycle before it looks at the mailbox, so a cycle that comes due while a
    /// drain round is being opened (or while its items are with the consumer) takes the push's turn
    /// back. A host that polls more often than a drain round takes — here
    /// <see cref="MediatorClientOptions.LivePollInterval"/> 10 s, inside its range [1 s, 24 h], with
    /// the default 20 s <see cref="MediatorClientOptions.UnpackTimeout"/> — and a sender whose DID
    /// never resolves, with one slow message in every drain round (three or four of them cycle
    /// through this library's 60 s lease), keep an honest push waiting for ever: every round is
    /// followed by a cycle, every cycle by a round. The push stays leased at the mediator; at this
    /// library's mediator it comes back through the drain when its lease ends, at a mediator that
    /// keeps pushed messages for the connection it never arrives while the attack lasts.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADV4_1_a_cycle_that_comes_due_during_every_drain_round_takes_the_waiting_pushs_turn_back_for_ever()
    {
        var (exchange, client) = Live(o =>
        {
            o.UnpackTimeout = Budget;
            o.LivePollInterval = TimeSpan.FromSeconds(10);
        });
        exchange.UseBudget(Budget);
        var round = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
        {
            var n = Interlocked.Increment(ref round);
            return Delivery(r, ($"d{n}", $"stall:d{n}"));
        });
        // Each stall is signalled, so the test tells a move that needs the clock from one that does
        // not: once a round's stall has started, its delivery's budget timer exists and the clock
        // passes it (the previous round's budget fired, and a fired timer is not scheduled, whenever
        // the pool disposes it); a move that yields without a stall (the push) is taken as it comes.
        var stalled = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        exchange.Unpacker = (text, ct) =>
        {
            if (text.StartsWith("stall:", StringComparison.Ordinal))
                Volatile.Read(ref stalled[0]).TrySetResult();
            return StallsUntilCancelled(text, ct);
        };
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            // An honest message is pushed as live mode goes on.
            if (r.Live && exchange.SentOf(MessagePickup.LiveDeliveryChangeType).Count == 1)
                exchange.Registration!.TryPush(Push(("p1", "ok:p1"))).Should().BeTrue();
            return Status(r, live: r.Live);
        });

        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        Task<bool> NextMove()
        {
            Volatile.Write(ref stalled[0], new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return stream.MoveNextAsync().AsTask();
        }
        var move = NextMove();
        var items = new List<PickupItem>();
        try
        {
            // Six drain rounds: two minutes, twice this library's lease.
            for (var i = 0; i < 6 && !items.Any(x => x.Id == "p1"); i++)
            {
                if (await Task.WhenAny(move, Volatile.Read(ref stalled[0]).Task).WaitAsync(PickupTest.Timeout) != move)
                {
                    await exchange.Clock.WaitForTimerAsync(Budget, 0, PickupTest.Timeout); // the round's budget
                    exchange.Clock.Advance(Budget);
                }
                (await PickupTest.Within(move)).Should().BeTrue();
                items.Add(stream.Current);
                move = NextMove();
            }

            items.Select(x => x.Id).Should().Contain("p1",
                "a waiting push gets its turn after a drain round; here every round ({0}) was followed by a cycle that took the turn back",
                string.Join(", ", Types(exchange)));
        }
        finally
        {
            await StopAsync(stream, move, cts);
        }
    }

    /// <summary>
    /// ADV3-1 held (regression): with the default interval, eight honest pushes waiting behind a
    /// drain that keeps meeting slow messages (a cycle coming due every third round among them) each
    /// arrive once, in order, and each is acknowledged once; every slow message is a failure item.
    /// </summary>
    [Fact]
    public async Task With_the_default_interval_pushes_waiting_behind_a_slow_drain_each_arrive_once_and_are_acknowledged_once()
    {
        var (exchange, client) = Live(o => o.UnpackTimeout = Budget);
        exchange.UseBudget(Budget);
        var round = 0;
        exchange.On(MessagePickup.DeliveryRequestType, r =>
        {
            var n = Interlocked.Increment(ref round);
            return Delivery(r, ($"d{n}", $"stall:d{n}"));
        });
        var stalled = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        exchange.Unpacker = async (text, ct) =>
        {
            if (text.StartsWith("stall:", StringComparison.Ordinal))
            {
                Volatile.Read(ref stalled[0]).TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return DefaultOutcome(text);
        };
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (r.Live && exchange.SentOf(MessagePickup.LiveDeliveryChangeType).Count == 1)
            {
                for (var p = 1; p <= LiveRegistration.PushCapacity; p++)
                    exchange.Registration!.TryPush(Push(($"p{p}", $"ok:p{p}"))).Should().BeTrue();
            }
            return Status(r, live: r.Live);
        });

        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var items = new List<PickupItem>();
        var expected = Enumerable.Range(1, LiveRegistration.PushCapacity).Select(p => $"p{p}").ToArray();
        Task<bool> move;
        for (var step = 0; step < 40 && items.Count(x => x.Id.StartsWith('p')) < expected.Length; step++)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref stalled[0], signal);
            move = stream.MoveNextAsync().AsTask();
            if (await Task.WhenAny(move, signal.Task).WaitAsync(PickupTest.Timeout) != move)
            {
                await exchange.Clock.WaitForTimerAsync(Budget, 0, PickupTest.Timeout); // the round's budget
                exchange.Clock.Advance(Budget);
            }
            (await PickupTest.Within(move)).Should().BeTrue();
            items.Add(stream.Current);
        }
        move = stream.MoveNextAsync().AsTask(); // the last push is taken; the next round stalls
        await StopAsync(stream, move, cts);

        items.Where(x => x.Id.StartsWith('p')).Select(x => x.Id).Should().Equal(expected);
        items.Where(x => x.Id.StartsWith('p')).Should().OnlyContain(x => x.Error == null);
        items.Where(x => x.Id.StartsWith('d')).Should().OnlyContain(x => x.Error is TimeoutException);
        // Updated with R1-1's fix (PR #112 review): a cycle that has started finishes its drain before
        // the next one is armed, so no cycle comes due while this drain keeps meeting slow messages.
        Types(exchange).Count(t => t == LiveOn).Should().Be(1, "no cycle can come due while a drain is in progress (R1-1)");
        exchange.AcknowledgedIds.Should().BeEquivalentTo(expected).And.OnlyHaveUniqueItems();
    }

    // ---- ADV3-5: the clock -----------------------------------------------------------------------

    /// <summary>
    /// ADV4-4 (was a defect, Low; fixed in #111: every "due" is the firing of an alarm the stream
    /// armed, and no clock reading is compared, so a host <see cref="TimeProvider"/> whose readings go
    /// lower on consecutive reads changes no decision). Property checked — <see cref="LiveDeliveryStream"/>
    /// remarks: a host clock "whose readings keep going lower, costs nothing: no early cycle, retry or
    /// flush, no storm".
    /// <para>
    /// The host clock reads one tick lower each time it is read (a wall-clock based provider being
    /// slewed or corrected backwards, a buggy virtualised clock), and no timer moves. Checked: the
    /// stream sends one <c>live-delivery-change</c> at the start, drains, and yields the message
    /// pushed meanwhile.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADV4_4_a_clock_whose_readings_keep_going_lower_makes_the_stream_send_live_delivery_change_in_a_tight_loop()
    {
        var clock = new BackwardTimeProvider();
        var exchange = new FakePickupExchange(time: null) { Route = WebSocketRoute };
        using var cts = new CancellationTokenSource();
        var liveOn = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (r.Live)
            {
                var n = Interlocked.Increment(ref liveOn);
                if (n == 1)
                    exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
                if (n == 50)
                    cts.Cancel(); // the test's brake: the stream would go on for ever
            }
            return Status(r, live: r.Live);
        });
        var client = new PickupClient(new SteppedExchange(exchange, clock), logger: null);
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        Exception? error = null;
        var moved = false;
        try
        {
            moved = await PickupTest.Within(stream.MoveNextAsync());
        }
        catch (Exception ex)
        {
            error = ex;
        }
        var current = moved ? stream.Current : null;
        await stream.DisposeAsync();

        var sent = Volatile.Read(ref liveOn);
        sent.Should().Be(1, "no time passed on the timers: one cycle at the start, then the pushed message ({0} live-delivery-change were sent; the stream ended with {1})",
            sent, error?.GetType().Name ?? "nothing");
        current?.Id.Should().Be("q1");
    }

    /// <summary>
    /// ADV4-4, the back-off side of the same rule (fixed in #111): with a clock that reads lower
    /// right after the back-off was set, the back-off (1 s, doubling) still lasts until its alarm
    /// fires, so a mediator that is unreachable for a moment gets one attempt and a pause, not
    /// attempts back to back that end the stream.
    /// </summary>
    [Fact]
    public async Task ADV4_4_a_clock_whose_readings_keep_going_lower_skips_every_back_off_and_ends_the_stream_at_once()
    {
        var clock = new BackwardTimeProvider();
        var exchange = new FakePickupExchange(time: null) { Route = WebSocketRoute };
        var attempts = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Task.FromResult(Status(r, live: false));
            // The brake: a stream that keeps trying with no pause would never return to the test
            // (its attempts are synchronous here), so the 50th attempt ends it at once.
            if (Interlocked.Increment(ref attempts) >= 50)
                return Task.FromException<InboundMessageSnapshot>(new InvalidDataException("brake: the stream kept trying"));
            return Task.FromException<InboundMessageSnapshot>(new TimeoutException("the mediator does not answer"));
        });
        var client = new PickupClient(new SteppedExchange(exchange, clock), logger: null);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        // Everything up to the back-off's wait is synchronous in the fake: when the call returns, the
        // stream is either waiting on the back-off's timer or already over.
        var move = stream.MoveNextAsync().AsTask();
        var endedAtOnce = move.IsCompleted;
        if (!endedAtOnce)
            await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        var sent = Volatile.Read(ref attempts);
        if (endedAtOnce)
        {
            await FluentActions.Awaiting(() => move).Should().ThrowAsync<Exception>();
            await stream.DisposeAsync();
        }
        else
        {
            await StopAsync(stream, move, cts);
        }

        sent.Should().Be(1, "after a failed attempt the stream backs off for a second on its timers, which did not move");
        endedAtOnce.Should().BeFalse("a mediator unreachable for a moment does not end the stream without a single back-off");
    }

    /// <summary>
    /// ADV3-5 held (regression): a clock that steps back an hour, forward two, and back one again
    /// costs nothing — no extra cycle, no storm, every pushed message arrives, and a stream left
    /// alone afterwards sends nothing until its interval passes. Updated in #111: the stream decides
    /// "due" by alarms, so a step no longer costs one early cycle (it did when "due" was a clock
    /// reading below the one a point was set at).
    /// </summary>
    [Fact]
    public async Task A_clock_that_steps_back_forward_and_back_again_costs_no_extra_cycle()
    {
        var clock = new SteppingTimeProvider();
        var exchange = new FakePickupExchange(time: null) { Route = WebSocketRoute };
        var client = new PickupClient(new SteppedExchange(exchange, clock), logger: null);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // live-on, a drain that found nothing, the wait

        clock.StepBack(TimeSpan.FromHours(1));
        exchange.Registration!.TryPush(Push(("q1", "ok:m1"))).Should().BeTrue();
        (await PickupTest.Within(first)).Should().BeTrue();
        stream.Current.Id.Should().Be("q1");

        clock.StepBack(TimeSpan.FromHours(-2)); // forward two hours
        exchange.Registration!.TryPush(Push(("q2", "ok:m2"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("q2");

        clock.StepBack(TimeSpan.FromHours(1));
        exchange.Registration!.TryPush(Push(("q3", "ok:m3"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("q3");

        exchange.Registration!.TryPush(Push(("q4", "ok:m4"))).Should().BeTrue();
        (await Next(stream)).Id.Should().Be("q4");

        var waiting = stream.MoveNextAsync().AsTask();
        await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        var types = Types(exchange);
        types.Count(t => t == LiveOn).Should().Be(1, "one cycle at the start, and a step of the clock costs no cycle: {0}", string.Join(", ", types));
        types.Count(t => t == Drain).Should().Be(1);
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q2", "q3", "q4" }, "a step makes no flush due early or late: the four are acknowledged once nothing is ready");
        await StopAsync(stream, waiting, cts);
    }

    // A "stall:" message's sender DID never resolves: it waits until its token is cancelled.
    private static async Task<DeliveredOutcome> StallsUntilCancelled(string text, CancellationToken ct)
    {
        if (text.StartsWith("stall:", StringComparison.Ordinal))
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return DefaultOutcome(text);
    }
}
