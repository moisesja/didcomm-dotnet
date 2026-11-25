using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111: a message queued behind messages that never open, when the deliveries that bring them are
/// far apart in time: a live stream with a long <see cref="MediatorClientOptions.LivePollInterval"/>,
/// a host that runs its fetch loop now and then, and a host that starts a new process for each loop.
/// What is checked in every case: whether the message behind is opened, and in which cycle or run.
/// Simulated time (the real unpacker and its memory run on the manual clock); nothing sleeps, and
/// real time is only a hang guard. The host's lookups honour their token and have ended before the
/// next delivery (<see cref="Qa1Loop"/> and <see cref="SimExchange"/> state that model).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What decides it.</strong> The client opens up to sixteen messages of a delivery at once
/// (#111, owner decision G). A message with fewer than sixteen that never open in front of it in its
/// own delivery gets a worker in that delivery and opens, however far apart deliveries are and
/// whatever the client remembers: the rows with four and with fifteen in front. With sixteen or more
/// in front of it in one delivery, the sixteen keep every worker until the budget ends and it is out
/// of time before its turn; it is then reached only by a later delivery that holds those sixteen
/// back, which needs the client's memory of them to be still there and still inside its pause.
/// </para>
/// <para>
/// <strong>The residual, pinned here (R-5 restated).</strong> The memory lives in the process, and
/// its longest pause is 256 budgets (about 85 minutes at the default budget). So with sixteen or
/// more that never open in front in one delivery, deliveries more than that apart (a fetch loop run
/// every two hours; a mediator whose lease is two hours) or a new process for each loop open the
/// sixteen again every time, and the message behind them is not reached by the loop alone; a host
/// that discards the failures it is handed reaches it. This library's mediator delivers at most ten
/// messages at once (<c>PickupClientSlowSenderAcceptanceTests</c> in <c>DidComm.InteropTests</c>
/// measures it), so sixteen in one delivery takes another mediator.
/// </para>
/// </remarks>
public sealed class SlowSenderSpacedDeliveriesRt3Tests(ITestOutputHelper output)
{
    private const int Four = 4;
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s

    // ---- the live stream (a delivery has at most ten messages) -------------------------------------

    /// <summary>
    /// A stream at the default <see cref="MediatorClientOptions.LivePollInterval"/> (one minute), at a
    /// mediator that leases for 60 s: four messages that never open and one that does were queued
    /// before the stream started. All five start in the first round; the fifth opens there and is
    /// yielded when the round's budget ends (20 s), in the first cycle, and each of the four is
    /// opened once.
    /// </summary>
    /// <remarks>
    /// Reworked for sixteen opened at once. With four at a time the four took every worker, and this
    /// test pinned "in its second cycle", once the four were held back.
    /// </remarks>
    [Fact]
    public async Task At_the_default_poll_interval_the_stream_opens_the_message_behind_four_that_never_open_in_its_first_cycle()
    {
        var run = await RunStreamAsync(new MediatorClientOptions().LivePollInterval, Lease, horizon: TimeSpan.FromHours(1));

        run.ReachedAt.Should().Be(Budget, "the fifth message gets a worker in the first round, and the round is yielded when its budget ends");
        run.Cycles.Should().Be(1);
        run.HostCalls.Should().Be(Four + 1, "each of the four is opened once, and the fifth");
    }

    /// <summary>
    /// The same queue, for a stream whose <see cref="MediatorClientOptions.LivePollInterval"/> is one
    /// hour (the longest delay this library's mediator allows a sender by default, which the option's
    /// documentation gives as the value to stay at or below): the fifth message is opened within the
    /// simulated day.
    /// </summary>
    [Fact]
    public async Task At_a_poll_interval_of_one_hour_the_stream_opens_the_message_behind_four_that_never_open_within_a_day()
    {
        var run = await RunStreamAsync(TimeSpan.FromHours(1), Lease, horizon: TimeSpan.FromHours(24));

        run.ReachedAt.Should().NotBeNull("the fifth message gets a worker in the first round");
    }

