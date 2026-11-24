using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-3 red-team pass on <see cref="PickupClient.ReceiveLiveAsync"/> after fix round 2 (#96): the
/// eager per-delivery opening under one budget, push storms, and the stream's own clock. Every step
/// is driven by the fake exchange (synchronous answers) and the manual clock; nothing sleeps
/// (L-044). The stream, the delivery budget and the duplicate filter are the production ones.
/// </summary>
public sealed class PickupClientLiveAdv3Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;

    /// <summary>
    /// ADV3-1 (defect, Medium; fixed in fix round 3: once a cycle drains, its drain rounds and the pushes take turns, a drain round first). Property broken — <see cref="PickupClient.ReceiveLiveAsync"/>
    /// summary: "Every <see cref="MediatorClientOptions.LivePollInterval"/> it turns live delivery on
    /// again and takes what is queued: that picks up messages whose delivery was delayed and
    /// recovers from a dropped connection"; and its remarks: "The drain when the stream starts and
    /// the one every LivePollInterval are how such a message reaches you".
    /// <para>
    /// Attack: the stream's loop serves a buffered push before a drain round
    /// (<c>LiveDeliveryStream.NextAsync</c>: <c>_ready</c>, then <c>Pushes.TryRead</c>, then the drain),
    /// and since fix round 2 it opens each push whole before going on, under the full budget (a live
    /// push carries one message, so its share is the whole 20 s). A sender whose DID never resolves
    /// (or resolves just under the budget) — any outsider who can address the recipient through its
    /// mediator — sends one message every 20 s or faster. Each is pushed live, and while one is being
    /// opened the next is already buffered, so the loop never finds the push buffer empty and never
    /// sends a <c>delivery-request</c>. The cycle still re-arms live mode every interval, but its drain
    /// never runs: what only a drain reaches — messages queued before the stream started (here
    /// <c>queued-1</c>), messages whose sender delayed them (FR-PICK-08: never pushed when due), and
    /// pushes the full buffer dropped (eight attacker messages in a burst fill it) — is never
    /// delivered for as long as the attacker keeps sending, three messages a minute. With a 30-day
    /// <c>MessagePickupOptions.MaxMessageAge</c> at the mediator, an attack that outlasts it loses them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADV3_1_a_sender_whose_pushes_each_take_the_budget_to_open_keeps_the_live_stream_from_ever_draining()
    {
        var (exchange, client) = Live(o => o.UnpackTimeout = Budget);
        exchange.UseBudget(Budget);
        // Queued before the stream started (or delayed by its sender, or its push dropped): only a drain reaches it.
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("queued-1", "ok:queued-1")), r => Status(r));
        // Each slow push signals once its opening has started: its budget and its share exist then.
        // That is the rendezvous, not a count of scheduled timers: the next cycle's alarm and the
        // flush age are scheduled meanwhile (#111).
        var stalled = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        exchange.Unpacker = async (text, ct) =>
        {
            if (text.StartsWith("stall:", StringComparison.Ordinal))
            {
                Volatile.Read(ref stalled[0]).TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // the attacker's DID never resolves; the token is honoured
            }
            return DefaultOutcome(text);
        };
        var cycles = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            // The attacker's first message arrives just after live mode goes on, so it is pushed.
            if (r.Live && Interlocked.Increment(ref cycles) == 1)
                exchange.Registration!.TryPush(Push(("s0", "stall:s0"))).Should().BeTrue();
            return Status(r, live: r.Live);
        });

        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask();
        var interval = exchange.Options.LivePollInterval;
        var rounds = (int)(5 * interval.Ticks / Budget.Ticks); // five poll intervals of attack
        var items = new List<PickupItem>();
        try
        {
            // Fix round 3: the cycle's drain round runs before the stream takes the attacker's first
            // push (s0, pushed while live mode went on), so the queued message is the first item.
            (await PickupTest.Within(move)).Should().BeTrue();
            stream.Current.Id.Should().Be("queued-1");
            Volatile.Write(ref stalled[0], new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            move = stream.MoveNextAsync().AsTask();

            for (var i = 0; i < rounds; i++)
            {
                // The push being opened: its budget and its message's share exist.
                await Volatile.Read(ref stalled[0]).Task.WaitAsync(PickupTest.Timeout);
                // The attacker's next message arrives while this one is still being opened.
                exchange.Registration!.TryPush(Push(($"s{i + 1}", $"stall:s{i + 1}"))).Should().BeTrue();
                exchange.Clock.Advance(Budget);
                (await PickupTest.Within(move)).Should().BeTrue();
                items.Add(stream.Current);
                Volatile.Write(ref stalled[0], new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                move = stream.MoveNextAsync().AsTask();
            }

            items.Should().OnlyContain(i => i.Error is TimeoutException, "each attacker message fails at its budget");
            cycles.Should().BeGreaterThanOrEqualTo(5, "the cycle re-armed live mode every poll interval");
            exchange.SentOf(MessagePickup.DeliveryRequestType).Should().NotBeEmpty(
                "a live stream must drain at least once per poll interval ({0}); after {1} of attack it has never asked for what is queued, so queued-1 never arrives", interval, Budget * rounds);
        }
        finally
        {
            await StopAsync(stream, move, cts);
        }
    }

    /// <summary>
    /// ADV3-1 companion (passes): the same stream with no attacker drains at the start and every
    /// interval, so the property the attack breaks is the documented one, not an artefact of the
    /// fake. The queued message arrives by the first drain.
    /// </summary>
    [Fact]
    public async Task Without_a_steady_flow_of_pushes_the_same_stream_drains_and_the_queued_message_arrives()
    {
        var (exchange, client) = Live(o => o.UnpackTimeout = Budget);
        exchange.UseBudget(Budget);
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("queued-1", "ok:queued-1")), r => Status(r));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        (await Next(stream)).Id.Should().Be("queued-1");
        Types(exchange).Should().Equal(LiveOn, Drain);
    }

    /// <summary>
    /// ADV3-5 (defect, Low; fixed in fix round 3: a clock reading below a stored one counts as due). Property broken — <see cref="MediatorClientOptions.LivePollInterval"/>:
    /// "How often a live stream ... turns live delivery on again and collects what is queued"; and the
    /// project's own rule for clocks that step back (L-060; <c>NegotiatedVersionCache</c> treats "a
    /// clock that stepped back" as expired, at the cost of one more query). Attack (a host condition,
    /// not a remote one): the container's <see cref="TimeProvider"/> — taken from DI — derives its
    /// timestamps from a wall clock that is stepped back (an NTP correction, a VM restored from a
    /// snapshot). The stream keeps <c>_nextCycleAt</c> as an absolute timestamp, so after a step back
    /// of one hour its wait for the next cycle is the interval plus that hour: no live-mode re-arm, no
    /// drain, for an hour. Delayed messages and messages pushed while the connection was down wait
    /// that much longer, and a back-off (<c>_notBefore</c>) is stretched the same way.
    /// </summary>
    [Fact]
    public async Task ADV3_5_a_timestamp_that_steps_back_an_hour_postpones_the_next_poll_by_that_hour()
    {
        var clock = new SteppingTimeProvider();
        var exchange = new FakePickupExchange(time: null) { Route = WebSocketRoute };
        var stepped = new SteppedExchange(exchange, clock);
        var client = new PickupClient(stepped, logger: null);
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask();
        try
        {
            await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout); // waiting for a push or the next cycle
            Types(exchange).Should().Equal(LiveOn, Drain);

            clock.StepBack(TimeSpan.FromHours(1));
            clock.Inner.Advance(exchange.Options.LivePollInterval); // the wait's timer fires: the cycle is due by the interval
            await clock.Inner.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

            Types(exchange).Should().Equal(new[] { LiveOn, Drain, LiveOn, Drain },
                "one poll interval ({0}) has passed in real time; a stream whose clock stepped back must still poll, not wait the step back out", exchange.Options.LivePollInterval);
        }
        finally
        {
            await StopAsync(stream, move, cts);
        }
    }

    /// <summary>
    /// Held (passes): a push storm against a stream that is opening a delivery is bounded — the
    /// mailbox keeps 8 pushes, a ninth is refused (dropped, never acknowledged), and nothing the
    /// stream did not yield is acknowledged; once the slow delivery ends the buffered pushes are
    /// opened and yielded in order. Memory per stream: one delivery being opened, 8 buffered pushes.
    /// </summary>
    [Fact]
    public async Task A_push_storm_while_a_delivery_is_being_opened_is_bounded_and_nothing_dropped_is_acknowledged()
    {
        var (exchange, client) = Live(o => o.UnpackTimeout = Budget);
        exchange.UseBudget(Budget);
        // "slow" signals once its opening has started (its budget and its share exist then); the next
        // cycle's alarm is scheduled meanwhile, so a count of scheduled timers is no rendezvous (#111).
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.Unpacker = async (text, ct) =>
        {
            if (text.StartsWith("stall:", StringComparison.Ordinal))
            {
                stalled.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return DefaultOutcome(text);
        };
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (r.Live && exchange.SentOf(MessagePickup.LiveDeliveryChangeType).Count == 1)
                exchange.Registration!.TryPush(Push(("slow", "stall:slow"))).Should().BeTrue();
            return Status(r, live: r.Live);
        });
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Status(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask();
        try
        {
            await stalled.Task.WaitAsync(PickupTest.Timeout); // "slow" is being opened
            var accepted = Enumerable.Range(0, 1000).Count(i => exchange.Registration!.TryPush(Push(($"p{i}", $"ok:p{i}"))));
            accepted.Should().Be(LiveRegistration.PushCapacity);

            exchange.Clock.Advance(Budget);
            (await PickupTest.Within(move)).Should().BeTrue();
            stream.Current.Id.Should().Be("slow");
            for (var i = 0; i < LiveRegistration.PushCapacity; i++)
                (await Next(stream)).Id.Should().Be($"p{i}");
            move = stream.MoveNextAsync().AsTask();
            await exchange.Clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);

            exchange.AcknowledgedIds.Should().BeEquivalentTo(Enumerable.Range(0, LiveRegistration.PushCapacity).Select(i => $"p{i}"),
                "only what was yielded and taken is acknowledged; the dropped pushes and the failed one are not");
        }
        finally
        {
            await StopAsync(stream, move, cts);
        }
    }

    /// <summary>
    /// Held (passes): the consumer's cancellation while a delivery is being opened ends the stream
    /// with nothing of that delivery acknowledged or remembered, the budget's and the shares' timers
    /// gone, and the mailbox closed.
    /// </summary>
    [Fact]
    public async Task Cancelled_while_a_push_is_opened_leaves_no_timer_no_registration_and_nothing_remembered()
    {
        var (exchange, client) = Live(o => o.UnpackTimeout = Budget);
        exchange.UseBudget(Budget);
        // Every worker signals once its message's opening has started (the budget and that share exist
        // then); the next cycle's alarm is scheduled meanwhile, so a count of scheduled timers is no
        // rendezvous (#111).
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.Unpacker = async (text, ct) =>
        {
            if (Interlocked.Increment(ref entered) == DeliveredMessageUnpacker.FetchConcurrency)
                allEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return DefaultOutcome(text);
        };
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (r.Live)
                exchange.Registration!.TryPush(Push(("p1", "ok:p1"), ("p2", "ok:p2"), ("p3", "ok:p3"), ("p4", "ok:p4"), ("p5", "ok:p5")));
            return Status(r, live: r.Live);
        });
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var move = stream.MoveNextAsync().AsTask();
        await allEntered.Task.WaitAsync(PickupTest.Timeout);

        await StopAsync(stream, move, cts);

        exchange.AcknowledgedIds.Should().BeEmpty();
        foreach (var id in new[] { "p1", "p2", "p3", "p4", "p5" })
            exchange.Duplicates.Contains(L, DefaultOutcome($"ok:{id}").DedupeKey).Should().BeFalse();
        exchange.Registrations.Should().BeEmpty();
        SpinWait.SpinUntil(() => exchange.Clock.ScheduledTimers == 0 && exchange.Budgeted!.Abandoned == 0, PickupTest.Timeout)
            .Should().BeTrue("the budget's and the shares' timers are disposed and every unpack let its slot go");
    }

    /// <summary>
    /// Held, pinned (passes): with <see cref="MediatorClientOptions.DedupeCapacity"/> 0 a live stream
    /// yields a message that comes twice in one delivery twice. The option's text promises the
    /// in-delivery rule for "a fetch" only, so this is documented behaviour; recorded here because
    /// <see cref="PickupClient.FetchAsync"/> keeps a per-delivery set and the stream does not.
    /// </summary>
    [Fact]
    public async Task Pinned_with_dedupe_off_a_live_stream_yields_a_message_twice_from_one_delivery()
    {
        var (exchange, client) = Live(o => o.DedupeCapacity = 0);
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m1")), r => Status(r));
        await using var stream = client.ReceiveLiveAsync(M, L).GetAsyncEnumerator();

        (await Next(stream)).Unpacked!.Message.Id.Should().Be("m1");
        (await Next(stream)).Unpacked!.Message.Id.Should().Be("m1", "nothing remembers the first copy when the memory is off");
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> over a <see cref="ManualTimeProvider"/> whose timestamps (and wall
/// clock) can be stepped back, as a host clock derived from wall time is by an NTP correction or a
/// VM restore. Timers are the inner clock's: they fire when the inner clock is advanced.
/// </summary>
internal sealed class SteppingTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public ManualTimeProvider Inner { get; } = new();

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override long GetTimestamp() => Inner.GetTimestamp() - Interlocked.Read(ref _offsetTicks);

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow() - TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => Inner.CreateTimer(callback, state, dueTime, period);

    /// <summary>Step the clock back by <paramref name="by"/>.</summary>
    public void StepBack(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}

/// <summary>A <see cref="FakePickupExchange"/> seen through another clock (the stream reads <see cref="IMediatorExchange.Time"/>).</summary>
internal sealed class SteppedExchange(FakePickupExchange inner, TimeProvider time) : IMediatorExchange
{
    public MediatorClientOptions Options => inner.Options;

    public TimeProvider Time => time;

    public DuplicateFilter Duplicates => inner.Duplicates;

    public NegotiatedVersionCache<CoordinateMediationVersion> Versions => inner.Versions;
    public NegotiatedVersionCache<MessagePickupVersion> PickupVersions => inner.PickupVersions;

    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
        => inner.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);

    public Task<DidComm.Protocols.InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, DidComm.Messages.Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
        => inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);

    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => inner.RegisterLiveStream(mediatorDid, localDid);


    public Task SendOneWayAsync(MediatorRoute route, DidComm.Messages.Message request, string name, string mediatorDid, string localDid, CancellationToken ct)

        => inner.SendOneWayAsync(route, request, name, mediatorDid, localDid, ct);

    public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, string? requestId, CancellationToken ct)
        => inner.UnpackDeliveredBatchAsync(scope, messages, requestId, ct);
}
