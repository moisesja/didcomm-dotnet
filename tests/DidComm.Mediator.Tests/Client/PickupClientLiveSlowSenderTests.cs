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
/// at a time, under the real per-delivery budget. The clock is moved one timer at a time, and only
/// when the stream waits on one: a delivery whose every message has an outcome or waits on its timer
/// (or four wait), or the stream waiting for its next cycle.
/// </summary>
public sealed class PickupClientLiveSlowSenderTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;
    private static readonly TimeSpan Interval = new MediatorClientOptions().LivePollInterval;

    /// <summary>Messages that never open in front × the mediator's lease (60 s, the default, and 10 s).</summary>
    public static TheoryData<int, int> Matrix()
    {
        var data = new TheoryData<int, int>();
        foreach (var slow in new[] { 1, 16, 31, 99 })
        {
            foreach (var lease in new[] { 60, 10 })
                data.Add(slow, lease);
        }
        return data;
    }

    /// <summary>
    /// <paramref name="slow"/> messages whose senders never resolve, then an honest one, at a mediator
    /// whose lease is <paramref name="leaseSeconds"/>. A cycle learns 10 messages per drain round, and
    /// as many rounds as fit in one lease before the first round's messages come back (at least one:
    /// then a round of messages already reported in this drain ends it). So the honest message is
    /// reached in cycle <c>C = ceil((slow + 1) / (10 · max(1, floor(lease / UnpackTimeout))))</c>, each
    /// message that never opens being opened once, when the learning fits in their first pause (ten
    /// minutes). When it does not (99 at a 10 s lease), the messages learned first are opened once
    /// more as their pauses end, and it is reached within <c>2C − 1</c> cycles.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_live_stream_reaches_the_message_behind_ones_that_never_open_within_the_live_bound(int slow, int leaseSeconds)
    {
        var time = new ManualTimeProvider();
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        var mailbox = new FakeMailbox(time, lease, Enumerable.Range(0, slow).Select(i => ($"s{i}", $"stall:{i}")).Append(("h", "ok:honest")).ToArray());
        var exchange = mailbox.Attach(new FakePickupExchange(time: time) { Route = WebSocketRoute }.UseBudget(Budget));
        var stalls = new StallLog();
        exchange.Unpacker = (text, ct) => text.StartsWith("stall:", StringComparison.Ordinal) ? stalls.StallAsync(text, ct) : Task.FromResult(DefaultOutcome(text));
        var driver = new LiveDriver(time, exchange.Budgeted!, stalls, new DeliveryScope(M, L));
        mailbox.OnDeliver = driver.Delivered;
        var cyclesNeeded = (slow + PerCycle(lease)) / PerCycle(lease);
        var fitsInFirstPause = LearnsWithinFirstPause(cyclesNeeded, lease, exchange.Budgeted!.Memory.FirstPause);
        var bound = fitsInFirstPause ? cyclesNeeded : 2 * cyclesNeeded - 1;
        using var cts = new CancellationTokenSource();
        var stream = PickupTest.Client(exchange).ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();
        var started = time.GetUtcNow();

        var reached = false;
        Task<bool> move;
        while (LiveOns(exchange) <= bound)
        {
            move = stream.MoveNextAsync().AsTask();
            await driver.DriveAsync(move);
            (await move).Should().BeTrue();
            if (stream.Current.Unpacked?.Message.Id == "honest")
            {
                reached = true;
                break;
            }
            stream.Current.Error.Should().BeOfType<TimeoutException>("a message that never opens fails at its share, or at once while it is held back");
        }
        var cycles = LiveOns(exchange);
        var elapsed = time.GetUtcNow() - started;
        move = stream.MoveNextAsync().AsTask();
        await PickupClientLiveStreamTests.StopAsync(stream, move, cts);

        output.WriteLine($"{slow} never open, lease {leaseSeconds} s: reached in cycle {cycles} after {elapsed.TotalSeconds} s; bound {bound} cycles (C = {cyclesNeeded}); each opened at most {stalls.MostOpenings} time(s)");
        reached.Should().BeTrue("the stream reaches the honest message within {0} cycles; after {1} it had not", bound, cycles);
        cycles.Should().BeLessThanOrEqualTo(bound);
        stalls.MostOpenings.Should().Be(fitsInFirstPause || slow < PerCycle(lease) ? 1 : 2, "a message that never opens is opened again only once its pause (ten minutes) has ended");
    }

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
    private sealed class StallLog
    {
        private readonly ConcurrentDictionary<long, CancellationToken> _waiting = new();
        private readonly ConcurrentDictionary<string, int> _openings = new(StringComparer.Ordinal);
        private long _next;

        /// <summary>Calls waiting with their token not cancelled.</summary>
        public int Live => _waiting.Values.Count(t => !t.IsCancellationRequested);

        /// <summary>The most times one message was opened.</summary>
        public int MostOpenings => _openings.IsEmpty ? 0 : _openings.Values.Max();

        public async Task<DeliveredOutcome> StallAsync(string text, CancellationToken ct)
        {
            _openings.AddOrUpdate(text, 1, (_, n) => n + 1);
            var id = Interlocked.Increment(ref _next);
            _waiting[id] = ct;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
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
        private Opening? _last;

        /// <summary>The mailbox's hook: a delivery is about to be answered (the previous one is fully opened).</summary>
        public void Delivered(IReadOnlyList<string> ids)
        {
            lock (_gate)
                _last = new Opening(ids, unpacker.Settled, time.GetUtcNow());
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
                        time.Advance(due - time.GetUtcNow());
                    continue;
                }
                time.Advance(time.NextDueAt!.Value - time.GetUtcNow());
            }
            await move;
        }

        private bool CycleDue() => unpacker.Opening == 0 && time.IsScheduled(Interval);

        private bool Parked(out Opening? opening)
        {
            lock (_gate)
                opening = _last;
            if (opening is null || unpacker.Opening == 0)
                return false;
            var opened = opening.Opened ??= opening.Ids.Count - unpacker.Memory.HeldBack(scope, opening.Ids).Count(held => held);
            var live = stalls.Live;
            return live == DeliveredMessageUnpacker.FetchConcurrency || unpacker.Settled - opening.Settled + live == opened;
        }

        private sealed class Opening(IReadOnlyList<string> ids, long settled, DateTimeOffset at)
        {
            public IReadOnlyList<string> Ids { get; } = ids;

            public long Settled { get; } = settled;

            public DateTimeOffset At { get; } = at;

            public int? Opened { get; set; }
        }
    }
}
