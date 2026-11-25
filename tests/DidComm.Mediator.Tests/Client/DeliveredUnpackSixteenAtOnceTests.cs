using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Owner decision G for #111, "Open 16 at once (Recommended)": up to sixteen messages of a delivery are opened at
/// the same time (<see cref="DeliveredMessageUnpacker.FetchConcurrency"/>), and one named sender may
/// have sixteen host calls running in a mailbox (<see cref="UnpackReservations.MaxPerSender"/>).
/// The two bounds belong together: a delivery takes sixteen of its messages at once, so a sender
/// whose bound were lower would have its own messages refused inside one delivery. The tests here
/// pin the two values, that relation, what an honest sender's ten or sixteen messages in one
/// delivery get, and both sides of what a sender whose lookups never end can hold. Every timing is
/// driven by the injected <see cref="ManualTimeProvider"/> and rendezvous on host calls; real time
/// is only a hang guard.
/// </summary>
public sealed class DeliveredUnpackSixteenAtOnceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const string MalloryKid = "did:example:mallory#key-x25519-1";
    private const string SamKid = "did:example:sam#key-x25519-1";
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;
    private const int PerSender = UnpackReservations.MaxPerSender;

    // ---- the two values, and how they belong together ------------------------------------------

    /// <summary>The two values owner decision G names: sixteen opened at once, sixteen per sender.</summary>
    [Fact]
    public void Sixteen_messages_of_a_delivery_are_opened_at_once_and_one_sender_may_have_sixteen_host_calls()
    {
        DeliveredMessageUnpacker.FetchConcurrency.Should().Be(16, "sixteen messages of a delivery are opened at once (owner decision G)");
        UnpackReservations.MaxPerSender.Should().Be(16, "one sender may have sixteen host calls running at once (owner decision G)");
    }

    /// <summary>
    /// A delivery takes as many of its messages at once as it opens at once, each with a reservation
    /// in its sender's partition (or, without a named sender, in the mailbox's partition for such
    /// messages). A partition's bound below that number refuses messages of one sender inside one
    /// delivery, though nothing of that sender was running before it.
    /// </summary>
    [Fact]
    public void A_partitions_bound_is_not_below_what_one_delivery_opens_at_once()
    {
        UnpackReservations.MaxPerSender.Should().BeGreaterThanOrEqualTo(DeliveredMessageUnpacker.FetchConcurrency,
            "a delivery opens {0} messages at once, and they may all be from one named sender", DeliveredMessageUnpacker.FetchConcurrency);
        UnpackReservations.MaxPerAnonymous.Should().BeGreaterThanOrEqualTo(DeliveredMessageUnpacker.FetchConcurrency,
            "a delivery opens {0} messages at once, and none of them may name a sender", DeliveredMessageUnpacker.FetchConcurrency);
    }

    /// <summary>
    /// The behaviour the relation protects: ten messages (what this library's mediator delivers at
    /// once at its defaults) and sixteen messages from one sender in one delivery, each taking one
    /// second to open.
    /// All of them open and none is refused; from a named sender, and without a named sender. The
    /// counts are printed, and are in the failure's text: with sixteen opened at once and a bound
    /// of four per sender, only four of the named sender's open and the others are refused.
    /// </summary>
    [Theory]
    [InlineData(true, 10)]
    [InlineData(true, 16)]
    [InlineData(false, 10)]
    [InlineData(false, 16)]
    public async Task Messages_of_one_sender_in_one_delivery_each_taking_a_second_all_open_and_none_is_refused(bool named, int count)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            var lookup = Task.Delay(TimeSpan.FromSeconds(1), time, ct); // its timer exists before the call is logged
            using (calls.Enter(label, ct))
                await lookup.ConfigureAwait(false);
            return UnpackFakes.Opened(label);
        }, Budget, time);
        var messages = Enumerable.Range(0, count)
            .Select(i => Message(named ? TestJwe.Authcrypt(SamKid, "sam-" + i) : TestJwe.Anoncrypt("sam-" + i), "sam-" + i))
            .ToArray();

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        // Every message is being opened (its one-second lookup is under way) or has its outcome
        // already (a refusal is decided at once).
        SpinWait.SpinUntil(() => calls.Count + unpacker.Settled >= count, RealTime).Should().BeTrue("every message was taken at once: {0} being opened, {1} decided", calls.Count, unpacker.Settled);
        var startedAtOnce = calls.Count;
        time.Advance(TimeSpan.FromSeconds(1));
        var outcomes = await delivery.WaitAsync(RealTime);

        var opened = outcomes.Count(o => o.Succeeded);
        var refused = outcomes.Count(IsRefusal);
        output.WriteLine($"{count} messages {(named ? "from one named sender" : "without a named sender")} in one delivery, {AtOnce} opened at once, " +
            $"{(named ? PerSender : UnpackReservations.MaxPerAnonymous)} per partition: started at once {startedAtOnce}, opened {opened}, refused {refused}");
        $"opened {opened}, refused {refused}, started at once {startedAtOnce}".Should().Be($"opened {count}, refused 0, started at once {count}",
            "all {0} are being opened at once, open after their one second, and none is refused", count);
        unpacker.Memory.Count.Should().Be(0, "a message that opened is not remembered");
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Partitions == 0, RealTime).Should().BeTrue();
    }

    // ---- the other side: a sender whose lookups never end ----------------------------------------

    /// <summary>
    /// The bound on one sender, across deliveries of ten (what this library's mediator delivers at
    /// once at its defaults): Mallory's lookups never end and ignore their token. Her first ten messages are let go
    /// at their delivery's budget and still run. The next delivery brings eight more of hers and,
    /// behind them, a message of Alice's: six of Mallory's start, which makes sixteen running; her
    /// seventeenth and eighteenth are refused for her bound at once, without starting; and Alice's
    /// message opens at once. All of that before the clock moves again.
    /// </summary>
    [Fact]
    public async Task A_senders_seventeenth_and_later_messages_are_refused_at_once_while_its_sixteen_lookups_run_and_another_senders_message_behind_them_opens()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("mallory", StringComparison.Ordinal) ? never.Task : Task.FromResult(UnpackFakes.Opened(label)); // hers ignore their token
        }, Budget, time);
        DeliveredInput Hers(int i) => Message(TestJwe.Authcrypt(MalloryKid, "mallory-" + i), "mallory-" + i);
        try
        {
            var first = unpacker.UnpackAllAsync(Bob, Enumerable.Range(0, 10).Select(Hers).ToArray(), "request-1", default);
            await calls.WaitForAsync(10, RealTime);
            time.Advance(Budget);
            (await first.WaitAsync(RealTime)).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
            unpacker.Reserved.Should().Be(10, "her ten lookups still run and keep their reservations");
            var settled = unpacker.Settled;

            var messages = Enumerable.Range(10, 8).Select(Hers).Append(Message(TestJwe.Authcrypt(AliceKid, "alice-1"), "alice-1")).ToArray();
            var second = unpacker.UnpackAllAsync(Bob, messages, "request-2", default);
            await calls.WaitForAsync(10 + (PerSender - 10) + 1, RealTime); // six more of hers, and Alice's
            SpinWait.SpinUntil(() => unpacker.Settled == settled + 3, RealTime).Should().BeTrue("her two refusals and Alice's message have their outcomes before the clock moves");

            second.IsCompleted.Should().BeFalse("six of hers are being opened");
            unpacker.Reserved.Should().Be(PerSender, "sixteen of her lookups run: her bound");
            calls.Labels.Count(l => l.StartsWith("mallory", StringComparison.Ordinal)).Should().Be(PerSender);
            calls.Labels.Should().NotContain(new[] { "mallory-16", "mallory-17" }, "a refused message never starts");
            calls.Labels.Should().Contain("alice-1");

            time.Advance(Budget);
            var outcomes = await second.WaitAsync(RealTime);
            outcomes.Take(6).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
            outcomes.Skip(6).Take(2).Should().OnlyContain(o => IsRefusal(o) && o.Error!.Message.Contains("for its sender"), "her seventeenth and eighteenth");
            outcomes[8].Succeeded.Should().BeTrue("another sender's message behind them opens");
            unpacker.Memory.HeldBack(Bob, new[] { "mallory-16", "mallory-17" }).Should().OnlyContain(h => !h, "a refusal is not remembered");
            output.WriteLine($"one sender, lookups that never end and ignore their token, deliveries of ten: running {unpacker.Reserved}, refused at once {outcomes.Count(IsRefusal)}, another sender's message behind them opened {outcomes[8].Succeeded}");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Partitions == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// What one sender can hold in one delivery, sixteen host calls (the cost stated in the option
    /// chosen as owner decision G): seventeen messages of Mallory's
    /// that never open, and a message of Alice's behind them, in one delivery. Sixteen of Mallory's
    /// start and keep all sixteen workers until the budget ends; her seventeenth and Alice's
    /// message do not get their turn in that delivery. One tick before the budget's end neither
    /// has started. The sixteen are remembered, so at the next delivery of the same messages they
    /// are held back and Alice's message opens.
    /// <para>
    /// Mallory's seventeenth then depends on her lookups. When they honour their token they ended
    /// with the first delivery, so it starts, and has that delivery's budget. When they ignore
    /// their token the sixteen still run and keep their reservations: it is refused for her bound
    /// at once, and the delivery ends without the clock moving.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WhenCancelled.Ends)]
    [InlineData(WhenCancelled.RunsOn)]
    public async Task Sixteen_never_opening_messages_of_one_sender_hold_every_worker_for_the_budget_and_the_message_behind_them_gets_its_turn_in_the_next_delivery(WhenCancelled whenCancelled)
    {
        PerSender.Should().Be(AtOnce, "the sixteen that start are both what a delivery opens at once and Mallory's bound");
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hers = NeverOpens.Lookup(whenCancelled, calls, new RegistrationGate(), never.Task);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label.StartsWith("mallory", StringComparison.Ordinal))
                return hers(packed, ct);
            calls.Enter(label, ct);
            return Task.FromResult(UnpackFakes.Opened(label));
        }, Budget, time);
        var seventeenth = "mallory-" + AtOnce;
        var messages = Enumerable.Range(0, AtOnce + 1).Select(i => Message(TestJwe.Authcrypt(MalloryKid, "mallory-" + i), "mallory-" + i))
            .Append(Message(TestJwe.Authcrypt(AliceKid, "alice-1"), "alice-1"))
            .ToArray();
        var ids = messages.Select(m => m.Id).ToArray();
        try
        {
            var first = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
            await calls.WaitForAsync(AtOnce, RealTime);
            time.Advance(Budget - TimeSpan.FromTicks(1));

            first.IsCompleted.Should().BeFalse("one tick before the budget ends");
            calls.Count.Should().Be(AtOnce, "sixteen of Mallory's hold the sixteen workers");
            calls.Labels.Should().NotContain(new[] { seventeenth, "alice-1" }, "neither her seventeenth nor Alice's message has had its turn");
            unpacker.Settled.Should().Be(0, "nothing of the delivery has an outcome yet");

            time.Advance(TimeSpan.FromTicks(1));
            var outcomes = await first.WaitAsync(RealTime);
            outcomes.Take(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
            outcomes.Skip(AtOnce).Should().HaveCount(2).And.OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("before its turn came"),
                "her seventeenth and Alice's message did not get their turn: the cost of one sender holding sixteen");
            unpacker.Memory.HeldBack(Bob, ids).Should().Equal(Enumerable.Repeat(true, AtOnce).Concat(new[] { false, false }),
                "the sixteen had the whole budget; a message that did not start is not remembered");
            output.WriteLine($"one delivery, {AtOnce + 1} of one sender that never open ({whenCancelled}) and another sender's message behind them: started {calls.Count}, out of time before their turn {outcomes.Count(o => o.Error!.Message.Contains("before its turn came"))}, remembered {unpacker.Memory.Count}");

            if (whenCancelled == WhenCancelled.Ends)
            {
                // Her lookups honoured their token: they have ended and given their reservations back.
                SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
                var second = unpacker.UnpackAllAsync(Bob, messages, "request-2", default);
                await calls.WaitForAsync(AtOnce + 2, RealTime); // her seventeenth, and Alice's
                SpinWait.SpinUntil(() => unpacker.Settled == 1, RealTime).Should().BeTrue("Alice's message opened before the clock moved");
                second.IsCompleted.Should().BeFalse("her seventeenth is being opened");
                time.Advance(Budget);
                var again = await second.WaitAsync(RealTime);

                again.Take(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
                again[AtOnce].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");
                again[AtOnce + 1].Succeeded.Should().BeTrue("the sixteen in front are held back, so Alice's message gets its turn");
                calls.Labels.Skip(AtOnce).Should().BeEquivalentTo(new[] { seventeenth, "alice-1" });
            }
            else
            {
                unpacker.Reserved.Should().Be(AtOnce, "her sixteen lookups ignore their token and still run");
                // No clock move: the delivery ends on its own.
                var again = await unpacker.UnpackAllAsync(Bob, messages, "request-2", default).WaitAsync(RealTime);

                again.Take(AtOnce).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
                IsRefusal(again[AtOnce]).Should().BeTrue("her seventeenth is refused while her sixteen lookups run");
                again[AtOnce].Error!.Message.Should().Contain("for its sender");
                again[AtOnce + 1].Succeeded.Should().BeTrue("the sixteen in front are held back, so Alice's message gets its turn");
                calls.Labels.Skip(AtOnce).Should().Equal("alice-1");
                unpacker.Reserved.Should().Be(AtOnce, "the refused message took no reservation");
            }
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Partitions == 0, RealTime).Should().BeTrue();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static DeliveredInput Message(string packed, string id) => new(id, TestJwe.Bytes(packed));

    private static bool IsRefusal(DeliveredOutcome outcome)
        => outcome.Error is TimeoutException t && t.Message.Contains("capacity exhausted", StringComparison.Ordinal);
}
