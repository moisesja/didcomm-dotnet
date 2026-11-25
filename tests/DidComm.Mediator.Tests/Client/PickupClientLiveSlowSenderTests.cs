using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Protocols;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// S7 (#111 acceptance): the live stream's version of the fetch loop's acceptance matrix. A mediator
/// with leases (<see cref="FakeMailbox"/>) queues messages whose senders never resolve, then an
/// honest message; the stream turns live mode on and drains, one <c>delivery-request{10}</c> round
/// at a time, under the real per-delivery budget, at the default
/// <see cref="MediatorClientOptions.LivePollInterval"/>. The clock is moved one timer at a time, and
/// only when the stream waits on one: a delivery whose every message has an outcome or waits on its
/// timer (or sixteen wait), or the stream waiting for its next cycle. So the time to the honest
/// message is the time deliveries were being opened plus the time the stream waited between cycles,
/// and nothing else; both are measured, printed and pinned per cell.
/// </summary>
/// <remarks>
/// <strong>The host's lookups, as modelled:</strong> a lookup that honours its token has ended
/// before the mediator answers the stream's next delivery request. A host call keeps its
/// reservation until it ends, and one whose token was cancelled ends a moment after its delivery's
/// result is set (where its continuation runs is the scheduler's choice). So the mailbox's hook
/// holds each answer until no reservation is held: the event "every host call of the delivery
/// before has ended", not a neighbour of it. Without that, a round of ten could start while the ten
/// calls of the round before still held their reservations, and with sixteen opened at once against
/// an anonymous bound of sixteen, four of its messages would be refused for that bound: the figures
/// would depend on the thread pool (the cause of this matrix's rows failing in some full runs). What
/// a delivery does when it does arrive before such lookups have ended is measured on its own
/// (<see cref="DeliveredUnpackNextDeliveryRt3Tests"/>).
/// </remarks>
public sealed class PickupClientLiveSlowSenderTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;
    private static readonly TimeSpan Interval = new MediatorClientOptions().LivePollInterval;

    private const int None = -1;

    // A horizon, not a bound: a run ends when the honest message is yielded or this much clock time
    // has passed, so "not reached" would mean "not reached within six hours" (more than four times
    // the longest pause, 256 budgets).
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(6);

    /// <summary>
    /// Messages that never open in front × the mediator's lease (60 s, the default, and 10 s), each
    /// cell with its measured figures: the seconds until the honest message is yielded, how many of
    /// them the stream waited between cycles, the cycle it is yielded in, the deliveries that lasted
    /// a budget, the delivery requests sent, the host calls made for the messages that never open,
    /// the most times one of them was opened, and the second at which one was first opened again
    /// (<see cref="None"/>: never).
    /// </summary>
    public static TheoryData<int, int, int, int, int, int, int, int, int, int> Matrix()
        => new()
        {
            // N, lease | seconds, between cycles | cycle | budget deliveries, requests | host calls | most openings, first re-opening
            { 1, 60, 20, 0, 1, 1, 1, 1, 1, None },
            { 1, 10, 20, 0, 1, 1, 1, 1, 1, None },
            { 16, 60, 40, 0, 1, 2, 2, 16, 1, None },
            { 16, 10, 100, 60, 2, 2, 4, 16, 1, None },
            { 31, 60, 140, 60, 2, 4, 8, 31, 1, None },
            { 31, 10, 260, 180, 4, 4, 13, 31, 1, None },
            { 99, 60, 380, 180, 4, 10, 31, 99, 1, None },
            // The one cell that needs a second learning pass, as it did before the client opened
            // sixteen at once: learning 99 at one round per cycle outlasts the first pause, so from
            // 640 s on the messages learned first are opened again, each a second time.
            { 99, 10, 1380, 1020, 18, 18, 108, 179, 2, 640 },
        };

    /// <summary>
    /// <paramref name="slow"/> messages whose senders never resolve, then an honest one, at a mediator
    /// whose lease is <paramref name="leaseSeconds"/>. A cycle learns 10 messages per drain round, and
    /// as many rounds as fit in one lease before the first round's messages come back (at least one:
    /// then a round of messages already reported in this drain ends it). So the honest message is
    /// reached in cycle <c>C = ceil((slow + 1) / (10 · max(1, floor(lease / UnpackTimeout))))</c>, each
    /// message that never opens being opened once, when the learning fits in their first pause (ten
    /// minutes). When it does not (99 at a 10 s lease), the messages learned first are opened once
    /// more as their pauses end, and it is reached within <c>2C − 1</c> cycles. That is what this
    /// test asserted before the client opened sixteen at once, and it asserts it unchanged. It also
    /// pins what is measured in each cell: the time to the honest message, split into opening (whole
    /// budgets) and waiting between cycles; the cycle; the delivery requests; the host calls; and
    /// how often and from when a message that never opens is opened again.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_live_stream_reaches_the_message_behind_ones_that_never_open_within_the_live_bound(
        int slow, int leaseSeconds, int seconds, int betweenCyclesSeconds, int cycle, int budgetDeliveries, int requests, int hostCalls, int mostOpenings, int firstReopeningSeconds)
    {
        var cell = await MeasureAsync(slow, leaseSeconds, holdTheFirstRoundsEnds: false);

        // What this test asserted at 22973ae, unchanged: the bound of that head.
        cell.Reached.Should().BeTrue("the stream reaches the honest message within {0} cycles; after {1} it had not", cell.Bound, cell.Cycles);
        cell.Cycles.Should().BeLessThanOrEqualTo(cell.Bound);
        cell.MostOpenings.Should().Be(cell.FitsInFirstPause || slow < PerCycle(TimeSpan.FromSeconds(leaseSeconds)) ? 1 : 2, "a message that never opens is opened again only once its pause (ten minutes) has ended");

        // What is measured in this cell, pinned.
        cell.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds), "the measured time to the honest message");
        cell.BetweenCycles.Should().Be(TimeSpan.FromSeconds(betweenCyclesSeconds), "the part of it the stream waited for its next cycle");
        cell.Opening.Should().Be(cell.Elapsed - cell.BetweenCycles, "nothing else moves the clock");
        cell.Opening.Should().Be(Budget * budgetDeliveries, "a delivery takes a whole budget or no time");
        cell.BudgetDeliveries.Should().Be(budgetDeliveries);
        cell.Cycles.Should().Be(cycle, "the cycle in which the honest message is yielded");
        cell.Requests.Should().Be(requests);
        cell.HostCalls.Should().Be(hostCalls);
        cell.MostOpenings.Should().Be(mostOpenings, "the most times one message that never opens was opened");
        cell.FirstReopeningAt.Should().Be(firstReopeningSeconds == None ? null : TimeSpan.FromSeconds(firstReopeningSeconds), "when a message that never opens was first opened again");
        cell.Refused.Should().Be(0, "no message is refused: a round has at most ten messages, the anonymous partition holds sixteen, and the calls of the round before have ended");
        cell.BeforeItsTurn.Should().Be(0, "every message of a round of at most ten starts when the round does");
    }

    /// <summary>
    /// The model's lookups, forced (why this matrix's rows failed in some full runs): the ten host
    /// calls of the first round see their token cancelled when the budget ends and then stay held
    /// at a gate, each still holding its reservation, until the mailbox's hook has been seen waiting
    /// for them. The second round's answer is held meanwhile, so the cell (16 in front, a 60 s
    /// lease) is the pinned one: nothing is refused and the honest message is yielded at 40 s. With
    /// the hook's wait taken out, the second round starts with ten of the anonymous partition's
    /// sixteen reservations held: six of its seven messages start, and the seventh, the honest one,
    /// is refused for that bound.
    /// </summary>
    [Fact]
    public async Task A_round_is_answered_only_once_the_host_calls_of_the_round_before_have_ended()
    {
        var cell = await MeasureAsync(slow: 16, leaseSeconds: 60, holdTheFirstRoundsEnds: true);

        cell.MostReservedBeforeAnAnswer.Should().Be(10, "the ten host calls of the first round still held their reservations when the second round's answer was ready");
        cell.Refused.Should().Be(0, "the answer was held until they had ended");
        cell.Reached.Should().BeTrue();
        cell.Elapsed.Should().Be(TimeSpan.FromSeconds(40), "the pinned figure of the cell");
        cell.HostCalls.Should().Be(16);
    }

    private async Task<Cell> MeasureAsync(int slow, int leaseSeconds, bool holdTheFirstRoundsEnds)
    {
        var time = new ManualTimeProvider();
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        var mailbox = new FakeMailbox(time, lease, Enumerable.Range(0, slow).Select(i => ($"s{i}", $"stall:{i}")).Append(("h", "ok:honest")).ToArray());
        var exchange = mailbox.Attach(new FakePickupExchange(time: time) { Route = WebSocketRoute }.UseBudget(Budget));
        var stalls = new StallLog(time);
        exchange.Unpacker = (text, ct) => text.StartsWith("stall:", StringComparison.Ordinal) ? stalls.StallAsync(text, ct) : Task.FromResult(DefaultOutcome(text));
        var driver = new LiveDriver(time, exchange.Budgeted!, stalls, new DeliveryScope(M, L));
        mailbox.OnDeliver = driver.Delivered;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? opener = null;
        if (holdTheFirstRoundsEnds)
        {
            stalls.EndGate = gate.Task;
            // Opened only once the hook has been seen waiting with reservations still held (and, as
            // a hang guard only, after the timeout).
            opener = Task.Run(() =>
            {
                SpinWait.SpinUntil(() => driver.SawReservationsHeld, PickupTest.Timeout);
                gate.TrySetResult();
            });
        }
        var cyclesNeeded = (slow + PerCycle(lease)) / PerCycle(lease);
        var fitsInFirstPause = LearnsWithinFirstPause(cyclesNeeded, lease, exchange.Budgeted!.Memory.FirstPause);
        var bound = fitsInFirstPause ? cyclesNeeded : 2 * cyclesNeeded - 1;
        using var cts = new CancellationTokenSource();
        var stream = PickupTest.Client(exchange).ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var started = time.GetUtcNow();
        stalls.Started = started;

        var reached = false;
        var refused = 0;
        var beforeItsTurn = 0;
        Task<bool> move;
        try
        {
            while (time.GetUtcNow() - started < Horizon)
            {
                move = stream.MoveNextAsync().AsTask();
                await driver.DriveAsync(move);
                (await move).Should().BeTrue();
                if (stream.Current.Unpacked?.Message.Id == "honest")
                {
                    reached = true;
                    break;
                }
                stream.Current.Error.Should().BeOfType<TimeoutException>("a message that never opens fails when its delivery's budget ends, or at once while it is held back");
                if (stream.Current.Error!.Message.Contains("unpack capacity exhausted", StringComparison.Ordinal))
                    refused++;
                if (stream.Current.Error.Message.Contains("before its turn came", StringComparison.Ordinal))
                    beforeItsTurn++;
                if (holdTheFirstRoundsEnds && refused > 0)
                    break; // the property under test is already decided
            }
        }
        finally
        {
            gate.TrySetResult();
        }
        var cycles = LiveOns(exchange);
        var rounds = exchange.SentOf(MessagePickup.DeliveryRequestType).Count;
        var elapsed = time.GetUtcNow() - started;
        move = stream.MoveNextAsync().AsTask();
        await PickupClientLiveStreamTests.StopAsync(stream, move, cts);
        if (opener is not null)
            await opener.WaitAsync(PickupTest.Timeout);

        output.WriteLine(FormattableString.Invariant($"S7 | N {slow} | lease {leaseSeconds} s | reached {(reached ? "yes" : "no")}, {elapsed.TotalSeconds} s = opening {driver.Opening.TotalSeconds} s + waiting between cycles {driver.BetweenCycles.TotalSeconds} s + other {(elapsed - driver.Opening - driver.BetweenCycles).TotalSeconds} s; cycle {cycles}; {driver.BudgetDeliveries} deliveries took a budget, of {rounds} delivery requests; {stalls.HostCalls} host calls for the {slow}; most openings of one message {stalls.MostOpenings}; first re-opening {(stalls.FirstReopeningAt is { } t ? t.TotalSeconds + " s" : "none")}; second learning pass {(stalls.MostOpenings > 1 ? "yes" : "no")}; refused {refused}; bound {bound} cycles (C = {cyclesNeeded}){(holdTheFirstRoundsEnds ? $"; ends held, most reserved before an answer {driver.MostReservedBeforeAnAnswer}" : "")}"));
        return new Cell(reached, elapsed, driver.Opening, driver.BetweenCycles, cycles, driver.BudgetDeliveries, rounds, stalls.HostCalls, stalls.MostOpenings, stalls.FirstReopeningAt, refused, beforeItsTurn, bound, fitsInFirstPause, driver.MostReservedBeforeAnAnswer);
    }

    private sealed record Cell(
        bool Reached, TimeSpan Elapsed, TimeSpan Opening, TimeSpan BetweenCycles, int Cycles, int BudgetDeliveries, int Requests, int HostCalls, int MostOpenings, TimeSpan? FirstReopeningAt,
        int Refused, int BeforeItsTurn, int Bound, bool FitsInFirstPause, int MostReservedBeforeAnAnswer);

    // The messages a cycle learns: 10 per drain round, as many rounds as fit in one lease (at least one).
    private static int PerCycle(TimeSpan lease) => LiveDeliveryStream.DrainLimit * Math.Max(1, (int)(lease / Budget));

    // Whether the last cycle the learning needs starts before the pause of the first messages learned
    // (struck one budget into the first cycle) ends. A cycle lasts its learning rounds, one budget
    // each, and then one LivePollInterval.
    private static bool LearnsWithinFirstPause(int cycles, TimeSpan lease, TimeSpan firstPause)
    {
        var period = Budget * (PerCycle(lease) / LiveDeliveryStream.DrainLimit) + Interval;
        return period * (cycles - 1) < Budget + firstPause;
    }

    private static int LiveOns(FakePickupExchange exchange)
        => exchange.SentOf(MessagePickup.LiveDeliveryChangeType).Count(r => r.Live);

    /// <summary>Host unpack calls of messages that never open: each waits until its token is cancelled.</summary>
    private sealed class StallLog(ManualTimeProvider time)
    {
        private readonly ConcurrentDictionary<long, CancellationToken> _waiting = new();
        private readonly ConcurrentDictionary<string, int> _openings = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private long _next;
        private int _hostCalls;
        private TimeSpan? _firstReopeningAt;

        /// <summary>The instant the times here are counted from.</summary>
        public DateTimeOffset Started { get; set; }

        /// <summary>How many host calls were made for messages that never open.</summary>
        public int HostCalls => Volatile.Read(ref _hostCalls);

        /// <summary>When, from <see cref="Started"/>, a host call first ran for a message that had already had one; <c>null</c>: never.</summary>
        public TimeSpan? FirstReopeningAt
        {
            get
            {
                lock (_gate)
                    return _firstReopeningAt;
            }
        }

        /// <summary>Calls waiting with their token not cancelled.</summary>
        public int Live => _waiting.Values.Count(t => !t.IsCancellationRequested);

        /// <summary>The most times one message was opened.</summary>
        public int MostOpenings => _openings.IsEmpty ? 0 : _openings.Values.Max();

        /// <summary>
        /// When set, a call whose token was cancelled ends only once this has completed: a lookup
        /// that honours its token and takes a moment more to end. Until then it holds its reservation.
        /// </summary>
        public Task? EndGate { get; set; }

        public async Task<DeliveredOutcome> StallAsync(string text, CancellationToken ct)
        {
            Interlocked.Increment(ref _hostCalls);
            if (_openings.AddOrUpdate(text, 1, (_, n) => n + 1) > 1)
            {
                lock (_gate)
                    _firstReopeningAt ??= time.GetUtcNow() - Started;
            }
            var id = Interlocked.Increment(ref _next);
            _waiting[id] = ct;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (EndGate is not null)
            {
                await EndGate.ConfigureAwait(false);
                throw;
            }
            finally
            {
                _waiting.TryRemove(id, out _);
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// Moves the clock for one <c>MoveNextAsync</c>, one timer at a time: when the delivery being
    /// opened waits only on its timers (to the next one due within its budget), or when the stream
    /// waits for its next cycle (to that cycle).
    /// </summary>
    private sealed class LiveDriver(ManualTimeProvider time, DeliveredMessageUnpacker unpacker, StallLog stalls, DeliveryScope scope)
    {
        private readonly object _gate = new();
        private Taken? _last;
        private int _mostReserved;

        /// <summary>The clock time that passed while a delivery was being opened.</summary>
        public TimeSpan Opening { get; private set; }

        /// <summary>The clock time that passed while the stream waited for its next cycle.</summary>
        public TimeSpan BetweenCycles { get; private set; }

        /// <summary>The deliveries whose opening lasted the whole budget.</summary>
        public int BudgetDeliveries { get; private set; }

        /// <summary>
        /// The mailbox's hook: a delivery is about to be answered. The answer is held until every
        /// host call of the delivery before has ended and given its reservation back (the model's
        /// lookups: see the class remarks).
        /// </summary>
        public void Delivered(IReadOnlyList<string> ids)
        {
            SpinWait.SpinUntil(() => HostCallsEnded(), PickupTest.Timeout).Should().BeTrue("every host call of the delivery before ends ({0} still reserved)", unpacker.Reserved);
            lock (_gate)
                _last = new Taken(ids, unpacker.Settled, time.GetUtcNow());
        }

        /// <summary>Whether the hook, while it held an answer, has seen a reservation still held.</summary>
        public bool SawReservationsHeld => Volatile.Read(ref _mostReserved) > 0;

        /// <summary>The most reservations the hook saw held while it held an answer.</summary>
        public int MostReservedBeforeAnAnswer => Volatile.Read(ref _mostReserved);

        private bool HostCallsEnded()
        {
            var reserved = unpacker.Reserved;
            if (reserved == 0)
                return true;
            if (reserved > Volatile.Read(ref _mostReserved))
                Volatile.Write(ref _mostReserved, reserved);
            return false;
        }

        public async Task DriveAsync(Task move)
        {
            while (!move.IsCompleted)
            {
                SpinWait.SpinUntil(() => move.IsCompleted || Parked(out _) || CycleDue(), PickupTest.Timeout).Should().BeTrue("the stream yields, or waits on a timer");
                if (move.IsCompleted)
                    break;
                if (Parked(out var opening))
                {
                    if (time.NextDueAt is { } due && due <= opening!.At + Budget)
                    {
                        var by = due - time.GetUtcNow();
                        time.Advance(by);
                        Opening += by;
                        if (due == opening.At + Budget)
                            BudgetDeliveries++;
                    }
                    continue;
                }
                var wait = time.NextDueAt!.Value - time.GetUtcNow();
                time.Advance(wait);
                BetweenCycles += wait;
            }
            await move;
        }

        private bool CycleDue() => unpacker.Opening == 0 && time.IsScheduled(Interval);

        private bool Parked(out Taken? opening)
        {
            lock (_gate)
                opening = _last;
            if (opening is null || unpacker.Opening == 0)
                return false;
            var opened = opening.Opened ??= opening.Ids.Count - unpacker.Memory.HeldBack(scope, opening.Ids).Count(held => held);
            var live = stalls.Live;
            return live == DeliveredMessageUnpacker.FetchConcurrency || unpacker.Settled - opening.Settled + live == opened;
        }

        private sealed class Taken(IReadOnlyList<string> ids, long settled, DateTimeOffset at)
        {
            public IReadOnlyList<string> Ids { get; } = ids;

            public long Settled { get; } = settled;

            public DateTimeOffset At { get; } = at;

            public int? Opened { get; set; }
        }
    }
}
