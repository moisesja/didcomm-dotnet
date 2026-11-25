using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT4 (#111): what <see cref="PickupItem.NotAttempted"/> says of a message that was started late in
/// its delivery and was still being opened when the delivery's budget ended, and what a host that
/// discards a failed item unless that flag is true (<see cref="Qa1Discard.WhatWasTried"/>, the host
/// of the cookbook's pickup section) then does with it. The set-up: as many messages as are opened
/// at once, each failing on its own after fifteen seconds, queued in front of one message that
/// takes six seconds to open, all in one fetch, at a mediator that leases for 60 s. The real
/// unpacker and its memory run on the manual clock; real time is only a hang guard.
/// </summary>
public sealed class NotAttemptedLateStartRt4Tests(ITestOutputHelper output)
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency; // sixteen
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s
    private static readonly TimeSpan InFrontFailAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OpensAfter = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Held: what the first fetch says of the message, and what the client's own memory says of it.
    /// It is started fifteen seconds into the budget, when the messages in front have failed, and is
    /// still being opened when the budget ends five seconds later: a failed item, "ran out while it
    /// was being opened", whose <see cref="PickupItem.NotAttempted"/> is false. The memory does not
    /// hold it back (its code started later than the start allowance), and a host that leaves it
    /// queued receives it opened by the first fetch after the lease, six seconds into that fetch.
    /// </summary>
    [Fact]
    public async Task A_message_started_late_and_still_being_opened_at_the_budgets_end_is_a_tried_item_that_is_not_remembered_and_opens_at_the_next_delivery_when_left_queued()
    {
        var (exchange, mediator) = Setup();

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit: 100, cap: TimeSpan.FromHours(1), discard: Qa1Discard.Nothing);

        var first = reach.Batches[0].Items.Should().ContainSingle(i => i.Id == "honest").Which;
        first.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        first.NotAttempted.Should().BeFalse("its host call was created");
        reach.Batches[0].Items.Where(i => i.Id != "honest").Should().HaveCount(AtOnce).And.OnlyContain(i => i.Error is DidComm.Exceptions.CryptoException, "the messages in front failed on their own");
        output.WriteLine($"left queued: returned opened: {reach.Found}, after {reach.Elapsed} simulated and {reach.Fetches} fetches; host calls for it: {exchange.Host.Calls.Labels.Count(l => l == "honest")}");
        reach.Found.Should().BeTrue("left queued, it is opened at the next delivery");
        reach.Elapsed.Should().Be(Lease + OpensAfter, "the first fetch after the lease starts it first");
        exchange.Host.Calls.Labels.Count(l => l == "honest").Should().Be(2, "it was not held back: its code ran again at the next delivery");
    }

    /// <summary>
    /// Reproduction (fails on a415f6d): the host that discards a failed item unless
    /// <see cref="PickupItem.NotAttempted"/> is true is expected to receive the message, as it does
    /// in <see cref="SlowSenderDiscardingHostRt3Tests"/> for a message that opens at once. Observed:
    /// the message is handed to it once, as a failed item whose flag is false, five seconds after
    /// its code started; the host discards it, and it is never returned opened.
    /// </summary>
    [Theory(Skip = "RT4: whether a host that discards only what was tried receives a message that takes six seconds to open, queued behind sixteen that fail after fifteen; observed: it is handed over once as tried, discarded, and never returned opened")]
    [InlineData(17)]
    [InlineData(100)]
    public async Task A_host_that_discards_only_what_was_tried_receives_a_message_that_takes_six_seconds_queued_behind_sixteen_that_fail_after_fifteen(int limit)
    {
        var (exchange, mediator) = Setup();

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(1), discard: Qa1Discard.WhatWasTried);

        var handed = reach.Batches.SelectMany(b => b.Items).Where(i => i.Id == "honest").ToArray();
        var returnedAs = string.Join(", ", handed.Select(i => i.Unpacked is not null ? "opened" : i.NotAttempted ? "a failed item, not attempted" : "a failed item, tried (" + i.Error!.GetType().Name + ")"));
        output.WriteLine($"limit {limit}: returned opened: {reach.Found}; {reach.Fetches} fetches in {reach.Elapsed} simulated; handed over as: {returnedAs}; still queued at the mediator: {mediator.Holds("honest")}; host calls for it: {exchange.Host.Calls.Labels.Count(l => l == "honest")}");
        reach.Found.Should().BeTrue("the message opens in six seconds when it is given them (handed over as: {0}; still queued at the mediator: {1})", returnedAs, mediator.Holds("honest"));
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
