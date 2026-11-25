using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-1 for #111: the memory of messages slow to open, seen through <see cref="PickupClient.FetchAsync"/>.
/// What is acknowledged (Message Pickup 3.0 and 4.0), a mediator that does not lease, the
/// <c>recipient_did</c> filter, a restart, several mediators and mailboxes on one client, a mediator
/// whose attachment ids repeat, move or collide with another mediator's, ids that differ only in
/// case, and host clocks that step, fall or throw. Every timing is on a manual clock.
/// </summary>
public sealed class SlowMemoryFetchQa1Tests
{
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private static readonly TimeSpan B = Qa1.Budget;
    private static readonly TimeSpan T0 = 30 * Qa1.Budget;

    // ---- acknowledgement (3.0 L177, 4.0 L268) ------------------------------------------------------

    /// <summary>
    /// A held-back message is never in a <c>messages-received</c>; a fetch made only of held-back
    /// messages sends none, makes no host call, arms no timer and returns at once; a fetch of
    /// held-back messages and an honest one acknowledges the honest one only. Under Message Pickup
    /// 3.0 and 4.0, at a mediator that does not lease.
    /// </summary>
    [Theory]
    [InlineData(MessagePickupVersion.V3)]
    [InlineData(MessagePickupVersion.V4)]
    public async Task A_held_back_message_is_never_acknowledged_and_a_fetch_of_only_held_back_messages_sends_nothing_and_returns_at_once(MessagePickupVersion version)
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease: null, configure: o => o.MessagePickupVersion = version);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: 4, honestAt: 4);
        var client = exchange.Client();
        await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4));
        var calls = exchange.Host.Calls.Count;
        var sent = exchange.Wire.Sent.Count;

        var onlyHeld = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4);

        onlyHeld.IsCompleted.Should().BeTrue("a fetch made only of held-back messages needs no clock");
        (await onlyHeld).Items.Should().HaveCount(4).And.OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("did not open when it was delivered before"));
        exchange.Wire.Sent.Count.Should().Be(sent + 1, "only the delivery-request was sent: no messages-received");
        exchange.Host.Calls.Count.Should().Be(calls);
        exchange.Clock.Scheduled.Should().Be(0);

        var mixed = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 5));

        mixed.Items.Single(i => i.Id == "honest").Unpacked.Should().NotBeNull();
        exchange.Wire.AcknowledgedIds.Should().Equal("honest");
        mediator.Count.Should().Be(4, "the held-back messages stay queued");
    }

    // ---- a mediator that does not lease (R-6) --------------------------------------------------------

    /// <summary>
    /// R-6: at a mediator that does not lease, a fetch of limit 4 meets the same four never-opening
    /// messages every time. After the first fetch learns them, each repeat fetch is instant, with no
    /// host call; the message behind them is reached once the host discards them with
    /// <see cref="PickupClient.AckAsync"/>.
    /// </summary>
    [Fact]
    public async Task At_a_mediator_that_does_not_lease_repeat_fetches_of_remembered_messages_are_instant_with_no_host_call()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease: null);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: 4, honestAt: 4);
        var client = exchange.Client();
        var start = exchange.Clock.TrueNow;
        await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4));
        (exchange.Clock.TrueNow - start).Should().Be(B);

        for (var f = 0; f < 50; f++)
        {
            var repeat = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4);
            repeat.IsCompleted.Should().BeTrue();
            (await repeat).Items.Should().OnlyContain(i => i.Error is TimeoutException);
        }

        exchange.Host.Calls.Count.Should().Be(4, "no repeat fetch opened anything");
        (exchange.Clock.TrueNow - start).Should().Be(B, "no repeat fetch took any time");
        await client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, Enumerable.Range(0, 4).Select(i => "junk-" + i).ToArray());
        var after = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4));
        after.Items.Single().Unpacked.Should().NotBeNull();
    }

    // ---- the recipient_did filter ------------------------------------------------------------------

    /// <summary>
    /// Under a <c>recipient_did</c> filter, a held-back message is a failure and is not acknowledged;
    /// the honest message in the same fetch is.
    /// </summary>
    [Fact]
    public async Task Under_a_recipient_filter_a_held_back_message_fails_at_once_and_is_not_acknowledged()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease: null);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: 1, honestAt: 1);
        var client = exchange.Client();
        await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 1, recipientDid: FakePickupExchange.L));

        var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 2, recipientDid: FakePickupExchange.L));

        batch.Items[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("did not open when it was delivered before");
        batch.Items[1].Unpacked.Should().NotBeNull();
        exchange.Wire.AcknowledgedIds.Should().Equal("honest");
    }

    /// <summary>
    /// R-7: a message for another of the agent's DIDs, delivered under a filter, whose sender's
    /// lookup takes 3 s. It opens, so it is never remembered; it is never acknowledged; so each
    /// fetch opens it again and pays its 3 s.
    /// </summary>
    [Fact]
    public async Task A_slow_message_for_another_of_my_dids_under_a_filter_is_opened_again_at_every_fetch_and_never_remembered()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(_ => Qa1Step.OpenAfter(TimeSpan.FromSeconds(3)), lease: null, recipientOf: _ => "did:example:carol");
        mediator.Enqueue("for-carol", Qa1.From(AliceKid, "for-carol"));
        var client = exchange.Client();
        var start = exchange.Clock.TrueNow;

        for (var f = 0; f < 3; f++)
        {
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 1, recipientDid: FakePickupExchange.L));
            batch.Items.Single().Error!.Message.Should().Contain("for another of your DIDs");
        }

        exchange.Host.Calls.Count.Should().Be(3);
        (exchange.Clock.TrueNow - start).Should().Be(TimeSpan.FromSeconds(9));
        exchange.Unpacker.Memory.Count.Should().Be(0);
        exchange.Wire.AcknowledgedIds.Should().BeEmpty();
    }

    // ---- a restart (R-5) ----------------------------------------------------------------------------

    /// <summary>
    /// R-5: a new client in a new process remembers nothing. Sixteen never-opening messages are
    /// learned and the message behind them taken; then another message arrives behind them. After a
    /// restart, reaching it costs exactly one more opening of each of the sixteen (and its own), and
    /// stays within the bound; without the restart it costs no opening of them.
    /// </summary>
    [Fact]
    public async Task After_a_restart_each_remembered_message_costs_exactly_one_more_opening_and_nothing_else()
    {
        var withRestart = await SecondReachAsync(restart: true);
        var without = await SecondReachAsync(restart: false);

        withRestart.Calls.Should().Be(16 + 1, "each remembered message is opened once more, and the new message once");
        withRestart.Labels.Where(l => l.StartsWith("junk-", StringComparison.Ordinal)).Should().OnlyHaveUniqueItems();
        withRestart.Reach.Elapsed.Should().BeLessThanOrEqualTo(SlowSenderFetchQa1Tests.Bound(16, 4));
        withRestart.Refusals.Should().Be(0);
        without.Calls.Should().Be(1, "the control: the remembered messages are held back");
        without.Reach.Elapsed.Should().Be(TimeSpan.Zero);
    }

    private static async Task<(SlowSenderFetchQa1Tests.Reach Reach, int Calls, IReadOnlyList<string> Labels, int Refusals)> SecondReachAsync(bool restart)
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label.StartsWith("honest", StringComparison.Ordinal) ? Qa1Step.Open : Qa1Step.Stall, Qa1.Lease);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: 16, honestAt: 16);
        (await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit: 4, cap: TimeSpan.FromHours(1))).Found.Should().BeTrue();
        mediator.Enqueue("honest-2", Qa1.From(AliceKid, "honest-2"));
        if (mediator.NextLeaseEnd() is { } end)
            exchange.Clock.Inner.Advance(end - exchange.Clock.TrueNow + Qa1.Lease); // every lease has ended
        if (restart)
            exchange.Restart();
        var before = exchange.Host.Calls.Count;

        var reach = await SlowSenderFetchQa1Tests.ReachAsync(exchange, mediator, limit: 4, cap: TimeSpan.FromHours(1), honest: "honest-2");

        reach.Found.Should().BeTrue();
        var labels = exchange.Host.Calls.Labels.Skip(before).ToArray();
        var refusals = reach.Batches.SelectMany(b => b.Items).Count(i => i.Error is TimeoutException t && t.Message.Contains("capacity exhausted"));
        return (reach, labels.Length, labels, refusals);
    }

    // ---- several mediators and mailboxes on one client -----------------------------------------------

    /// <summary>
    /// One client, three mediators, ten mailboxes each, sixteen never-opening messages in front of an
    /// honest one in every mailbox: every loop reaches its honest message within the bound, every
    /// pair keeps its own partition, and nothing learned in one pair holds back anything in another.
    /// </summary>
    [Fact]
    public async Task Many_mailboxes_at_many_mediators_on_one_client_each_reach_their_message_within_the_bound()
    {
        var clock = new Qa1Clock();
        var exchange = new Qa1Exchange(clock, new Qa1Host(clock, label => label.StartsWith("honest", StringComparison.Ordinal) ? Qa1Step.Open : Qa1Step.Stall));
        var mediators = Enumerable.Range(0, 3).Select(m => new Qa1Mediator(clock, Qa1.Lease, $"did:example:mediator-{m}")).ToArray();
        Qa1Mediator.InstallAll(exchange.Wire, mediators);
        var mailboxes = Enumerable.Range(0, 10).Select(b => $"did:example:local-{b}").ToArray();
        foreach (var mediator in mediators)
        {
            foreach (var mailbox in mailboxes)
            {
                // The same attachment ids in every pair: they are another message in each.
                for (var i = 0; i < 16; i++)
                    mediator.Enqueue("junk-" + i, Qa1.Anon("junk-" + i), mailbox);
                mediator.Enqueue("honest", Qa1.From(AliceKid, $"honest-{mediator.Did}-{mailbox}"), mailbox);
            }
        }
        var client = exchange.Client();

        foreach (var mediator in mediators)
        {
            foreach (var mailbox in mailboxes)
            {
                var start = clock.TrueNow;
                var calls = exchange.Host.Calls.Count;
                var found = false;
                var last = "";
                for (var f = 0; f < 200 && !found; f++)
                {
                    var batch = await exchange.DriveAsync(() => client.FetchAsync(mediator.Did, mailbox, limit: 4));
                    found = batch.Items.Any(i => i.Id == "honest" && i.Unpacked != null);
                    last = string.Join(",", batch.Items.Select(i => i.Id + ":" + (i.Error?.GetType().Name ?? "ok"))) + " drained " + batch.Drained;
                    if (!found && batch.Drained)
                        clock.Inner.Advance(mediator.NextLeaseEnd()!.Value - clock.TrueNow);
                }
                found.Should().BeTrue("{0} at {1}: last {2}", mailbox, mediator.Did, last);
                (clock.TrueNow - start).Should().BeLessThanOrEqualTo(SlowSenderFetchQa1Tests.Bound(16, 4), "{0} at {1}", mailbox, mediator.Did);
                (exchange.Host.Calls.Count - calls).Should().Be(17, "each of its sixteen is opened once, none is held back by another pair's entry");
            }
        }

        exchange.Unpacker.Memory.Partitions.Should().Be(30);
        exchange.Unpacker.Memory.Count.Should().Be(30 * 16);
    }

    // ---- a mediator whose ids repeat, move or collide -----------------------------------------------

    /// <summary>
    /// Another mediator's ids: mediator X delivers sixteen never-opening messages under the ids
    /// m-0..m-15, all of which start in one fetch of 16 and are remembered; mediator Y then delivers Bob's honest messages under the same ids. Y's messages are
    /// never held back, and X's entries still hold X's messages back.
    /// </summary>
    [Fact]
    public async Task A_mediator_reusing_another_mediators_ids_never_holds_back_that_mediators_honest_messages()
    {
        var clock = new Qa1Clock();
        var exchange = new Qa1Exchange(clock, new Qa1Host(clock, label => label.StartsWith("x-", StringComparison.Ordinal) ? Qa1Step.Stall : Qa1Step.Open));
        var x = new Qa1Mediator(clock, lease: null, "did:example:mediator-x");
        var y = new Qa1Mediator(clock, lease: null, "did:example:mediator-y");
        Qa1Mediator.InstallAll(exchange.Wire, x, y);
        for (var i = 0; i < 16; i++)
        {
            x.Enqueue("m-" + i, Qa1.Anon("x-" + i));
            y.Enqueue("m-" + i, Qa1.From(AliceKid, "y-" + i));
        }
        var client = exchange.Client();
        await exchange.DriveAsync(() => client.FetchAsync(x.Did, FakePickupExchange.L, limit: 16));
        exchange.Unpacker.Memory.Count.Should().Be(16);

        var fromY = await exchange.DriveAsync(() => client.FetchAsync(y.Did, FakePickupExchange.L, limit: 16));
        var fromX = client.FetchAsync(x.Did, FakePickupExchange.L, limit: 16);

        fromY.Items.Should().HaveCount(16).And.OnlyContain(i => i.Unpacked != null, "an id is unique only to its mediator");
        fromX.IsCompleted.Should().BeTrue();
        (await fromX).Items.Should().OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("did not open when it was delivered before"));
        exchange.Deliveries.Select(d => d.Scope.MediatorDid).Distinct().Should().BeEquivalentTo(new[] { x.Did, y.Did });
    }

    /// <summary>
    /// A mediator that gives the same message a new attachment id at every delivery: each delivery
    /// is a new entry, in that mediator's partition only. Its partition never holds more than 256
    /// entries, and the entries of another mediator for the same mailbox all survive.
    /// </summary>
    [Fact]
    public async Task A_mediator_that_renames_its_messages_at_every_delivery_fills_only_its_own_partition()
    {
        var clock = new Qa1Clock();
        var exchange = new Qa1Exchange(clock, new Qa1Host(clock, _ => Qa1Step.FailAfter(TimeSpan.FromSeconds(1))));
        var memory = exchange.Unpacker.Memory;
        var honestScope = new DeliveryScope("did:example:mediator-y", FakePickupExchange.L);
        var kept = Enumerable.Range(0, 200).Select(i => (string?)("y-" + i)).ToArray();
        foreach (var id in kept)
            memory.Strike(honestScope, id);
        var renaming = new Qa1Mediator(clock, lease: null, "did:example:mediator-x");
        renaming.Install(exchange.Wire);
        var client = exchange.Client();

        for (var round = 0; round < 100; round++)
        {
            // The same four messages, under ids it has never used before.
            for (var i = 0; i < 4; i++)
                renaming.Enqueue($"r{round}-{i}", Qa1.Anon("same-bytes-" + i));
            await exchange.DriveAsync(() => client.FetchAsync(renaming.Did, FakePickupExchange.L, limit: 4));
            await client.AckAsync(renaming.Did, FakePickupExchange.L, Enumerable.Range(0, 4).Select(i => $"r{round}-{i}").ToArray());
        }

        exchange.Host.Calls.Count.Should().Be(400, "a new id is a new entry: each delivery opens its messages");
        memory.Count.Should().Be(SlowMemory.EntriesPerPartition + kept.Length);
        memory.HeldBack(honestScope, kept).Should().OnlyContain(h => h, "the other mediator's entries all survive");
    }

    /// <summary>
    /// A mediator that delivers the remembered messages in another order, interleaved with an
    /// honest message: each is held back wherever it stands, and the honest message opens.
    /// </summary>
    [Fact]
    public async Task Remembered_messages_delivered_in_another_order_are_held_back_wherever_they_stand()
    {
        var clock = new Qa1Clock();
        var exchange = new Qa1Exchange(clock, new Qa1Host(clock, label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall));
        var first = new Qa1Mediator(clock, lease: null);
        first.Install(exchange.Wire);
        for (var i = 0; i < 16; i++)
            first.Enqueue("junk-" + i, Qa1.Anon("junk-" + i));
        var client = exchange.Client();
        for (var f = 0; f < 4; f++)
        {
            await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4));
            await client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, Enumerable.Range(4 * f, 4).Select(i => "junk-" + i).ToArray());
        }
        var reordered = new Qa1Mediator(clock, lease: null);
        reordered.Install(exchange.Wire);
        var order = Enumerable.Range(0, 16).Reverse().ToList();
        order.Insert(7, -1);
        foreach (var i in order)
            reordered.Enqueue(i < 0 ? "honest" : "junk-" + i, i < 0 ? Qa1.From(AliceKid, "honest") : Qa1.Anon("junk-" + i));
        var calls = exchange.Host.Calls.Count;
        var start = exchange.Clock.TrueNow;

        var items = (await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 17))).Items;

        (exchange.Clock.TrueNow - start).Should().Be(TimeSpan.Zero, "only the honest message is opened, and it opens at once");
        items.Single(i => i.Id == "honest").Unpacked.Should().NotBeNull();
        items.Where(i => i.Id != "honest").Should().HaveCount(16).And.OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("did not open when it was delivered before"));
        (exchange.Host.Calls.Count - calls).Should().Be(1);
    }

    /// <summary>
    /// D-8 on the fetch path: a never-opening message under the id "Msg-1" is remembered; an honest
    /// message under "msg-1" or "MSG-1" from the same mediator is another message and opens.
    /// </summary>
    [Fact]
    public async Task Attachment_ids_that_differ_only_in_case_are_different_messages_on_the_fetch_path()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label.StartsWith("honest", StringComparison.Ordinal) ? Qa1Step.Open : Qa1Step.Stall, lease: null);
        mediator.Enqueue("Msg-1", Qa1.Anon("junk"));
        var client = exchange.Client();
        await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 1));
        await client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, new[] { "Msg-1" });
        mediator.Enqueue("msg-1", Qa1.From(AliceKid, "honest-1"));
        mediator.Enqueue("MSG-1", Qa1.From(AliceKid, "honest-2"));
        mediator.Enqueue("Msg-1", Qa1.Anon("junk"));

        var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 3));

        batch.Items[0].Unpacked.Should().NotBeNull();
        batch.Items[1].Unpacked.Should().NotBeNull();
        batch.Items[2].Error!.Message.Should().Contain("did not open when it was delivered before", "the exact id is remembered");
    }

    /// <summary>
    /// Ids that cannot be remembered never reach the unpacker on the fetch path: an attachment
    /// without an id, or with an empty one, is unusable and not opened; the message with a usable id
    /// in the same delivery is opened and remembered as usual.
    /// </summary>
    [Fact]
    public async Task An_attachment_with_no_id_or_an_empty_id_is_never_opened_or_remembered()
    {
        var (exchange, _) = SlowSenderFetchQa1Tests.Setup(_ => Qa1Step.Stall, lease: null);
        exchange.Wire.On(MessagePickup.DeliveryRequestType, r =>
        {
            var noId = FakePickupExchange.RawEntry("x", Qa1.Anon("no-id"));
            noId.Id = null;
            var emptyId = FakePickupExchange.RawEntry("x", Qa1.Anon("empty-id"));
            emptyId.Id = "";
            return FakePickupExchange.RawDelivery(r, new[] { noId, emptyId, FakePickupExchange.RawEntry("usable", Qa1.Anon("usable")) });
        });

        await exchange.DriveAsync(() => exchange.Client().FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 3));

        exchange.Host.Calls.Labels.Should().Equal("usable");
        exchange.Deliveries.Single().Ids.Should().Equal("usable");
        exchange.Unpacker.Memory.Count.Should().Be(1);
    }

    /// <summary>
    /// A mediator that repeats an attachment id inside one delivery: the message is handed to the
    /// unpacker once (the first wins), so one delivery is one strike and its pause is the first one,
    /// not a doubled one.
    /// </summary>
    [Fact]
    public async Task An_id_repeated_inside_one_delivery_is_opened_and_struck_once()
    {
        var (exchange, _) = SlowSenderFetchQa1Tests.Setup(_ => Qa1Step.Stall, lease: null);
        exchange.Wire.On(MessagePickup.DeliveryRequestType, r => FakePickupExchange.RawDelivery(r, new[]
        {
            FakePickupExchange.RawEntry("twice", Qa1.Anon("copy-1")),
            FakePickupExchange.RawEntry("twice", Qa1.Anon("copy-2")),
        }));
        var client = exchange.Client();

        await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 2));

        exchange.Host.Calls.Labels.Should().Equal("copy-1");
        exchange.Clock.Inner.Advance(T0);
        var calls = exchange.Host.Calls.Count;
        await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 2));
        exchange.Host.Calls.Count.Should().Be(calls + 1, "its pause was the first one, so it is due after 10 minutes");
    }

    /// <summary>
    /// I-9 on the fetch path: the caller cancels a fetch 10 s into its delivery, while four messages
    /// run. The fetch ends cancelled, nothing is acknowledged, and none of the four is remembered,
    /// so the next fetch opens them again.
    /// </summary>
    [Fact]
    public async Task A_fetch_the_caller_cancels_while_messages_run_remembers_none_of_them_and_acknowledges_nothing()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(_ => Qa1Step.Stall, lease: null);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: 3, honestAt: 3);
        exchange.Host.Calls.Count.Should().Be(0);
        var client = exchange.Client();
        using var cancel = new CancellationTokenSource();

        var fetch = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4, ct: cancel.Token);
        await exchange.Host.Calls.WaitForAsync(4, Qa1Exchange.Guard);
        exchange.Clock.Inner.Advance(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        var ended = await Record.ExceptionAsync(() => fetch.WaitAsync(Qa1Exchange.Guard));

        ended.Should().BeAssignableTo<OperationCanceledException>();
        exchange.Unpacker.Memory.Count.Should().Be(0);
        exchange.Wire.AcknowledgedIds.Should().BeEmpty();
        var again = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4);
        await exchange.Host.Calls.WaitForAsync(8, Qa1Exchange.Guard);
        exchange.Clock.Inner.Advance(B);
        await again.WaitAsync(Qa1Exchange.Guard);
    }

    // ---- clocks (L-060, I-12) -----------------------------------------------------------------------

    /// <summary>
    /// I-12 through fetches: between a strike and the redelivery the host's clock steps forward two
    /// hours (the message is due and opened again), then back two hours (a reading below the strike
    /// is due: opened again), then runs normally: the message is held back exactly for its pause
    /// from the last strike, never longer, and never locked out.
    /// </summary>
    [Fact]
    public async Task A_clock_stepping_forward_and_back_never_holds_a_message_past_its_pause_or_locks_it_out()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(_ => Qa1Step.Stall, lease: null);
        mediator.Enqueue("junk-0", Qa1.Anon("junk-0"));
        var client = exchange.Client();
        var memory = exchange.Unpacker.Memory;
        async Task<bool> OpenedAsync()
        {
            var calls = exchange.Host.Calls.Count;
            await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 1));
            return exchange.Host.Calls.Count > calls;
        }

        (await OpenedAsync()).Should().BeTrue("first delivery");
        (await OpenedAsync()).Should().BeFalse("held back: pause of 10 min");
        exchange.Clock.Step(TimeSpan.FromHours(2));
        (await OpenedAsync()).Should().BeTrue("the reading is two hours on: due (second strike, pause 20 min)");
        exchange.Clock.Step(-TimeSpan.FromHours(2));
        (await OpenedAsync()).Should().BeTrue("a reading below the strike's is due (third strike, pause 40 min, stamped on the stepped-back clock)");
        (await OpenedAsync()).Should().BeFalse("held back from its new strike");
        exchange.Clock.Inner.Advance(memory.PauseAfter(3) - TimeSpan.FromTicks(1));
        (await OpenedAsync()).Should().BeFalse("one tick before its pause ends, counted from the last strike");
        exchange.Clock.Inner.Advance(TimeSpan.FromTicks(1));
        (await OpenedAsync()).Should().BeTrue("its pause has ended");
    }

    /// <summary>
    /// I-12 through fetches: a host clock whose readings keep going lower. Nothing is ever remembered
    /// (no message's start reads at or after its budget's), so every fetch opens the message again:
    /// the memory fails open, and every delivery still ends at its budget.
    /// </summary>
    [Fact]
    public async Task A_clock_whose_readings_keep_going_lower_never_holds_a_message_back()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(_ => Qa1Step.Stall, lease: null);
        mediator.Enqueue("junk-0", Qa1.Anon("junk-0"));
        exchange.Unpacker.Memory.Strike(Qa1.Bob, "junk-0");
        exchange.Clock.Falling = true;
        var client = exchange.Client();
        var start = exchange.Clock.TrueNow;

        for (var f = 0; f < 5; f++)
            await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 1));

        exchange.Host.Calls.Count.Should().Be(5, "a reading below the strike's is due, and nothing new is struck");
        (exchange.Clock.TrueNow - start).Should().Be(5 * B);
    }

    /// <summary>
    /// A host clock that throws: a fetch fails with the clock's exception and acknowledges nothing;
    /// nothing is remembered from it. Once the clock reads again, the message is opened (never held
    /// back on a reading that failed).
    /// </summary>
    [Fact]
    public async Task A_clock_that_throws_remembers_nothing_and_locks_nothing_out()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease: null);
        SlowSenderFetchQa1Tests.Queue(mediator, junk: 1, honestAt: 1);
        var client = exchange.Client();
        exchange.Clock.Throws = true;

        var failed = await Record.ExceptionAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 2));

        failed.Should().BeOfType<InvalidOperationException>();
        exchange.Wire.AcknowledgedIds.Should().BeEmpty();
        exchange.Unpacker.Memory.Count.Should().Be(0);
        exchange.Clock.Throws = false;
        var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 2));
        batch.Items[1].Unpacked.Should().NotBeNull();
        exchange.Host.Calls.Labels.Should().Contain("junk-0");
    }

    /// <summary>
    /// A host clock that starts throwing while a delivery is being opened, at the moment a message's
    /// failure is read: the delivery still ends at its budget, every message has an outcome, nothing
    /// is remembered from a failed reading, and the next fetch with a working clock opens the
    /// messages again.
    /// </summary>
    [Fact]
    public async Task A_clock_that_throws_while_a_delivery_is_opened_ends_it_at_its_budget_and_strikes_nothing()
    {
        var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(label => label.StartsWith("honest", StringComparison.Ordinal) ? Qa1Step.Open : Qa1Step.FailAfter(TimeSpan.FromSeconds(1)), lease: null);
        for (var i = 0; i < 4; i++)
            mediator.Enqueue("slow-" + i, Qa1.Anon("slow-" + i));
        mediator.Enqueue("honest", Qa1.From(AliceKid, "honest"));
        var client = exchange.Client();

        var fetch = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 5);
        await exchange.Host.Calls.WaitForAsync(4, Qa1Exchange.Guard);
        exchange.Clock.Throws = true;
        exchange.Clock.Inner.Advance(TimeSpan.FromSeconds(1));
        exchange.Clock.Inner.Advance(B);
        var batch = await fetch.WaitAsync(Qa1Exchange.Guard);
        exchange.Clock.Throws = false;

        batch.Items.Should().HaveCount(5).And.OnlyContain(i => i.Error != null);
        exchange.Unpacker.Memory.Count.Should().Be(0);
        var again = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 5));
        again.Items.Single(i => i.Id == "honest").Unpacked.Should().NotBeNull();
    }
}
