using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-1 for #111 on the fetch path: <see cref="PickupClient.FetchAsync"/> through a real unpacker on
/// a manual clock, against a Message Pickup mediator that leases what it delivers (or does not). The
/// findings the design settles, replayed through a fetch; where the message behind never-opening
/// messages is reached, by limit, by how many are in front and by lease; deliveries that mix honest
/// and failing messages; and an honest sender whose lookups are slow while other messages name its
/// key. The clock moves only to the next due timer, once every worker is parked (L-065).
/// </summary>
public sealed class SlowSenderFetchQa1Tests
{
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const string SamKid = "did:example:sam#key-x25519-1";
    private const string MalloryKid = "did:example:mallory#key-x25519-1";
    private static readonly TimeSpan B = Qa1.Budget;

    // ---- the findings, replayed through a fetch --------------------------------------------------

    /// <summary>
    /// ADV2-1 through a fetch: ten messages whose lookups never end, ahead of an honest one, at a
    /// mediator that leases for 60 s. The fetch of ten ends within one budget (inside the lease),
    /// every one of the ten had its turn, and the next fetch returns the honest message.
    /// </summary>
    [Fact]
    public async Task ADV2_1_a_fetch_of_ten_never_opening_messages_ends_within_one_budget_and_the_next_fetch_returns_the_one_behind()
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Qa1.Lease);
        Queue(mediator, junk: 10, honestAt: 10);
        var client = exchange.Client();

        var start = exchange.Clock.TrueNow;
        var first = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10));
        var elapsed = exchange.Clock.TrueNow - start;
        var second = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10));

        elapsed.Should().BeLessThanOrEqualTo(B, "the fetch ends within its budget, inside the lease");
        first.Items.Should().HaveCount(10).And.OnlyContain(i => i.Error is TimeoutException && !i.Error.Message.Contains("before its turn came"), "every message had its turn");
        second.Items.Should().ContainSingle(i => i.Id == "honest" && i.Error == null);
    }

    /// <summary>
    /// Same-delivery starvation through a fetch, at a mediator that does not lease: twenty messages
    /// that never open ahead of an honest one, in one fetch of 21. The first fetch starts sixteen of
    /// them and the honest one fails before its turn; the next fetch of the same 21 holds the sixteen
    /// back and returns the honest message.
    /// </summary>
    [Fact]
    public async Task Same_delivery_starvation_the_honest_message_behind_twenty_is_returned_by_the_next_fetch_of_the_same_messages()
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, lease: null);
        Queue(mediator, junk: 20, honestAt: 20);
        var client = exchange.Client();

        var first = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 21));
        var second = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 21));

        first.Items.Single(i => i.Id == "honest").Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        exchange.Host.Calls.Labels.Take(16).Should().OnlyContain(l => l.StartsWith("junk-", StringComparison.Ordinal));
        second.Items.Single(i => i.Id == "honest").Unpacked.Should().NotBeNull("the sixteen are held back, so the honest message gets its turn");
        second.Items.Take(16).Should().OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("slow to open when it was delivered before"));
        exchange.Wire.AcknowledgedIds.Should().Equal("honest");
    }

    /// <summary>
    /// ADV3-2 through fetches: twenty mailboxes each fetch four messages whose lookups ignore their
    /// token, all at once. Exactly 64 host calls start (the container's bound) and exactly sixteen
    /// messages are refused for the container; none of them is remembered or acknowledged. When the
    /// lookups end, every reservation comes back.
    /// </summary>
    [Fact]
    public async Task ADV3_2_fetches_from_twenty_mailboxes_reserve_exactly_the_containers_bound()
    {
        using var floor = new PoolFloor(parked: 4);
        var clock = new Qa1Clock();
        var host = new Qa1Host(clock, _ => new Qa1Step(Qa1Kind.IgnoresToken));
        var exchange = new Qa1Exchange(clock, host);
        var mediator = new Qa1Mediator(clock, Qa1.Lease);
        mediator.Install(exchange.Wire);
        const int mailboxes = UnpackReservations.MaxReserved / DeliveredMessageUnpacker.FetchConcurrency + 4;
        var dids = Enumerable.Range(0, mailboxes).Select(m => $"did:example:local-{m}").ToArray();
        foreach (var did in dids)
        {
            for (var i = 0; i < 4; i++)
                mediator.Enqueue($"{did}-{i}".Replace(':', '-'), Qa1.Anon($"{did}-{i}"), mailbox: did);
        }
        var client = exchange.Client();
        try
        {
            var fetches = dids.Select(did => client.FetchAsync(FakePickupExchange.M, did, limit: 4)).ToArray();
            await host.Calls.WaitForAsync(UnpackReservations.MaxReserved, Qa1Exchange.Guard);
            clock.Inner.Advance(B);
            var batches = await Task.WhenAll(fetches).WaitAsync(Qa1Exchange.Guard);

            var items = batches.SelectMany(b => b.Items).ToArray();
            items.Count(i => i.Error is TimeoutException t && t.Message.Contains("all mailboxes together")).Should().Be(mailboxes * 4 - UnpackReservations.MaxReserved);
            host.Calls.Count.Should().Be(UnpackReservations.MaxReserved);
            host.Calls.MaxRunning.Should().Be(UnpackReservations.MaxReserved);
            exchange.Unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved);
            exchange.Unpacker.Memory.Count.Should().Be(UnpackReservations.MaxReserved, "only the 64 that ran are remembered, not the refused");
            exchange.Wire.AcknowledgedIds.Should().BeEmpty();
        }
        finally
        {
            host.ReleaseIgnored();
        }
        SpinWait.SpinUntil(() => exchange.Unpacker.Reserved == 0, Qa1Exchange.Guard).Should().BeTrue();
    }

    /// <summary>
    /// ADV3-3 through a fetch: an honest message first, whose lookup takes 4.9 s, ahead of 99 that
    /// fail at once; and an honest message last whose lookup takes 15 s, behind 99 that fail at once.
    /// Both open; nothing is remembered.
    /// </summary>
    [Theory]
    [InlineData(0, 4.9)]
    [InlineData(99, 15.0)]
    public async Task ADV3_3_an_honest_message_among_ninety_nine_fast_failures_opens(int honestAt, double lookupSeconds)
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.OpenAfter(TimeSpan.FromSeconds(lookupSeconds)) : Qa1Step.FailAfter(TimeSpan.Zero), Qa1.Lease);
        Queue(mediator, junk: 99, honestAt: honestAt);
        var client = exchange.Client();

        var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100));

        batch.Items.Single(i => i.Id == "honest").Unpacked.Should().NotBeNull();
        batch.Items.Where(i => i.Id != "honest").Should().OnlyContain(i => i.Error is CryptoException);
        exchange.Unpacker.Memory.Count.Should().Be(0, "a failure that cost less than the slow threshold is not remembered, and an opened message is forgotten");
    }

    /// <summary>
    /// ADV3-6 through a fetch loop: sixteen messages that never open ahead of an honest one, limit
    /// 4, the mediator leasing for 60 s. The loop reaches the honest message within
    /// ceil(16 / 4) budgets (80 s).
    /// </summary>
    [Fact]
    public async Task ADV3_6_a_fetch_loop_reaches_the_message_behind_sixteen_never_opening_ones_within_the_bound()
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, Qa1.Lease);
        Queue(mediator, junk: 16, honestAt: 16);

        var reach = await ReachAsync(exchange, mediator, limit: 4, cap: TimeSpan.FromHours(2));

        reach.Found.Should().BeTrue();
        reach.Elapsed.Should().BeLessThanOrEqualTo(Bound(16, 4));
    }

    /// <summary>
    /// ADV2-2 through fetches, at a mediator that does not lease: Mallory's four lookups ignore
    /// their token and are let go at the budget. In the next fetch her fifth message is refused for her partition without starting,
    /// while Alice's message in the same fetch opens; the refused message is neither acknowledged
    /// nor remembered. Once Mallory's lookups end, her next message starts.
    /// </summary>
    [Fact]
    public async Task ADV2_2_a_senders_lookups_that_ignore_their_token_fill_only_its_partition_on_the_fetch_path()
    {
        var clock = new Qa1Clock();
        var host = new Qa1Host(clock, label => label.StartsWith("mallory", StringComparison.Ordinal) ? new Qa1Step(Qa1Kind.IgnoresToken) : Qa1Step.Open);
        var exchange = new Qa1Exchange(clock, host);
        var mediator = new Qa1Mediator(clock, lease: null);
        mediator.Install(exchange.Wire);
        for (var i = 0; i < 5; i++)
            mediator.Enqueue("mallory-" + i, Qa1.From(MalloryKid, "mallory-" + i));
        mediator.Enqueue("alice-1", Qa1.From(AliceKid, "alice-1"));
        var client = exchange.Client();
        try
        {
            var first = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 4);
            await host.Calls.WaitForAsync(4, Qa1Exchange.Guard);
            clock.Inner.Advance(B);
            (await first.WaitAsync(Qa1Exchange.Guard)).Items.Should().OnlyContain(i => i.Error is TimeoutException);

            var second = await client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10).WaitAsync(Qa1Exchange.Guard);

            second.Items.Single(i => i.Id == "mallory-4").Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted").And.Contain("for its sender");
            second.Items.Single(i => i.Id == "alice-1").Unpacked.Should().NotBeNull();
            host.Calls.Labels.Should().NotContain("mallory-4");
            exchange.Wire.AcknowledgedIds.Should().Equal("alice-1");
            exchange.Unpacker.Memory.HeldBack(Qa1.Bob, new[] { "mallory-4" }).Single().Should().BeFalse("a refusal is not remembered");
        }
        finally
        {
            host.ReleaseIgnored();
        }
        SpinWait.SpinUntil(() => exchange.Unpacker.Reserved == 0, Qa1Exchange.Guard).Should().BeTrue();
        var third = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10);
        await host.Calls.WaitForAsync(6, Qa1Exchange.Guard);
        clock.Inner.Advance(B);
        await third.WaitAsync(Qa1Exchange.Guard);
        host.ReleaseIgnored();

        host.Calls.Labels.Should().Contain("mallory-4", "her partition is free again");
    }

    // ---- where the message behind is reached -----------------------------------------------------

    /// <summary>
    /// The acceptance bound outside the engineers' matrix, where it holds: a fetch loop reaches the
    /// message behind N messages that never open within ceil(N / min(limit, 16)) budgets, at limits
    /// 1 to 3 with N small enough, at 16, at 100 with a short lease, and with N around and up to
    /// the memory's 256 entries per mailbox.
    /// </summary>
    [Theory]
    [InlineData(1, 16, 60)]
    [InlineData(2, 30, 60)]
    [InlineData(3, 45, 60)]
    [InlineData(10, 150, 60)]
    [InlineData(16, 99, 60)]
    [InlineData(16, 150, 60)]
    [InlineData(16, 255, 60)]
    [InlineData(16, 256, 60)]
    [InlineData(10, 255, 60)]
    [InlineData(10, 256, 60)]
    [InlineData(10, 257, 60)]
    [InlineData(100, 99, 10)]
    [InlineData(16, 99, 10)]
    public async Task The_message_behind_never_opening_messages_is_reached_within_the_bound(int limit, int n, int leaseSeconds)
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, TimeSpan.FromSeconds(leaseSeconds));
        Queue(mediator, junk: n, honestAt: n);

        var reach = await ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(30));

        reach.Found.Should().BeTrue();
        reach.Elapsed.Should().BeLessThanOrEqualTo(Bound(n, limit), "limit {0}, {1} in front, a lease of {2} s", limit, n, leaseSeconds);
        exchange.Wire.AcknowledgedIds.Should().Equal("honest");
    }

    /// <summary>
    /// The acceptance bound at limits 1 to 3, and at limit 4 with N near the memory's bound: a fetch
    /// loop reaches the message behind N messages that never open within ceil(N / min(limit, 16))
    /// budgets.
    /// </summary>
    [Theory(Skip = "QA defect: when learning the messages in front takes longer than the first pause, the message behind is reached beyond the stated bound, and at limit 1 with about 180 or more in front it is not reached")]
    [InlineData(1, 99, 60)]
    [InlineData(2, 99, 60)]
    [InlineData(3, 99, 60)]
    [InlineData(1, 99, 10)]
    [InlineData(4, 255, 60)]
    [InlineData(4, 256, 60)]
    [InlineData(1, 200, 60)]
    [InlineData(1, 255, 60)]
    public async Task The_message_behind_never_opening_messages_is_reached_within_the_bound_at_small_limits(int limit, int n, int leaseSeconds)
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, TimeSpan.FromSeconds(leaseSeconds));
        Queue(mediator, junk: n, honestAt: n);

        var reach = await ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(10));

        reach.Found.Should().BeTrue("limit {0}, {1} in front, a lease of {2} s: not reached after {3}", limit, n, leaseSeconds, reach.Elapsed);
        reach.Elapsed.Should().BeLessThanOrEqualTo(Bound(n, limit), "limit {0}, {1} in front, a lease of {2} s", limit, n, leaseSeconds);
    }

    /// <summary>
    /// The acceptance bound at limits above 16 at a mediator whose lease outlasts the budget: a
    /// fetch loop reaches the message behind N messages that never open within
    /// ceil(N / min(limit, 16)) budgets.
    /// </summary>
    [Theory(Skip = "QA defect: at limits above 16 the message behind is reached beyond the stated bound")]
    [InlineData(17, 99, 60)]
    [InlineData(17, 256, 60)]
    [InlineData(100, 99, 60)]
    [InlineData(17, 99, 10)]
    [InlineData(100, 256, 10)]
    public async Task The_message_behind_never_opening_messages_is_reached_within_the_bound_at_limits_above_sixteen(int limit, int n, int leaseSeconds)
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, TimeSpan.FromSeconds(leaseSeconds));
        Queue(mediator, junk: n, honestAt: n);

        var reach = await ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(30));

        reach.Found.Should().BeTrue();
        reach.Elapsed.Should().BeLessThanOrEqualTo(Bound(n, limit), "limit {0}, {1} in front, a lease of {2} s", limit, n, leaseSeconds);
    }

    /// <summary>
    /// More never-opening messages in front than the memory keeps per mailbox (256): the message
    /// behind them is still reached, at the cost of more openings (R-4).
    /// </summary>
    [Theory(Skip = "QA defect: with more never-opening messages in front than one mailbox of the memory keeps, the message behind is not reached")]
    [InlineData(4, 270, 60)]
    [InlineData(10, 300, 60)]
    [InlineData(16, 300, 60)]
    [InlineData(10, 300, 10)]
    public async Task The_message_behind_more_never_opening_messages_than_the_memory_keeps_is_still_reached(int limit, int n, int leaseSeconds)
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.Open : Qa1Step.Stall, TimeSpan.FromSeconds(leaseSeconds));
        Queue(mediator, junk: n, honestAt: n);

        var reach = await ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(10));

        reach.Found.Should().BeTrue("limit {0}, {1} in front, a lease of {2} s: not reached after {3} and {4} fetches", limit, n, leaseSeconds, reach.Elapsed, reach.Fetches);
    }

    // ---- deliveries that mix honest and failing messages -------------------------------------------

    /// <summary>
    /// Honest senders whose lookups take 2 s among messages that fail at once, in one fetch of 100:
    /// with 25 honest every one opens; with 50 those that do not open fail only before their turn
    /// (out of budget), never while being opened. Nothing is remembered either way.
    /// </summary>
    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    public async Task Honest_two_second_senders_among_fast_failures_never_fail_while_budget_is_left_and_are_never_remembered(int honest)
    {
        var (exchange, mediator) = Setup(label => label.StartsWith("honest", StringComparison.Ordinal) ? Qa1Step.OpenAfter(TimeSpan.FromSeconds(2)) : Qa1Step.FailAfter(TimeSpan.Zero), Qa1.Lease);
        var every = 100 / honest;
        for (var i = 0; i < 100; i++)
        {
            var label = i % every == 0 ? "honest-" + i : "junk-" + i;
            mediator.Enqueue(label, label.StartsWith("honest", StringComparison.Ordinal) ? Qa1.From(AliceKid, label) : Qa1.Anon(label));
        }
        var client = exchange.Client();

        var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100));

        var honestItems = batch.Items.Where(i => i.Id.StartsWith("honest", StringComparison.Ordinal)).ToArray();
        honestItems.Should().HaveCount(honest);
        honestItems.Should().OnlyContain(i => i.Unpacked != null || (i.Error is TimeoutException && i.Error.Message.Contains("before its turn came")));
        if (honest == 25)
            honestItems.Should().OnlyContain(i => i.Unpacked != null);
        exchange.Unpacker.Memory.Count.Should().Be(0);
    }

    /// <summary>
    /// A delivery of 100 honest senders whose lookups take 2 s: the first ones open, the rest fail
    /// before their turn, and none is remembered, so the next fetch opens the next ones.
    /// </summary>
    [Fact]
    public async Task A_fetch_of_one_hundred_honest_two_second_senders_opens_the_first_and_remembers_none()
    {
        var (exchange, mediator) = Setup(_ => Qa1Step.OpenAfter(TimeSpan.FromSeconds(2)), lease: null);
        for (var i = 0; i < 100; i++)
            mediator.Enqueue("honest-" + i, Qa1.From($"did:example:sender-{i}#key-1", "honest-" + i));
        var client = exchange.Client();

        var first = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100));
        var opened = first.Items.Count(i => i.Unpacked != null);
        var second = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100));

        opened.Should().Be(32, "rounds of four start every 2 s until the floor's 5 s are all that is left");
        first.Items.Where(i => i.Unpacked == null).Should().OnlyContain(i => i.Error is TimeoutException && i.Error.Message.Contains("before its turn came"));
        exchange.Unpacker.Memory.Count.Should().Be(0, "a message that did not start is never remembered");
        second.Items.Should().HaveCount(100 - 32);
        second.Items.Take(32).Should().OnlyContain(i => i.Unpacked != null, "none is held back");
    }

    /// <summary>
    /// Either side of the floor: an honest message first in a fetch of 100 whose 99 others fail at
    /// once. One whose lookup takes 4.9 s opens.
    /// </summary>
    [Fact]
    public async Task An_honest_sender_just_under_the_floor_first_among_fast_failures_opens()
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.OpenAfter(TimeSpan.FromSeconds(4.9)) : Qa1Step.FailAfter(TimeSpan.Zero), Qa1.Lease);
        Queue(mediator, junk: 99, honestAt: 0);

        var batch = await exchange.DriveAsync(() => exchange.Client().FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 100));

        batch.Items[0].Unpacked.Should().NotBeNull();
        exchange.Unpacker.Memory.Count.Should().Be(0);
    }

    /// <summary>
    /// Either side of the floor, the other side: an honest message first in a fetch of 100 whose 99
    /// others fail at once, its lookup taking 5.1 s. The 99 take no time, so most of the budget is
    /// left: it opens, and it is not remembered.
    /// </summary>
    [Theory(Skip = "QA defect: an honest message first among fast failures is cut at the floor while most of the budget is left, and is then remembered")]
    [InlineData(100)]
    [InlineData(13)]
    public async Task An_honest_sender_just_over_the_floor_first_among_fast_failures_opens_while_most_of_the_budget_is_left(int limit)
    {
        var (exchange, mediator) = Setup(label => label == "honest" ? Qa1Step.OpenAfter(TimeSpan.FromSeconds(5.1)) : Qa1Step.FailAfter(TimeSpan.Zero), Qa1.Lease);
        Queue(mediator, junk: limit - 1, honestAt: 0);
        var start = exchange.Clock.TrueNow;

        var batch = await exchange.DriveAsync(() => exchange.Client().FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit));

        var elapsed = exchange.Clock.TrueNow - start;
        batch.Items[0].Error.Should().BeNull("the {0} others failed at once, so {1} of the budget was left when it was cut", limit - 1, B - elapsed);
        exchange.Unpacker.Memory.Count.Should().Be(0);
    }

    // ---- an honest slow sender with messages naming its key (D-13) ---------------------------------

    /// <summary>
    /// D-13: Sam's lookups take 2, 3 or 5 s; twelve messages naming Sam's key take as long and then
    /// fail. Sam's real message is in front of them, among them or behind them, fetched at limit 4
    /// or 10 with a 60 s lease. It reaches the app within the bound for the messages in front of it
    /// (and the budget of the fetch that returns it), and is never refused for its partition.
    /// </summary>
    [Theory]
    [InlineData(2, 0, 4)]
    [InlineData(2, 6, 4)]
    [InlineData(2, 12, 4)]
    [InlineData(3, 0, 10)]
    [InlineData(3, 6, 10)]
    [InlineData(3, 12, 10)]
    [InlineData(5, 0, 4)]
    [InlineData(5, 6, 4)]
    [InlineData(5, 12, 4)]
    [InlineData(5, 0, 10)]
    [InlineData(5, 6, 10)]
    [InlineData(5, 12, 10)]
    public async Task D13_a_slow_honest_senders_message_among_messages_naming_its_key_reaches_the_app_and_is_never_refused(int lookupSeconds, int samAt, int limit)
    {
        var lookup = TimeSpan.FromSeconds(lookupSeconds);
        var (exchange, mediator) = Setup(label => label == "sam" ? Qa1Step.OpenAfter(lookup) : Qa1Step.FailAfter(lookup), Qa1.Lease);
        for (int i = 0, j = 0; i <= 12; i++)
        {
            if (i == samAt)
                mediator.Enqueue("sam", Qa1.From(SamKid, "sam"));
            else
            {
                mediator.Enqueue("junk-" + j, Qa1.From(SamKid, "junk-" + j));
                j++;
            }
        }

        var reach = await ReachAsync(exchange, mediator, limit, cap: TimeSpan.FromHours(2), honest: "sam");

        reach.Found.Should().BeTrue();
        reach.Elapsed.Should().BeLessThanOrEqualTo(Bound(samAt, limit) + B, "the bound for the messages in front of it, plus the fetch that returns it");
        reach.Batches.SelectMany(b => b.Items).Where(i => i.Id == "sam").Should().NotContain(i => i.Error is TimeoutException && i.Error.Message.Contains("capacity exhausted"));
    }

    /// <summary>
    /// D-13 at limit 16, where every share is the floor: Sam's lookups take 4.9 s, just under it;
    /// twelve messages naming Sam's key take as long and fail. Sam's real message, in front of them,
    /// among them or behind them, reaches the app within the bound and is never refused.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(12)]
    public async Task D13_at_limit_sixteen_a_sender_just_under_the_floor_reaches_the_app_and_is_never_refused(int samAt)
    {
        var lookup = TimeSpan.FromSeconds(4.9);
        var (exchange, mediator) = Setup(label => label == "sam" ? Qa1Step.OpenAfter(lookup) : Qa1Step.FailAfter(lookup), Qa1.Lease);
        for (int i = 0, j = 0; i <= 12; i++)
        {
            if (i == samAt)
                mediator.Enqueue("sam", Qa1.From(SamKid, "sam"));
            else
            {
                mediator.Enqueue("junk-" + j, Qa1.From(SamKid, "junk-" + j));
                j++;
            }
        }

        var reach = await ReachAsync(exchange, mediator, limit: 16, cap: TimeSpan.FromHours(2), honest: "sam");

        reach.Found.Should().BeTrue();
        reach.Elapsed.Should().BeLessThanOrEqualTo(Bound(samAt, 16) + B);
        reach.Batches.SelectMany(b => b.Items).Where(i => i.Id == "sam").Should().NotContain(i => i.Error is TimeoutException && i.Error.Message.Contains("capacity exhausted"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    internal static (Qa1Exchange Exchange, Qa1Mediator Mediator) Setup(Func<string, Qa1Step> step, TimeSpan? lease, Action<MediatorClientOptions>? configure = null, Func<string, string>? recipientOf = null)
    {
        var clock = new Qa1Clock();
        var exchange = new Qa1Exchange(clock, new Qa1Host(clock, step, recipientOf), configure);
        var mediator = new Qa1Mediator(clock, lease);
        mediator.Install(exchange.Wire);
        return (exchange, mediator);
    }

    // `junk` anoncrypted messages "junk-i", with Alice's "honest" at position `honestAt`.
    internal static void Queue(Qa1Mediator mediator, int junk, int honestAt)
    {
        for (int i = 0, j = 0; i <= junk; i++)
        {
            if (i == honestAt)
                mediator.Enqueue("honest", Qa1.From(AliceKid, "honest"));
            else
            {
                mediator.Enqueue("junk-" + j, Qa1.Anon("junk-" + j));
                j++;
            }
        }
    }

    // ceil(n / min(limit, 16)) budgets: the plan's acceptance bound for a fetch loop.
    internal static TimeSpan Bound(int n, int limit)
        => TimeSpan.FromTicks(B.Ticks * (long)Math.Ceiling(n / (double)Math.Min(limit, 16)));

    internal sealed record Reach(bool Found, TimeSpan Elapsed, int Fetches, IReadOnlyList<PickupBatch> Batches);

    /// <summary>
    /// A fetch loop of the eagerest host: it fetches until drained and, when drained, fetches again
    /// as soon as the mediator's next lease ends; it stops when <paramref name="honest"/> is returned
    /// opened, or after <paramref name="cap"/> of clock time.
    /// </summary>
    internal static async Task<Reach> ReachAsync(Qa1Exchange exchange, Qa1Mediator mediator, int limit, TimeSpan cap, string honest = "honest", string? recipientDid = null)
    {
        using var floor = new PoolFloor(parked: 2);
        var client = exchange.Client();
        var start = exchange.Clock.TrueNow;
        var batches = new List<PickupBatch>();
        while (exchange.Clock.TrueNow - start < cap)
        {
            var batch = await exchange.DriveAsync(() => client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit, recipientDid));
            batches.Add(batch);
            if (batch.Items.Any(i => i.Id == honest && i.Unpacked != null))
                return new Reach(true, exchange.Clock.TrueNow - start, batches.Count, batches);
            if (batch.Drained)
            {
                if (mediator.NextLeaseEnd() is not { } next)
                    break;
                exchange.Clock.Inner.Advance(next - exchange.Clock.TrueNow);
            }
        }
        return new Reach(false, exchange.Clock.TrueNow - start, batches.Count, batches);
    }
}
