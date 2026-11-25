using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT4 (#111): what <see cref="PickupItem.Inconclusive"/> says of a message that was started late in
/// its delivery and was still being opened when the delivery's budget ended, and what a host that
/// discards a failed item unless that flag is true (<see cref="Qa1Discard.UnlessInconclusive"/>,
/// the host of the cookbook's pickup section) then does with it. The set-up: as many messages as
/// are opened at once, each failing on its own after fifteen seconds, queued in front of one
/// message that takes six seconds to open (or never opens), all in one fetch, at a mediator that
/// leases for 60 s. Such a message did not have the whole budget: the memory does not strike it,
/// and its item is inconclusive (the coordinator's call: the flag follows the memory). The real
/// unpacker and its memory run on the manual clock; real time is only a hang guard.
/// </summary>
public sealed class InconclusiveLateStartRt4Tests(ITestOutputHelper output)
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency; // sixteen
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s
    private static readonly TimeSpan InFrontFailAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OpensAfter = TimeSpan.FromSeconds(6);

    /// <summary>
    /// What the first fetch says of the message, and what the client's own memory says of it. It is
    /// started fifteen seconds into the budget, when the messages in front have failed, and is still
    /// being opened when the budget ends five seconds later: a failed item, "ran out while it was
    /// being opened", whose <see cref="PickupItem.Inconclusive"/> is true. The memory does not hold
    /// it back (its code started later than the start allowance), and a host that leaves it queued
    /// receives it opened by the first fetch after the lease, six seconds into that fetch: the
    /// sixteen in front were struck when they failed and are held back then, so it starts first.
    /// </summary>
    /// <remarks>
    /// This test pinned the flag false in fix round 3 ("its host call was created"). The flag now
    /// says what the memory says: the message was not struck, so its item is inconclusive.
    /// </remarks>
    [Fact]
    public async Task A_message_started_late_and_still_being_opened_at_the_budgets_end_is_inconclusive_and_not_remembered_and_opens_at_the_next_delivery_when_left_queued()
    {
        var (exchange, mediator) = Setup();

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit: 100, cap: TimeSpan.FromHours(1), discard: Qa1Discard.Nothing);

        var first = reach.Batches[0].Items.Should().ContainSingle(i => i.Id == "honest").Which;
        first.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        first.Inconclusive.Should().BeTrue("it started later than the start allowance, so it did not have the whole budget and was not struck");
        reach.Batches[0].Items.Where(i => i.Id != "honest").Should().OnlyContain(i => !i.Inconclusive, "a failure of its own counts");
        reach.Batches[0].Items.Where(i => i.Id != "honest").Should().HaveCount(AtOnce).And.OnlyContain(i => i.Error is DidComm.Exceptions.CryptoException, "the messages in front failed on their own");
        output.WriteLine($"left queued: returned opened: {reach.Found}, after {reach.Elapsed} simulated and {reach.Fetches} fetches; host calls for it: {exchange.Host.Calls.Labels.Count(l => l == "honest")}");
        reach.Found.Should().BeTrue("left queued, it is opened at the next delivery");
        reach.Elapsed.Should().Be(Lease + OpensAfter, "the first fetch after the lease starts it first");
        exchange.Host.Calls.Labels.Count(l => l == "honest").Should().Be(2, "it was not held back: its code ran again at the next delivery");
    }

    /// <summary>
    /// The host that discards a failed item unless <see cref="PickupItem.Inconclusive"/> is true
    /// receives the message. The first fetch hands it over once, as an inconclusive item (started
    /// late and cut), and the host leaves it queued; the sixteen in front failed on their own, and
    /// the host discards them. The first fetch after the lease starts the message first, and it
    /// opens six seconds into that fetch: 66 simulated seconds in all, at either limit.
    /// </summary>
    /// <remarks>
    /// RT4's reproduction, red on 98a32ac at both limits ("Expected reach.Found to be True ...
    /// (handed over as: a failed item, tried (TimeoutException); still queued at the mediator:
    /// False), but found False": the item's flag was false, the host discarded it, and it was never
    /// returned opened).
    /// </remarks>
    [Theory]
    [InlineData(17)]
    [InlineData(100)]
    public async Task A_host_that_discards_a_failed_item_unless_it_is_inconclusive_receives_a_message_that_takes_six_seconds_queued_behind_sixteen_that_fail_after_fifteen(int limit)
    {
        var (exchange, mediator) = Setup();

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(1), discard: Qa1Discard.UnlessInconclusive);

        var handed = reach.Batches.SelectMany(b => b.Items).Where(i => i.Id == "honest").ToArray();
        var returnedAs = string.Join(", ", handed.Select(i => i.Unpacked is not null ? "opened" : i.Inconclusive ? "a failed item, inconclusive" : "a failed item, conclusive (" + i.Error!.GetType().Name + ")"));
        output.WriteLine($"limit {limit}: returned opened: {reach.Found}; {reach.Fetches} fetches in {reach.Elapsed} simulated; handed over as: {returnedAs}; still queued at the mediator: {mediator.Holds("honest")}; host calls for it: {exchange.Host.Calls.Labels.Count(l => l == "honest")}");
        reach.Found.Should().BeTrue("the message opens in six seconds when it is given them (handed over as: {0}; still queued at the mediator: {1})", returnedAs, mediator.Holds("honest"));
        returnedAs.Should().Be("a failed item, inconclusive, opened");
        reach.Elapsed.Should().Be(Lease + OpensAfter, "the first fetch after the lease starts it first");
        mediator.Count.Should().Be(0, "the sixteen in front were discarded as conclusive failures, and the message was acknowledged");
    }

    /// <summary>
    /// The same queue with a last message that never opens, for the same host: what the rule costs.
    /// The first fetch hands that message over as inconclusive (started late and cut), and the host
    /// leaves it queued. The first fetch after the lease starts it with the budget; it has the whole
    /// budget, does not open, and is handed over as a conclusive failure, which the host discards:
    /// one delivery later than a host that discards every failure. The queue is empty 80 simulated
    /// seconds after the first fetch began (the lease, then one budget).
    /// </summary>
    [Theory]
    [InlineData(17)]
    [InlineData(100)]
    public async Task A_message_that_never_opens_and_was_started_late_is_inconclusive_once_and_a_conclusive_failure_at_the_next_delivery(int limit)
    {
        using var floor = new PoolFloor(parked: 2);
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "never" ? Qa1Step.Stall : Qa1Step.FailAfter(InFrontFailAfter), Lease);
        for (var i = 0; i < AtOnce; i++)
            mediator.Enqueue($"junk-{i}", Qa1.Anon($"junk-{i}"));
        mediator.Enqueue("never", Qa1.From("did:example:alice#key-x25519-1", "never"));
        var client = exchange.Client();
        var start = exchange.Clock.TrueNow;
        var handed = new List<string>();

        while (mediator.Count > 0 && exchange.Clock.TrueNow - start < TimeSpan.FromHours(1))
        {
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit));
            handed.AddRange(batch.Items.Where(i => i.Id == "never").Select(i => i.Inconclusive ? "inconclusive" : "conclusive"));
            Qa1.LookupsEnded(exchange.Unpacker);
            if (Qa1.ToDiscard(batch.Items, Qa1Discard.UnlessInconclusive) is { Length: > 0 } conclusive)
                await client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, conclusive).WaitAsync(Qa1Exchange.Guard);
            if (batch.Drained && mediator.Count > 0)
                exchange.Clock.Inner.Advance(mediator.NextLeaseEnd()!.Value - exchange.Clock.TrueNow);
        }

        var elapsed = exchange.Clock.TrueNow - start;
        output.WriteLine($"limit {limit}: the message that never opens was handed over as: {string.Join(", ", handed)}; queue empty after {elapsed} simulated; host calls for it: {exchange.Host.Calls.Labels.Count(l => l == "never")}");
        handed.Should().Equal("inconclusive", "conclusive");
        mediator.Count.Should().Be(0);
        elapsed.Should().Be(Lease + Qa1.Budget);
        exchange.Host.Calls.Labels.Count(l => l == "never").Should().Be(2);
    }

    // Sixteen anoncrypted messages that each fail on their own after fifteen seconds, then Alice's
    // message, which opens after six.
    private static (Qa1Exchange Exchange, Qa1Mediator Mediator) Setup()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.OpenAfter(OpensAfter) : Qa1Step.FailAfter(InFrontFailAfter), Lease);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: AtOnce, honestAt: AtOnce);
        return (exchange, mediator);
    }
}
