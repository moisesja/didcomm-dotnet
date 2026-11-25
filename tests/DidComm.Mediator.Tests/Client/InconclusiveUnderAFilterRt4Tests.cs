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
/// turns it away, so it is not acknowledged; it is a failed item that is not inconclusive, and the
/// documentation says that is not a reason to discard it (R-7: it is opened again each time it
/// comes). What is checked: whether the message queued behind sixteen of them, which opens at once,
/// is ever returned opened to a host that leaves them queued, and what
/// <see cref="PickupItem.Inconclusive"/> says of it and of messages that never open queued between.
/// The mediator delivers oldest first and leases for 60 s; the host's lookups honour their token and
/// block no thread; the real unpacker and its memory run on the manual clock, and real time is only
/// a hang guard.
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
        /// <summary>It discards a failed item unless it is inconclusive or a filter turned it away (the documentation's example of an item not to discard).</summary>
        LeavesWhatTheFilterTurnedAway,

        /// <summary>It discards a failed item unless it is inconclusive.</summary>
        DiscardsUnlessInconclusive,
    }

    /// <summary>
    /// Reproduction (fails on ae71081): the host fetches under the filter and leaves queued what the
    /// filter turned away. Expected: the message that opens at once is returned opened within the
    /// horizon of two hours. Observed: in every fetch the sixteen in front are opened again and
    /// turned away again, never remembered; the message behind them is handed over as inconclusive
    /// ("before its turn came") every time and is never opened; and in the second row the sixteen
    /// that never open are started later than the start allowance every time, so they are
    /// inconclusive every time and never remembered either.
    /// </summary>
    [Theory(Skip = "RT4: whether a host that fetches under a recipientDid filter and leaves queued what the filter turned away receives the message queued behind sixteen such messages; observed: it is inconclusive in every fetch and never opened, and messages that never open queued between are inconclusive every time and never remembered")]
    [InlineData(InFront.SixteenThatOpenForAnotherDidLate)]
    [InlineData(InFront.SixteenThatOpenForAnotherDidThenSixteenThatNeverOpen)]
    public async Task Under_a_filter_a_host_that_leaves_what_the_filter_turned_away_queued_receives_the_message_behind_sixteen_such_messages(InFront inFront)
    {
        var run = await RunAsync(inFront, Host.LeavesWhatTheFilterTurnedAway, filtered: true);

        run.Opened.Should().BeTrue("the message opens at once when a worker takes it in time (fetches: {0}; handed over inconclusive {1} times, as: {2}; remembered at the end: {3}; never-opening messages handed over inconclusive {4} times and conclusive {5} times)",
            run.Fetches, run.TimesInconclusive, run.HandedOverAs, run.Remembered, run.NeverOpeningInconclusive, run.NeverOpeningConclusive);
    }

    /// <summary>
    /// Held: the same queues under the same filter, for a host that discards a failed item unless
    /// it is inconclusive, what the filter turned away included: the message behind is returned
    /// opened, after the simulated time given. (That host has then deleted sixteen messages that
    /// opened for another of its DIDs.)
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
    /// Held: the same queues fetched without a filter, the remedy the documentation names. The
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

    private sealed record Run(bool Opened, TimeSpan Elapsed, int Fetches, int TimesInconclusive, string HandedOverAs, int Remembered, int NeverOpeningInconclusive, int NeverOpeningConclusive, int Discarded);

    private async Task<Run> RunAsync(InFront inFront, Host host, bool filtered)
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
        int fetches = 0, timesInconclusive = 0, neverInconclusive = 0, neverConclusive = 0, discarded = 0;
        var opened = false;
        while (!opened && exchange.Clock.TrueNow - start < Horizon)
        {
            // The lookups of the delivery before have ended before the next one is opened.
            Qa1.LookupsEnded(exchange.Unpacker);
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100, filter));
            fetches++;
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

        var run = new Run(opened, exchange.Clock.TrueNow - start, fetches, timesInconclusive, string.Join(" / ", handed), exchange.Unpacker.Memory.Count, neverInconclusive, neverConclusive, discarded);
        output.WriteLine($"{inFront}, host {host}, filter {(filtered ? "on" : "off")}: returned opened: {run.Opened} after {run.Elapsed} simulated and {run.Fetches} fetches; handed over inconclusive {run.TimesInconclusive} times, as: {run.HandedOverAs}; host calls: {exchange.Host.Calls.Count} (for the message: {exchange.Host.Calls.Labels.Count(l => l == "honest")}); remembered at the end: {run.Remembered}; never-opening messages handed over inconclusive {run.NeverOpeningInconclusive} times, conclusive (not held back) {run.NeverOpeningConclusive} times; discarded by the host: {run.Discarded}; still queued: {mediator.Count}");
        return run;
    }
}
