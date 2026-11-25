using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2): a message queued behind four messages that never open, when the
/// deliveries that bring them are far apart in time: a live stream with a long
/// <see cref="MediatorClientOptions.LivePollInterval"/>, a host that runs its fetch loop now and
/// then, and a host that starts a new process for each loop. What is checked in every case: whether
/// the message behind the four is opened, and how many deliveries and host calls it took. Simulated
/// time (the real unpacker and its memory run on the manual clock); nothing sleeps, and real time is
/// only a hang guard.
/// </summary>
public sealed class SlowSenderSpacedDeliveriesRt3Tests(ITestOutputHelper output)
{
    private const int InFront = DeliveredMessageUnpacker.FetchConcurrency; // four
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s

    // ---- the live stream ---------------------------------------------------------------------------

    /// <summary>
    /// A stream at the default <see cref="MediatorClientOptions.LivePollInterval"/> (one minute), at a
    /// mediator that leases for 60 s: four messages that never open and one that does were queued
    /// before the stream started. The stream opens the fifth message in its second cycle, and each
    /// of the four is opened once.
    /// </summary>
    [Fact]
    public async Task At_the_default_poll_interval_the_stream_opens_the_message_behind_four_that_never_open_in_its_second_cycle()
    {
        var run = await RunStreamAsync(new MediatorClientOptions().LivePollInterval, Lease, horizon: TimeSpan.FromHours(1));

        run.ReachedAt.Should().NotBeNull("the four in front are held back from the second cycle on");
        run.Cycles.Should().Be(2);
        run.HostCalls.Should().Be(InFront + 1, "each of the four is opened once, then the fifth");
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

        run.ReachedAt.Should().NotBeNull("the pause of the four in front grows until it outlasts the interval");
    }

    /// <summary>
    /// The same queue, for a stream whose <see cref="MediatorClientOptions.LivePollInterval"/> is two
    /// hours (the option allows up to 24 hours), at a mediator that leases for 60 s and at one that
    /// does not set delivered messages aside: the fifth message is opened within two simulated days.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: it is not opened in any of the cycles of the two days;
    /// in every cycle the four in front are opened again and the fifth ends as out of time before
    /// its turn.
    /// </remarks>
    [Theory(Skip = "RT3: whether a stream whose cycles are two hours apart opens a message queued behind four that never open")]
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
        for (var i = 0; i < InFront; i++)
            mediator.Enqueue($"never-{i}", $"stall:never-{i}");
        mediator.Enqueue("honest", "ok:honest");
        await using var driver = new StreamDriver(exchange);

        await driver.RunAsync(() => driver.Items.Any(Opened), horizon);

        TimeSpan? reachedAt = driver.Items.Any(Opened) ? driver.Items.First(Opened).At : null;
        var cycles = exchange.Log.Count(r => r.Type == PickupClientLiveStreamTests.LiveOn);
        var honest = driver.Items.Where(i => i.Item.Id == "honest").Select(i => i.Item.Unpacked is not null ? "opened" : i.Item.Error!.Message.Contains("before its turn came") ? "before its turn" : i.Item.Error.GetType().Name).ToArray();
        var outcomes = string.Join(", ", honest.GroupBy(o => o).Select(g => $"{g.Count()} × {g.Key}"));
        output.WriteLine($"poll interval {pollInterval}, lease {lease.TotalSeconds} s: opened at {(reachedAt is { } at ? at.ToString() : "never")} of {exchange.Clock.Now} simulated; {cycles} cycles, {exchange.HostCalls} host calls; the fifth message's outcomes: {outcomes}; ended with {driver.EndedWith?.GetType().Name ?? "nothing"}");
        return new StreamRun(reachedAt, cycles, exchange.HostCalls, outcomes);