    /// <summary>
    /// The same queue, for a stream whose <see cref="MediatorClientOptions.LivePollInterval"/> is two
    /// hours (the option allows up to 24 hours), at a mediator that leases for 60 s and at one that
    /// does not set delivered messages aside: the fifth message is opened within two simulated days.
    /// </summary>
    /// <remarks>
    /// The independent pass on fix round 2 committed this skipped: with four opened at once it was
    /// not opened in any cycle of the two days. With sixteen at once it is opened in the first round.
    /// </remarks>
    [Theory]
    [InlineData(60)]
    [InlineData(0)]
    public async Task At_a_poll_interval_of_two_hours_the_stream_opens_the_message_behind_four_that_never_open_within_two_days(int leaseSeconds)
    {
        var run = await RunStreamAsync(TimeSpan.FromHours(2), TimeSpan.FromSeconds(leaseSeconds), horizon: TimeSpan.FromHours(48));

        run.ReachedAt.Should().NotBeNull("the message behind the four is opened ({0} cycles, {1} host calls, its outcomes: {2})", run.Cycles, run.HostCalls, run.HonestOutcomes);
    }

    private async Task<StreamRun> RunStreamAsync(TimeSpan pollInterval, TimeSpan lease, TimeSpan horizon)
    {
        var exchange = new SimExchange(o => o.LivePollInterval = pollInterval);
        var mediator = new LeasingMediator(exchange, lease);
        for (var i = 0; i < Four; i++)
            mediator.Enqueue($"never-{i}", $"stall:never-{i}");
        mediator.Enqueue("honest", "ok:honest");
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => driver.Items.Any(Opened), horizon);

        TimeSpan? reachedAt = driver.Items.Any(Opened) ? driver.Items.First(Opened).At : null;
        var cycles = exchange.Log.Count(r => r.Type == PickupClientLiveStreamTests.LiveOn);
        var honest = driver.Items.Where(i => i.Item.Id == "honest").Select(i => i.Item.Unpacked is not null ? "opened" : i.Item.Error!.Message.Contains("before its turn came") ? "before its turn" : i.Item.Error.GetType().Name).ToArray();
        var outcomes = string.Join(", ", honest.GroupBy(o => o).Select(g => $"{g.Count()} × {g.Key}"));
        output.WriteLine($"F1 | stream | {Four} in front | poll interval {pollInterval}, lease {lease.TotalSeconds} s: opened at {(reachedAt is { } at ? at.ToString() : "never")} of {exchange.Clock.Now} simulated; {cycles} cycles, {exchange.HostCalls} host calls; the fifth message's outcomes: {outcomes}; ended with {driver.EndedWith?.GetType().Name ?? "nothing"}");
        return new StreamRun(reachedAt, cycles, exchange.HostCalls, outcomes);

