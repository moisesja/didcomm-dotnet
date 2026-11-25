using System.Diagnostics;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT4 (#111): real threads and the real clock (<see cref="TimeProvider.System"/>, as in
/// production; the other tests of the unpacker move a manual clock from a thread of their own, so a
/// budget's timer never waits for a pool thread there). Several deliveries are opened at the same
/// time on one container, each of messages whose host lookups block their thread until their token
/// is cancelled (or, in one row, until the test ends), and one message that opens at once, in a
/// delivery of its own or inside the first delivery. The thread pool is the test process's own, at
/// the floor it has when the test starts: run one row per process for a figure that means anything
/// (and with <c>DOTNET_PROCESSOR_COUNT</c> set for a smaller floor). What is measured, each time
/// counted from the start of the delivery it belongs to: when each host call's code started, when
/// each delivery's result was set, what the single message's outcome says, how many messages were
/// remembered, and when the reservations came back.
/// </summary>
/// <remarks>
/// Who opens the deliveries decides part of the outcome (<see cref="OpenedFrom"/>): a delivery's
/// budget is a timer created on the thread that opens it, and its host calls are queued from that
/// thread. The clients open a delivery where a fetch or a stream continues, on a pool thread; the
/// rows that open every delivery from one thread of the test's own are the unpacker's entry point
/// called that way, which no client does.
/// </remarks>
[Collection(BusyPoolRt4Collection.Name)]
public sealed class BlockingLookupsRealClockRt4Tests(ITestOutputHelper output)
{
    private const string Measurement = "RT4: a measurement on real threads and the real clock (20 s and more per row; one row per process). ";
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A measurement, and a reproduction for the rows that fail (on a415f6d: the rows of three and
    /// more deliveries of sixteen, and of eleven deliveries of four, all opened from one thread).
    /// Expected, from the unpacker's contract ("the returned task is complete when the last message
    /// has ended or, at the latest, when the budget runs out", "the budget decides the outcome, even
    /// when a resolver blocks its thread"): every delivery's result is set by its budget's end, here
    /// allowed two seconds more, and no host call's code starts after that. Observed in the failing
    /// rows: the results of all deliveries but one are set seconds after their budget, and host
    /// calls are started until then. In the rows that open each delivery from a thread of its own,
    /// or from pool threads, the results were set within a second of the budget.
    /// </summary>
    [Theory(Skip = Measurement + "Checked: whether every delivery's result is set by its budget's end, and no host call starts after it, while lookups block their threads; observed in the rows where one thread opens several deliveries: results set seconds late, and host calls started until then")]
    [InlineData(1, 16, false, OpenedFrom.OneThread)]
    [InlineData(2, 16, false, OpenedFrom.OneThread)]
    [InlineData(3, 16, false, OpenedFrom.OneThread)]
    [InlineData(4, 16, false, OpenedFrom.OneThread)]
    [InlineData(6, 16, false, OpenedFrom.OneThread)]
    [InlineData(4, 4, false, OpenedFrom.OneThread)]
    [InlineData(11, 4, false, OpenedFrom.OneThread)]
    [InlineData(4, 16, true, OpenedFrom.OneThread)]
    [InlineData(4, 16, false, OpenedFrom.AThreadEach)]
    [InlineData(6, 16, false, OpenedFrom.AThreadEach)]
    [InlineData(3, 16, false, OpenedFrom.PoolThreads)]
    [InlineData(4, 16, false, OpenedFrom.PoolThreads)]
    [InlineData(6, 16, false, OpenedFrom.PoolThreads)]
    public void On_the_real_clock_every_deliverys_result_is_set_by_its_budgets_end_and_no_host_call_starts_after_it_while_lookups_block_their_threads(int deliveries, int perDelivery, bool lookupsIgnoreTheirToken, OpenedFrom openedFrom)
    {
        var run = Run(deliveries, perDelivery, lookupsIgnoreTheirToken, openedFrom);

        run.Took.Max().Should().BeLessThanOrEqualTo(Budget + Margin, "every delivery's result is set when its budget of {0} s runs out (each delivery took: {1} s)", Budget.TotalSeconds, string.Join(", ", run.Took.Select(t => t.TotalSeconds.ToString("F2"))));
        run.LatestStartIntoItsDelivery.Should().BeLessThanOrEqualTo(Budget + Margin, "no host call's code starts after its delivery's budget ran out");
    }

    /// <summary>
    /// A measurement, and a reproduction for the rows that fail (on a415f6d: three and four
    /// deliveries of sixteen), every delivery opened from one thread. Expected: the message that
    /// opens at once, alone in its delivery, is returned opened, or its outcome says it was not
    /// attempted when no host code ran for it. Observed in the failing rows: no host code ran for
    /// it, and its outcome is "ran out while it was being opened" with the not-attempted flag false.
    /// </summary>
    [Theory(Skip = Measurement + "Checked: whether a message that opens at once, alone in its delivery, is opened or handed over as not attempted while other deliveries' lookups block their threads; observed in some rows: no host code ran for it and it is handed over as tried")]
    [InlineData(2, 16, false)]
    [InlineData(3, 16, false)]
    [InlineData(4, 16, false)]
    [InlineData(4, 4, false)]
    public void On_the_real_clock_a_message_that_opens_at_once_alone_in_its_delivery_is_opened_or_not_attempted_while_other_deliveries_lookups_block_their_threads(int deliveries, int perDelivery, bool lookupsIgnoreTheirToken)
    {
        var run = Run(deliveries, perDelivery, lookupsIgnoreTheirToken, OpenedFrom.OneThread);

        (run.SingleOpened || run.SingleInconclusive == true).Should().BeTrue("a message that opens at once is opened, or is handed over as not attempted when no host code ran for it (host calls for it: {0}; its outcome: {1}; not attempted: {2})", run.SingleHostCalls, run.SingleKind, run.SingleInconclusive);
    }

    /// <summary>
    /// The same deliveries, opened from pool threads as a fetch that continues on the pool opens
    /// them, with the message that opens at once inside the first delivery, at the position given
    /// among its sixteen. A measurement, and a reproduction for a row that fails; on a415f6d the row
    /// of six deliveries failed in two runs of six, and no other row failed. Expected: that message
    /// is returned opened, or its outcome says it was not attempted when no host code ran for it.
    /// Observed in the failing runs: no host code ran for it, and its outcome is "ran out while it
    /// was being opened" with the not-attempted flag false.
    /// </summary>
    [Theory(Skip = Measurement + "Checked: whether a message that opens at once, inside a delivery whose other lookups block their threads, is opened or handed over as not attempted; observed in some rows: no host code ran for it and it is handed over as tried")]
    [InlineData(1, 2)]
    [InlineData(1, 13)]
    [InlineData(2, 13)]
    [InlineData(3, 13)]
    [InlineData(4, 2)]
    [InlineData(4, 8)]
    [InlineData(4, 13)]
    [InlineData(6, 13)]
    public void On_the_real_clock_a_message_that_opens_at_once_inside_a_delivery_opened_from_the_pool_is_opened_or_not_attempted_while_the_other_lookups_block_their_threads(int deliveries, int position)
    {
        var run = Run(deliveries, perDelivery: 16, lookupsIgnoreTheirToken: false, OpenedFrom.PoolThreads, singleAt: position);

        (run.SingleOpened || run.SingleInconclusive == true).Should().BeTrue("a message that opens at once is opened, or is handed over as not attempted when no host code ran for it (host calls for it: {0}; its outcome: {1}; not attempted: {2})", run.SingleHostCalls, run.SingleKind, run.SingleInconclusive);
    }

    /// <summary>Which thread opens each delivery, and so creates its budget's timer.</summary>
    public enum OpenedFrom
    {
        /// <summary>One thread of the test's own opens them all, one after the other.</summary>
        OneThread,

        /// <summary>Each is opened from a new thread of its own.</summary>
        AThreadEach,

        /// <summary>Each is opened by a thread-pool work item of its own.</summary>
        PoolThreads,
    }

    // Took: from each delivery's own start to its result. LatestStartIntoItsDelivery: the latest a
    // host call's code started, counted from its own delivery's start.
    private sealed record Measured(TimeSpan[] Took, TimeSpan LatestStartIntoItsDelivery, bool SingleOpened, bool? SingleInconclusive, int SingleHostCalls, string SingleKind);

    private Measured Run(int deliveries, int perDelivery, bool lookupsIgnoreTheirToken, OpenedFrom openedFrom, int singleAt = -1)
    {
        ThreadPool.GetMinThreads(out var floor, out _);
        var threadsAtStart = ThreadPool.ThreadCount;
        var clock = Stopwatch.StartNew();
        var openedAt = new TimeSpan[singleAt < 0 ? deliveries + 1 : deliveries];
        var junkStarts = new System.Collections.Concurrent.ConcurrentBag<(int Delivery, TimeSpan At)>();
        var singleStarts = new System.Collections.Concurrent.ConcurrentBag<TimeSpan>();
        using var end = new ManualResetEventSlim(false);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label == "single")
            {
                singleStarts.Add(clock.Elapsed);
                return Task.FromResult(UnpackFakes.Opened(label));
            }
            junkStarts.Add((int.Parse(label.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture), clock.Elapsed));
            if (lookupsIgnoreTheirToken)
                end.Wait(TimeSpan.FromSeconds(150));
            else
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(150));
            ct.ThrowIfCancellationRequested();
            throw new DidComm.Exceptions.CryptoException("JWE could not be decrypted.");
        }, Budget, TimeProvider.System);

        // The single message has a delivery of its own (the last one) unless it has a position in the first.
        var all = singleAt < 0 ? deliveries + 1 : deliveries;
        var singleIn = singleAt < 0 ? deliveries : 0;
        var results = new Task<IReadOnlyList<DeliveredOutcome>>[all];
        var setAt = new TimeSpan?[all];
        TimeSpan? reservedBackAt = null;
        var reservedWhenSingleEnded = -1;
        DeliveredOutcome? single = null;
        Exception? error = null;

        void Open(int d)
        {
            openedAt[d] = clock.Elapsed;
            if (d == deliveries)
            {
                results[d] = unpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, FakePickupExchange.L), new[] { Message("did:example:alice#key-x25519-1", "single") }, "request-2", default);
                return;
            }
            var scope = new DeliveryScope(FakePickupExchange.M, $"did:example:mailbox-{d}");
            var messages = Enumerable.Range(0, perDelivery).Select(i => d == 0 && i == singleAt ? Message("did:example:alice#key-x25519-1", "single") : Message($"did:example:sender-{d}-{i}#key-1", $"junk-{d}-{i}")).ToArray();
            results[d] = unpacker.UnpackAllAsync(scope, messages, "request-1", default);
        }

        var driver = new Thread(() =>
        {
            try
            {
                clock.Restart();
                if (openedFrom == OpenedFrom.PoolThreads)
                {
                    // Each delivery is opened by a work item of its own, queued together while the
                    // pool's threads are free: a delivery's budget is created on a pool thread, as
                    // when a fetch continues there.
                    using var opened = new CountdownEvent(all);
                    for (var d = 0; d < all; d++)
                    {
                        ThreadPool.UnsafeQueueUserWorkItem(index =>
                        {
                            Open(index);
                            opened.Signal();
                        }, d, preferLocal: false);
                    }
                    opened.Wait(TimeSpan.FromSeconds(100));
                }
                else
                {
                    for (var d = 0; d < all; d++)
                    {
                        if (openedFrom == OpenedFrom.AThreadEach)
                        {
                            var index = d;
                            var opener = new Thread(() => Open(index)) { IsBackground = true };
                            opener.Start();
                            opener.Join();
                        }
                        else
                        {
                            Open(d);
                        }
                    }
                }
                var horizon = Budget + TimeSpan.FromSeconds(100);
                while (clock.Elapsed < horizon && (setAt.Any(t => t is null) || (!lookupsIgnoreTheirToken && reservedBackAt is null)))
                {
                    for (var d = 0; d < all; d++)
                    {
                        if (setAt[d] is null && results[d].IsCompleted)
                        {
                            setAt[d] = clock.Elapsed;
                            if (d == singleIn)
                                reservedWhenSingleEnded = unpacker.Reserved;
                        }
                    }
                    if (reservedBackAt is null && setAt.All(t => t is not null) && unpacker.Reserved == 0)
                        reservedBackAt = clock.Elapsed;
                    Thread.Sleep(2);
                }
                if (results[singleIn].IsCompletedSuccessfully)
                    single = singleAt < 0 ? results[singleIn].Result.Single() : results[singleIn].Result[singleAt];
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        { IsBackground = true, Name = "RT4 driver" };
        driver.Start();
        var joined = driver.Join(TimeSpan.FromSeconds(200));
        var reservedAtTheEnd = unpacker.Reserved;
        var remembered = unpacker.Memory.Count;
        end.Set();
        joined.Should().BeTrue();
        error.Should().BeNull();

        var allowance = unpacker.StartAllowance;
        // Each start counted from its own delivery's start.
        var starts = junkStarts.Select(j => j.At - openedAt[j.Delivery]).OrderBy(t => t).ToArray();
        var took = Enumerable.Range(0, all).Select(d => (setAt[d] ?? TimeSpan.MaxValue) - openedAt[d]).ToArray();
        var junk = deliveries * perDelivery - (singleAt < 0 ? 0 : 1);
        var kind = single is null ? "no result" : single.Succeeded ? "opened" : single.Error!.Message.Contains("while it was being opened") ? "failed: ran out while it was being opened" : single.Error.Message.Contains("before its turn came") ? "failed: before its turn came" : single.Error.Message.Contains("capacity exhausted") ? "failed: capacity exhausted" : "failed: " + single.Error.GetType().Name;
        var inconclusive = single is null ? null : InconclusiveOf(single);
        output.WriteLine($"{deliveries} deliveries of {perDelivery} blocking lookups (ignoring their token: {lookupsIgnoreTheirToken}; deliveries opened from: {openedFrom}) and one message that opens at once ({(singleAt < 0 ? "in a delivery of its own" : "in the first delivery, at position " + (singleAt + 1))}); opened at once per delivery: {DeliveredMessageUnpacker.FetchConcurrency}; processors {Environment.ProcessorCount}, pool floor {floor}, pool threads at start {threadsAtStart}, at the end {ThreadPool.ThreadCount}");
        output.WriteLine($"deliveries opened at: {string.Join(", ", openedAt.Select(t => t.TotalSeconds.ToString("F2")))} s; every time below is counted from the start of the delivery it belongs to");
        output.WriteLine($"blocking lookups whose code started: {starts.Length} of {junk}; within the start allowance of {allowance.TotalSeconds} s: {starts.Count(t => t <= allowance)}; later: {starts.Count(t => t > allowance)}; after the budget's {Budget.TotalSeconds} s: {starts.Count(t => t > Budget)}; never: {junk - starts.Length}; last start at {(starts.Length > 0 ? starts[^1].TotalSeconds.ToString("F2") : "-")} s");
        output.WriteLine($"remembered at the end: {remembered}");
        output.WriteLine($"the single message: host calls {singleStarts.Count}{(singleStarts.Count > 0 ? $" (code started at {(singleStarts.Min() - openedAt[singleIn]).TotalSeconds:F2} s)" : string.Empty)}; its outcome: {kind}; not attempted: {(inconclusive is { } flag ? flag.ToString() : "no such flag")}; its delivery's result set at {took[singleIn].TotalSeconds:F2} s");
        output.WriteLine($"results of the deliveries of blocking lookups set at: {string.Join(", ", took.Take(deliveries).Select(t => t == TimeSpan.MaxValue ? "never" : t.TotalSeconds.ToString("F2")))} s (budget {Budget.TotalSeconds} s)");
        output.WriteLine($"reserved when the single message's delivery ended: {reservedWhenSingleEnded}; reservations all back at: {(reservedBackAt is { } back ? back.TotalSeconds.ToString("F2") + " s" : "not within the horizon")}; reserved when the test ended: {reservedAtTheEnd}");

        setAt.Should().OnlyContain(t => t != null, "every delivery's result is set within the horizon");
        single.Should().NotBeNull();
        return new Measured(took, starts.Length > 0 ? starts[^1] : TimeSpan.Zero, single!.Succeeded, inconclusive, singleStarts.Count, kind);
    }

    private static DeliveredInput Message(string kid, string label) => new(label, TestJwe.Bytes(TestJwe.Authcrypt(kid, label)));

    // Read by name, so the same measurement runs against a tree whose outcomes have no such flag.
    private static bool? InconclusiveOf(DeliveredOutcome outcome)
        => typeof(DeliveredOutcome).GetProperty("Inconclusive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(outcome) as bool?;
}
