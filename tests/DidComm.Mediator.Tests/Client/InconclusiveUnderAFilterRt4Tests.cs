using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT4 (#111): a fetch loop under a <c>recipientDid</c> filter, with messages queued in front that
/// open for another of the host's DIDs. Such a message opens, so the memory forgets it; the filter
/// turns it away, so it is not acknowledged; left queued, it is opened again at every delivery
/// under the filter and stays in front (R-7). The contract (the coordinator's call on the review's
/// finding F5): a message the filter turned away is the host's to settle, by taking it with a
/// fetch without the filter or by discarding it with <see cref="PickupClient.AckAsync"/>; it is not
/// to be left queued. What is pinned here: what a host that leaves such messages queued gets (the
/// message behind sixteen of them is never returned opened, and is inconclusive at every fetch),
/// and that each way of settling them returns it: discarding them, fetching without the filter, and
/// fetching under the filter while taking them with a fetch without it every so often. The mediator
/// delivers oldest first and leases for 60 s; the host's lookups honour their token and block no
/// thread; the real unpacker and its memory run on the manual clock, and real time is only a hang
/// guard.
/// </summary>
public sealed class InconclusiveUnderAFilterRt4Tests(ITestOutputHelper output)
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency; // sixteen
    private const string AnotherOfTheHostsDids = "did:example:bob-two";
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(2);

    /// <summary>What is queued in front of the message that opens at once.</summary>
    public enum InFront
    {
        /// <summary>Sixteen messages that open for another of the host's DIDs, each after a little more than three quarters of the budget.</summary>
        SixteenThatOpenForAnotherDidLate,

        /// <summary>Sixteen messages that open for another of the host's DIDs after three seconds each, then sixteen that never open.</summary>
        SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen,
    }

    /// <summary>What the host does with the failed items of a fetch.</summary>
    public enum Host
    {
        /// <summary>It discards a failed item unless it is inconclusive or a filter turned it away: what the filter turned away it leaves queued, against the contract.</summary>
        LeavesWhatTheFilterTurnedAway,

        /// <summary>It discards a failed item unless it is inconclusive, what the filter turned away included.</summary>
        DiscardsUnlessInconclusive,

        /// <summary>
        /// It fetches under the filter and discards as <see cref="LeavesWhatTheFilterTurnedAway"/>
        /// does; once a given number of deliveries under the filter have handed it something the
        /// filter turned away, its next fetch is without the filter, which returns those messages
        /// opened.
        /// </summary>
        TakesWhatTheFilterTurnedAwayWithoutTheFilter,
    }

    /// <summary>
    /// A pin of documented behaviour (the review's finding F5; the coordinator's call: closed by the
    /// contract, no change to the client): the host fetches under the filter and leaves queued what
    /// the filter turned away. In every fetch the sixteen in front are opened again and turned away
    /// again, never remembered. The message behind them, which would open at once, is handed over as
    /// inconclusive ("before its turn came") at every delivery and is not returned opened within the
    /// two hours: 120 times in 240 fetches, with no host call for it. In the second row the sixteen
    /// that never open, queued between, are started later than the start allowance every time: they
    /// are inconclusive at every delivery (1920 times) and never remembered.
    /// </summary>
    /// <remarks>
    /// The review committed this as a reproduction that expected the message to be returned opened
    /// (red on ae71081). Its limits, as the review states them: the host fetches under a filter and
    /// never without one; the sender knows another DID whose keys the host holds; a delivery brings
    /// more than sixteen messages.
    /// </remarks>
    [Theory]
    [InlineData(InFront.SixteenThatOpenForAnotherDidLate, 17, 0)]
    [InlineData(InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen, 33, 1920)]
    public async Task Under_a_filter_a_host_that_leaves_queued_what_the_filter_turned_away_does_not_receive_the_message_behind_sixteen_such_messages_which_is_inconclusive_at_every_fetch(InFront inFront, int stillQueued, int neverOpeningInconclusive)
    {
        var run = await RunAsync(inFront, Host.LeavesWhatTheFilterTurnedAway, filtered: true);

        run.Opened.Should().BeFalse("left queued, the sixteen the filter turned away are opened again at every fetch and keep it from its turn");
        run.Elapsed.Should().Be(Horizon);
        run.Fetches.Should().Be(240, "two fetches per lease: one that brings the delivery, one that finds everything leased");
        run.TimesInconclusive.Should().Be(120, "once per delivery");
        run.HandedOverAs.Should().Be("inconclusive, before its turn came");
        run.HostCallsForTheMessage.Should().Be(0);
        run.Remembered.Should().Be(0, "a message that opens is forgotten, and nothing here had its whole budget");
        run.NeverOpeningInconclusive.Should().Be(neverOpeningInconclusive, "started later than the start allowance at every delivery");
        run.NeverOpeningConclusive.Should().Be(0);
        run.Discarded.Should().Be(0);
        run.TakenWithoutTheFilter.Should().Be(0);
        run.StillQueued.Should().Be(stillQueued, "nothing was settled");
    }

    /// <summary>
    /// The contract's second way, done every so often: the host fetches under the filter, and once
    /// the given number of deliveries have handed it something the filter turned away, its next
    /// fetch is without the filter. That fetch returns the sixteen opened (they are the host's, for
    /// another of its DIDs) and acknowledges them, and the message behind is returned opened after
    /// the simulated time given: at the delivery after they are gone, or, in the second row, after
    /// the sixteen that never open have had their whole budget once more and were discarded.
    /// </summary>
    [Theory]
    [InlineData(InFront.SixteenThatOpenForAnotherDidLate, 1, 120)]
    [InlineData(InFront.SixteenThatOpenForAnotherDidLate, 5, 360)]
    [InlineData(InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen, 1, 180)]
    [InlineData(InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen, 5, 420)]
    public async Task Under_a_filter_a_host_that_takes_what_the_filter_turned_away_with_a_fetch_without_the_filter_every_so_often_receives_the_message(InFront inFront, int afterDeliveries, int seconds)
    {
        var run = await RunAsync(inFront, Host.TakesWhatTheFilterTurnedAwayWithoutTheFilter, filtered: true, afterDeliveries);

        run.Opened.Should().BeTrue("the sixteen were taken with a fetch without the filter (fetches: {0}; handed over as: {1})", run.Fetches, run.HandedOverAs);
        run.TakenWithoutTheFilter.Should().Be(AtOnce, "the fetch without the filter returned each of the sixteen opened");
        run.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds));
        run.StillQueued.Should().Be(0, "the sixteen were acknowledged when they were taken, and so was the message");
    }

    /// <summary>
    /// The contract's first way: the same queues under the same filter, for a host that discards a
    /// failed item unless it is inconclusive, what the filter turned away included. The message
    /// behind is returned opened, after the simulated time given. (That host has then deleted
    /// sixteen messages that opened for another of its DIDs.)
    /// </summary>
    [Theory]
    [InlineData(InFront.SixteenThatOpenForAnotherDidLate, 60)]
    [InlineData(InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen, 120)]
    public async Task Under_a_filter_a_host_that_discards_unless_inconclusive_receives_the_message_behind_sixteen_that_the_filter_turned_away(InFront inFront, int seconds)
    {
        var run = await RunAsync(inFront, Host.DiscardsUnlessInconclusive, filtered: true);

        run.Opened.Should().BeTrue();
        run.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds));
        run.Discarded.Should().BeGreaterThanOrEqualTo(AtOnce, "what the filter turned away was discarded");
    }

    /// <summary>
    /// The contract's second way, done throughout: the same queues fetched without a filter. The
    /// sixteen in front open and are acknowledged, and the message behind is returned opened, after
    /// the simulated time given. In the second row the sixteen that never open are inconclusive once
    /// (started late, behind the sixteen that opened) and remembered at the delivery after.
    /// </summary>
    [Theory]
    [InlineData(InFront.SixteenThatOpenForAnotherDidLate, 60)]
    [InlineData(InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen, 120)]
    public async Task Without_a_filter_the_message_behind_sixteen_that_open_for_another_of_the_hosts_dids_is_returned_opened(InFront inFront, int seconds)
    {
        var run = await RunAsync(inFront, Host.LeavesWhatTheFilterTurnedAway, filtered: false);

        run.Opened.Should().BeTrue();
        run.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds));
        if (inFront == InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen)
        {
            run.NeverOpeningInconclusive.Should().Be(AtOnce, "each was started late once, behind the sixteen that opened");
            run.Remembered.Should().Be(AtOnce, "and had its whole budget at the delivery after");
        }
    }

    private sealed record Run(bool Opened, TimeSpan Elapsed, int Fetches, int TimesInconclusive, string HandedOverAs, int Remembered, int NeverOpeningInconclusive, int NeverOpeningConclusive, int Discarded, int TakenWithoutTheFilter, int StillQueued, int HostCallsForTheMessage);

    // `afterDeliveries`: for the host that takes what the filter turned away, how many deliveries
    // under the filter hand it such an item before its next fetch is without the filter.
    private async Task<Run> RunAsync(InFront inFront, Host host, bool filtered, int afterDeliveries = 1)
    {
        using var floor = new PoolFloor(parked: 2);
        var forAnother = inFront == InFront.SixteenThatOpenForAnotherDidLate
            ? Qa1.Budget - TimeSpan.FromTicks(Qa1.Budget.Ticks / 4) + TimeSpan.FromMilliseconds(100) // a little past the last instant a message may start
            : TimeSpan.FromSeconds(3); // later than the start allowance of an eighth of the budget
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(
            label => label == "honest" ? Qa1Step.Open
                : label.StartsWith("other-", StringComparison.Ordinal) ? Qa1Step.OpenAfter(forAnother)
                : Qa1Step.Stall,
            Lease,
            recipientOf: label => label.StartsWith("other-", StringComparison.Ordinal) ? AnotherOfTheHostsDids : FakePickupExchange.L);
        // Every message has a sender of its own, so no sender's bound is in play.
        for (var i = 0; i < AtOnce; i++)
            mediator.Enqueue($"other-{i}", Qa1.From($"did:example:sender-other-{i}#key-x25519-1", $"other-{i}"));
        if (inFront == InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen)
        {
            for (var i = 0; i < AtOnce; i++)
                mediator.Enqueue($"never-{i}", Qa1.From($"did:example:sender-never-{i}#key-x25519-1", $"never-{i}"));
        }
        mediator.Enqueue("honest", Qa1.From("did:example:alice#key-x25519-1", "honest"));

        var client = exchange.Client();
        var filter = filtered ? FakePickupExchange.L : null;
        var start = exchange.Clock.TrueNow;
        var handed = new List<string>();
        int fetches = 0, timesInconclusive = 0, neverInconclusive = 0, neverConclusive = 0, discarded = 0, taken = 0, deliveriesWithSomethingTurnedAway = 0;
        var opened = false;
        while (!opened && exchange.Clock.TrueNow - start < Horizon)
        {
            // The lookups of the delivery before have ended before the next one is opened.
            Qa1.LookupsEnded(exchange.Unpacker);
            var withoutTheFilter = host == Host.TakesWhatTheFilterTurnedAwayWithoutTheFilter && deliveriesWithSomethingTurnedAway >= afterDeliveries;
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100, withoutTheFilter ? null : filter));
            fetches++;
            if (batch.Items.Count > 0)
            {
                // A fetch without the filter that brought a delivery has taken them; one that found
                // everything leased is made again when the lease has ended.
                if (withoutTheFilter)
                    deliveriesWithSomethingTurnedAway = 0;
                else if (batch.Items.Any(i => i.Error is not null && i.Error.Message.Contains("for another of your DIDs")))
                    deliveriesWithSomethingTurnedAway++;
            }
            taken += batch.Items.Count(i => i.Unpacked is not null && i.Id.StartsWith("other-", StringComparison.Ordinal));
            var discard = new List<string>();
            foreach (var item in batch.Items)
            {
                if (item.Id == "honest")
                {
                    opened = item.Unpacked is not null;
                    timesInconclusive += item.Inconclusive ? 1 : 0;
                    var kind = item.Unpacked is not null ? "opened" : (item.Inconclusive ? "inconclusive" : "conclusive") + (item.Error!.Message.Contains("before its turn came") ? ", before its turn came" : item.Error.Message.Contains("while it was being opened") ? ", while it was being opened" : ", " + item.Error.GetType().Name);
                    if (handed.Count == 0 || handed[^1] != kind)
                        handed.Add(kind);
                    continue;
                }
                if (item.Id.StartsWith("never-", StringComparison.Ordinal) && item.Error is not null && !item.Error.Message.Contains("delivered before"))
                {
                    neverInconclusive += item.Inconclusive ? 1 : 0;
                    neverConclusive += item.Inconclusive ? 0 : 1;
                }
                if (item.Error is null || item.Inconclusive)
                    continue;
                var turnedAway = item.Error.Message.Contains("for another of your DIDs");
                if (host == Host.DiscardsUnlessInconclusive || !turnedAway)
                    discard.Add(item.Id);
            }
            if (opened)
                break;
            if (discard.Count > 0)
            {
                Qa1.LookupsEnded(exchange.Unpacker);
                await client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, discard).WaitAsync(Qa1Exchange.Guard);
                discarded += discard.Count;
            }
            if (batch.Drained)
            {
                if (mediator.NextLeaseEnd() is not { } next)
                    break; // nothing is queued
                exchange.Clock.Inner.Advance(next - exchange.Clock.TrueNow);
            }
        }

        var run = new Run(opened, exchange.Clock.TrueNow - start, fetches, timesInconclusive, string.Join(" / ", handed), exchange.Unpacker.Memory.Count, neverInconclusive, neverConclusive, discarded, taken, mediator.Count, exchange.Host.Calls.Labels.Count(l => l == "honest"));
        output.WriteLine($"{inFront}, host {host}{(host == Host.TakesWhatTheFilterTurnedAwayWithoutTheFilter ? $" after {afterDeliveries} deliveries" : string.Empty)}, filter {(filtered ? "on" : "off")}: returned opened: {run.Opened} after {run.Elapsed} simulated and {run.Fetches} fetches; taken with a fetch without the filter: {run.TakenWithoutTheFilter}; handed over inconclusive {run.TimesInconclusive} times, as: {run.HandedOverAs}; host calls: {exchange.Host.Calls.Count} (for the message: {exchange.Host.Calls.Labels.Count(l => l == "honest")}); remembered at the end: {run.Remembered}; never-opening messages handed over inconclusive {run.NeverOpeningInconclusive} times, conclusive (not held back) {run.NeverOpeningConclusive} times; discarded by the host: {run.Discarded}; still queued: {mediator.Count}");
        return run;
    }
}