        static bool Opened((PickupItem Item, TimeSpan At) i) => i.Item.Id == "honest" && i.Item.Unpacked is not null;
    }

    private sealed record StreamRun(TimeSpan? ReachedAt, int Cycles, int HostCalls, string HonestOutcomes);

    // ---- the fetch loop, four in front -------------------------------------------------------------

    /// <summary>
    /// A host that fetches until drained, then again five minutes later, in one process, at a
    /// mediator that leases for 60 s: four messages that never open are queued in front of one that
    /// does. The first loop returns the fifth message opened: it gets a worker in the first fetch.
    /// </summary>
    /// <remarks>
    /// Reworked for sixteen opened at once. With four at a time this test pinned "in its second
    /// run", once the four were held back; that point is now made with sixteen in front in one
    /// delivery (<see cref="With_sixteen_or_more_in_front_in_one_delivery_a_fetch_loop_run_every_five_minutes_in_one_process_returns_the_message_behind_them_in_its_second_run"/>).
    /// </remarks>
    [Fact]
    public async Task A_fetch_loop_run_every_five_minutes_returns_the_message_behind_four_that_never_open_in_its_first_run()
    {
        var run = await RunLoopsAsync(between: TimeSpan.FromMinutes(5), newProcessEachRun: false, runs: 12, inFront: Four, limit: 10);

        run.ReachedInRun.Should().Be(1, "the fifth message gets a worker in the first fetch");
    }

    /// <summary>
    /// The same queue, for a host that runs its fetch loop every two hours in one process: one of
    /// its first twelve runs returns the fifth message opened.
    /// </summary>
    /// <remarks>
    /// The independent pass on fix round 2 committed this skipped: with four opened at once none of
    /// the twelve runs returned it. With sixteen at once the first does.
    /// </remarks>
    [Fact]
    public async Task A_fetch_loop_run_every_two_hours_returns_the_message_behind_four_that_never_open()
    {
        var run = await RunLoopsAsync(between: TimeSpan.FromHours(2), newProcessEachRun: false, runs: 12, inFront: Four, limit: 10);

        run.ReachedInRun.Should().NotBeNull("one of the runs returns the message behind the four ({0} host calls in {1} runs)", run.HostCalls, run.Runs);
    }

    /// <summary>
    /// The same queue, for a host that starts a new process for every run of its fetch loop (a
    /// scheduled job), five minutes apart: one of its first twelve runs returns the fifth message
    /// opened. A new process has an empty memory (R-5).
    /// </summary>
    /// <remarks>
    /// The independent pass on fix round 2 committed this skipped: with four opened at once none of
    /// the twelve runs returned it. With sixteen at once the first does.
    /// </remarks>
    [Fact]
    public async Task A_fetch_loop_in_a_new_process_each_run_returns_the_message_behind_four_that_never_open()
    {
        var run = await RunLoopsAsync(between: TimeSpan.FromMinutes(5), newProcessEachRun: true, runs: 12, inFront: Four, limit: 10);

        run.ReachedInRun.Should().NotBeNull("one of the runs returns the message behind the four ({0} host calls in {1} runs)", run.HostCalls, run.Runs);
    }

    /// <summary>
    /// The eagerest host (it fetches until drained and again as soon as the mediator's next lease
    /// ends), default client options, at a mediator that leases what it delivers for fifteen
    /// minutes: four messages that never open are queued in front of one that does. The loop returns
    /// the fifth message opened within the simulated day.
    /// </summary>
    [Fact]
    public async Task At_a_lease_of_fifteen_minutes_the_eagerest_fetch_loop_returns_the_message_behind_four_that_never_open_within_a_day()
    {
        var reach = await ReachAtLeaseAsync(TimeSpan.FromMinutes(15), cap: TimeSpan.FromHours(24), inFront: Four, limit: 10);

        reach.Found.Should().BeTrue("the fifth message gets a worker in the first fetch");
    }

    /// <summary>
    /// The same queue and host at a mediator that leases for two hours (this library's mediator
    /// allows a lease of up to 24 hours): the loop returns the fifth message opened within two
    /// simulated days.
    /// </summary>
    /// <remarks>
    /// The independent pass on fix round 2 committed this skipped: with four opened at once it was
    /// not returned within the two days. With sixteen at once the first fetch returns it.
    /// </remarks>
    [Fact]
    public async Task At_a_lease_of_two_hours_the_eagerest_fetch_loop_returns_the_message_behind_four_that_never_open_within_two_days()
    {
        var reach = await ReachAtLeaseAsync(TimeSpan.FromHours(2), cap: TimeSpan.FromHours(48), inFront: Four, limit: 10);

        reach.Found.Should().BeTrue("the message behind the four is returned opened ({0} fetches in {1})", reach.Fetches, reach.Elapsed);
    }

    // ---- the fetch loop, up to and beyond sixteen in front in one delivery -------------------------

    /// <summary>How the deliveries are spaced.</summary>
    public enum Spacing
    {
        /// <summary>The fetch loop runs every two hours, in one process, twelve times.</summary>
        EveryTwoHoursInOneProcess,

        /// <summary>The fetch loop runs every five minutes, each time in a new process (an empty memory), twelve times.</summary>
        EveryFiveMinutesInANewProcessEachTime,

        /// <summary>The eagerest loop at a mediator whose lease is two hours, for two days.</summary>
        AtALeaseOfTwoHours,
    }

    /// <summary>
    /// Fewer than sixteen in front in the message's own delivery: fifteen at a limit of 16 or 100
    /// (one delivery of sixteen); and sixteen, seventeen or ninety-nine at a limit of 16, where a
    /// delivery holds sixteen of them and the message behind comes in a later fetch of the same loop,
    /// with fewer than sixteen in front of it. Whatever the spacing, the first loop returns it opened.
    /// </summary>
    [Theory]
    [MemberData(nameof(FewerThanSixteenInItsDelivery))]
    public async Task With_fewer_than_sixteen_in_front_in_its_own_delivery_the_message_behind_is_returned_by_the_first_loop_whatever_the_spacing(Spacing spacing, int inFront, int limit)
    {
        var run = await RunSpacedAsync(spacing, inFront, limit);

        run.ReachedInRun.Should().Be(1, "it gets a worker in its own delivery ({0} in front, limit {1})", inFront, limit);
    }

    public static TheoryData<Spacing, int, int> FewerThanSixteenInItsDelivery()
    {
        var data = new TheoryData<Spacing, int, int>();
        foreach (var spacing in Enum.GetValues<Spacing>())
        {
            foreach (var (inFront, limit) in new[] { (15, 16), (15, 100), (16, 16), (17, 16), (99, 16) })
                data.Add(spacing, inFront, limit);
        }
        return data;
    }

    /// <summary>
    /// The residual, pinned (R-5 restated; see the class remarks): sixteen, seventeen or ninety-nine
    /// that never open in front of the message in one delivery (a limit of 100, at a mediator that
    /// delivers that many). With the loop run every two hours in one process, in a new process each
    /// time, or at a lease of two hours, no run returns the message behind them: every delivery
    /// opens sixteen of the ones in front again and the message is out of time before its turn.
    /// </summary>
    /// <remarks>
    /// The same rows on <c>22973ae</c> (four opened at once, a time limit per message), measured by
    /// this test on an export of it: not returned either, in any row.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SixteenOrMoreInOneDelivery))]
    public async Task Residual_with_sixteen_or_more_in_front_in_one_delivery_and_deliveries_further_apart_than_the_longest_pause_or_a_new_process_each_time_the_message_behind_is_not_returned(Spacing spacing, int inFront, int limit)
    {
        var run = await RunSpacedAsync(spacing, inFront, limit);

        run.ReachedInRun.Should().BeNull("sixteen of the {0} in front take every worker in each delivery, and nothing holds them back by the next one", inFront);
        run.HonestOutcomes.Should().OnlyContain(o => o == "before its turn", "the message behind is never refused or opened: its turn does not come");
    }

    public static TheoryData<Spacing, int, int> SixteenOrMoreInOneDelivery()
    {
        var data = new TheoryData<Spacing, int, int>();
        foreach (var spacing in Enum.GetValues<Spacing>())
        {
            foreach (var inFront in new[] { 16, 17, 99 })
                data.Add(spacing, inFront, 100);
        }
        return data;
    }

    /// <summary>
    /// What the memory is for, with sixteen opened at once: sixteen (or seventeen) that never open in
    /// front in one delivery, and the fetch loop run again five minutes later in the same process.
    /// The first run opens the sixteen and the message behind is out of time before its turn; the
    /// second holds the sixteen back and returns it opened.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public async Task With_sixteen_or_more_in_front_in_one_delivery_a_fetch_loop_run_every_five_minutes_in_one_process_returns_the_message_behind_them_in_its_second_run(int inFront)
    {
        var run = await RunLoopsAsync(between: TimeSpan.FromMinutes(5), newProcessEachRun: false, runs: 12, inFront, limit: 100);

        run.ReachedInRun.Should().Be(2, "the sixteen that ran in the first run are held back in the second");
        run.HostCalls.Should().Be(inFront + 1, "each message in front is opened once, then the message behind");
    }

    private async Task<LoopRuns> RunSpacedAsync(Spacing spacing, int inFront, int limit)
    {
        if (spacing != Spacing.AtALeaseOfTwoHours)
            return await RunLoopsAsync(spacing == Spacing.EveryTwoHoursInOneProcess ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(5), newProcessEachRun: spacing == Spacing.EveryFiveMinutesInANewProcessEachTime, runs: 12, inFront, limit);
        var reach = await ReachAtLeaseAsync(TimeSpan.FromHours(2), cap: TimeSpan.FromHours(48), inFront, limit);
        var outcomes = reach.Batches.SelectMany(b => b.Items).Where(i => i.Id == "honest").Select(Outcome).ToArray();
        return new LoopRuns(reach.Found ? 1 : null, 1, reach.HostCalls, outcomes);
    }

    private async Task<Qa1Reach> ReachAtLeaseAsync(TimeSpan lease, TimeSpan cap, int inFront, int limit)
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease);
        for (var i = 0; i < inFront; i++)
            mediator.Enqueue($"never-{i}", Qa1.Anon($"never-{i}"));
        mediator.Enqueue("honest", Qa1.Anon("honest"));

        var reach = await Qa1Loop.RunAsync(exchange, mediator, limit, cap);

        output.WriteLine($"F1 | the eagerest loop at a lease of {lease} | {inFront} in front | limit {limit}: returned opened {(reach.Found ? "after " + reach.Elapsed : "never, in " + reach.Elapsed)} simulated; {reach.Fetches} fetches, {reach.HostCalls} host calls");
        return reach;
    }

    // ---- limits outside the measured matrix ---------------------------------------------------------

    /// <summary>
    /// The eagerest fetch loop at a mediator that leases for 60 s, with 99 messages that never open
    /// in front of one that does, at limits other than 4 and 10: the loop returns the last message
    /// opened within twelve simulated hours. The time each limit took is written to the output.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(100)]
    public async Task At_other_limits_the_eagerest_fetch_loop_returns_the_message_behind_ninety_nine_that_never_open(int limit)
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Lease);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: 99, honestAt: 99);

        var reach = await Qa1Loop.RunAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(12));

        output.WriteLine($"limit {limit}, 99 in front, lease {Lease.TotalSeconds} s: returned opened {(reach.Found ? "after " + reach.Elapsed : "never, in " + reach.Elapsed)} simulated; {reach.Fetches} fetches, {reach.HostCalls} host calls");
        reach.Found.Should().BeTrue("the message behind the 99 is returned opened ({0} fetches in {1})", reach.Fetches, reach.Elapsed);
    }

    // A host's fetch loop, run `runs` times, `between` apart: each run fetches until the mediator
    // says there is nothing to deliver now. It runs on a pool thread; each fetch starts only once
    // the host calls of the fetch before have ended (Qa1Exchange.DriveAsync waits for them).
    private Task<LoopRuns> RunLoopsAsync(TimeSpan between, bool newProcessEachRun, int runs, int inFront, int limit)
        => Task.Run(async () =>
        {
            using var floor = new PoolFloor(parked: 2);
            var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Lease);
            for (var i = 0; i < inFront; i++)
                mediator.Enqueue($"never-{i}", Qa1.Anon($"never-{i}"));
            mediator.Enqueue("honest", Qa1.Anon("honest"));
            var client = exchange.Client();
            int? reachedInRun = null;
            var outcomes = new List<string>();
            var hostCalls = 0;
            var start = exchange.Clock.TrueNow;
            var run = 0;
            while (run < runs && reachedInRun is null)
            {
                run++;
                if (newProcessEachRun && run > 1)
                {
                    exchange.Restart();
                    client = exchange.Client();
                }
                while (true)
                {
                    var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit)).ConfigureAwait(false);
                    outcomes.AddRange(batch.Items.Where(i => i.Id == "honest").Select(Outcome));
                    if (batch.Items.Any(i => i.Id == "honest" && i.Unpacked is not null))
                        reachedInRun = run;
                    if (batch.Drained || reachedInRun is not null)
                        break;
                }
                // The host calls of this run have ended before the process goes, or the next run starts.
                SpinWait.SpinUntil(() => exchange.Unpacker.Reserved == 0, Qa1Exchange.Guard).Should().BeTrue("every host call of the run ends");
                hostCalls = exchange.Host.Calls.Count;
                if (reachedInRun is null)
                    exchange.Clock.Inner.Advance(between);
            }
            output.WriteLine($"F1 | runs {between} apart, {(newProcessEachRun ? "a new process each" : "one process")} | {inFront} in front | limit {limit}: returned opened in run {(reachedInRun is { } r ? r.ToString() : "none")} of {run}, after {exchange.Clock.TrueNow - start} simulated; {hostCalls} host calls; the message's outcomes: {string.Join(", ", outcomes.GroupBy(o => o).Select(g => $"{g.Count()} × {g.Key}"))}");
            return new LoopRuns(reachedInRun, run, hostCalls, outcomes);
        });

    private static string Outcome(PickupItem item)
        => item.Unpacked is not null ? "opened"
            : item.Error!.Message.Contains("before its turn came") ? "before its turn"
            : item.Error.Message.Contains("unpack capacity exhausted") ? "refused"
            : item.Error.GetType().Name;

    private sealed record LoopRuns(int? ReachedInRun, int Runs, int HostCalls, IReadOnlyList<string> HonestOutcomes);
}
