using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Hosts that discard the failed items a fetch hands them (<see cref="PickupClient.AckAsync"/>), with
/// messages that never open queued in front of one that does (#111). The careful host discards a
/// failed item unless its <see cref="PickupItem.NotAttempted"/> is true
/// (<see cref="Qa1Discard.WhatWasTried"/>); what is checked for it: the message behind is returned
/// to it opened, every message handed to it as not attempted is tried in a later delivery, and its
/// queue empties. A host that does not read the flag and discards every failed item
/// (<see cref="Qa1Discard.EveryFailure"/>) is pinned for what it loses. The real unpacker and its
/// memory run on the manual clock; nothing sleeps, and real time is only a hang guard.
/// </summary>
public sealed class SlowSenderDiscardingHostRt3Tests(ITestOutputHelper output)
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency; // sixteen
    private const string MalloryKid = "did:example:mallory#key-x25519-1";
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s

    // ---- the careful host ------------------------------------------------------------------------

    /// <summary>
    /// The careful host with N messages that never open in front of one that does, at a mediator
    /// that leases for 60 s, for each fetch limit: the last message is returned to it opened, after
    /// the simulated time given, having been handed to it as not attempted the number of times
    /// given. At limits up to sixteen a delivery starts all of its messages, and no item is handed
    /// over as not attempted at all.
    /// </summary>
    [Theory]
    [InlineData(4, 10, 20, 0)]
    [InlineData(16, 10, 40, 0)]
    [InlineData(17, 10, 40, 0)]
    [InlineData(99, 10, 200, 0)]
    [InlineData(300, 10, 600, 0)]
    [InlineData(4, 16, 20, 0)]
    [InlineData(16, 16, 20, 0)]
    [InlineData(17, 16, 40, 0)]
    [InlineData(99, 16, 140, 0)]
    [InlineData(300, 16, 380, 0)]
    [InlineData(4, 100, 20, 0)]
    [InlineData(16, 100, 60, 1)]
    [InlineData(17, 100, 80, 1)]
    [InlineData(99, 100, 380, 6)]
    [InlineData(300, 100, 380, 5)]
    public async Task A_host_that_discards_only_what_was_tried_receives_the_message_behind_N_that_never_open(int inFront, int limit, int seconds, int timesNotAttempted)
    {
        var run = await RunAsync(inFront, limit, Qa1Discard.WhatWasTried);

        run.Reach.Found.Should().BeTrue("the message behind the {0} is returned opened at limit {1} (still queued at the mediator: {2}; handed to host code: {3}; returned as: {4})",
            inFront, limit, run.StillQueued, run.HandedToHostCode, run.ReturnedAs);
        run.Reach.Elapsed.Should().Be(TimeSpan.FromSeconds(seconds));
        run.TimesNotAttempted.Should().Be(timesNotAttempted, "how often the message behind was handed over as not attempted before it was opened");
        if (limit <= AtOnce)
            run.NotAttemptedItems.Should().Be(0, "a delivery of at most {0} messages starts them all", AtOnce);
        run.StillQueued.Should().BeFalse("it was acknowledged when it opened");
    }

    /// <summary>
    /// One fewer message that never opens than are opened at once, in front of one that does, in
    /// one fetch: every message of the delivery gets a worker, so the last one is returned opened in
    /// the first fetch, whatever the host does with the failures.
    /// </summary>
    [Theory]
    [InlineData(Qa1Discard.Nothing)]
    [InlineData(Qa1Discard.WhatWasTried)]
    [InlineData(Qa1Discard.EveryFailure)]
    public async Task Behind_fifteen_that_never_open_in_one_fetch_the_message_is_returned_opened_in_the_first_fetch_whatever_the_host_discards(Qa1Discard discard)
    {
        var run = await RunAsync(inFront: AtOnce - 1, limit: AtOnce, discard);

        run.Reach.Found.Should().BeTrue("every message of the delivery had a worker");
        run.Reach.Fetches.Should().Be(1);
        run.NotAttemptedItems.Should().Be(0);
    }

    /// <summary>
    /// The careful host with nothing but its fetch loop: N messages that never open and one that
    /// does, last. Every message handed to it as not attempted is handed to it again later as tried
    /// (or opened), and its queue is empty after the simulated time given; the most times one
    /// message was handed over as not attempted is the number given.
    /// </summary>
    [Theory]
    [InlineData(99, 10, 200, 0)]
    [InlineData(99, 16, 140, 0)]
    [InlineData(17, 100, 80, 1)]
    [InlineData(99, 100, 380, 6)]
    [InlineData(300, 100, 420, 6)]
    public async Task A_host_that_discards_only_what_was_tried_empties_its_queue_and_every_message_not_attempted_is_tried_later(int inFront, int limit, int seconds, int mostTimesNotAttempted)
    {
        using var floor = new PoolFloor(parked: 2);
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Lease);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: inFront, honestAt: inFront);
        var client = exchange.Client();
        var start = exchange.Clock.TrueNow;
        var notAttempted = new Dictionary<string, int>();
        var pending = new HashSet<string>();
        var fetches = 0;

        while (mediator.Count > 0 && exchange.Clock.TrueNow - start < TimeSpan.FromHours(2))
        {
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit));
            fetches++;
            foreach (var item in batch.Items)
            {
                if (item.NotAttempted)
                {
                    notAttempted[item.Id] = notAttempted.GetValueOrDefault(item.Id) + 1;
                    pending.Add(item.Id);
                }
                else
                {
                    pending.Remove(item.Id); // opened, or tried and failed
                }
            }
            Qa1.LookupsEnded(exchange.Unpacker);
            if (Qa1.ToDiscard(batch.Items, Qa1Discard.WhatWasTried) is { Length: > 0 } tried)
                await client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, tried).WaitAsync(Qa1Exchange.Guard);
            if (batch.Drained && mediator.Count > 0)
                exchange.Clock.Inner.Advance(mediator.NextLeaseEnd()!.Value - exchange.Clock.TrueNow);
        }

        var elapsed = exchange.Clock.TrueNow - start;
        var most = notAttempted.Count == 0 ? 0 : notAttempted.Values.Max();
        output.WriteLine($"{inFront} in front, limit {limit}: queue empty after {elapsed} simulated and {fetches} fetches; {notAttempted.Count} messages were handed over as not attempted, one of them at most {most} times; {exchange.Host.Calls.Count} host calls");
        mediator.Count.Should().Be(0, "every message was tried: the one that opens was acknowledged, the others discarded");
        pending.Should().BeEmpty("every message handed over as not attempted was handed over again later as tried");
        exchange.Host.Calls.Count.Should().Be(inFront + 1, "each message was handed to host code once");
        elapsed.Should().Be(TimeSpan.FromSeconds(seconds));
        most.Should().Be(mostTimesNotAttempted);
    }

    // ---- a host that does not read the flag --------------------------------------------------------

    /// <summary>
    /// Why the documentation says not to discard an item whose
    /// <see cref="PickupItem.NotAttempted"/> is true: a host that discards every failed item, with
    /// sixteen or 99 messages that never open in front of one that does, in one fetch of 100. The
    /// last message is handed to it once, as a failed item that was not attempted; the host's
    /// discard removes it from the mediator's queue; it is never handed to host code and never
    /// returned opened.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(99)]
    public async Task A_host_that_discards_every_failure_also_discards_the_message_that_was_not_attempted(int inFront)
    {
        var run = await RunAsync(inFront, limit: 100, Qa1Discard.EveryFailure);

        run.Reach.Found.Should().BeFalse("its only delivery ended before its turn, and the host discarded it");
        run.ReturnedAs.Should().Be("a failed item, not attempted");
        run.StillQueued.Should().BeFalse("the host's discard removed it");
        run.HandedToHostCode.Should().BeFalse("it was never tried");
    }

    // ---- refused for capacity: the known case -------------------------------------------------------

    /// <summary>
    /// A message refused for its sender's bound, at a mediator that does not set delivered messages
    /// aside: sixteen messages of one sender whose lookups ignore their token are opened and end at
    /// their budget, still holding that sender's sixteen reservations, and a seventeenth message of
    /// the same sender, which would open, is queued behind them. While those lookups have not ended,
    /// every delivery hands the seventeenth over as not attempted, without calling host code for it.
    /// The careful host leaves it queued, and the first delivery after the lookups end returns it
    /// opened. A host that discards every failed item has deleted it by then.
    /// </summary>
    [Theory]
    [InlineData(Qa1Discard.WhatWasTried, true)]
    [InlineData(Qa1Discard.EveryFailure, false)]
    public async Task A_message_refused_for_its_senders_bound_is_not_attempted_until_that_senders_lookups_end_and_only_a_host_that_left_it_queued_receives_it(Qa1Discard discard, bool received)
    {
        using var floor = new PoolFloor(parked: 2);
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "target" ? Qa1Step.Open : new Qa1Step(Qa1Kind.IgnoresToken), lease: null);
        for (var i = 0; i < UnpackReservations.MaxPerSender; i++)
            mediator.Enqueue($"held-{i}", Qa1.From(MalloryKid, $"held-{i}"));
        mediator.Enqueue("target", Qa1.From(MalloryKid, "target"));
        var client = exchange.Client();

        async Task<PickupBatch> FetchAndDiscardAsync()
        {
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: AtOnce));
            if (Qa1.ToDiscard(batch.Items, discard) is { Length: > 0 } ids)
                await client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, ids).WaitAsync(Qa1Exchange.Guard);
            return batch;
        }

        try
        {
            var first = await FetchAndDiscardAsync();
            first.Items.Should().HaveCount(UnpackReservations.MaxPerSender).And.OnlyContain(i => i.Error is TimeoutException && !i.NotAttempted, "each was being opened when the budget ended");
            exchange.Unpacker.Reserved.Should().Be(UnpackReservations.MaxPerSender, "the lookups ignore their token and keep their reservations");

            // Delivery after delivery, while the lookups have not ended.
            var deliveries = discard == Qa1Discard.WhatWasTried ? 3 : 1;
            for (var d = 0; d < deliveries; d++)
            {
                var refused = await FetchAndDiscardAsync();
                var item = refused.Items.Should().ContainSingle().Which;
                item.Id.Should().Be("target");
                item.NotAttempted.Should().BeTrue("it was refused for its sender's bound (delivery {0})", d + 2);
                item.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted");
            }
            exchange.Host.Calls.Labels.Should().NotContain("target");
            mediator.Holds("target").Should().Be(received, "only the host that left it queued still has it");
        }
        finally
        {
            exchange.Host.ReleaseIgnored();
        }
        Qa1.LookupsEnded(exchange.Unpacker);

        var after = await FetchAndDiscardAsync();

        if (received)
        {
            after.Items.Should().ContainSingle(i => i.Id == "target").Which.Unpacked.Should().NotBeNull("the lookups that were in its way have ended");
            exchange.Wire.AcknowledgedIds.Should().Contain("target");
        }
        else
        {
            after.Drained.Should().BeTrue("the host's discard had removed it");
            exchange.Host.Calls.Labels.Should().NotContain("target");
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<Run> RunAsync(int inFront, int limit, Qa1Discard discard)
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Lease);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: inFront, honestAt: inFront);

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(1), discard: discard);

        var items = reach.Batches.SelectMany(b => b.Items).ToArray();
        var honest = items.Where(i => i.Id == "honest").ToArray();
        var returnedAs = string.Join(", ", honest.Select(i => i.Unpacked is not null ? "opened" : i.NotAttempted ? "a failed item, not attempted" : "a failed item, " + i.Error!.GetType().Name));
        var run = new Run(reach, mediator.Holds("honest"), exchange.Host.Calls.Labels.Contains("honest"), returnedAs.Length == 0 ? "nothing" : returnedAs,
            honest.Count(i => i.NotAttempted), items.Count(i => i.NotAttempted));
        output.WriteLine($"{inFront} in front, limit {limit}, the host discards {discard}: returned opened: {reach.Found}; {reach.Fetches} fetches in {reach.Elapsed} simulated; handed over as not attempted: {run.TimesNotAttempted} times ({run.NotAttemptedItems} items of all messages); still queued at the mediator: {run.StillQueued}; handed to host code: {run.HandedToHostCode}; returned as: {run.ReturnedAs}");
        return run;
    }

    private sealed record Run(SlowSenderFetchQa1Tests.Reach Reach, bool StillQueued, bool HandedToHostCode, string ReturnedAs, int TimesNotAttempted, int NotAttemptedItems);
}