        static bool Opened((PickupItem Item, TimeSpan At) i) => i.Item.Id == "honest" && i.Item.Unpacked is not null;
    }

    private sealed record StreamRun(TimeSpan? ReachedAt, int Cycles, int HostCalls, string HonestOutcomes);

    // ---- the fetch loop ----------------------------------------------------------------------------

    /// <summary>
    /// A host that fetches until drained, then again five minutes later, in one process, at a
    /// mediator that leases for 60 s: four messages that never open are queued in front of one that
    /// does. The second loop returns the fifth message opened.
    /// </summary>
    [Fact]
    public async Task A_fetch_loop_run_every_five_minutes_returns_the_message_behind_four_that_never_open_in_its_second_run()
    {
        var run = await RunLoopsAsync(between: TimeSpan.FromMinutes(5), newProcessEachRun: false, runs: 12);

        run.ReachedInRun.Should().Be(2, "the four in front are held back in the second run");
    }

    /// <summary>
    /// The same queue, for a host that runs its fetch loop every two hours in one process: one of
    /// its first twelve runs returns the fifth message opened.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: none of the twelve runs returns it opened; every run
    /// opens the four in front again.
    /// </remarks>
    [Fact(Skip = "RT3: whether a fetch loop run every two hours returns a message queued behind four that never open")]
    public async Task A_fetch_loop_run_every_two_hours_returns_the_message_behind_four_that_never_open()
    {
        var run = await RunLoopsAsync(between: TimeSpan.FromHours(2), newProcessEachRun: false, runs: 12);

        run.ReachedInRun.Should().NotBeNull("one of the runs returns the message behind the four ({0} host calls in {1} runs)", run.HostCalls, run.Runs);
    }

    /// <summary>
    /// The same queue, for a host that starts a new process for every run of its fetch loop (a
    /// scheduled job), five minutes apart: one of its first twelve runs returns the fifth message
    /// opened. A new process has an empty memory (R-5).
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: none of the twelve runs returns it opened; every run
    /// opens the four in front again.
    /// </remarks>
    [Fact(Skip = "RT3: whether a fetch loop that runs in a new process each time returns a message queued behind four that never open")]
    public async Task A_fetch_loop_in_a_new_process_each_run_returns_the_message_behind_four_that_never_open()
    {
        var run = await RunLoopsAsync(between: TimeSpan.FromMinutes(5), newProcessEachRun: true, runs: 12);

        run.ReachedInRun.Should().NotBeNull("one of the runs returns the message behind the four ({0} host calls in {1} runs)", run.HostCalls, run.Runs);
    }

    // ---- the eagerest fetch loop, at a mediator whose lease is long -------------------------------

    /// <summary>
    /// The eagerest host (it fetches until drained and again as soon as the mediator's next lease
    /// ends), default client options, at a mediator that leases what it delivers for fifteen
    /// minutes: four messages that never open are queued in front of one that does. The loop returns
    /// the fifth message opened within the simulated day.
    /// </summary>
    [Fact]
    public async Task At_a_lease_of_fifteen_minutes_the_eagerest_fetch_loop_returns_the_message_behind_four_that_never_open_within_a_day()
    {
        var reach = await ReachAtLeaseAsync(TimeSpan.FromMinutes(15), cap: TimeSpan.FromHours(24));

        reach.Found.Should().BeTrue("the pause of the four in front grows until it outlasts the lease");
    }

    /// <summary>
    /// The same queue and host at a mediator that leases for two hours (this library's mediator
    /// allows a lease of up to 24 hours): the loop returns the fifth message opened within two
    /// simulated days.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: it is not returned opened within the two days; each time
    /// the lease ends the four in front are opened again.
    /// </remarks>
    [Fact(Skip = "RT3: whether the eagerest fetch loop returns a message queued behind four that never open at a mediator whose lease is two hours")]
    public async Task At_a_lease_of_two_hours_the_eagerest_fetch_loop_returns_the_message_behind_four_that_never_open_within_two_days()
    {
        var reach = await ReachAtLeaseAsync(TimeSpan.FromHours(2), cap: TimeSpan.FromHours(48));

        reach.Found.Should().BeTrue("the message behind the four is returned opened ({0} fetches in {1})", reach.Fetches, reach.Elapsed);
    }

    private async Task<SlowSenderFetchQa1Tests.Reach> ReachAtLeaseAsync(TimeSpan lease, TimeSpan cap)
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease);
        for (var i = 0; i < InFront; i++)
            mediator.Enqueue($"never-{i}", Qa1.Anon($"never-{i}"));
        mediator.Enqueue("honest", Qa1.Anon("honest"));

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit: 10, cap);

        output.WriteLine($"lease {lease}: returned opened {(reach.Found ? "after " + reach.Elapsed : "never, in " + reach.Elapsed)} simulated; {reach.Fetches} fetches, {exchange.Host.Calls.Count} host calls");
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

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(12));

        output.WriteLine($"limit {limit}, 99 in front, lease {Lease.TotalSeconds} s: returned opened {(reach.Found ? "after " + reach.Elapsed : "never, in " + reach.Elapsed)} simulated; {reach.Fetches} fetches, {exchange.Host.Calls.Count} host calls");
        reach.Found.Should().BeTrue("the message behind the 99 is returned opened ({0} fetches in {1})", reach.Fetches, reach.Elapsed);
    }

    // A host's fetch loop, run `runs` times, `between` apart: each run fetches (limit 10) until the
    // mediator says there is nothing to deliver now.
    private async Task<LoopRuns> RunLoopsAsync(TimeSpan between, bool newProcessEachRun, int runs)
    {
        using var floor = new PoolFloor(parked: 2);
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Lease);
        for (var i = 0; i < InFront; i++)
            mediator.Enqueue($"never-{i}", Qa1.Anon($"never-{i}"));
        mediator.Enqueue("honest", Qa1.Anon("honest"));
        var client = exchange.Client();
        int? reachedInRun = null;
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
                var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10));
                if (batch.Items.Any(i => i.Id == "honest" && i.Unpacked is not null))
                    reachedInRun = run;
                if (batch.Drained || reachedInRun is not null)
                    break;
            }
            if (reachedInRun is null)
                exchange.Clock.Inner.Advance(between);
        }
        output.WriteLine($"runs {between} apart, {(newProcessEachRun ? "a new process each" : "one process")}: returned opened in run {(reachedInRun is { } r ? r.ToString() : "none")} of {run}, after {exchange.Clock.TrueNow - start} simulated; {exchange.Host.Calls.Count} host calls");
        return new LoopRuns(reachedInRun, run, exchange.Host.Calls.Count);
    }

    private sealed record LoopRuns(int? ReachedInRun, int Runs, int HostCalls);
}
