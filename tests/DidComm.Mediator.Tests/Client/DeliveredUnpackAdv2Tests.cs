using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-2 red-team pass on the delivered-message unpacker (#96): what a sender whose DID resolves
/// slowly costs the recipient. Every timing is driven by the injected <see cref="ManualTimeProvider"/>
/// and rendezvous on scheduled timers (L-044); nothing sleeps.
/// </summary>
public sealed class DeliveredUnpackAdv2Tests
{
    private static readonly TimeSpan UnpackTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// ADV2-1 (fails: defect). Property broken — <see cref="Client.PickupClient.FetchAsync"/> remarks:
    /// "a loop that fetches until drained gets past failures to what is behind them". Attack: a sender
    /// (any outsider who can address the recipient through its mediator) sends ten messages whose
    /// sender DID resolves only when cancelled (a did:webvh host that holds the connection). One
    /// fetch of limit 10 unpacks them four at a time, each until <c>UnpackTimeout</c>, so the batch
    /// needs three waves (90 s by default) — longer than this library's mediator lease
    /// (<see cref="MessagePickupOptions.LeaseTimeout"/>, 60 s). Every lease has ended before the
    /// fetch returns, so the next fetch is handed the same ten failures again, and the messages
    /// queued behind them are never reached. The test asserts the claimed property: the batch is
    /// done by the time the lease ends.
    /// </summary>
    [Fact]
    public async Task ADV2_1_a_batch_of_stalling_senders_outlasts_the_mediators_lease()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled, UnpackTimeout, time);
        var lease = new MessagePickupOptions().LeaseTimeout;
        var payloads = Enumerable.Range(0, 10).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("stall-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, default);
        var elapsed = TimeSpan.Zero;
        try
        {
            // Wave by wave: each wave's deadlines are scheduled, then the clock passes them.
            while (!batch.IsCompleted && elapsed < lease)
            {
                await time.WaitForScheduledTimersAsync(1, RealTime);
                time.Advance(UnpackTimeout);
                elapsed += UnpackTimeout;
                await Task.Yield();
            }

            batch.IsCompleted.Should().BeTrue(
                "a fetch must finish failing its messages within the mediator's lease ({0}), or the next fetch gets the same failures and never reaches what is queued behind them", lease);
        }
        finally
        {
            // Let the rest of the batch end, so nothing outlives the test.
            while (!batch.IsCompleted)
            {
                await time.WaitForScheduledTimersAsync(1, RealTime);
                time.Advance(UnpackTimeout);
                await Task.Yield();
            }
            await batch.WaitAsync(RealTime);
        }
    }

    /// <summary>
    /// ADV2-1 companion, updated to owner decision E for #111 (one budget per delivery and no time
    /// limit per message). Ten stalling messages under a 30 s budget: the first four are being
    /// opened until the budget ends, and the six behind them are out of time before their turn.
    /// Every message has failed when the budget ends, none later. (Before fix round 2 of #111 each
    /// message had a share of the budget, and all ten had their turn.)
    /// </summary>
    [Fact]
    public async Task ADV2_1_ten_stalling_messages_all_fail_within_one_delivery_budget()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled, UnpackTimeout, time);
        var payloads = Enumerable.Range(0, 10).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("stall-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, default);
        await time.WaitForScheduledTimersAsync(1, RealTime); // the budget
        time.Advance(UnpackTimeout - TimeSpan.FromTicks(1));
        batch.IsCompleted.Should().BeFalse("the delivery is not decided before its budget ends");
        time.Advance(TimeSpan.FromTicks(1));
        var outcomes = await batch.WaitAsync(RealTime);

        outcomes.Should().HaveCount(10).And.OnlyContain(o => !o.Succeeded && o.Error is TimeoutException);
        outcomes.Take(4).Should().OnlyContain(o => o.Error!.Message.Contains("while it was being opened"));
        outcomes.Skip(4).Should().OnlyContain(o => o.Error!.Message.Contains("before its turn came"));
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("an unpack that honours cancellation gives its reservation back");
    }

    /// <summary>
    /// ADV2-1 follow-on (same-delivery starvation), updated to owner decision E for #111. Four
    /// senders whose DIDs never resolve are first in a delivery of ten. With no time limit per
    /// message they keep the four workers for the whole budget, and the six honest messages behind
    /// them are out of time before their turn in that delivery. The four are remembered when the
    /// budget ends, so the next delivery of the same ten holds them back and opens the six: they are
    /// not starved at every redelivery while the four stay at the front of the queue.
    /// </summary>
    [Fact]
    public async Task ADV2_1_four_stalling_senders_first_in_a_delivery_keep_it_for_one_budget_and_the_six_behind_them_open_in_the_next()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            using (calls.Enter(text, ct))
                return text.StartsWith("stall-", StringComparison.Ordinal) ? await StallUntilCancelled(text, ct) : Opened(text);
        }, UnpackTimeout, time);
        var scope = new DeliveryScope(FakePickupExchange.M, FakePickupExchange.L);
        var messages = Enumerable.Range(0, 4).Select(i => "stall-" + i).Concat(Enumerable.Range(0, 6).Select(i => "ok-" + i))
            .Select(t => new DeliveredInput(t, Encoding.UTF8.GetBytes(t))).ToArray();

        var first = unpacker.UnpackAllAsync(scope, messages, "request-7", default);
        await calls.WaitForAsync(DeliveredMessageUnpacker.FetchConcurrency, RealTime);
        time.Advance(UnpackTimeout);
        var outcomes = await first.WaitAsync(RealTime);

        outcomes.Take(4).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
        outcomes.Skip(4).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("before its turn came"), "the four in front kept every worker for the whole budget");

        var again = await unpacker.UnpackAllAsync(scope, messages, "request-8", default).WaitAsync(RealTime);

        again.Take(4).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
        again.Skip(4).Should().OnlyContain(o => o.Succeeded, "the four are held back, so the honest messages behind them are opened");
        calls.Count.Should().Be(10, "no stalling message was opened twice");
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    private static DeliveredUnpack Opened(string text)
    {
        var message = new DidComm.Messages.MessageBuilder().WithId(text).WithType("https://example.org/notes/1.0/note").WithBody(new System.Text.Json.Nodes.JsonObject()).Build();
        var result = new UnpackResult(message, new[] { DidComm.Jose.EnvelopeKind.Plaintext }, false, false, false, false, null, null, null, null, null, null, Array.Empty<string>(), null);
        return new DeliveredUnpack(result, text, 0);
    }

    /// <summary>
    /// ADV2-2 (Low), inverted by #111 (I-2, D-1): host unpack calls are reserved per sender, so a
    /// sender whose DID lookup ignores cancellation fills only its own partition. Mallory's four such
    /// messages are let go at their budgets and keep their reservations; her fifth is refused at once
    /// for her partition, without starting, while Alice's message in the same delivery opens. When the
    /// stalled lookups end, Mallory's partition is free again.
    /// </summary>
    [Fact]
    public async Task ADV2_2_a_lookup_that_ignores_cancellation_fills_only_its_senders_partition_and_another_senders_message_opens()
    {
        var time = new ManualTimeProvider();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("mallory", StringComparison.Ordinal)
                ? never.Task // ignores the token
                : Task.FromResult(UnpackFakes.Opened(label));
        }, UnpackTimeout, time);
        var scope = new DeliveryScope(FakePickupExchange.M, FakePickupExchange.L);
        try
        {
            var stalled = Enumerable.Range(0, UnpackReservations.MaxPerSender)
                .Select(i => unpacker.UnpackAllAsync(scope, new[] { Message(TestJwe.Authcrypt(MalloryKid, "mallory-" + i), "mallory-" + i) }, "request-" + i, default))
                .ToArray();
            await calls.WaitForAsync(UnpackReservations.MaxPerSender, RealTime);
            time.Advance(UnpackTimeout);
            foreach (var delivery in stalled)
                (await delivery.WaitAsync(RealTime)).Single().Succeeded.Should().BeFalse();
            unpacker.Reserved.Should().Be(UnpackReservations.MaxPerSender, "each lookup still running keeps its reservation");

            var mixed = await unpacker.UnpackAllAsync(scope, new[]
            {
                Message(TestJwe.Authcrypt(MalloryKid, "mallory-4"), "mallory-4"),
                Message(TestJwe.Authcrypt(AliceKid, "alice-1"), "alice-1"),
            }, "request-4", default).WaitAsync(RealTime);

            mixed[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted").And.Contain("for its sender");
            mixed[1].Succeeded.Should().BeTrue("another sender's partition is not full");
            calls.Labels.Should().NotContain("mallory-4", "the refused message never started");

            never.TrySetCanceled();
            SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("each reservation is given back when its lookup ends");
            await unpacker.UnpackAllAsync(scope, new[] { Message(TestJwe.Authcrypt(MalloryKid, "mallory-5"), "mallory-5") }, "request-5", default).WaitAsync(RealTime);
            calls.Labels.Should().Contain("mallory-5", "Mallory's partition is free again, so her next message starts");
        }
        finally
        {
            never.TrySetCanceled();
        }
    }

    /// <summary>
    /// ADV2-2 residual R-1 (#111): sixteen senders, each with four lookups that ignore cancellation,
    /// hold all 64 of the container's reservations; a seventeenth sender's message is then refused
    /// for the container until those lookups end.
    /// </summary>
    [Fact]
    public async Task ADV2_2_residual_many_senders_fill_the_container()
    {
        var time = new ManualTimeProvider();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("sender", StringComparison.Ordinal) ? never.Task : Task.FromResult(UnpackFakes.Opened(label));
        }, UnpackTimeout, time);
        var scope = new DeliveryScope(FakePickupExchange.M, FakePickupExchange.L);
        const int senders = UnpackReservations.MaxReserved / UnpackReservations.MaxPerSender;
        try
        {
            var stalled = Enumerable.Range(0, senders)
                .Select(s => unpacker.UnpackAllAsync(scope,
                    Enumerable.Range(0, UnpackReservations.MaxPerSender).Select(i => Message(TestJwe.Authcrypt($"did:example:sender-{s}#key-1", $"sender-{s}-{i}"), $"sender-{s}-{i}")).ToArray(),
                    "request-" + s, default))
                .ToArray();
            await calls.WaitForAsync(UnpackReservations.MaxReserved, RealTime);
            time.Advance(UnpackTimeout);
            foreach (var delivery in stalled)
                (await delivery.WaitAsync(RealTime)).Should().OnlyContain(o => !o.Succeeded);
            unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved);

            var refused = await unpacker.UnpackAllAsync(scope, new[] { Message(TestJwe.Authcrypt(AliceKid, "alice-1"), "alice-1") }, "request-alice", default).WaitAsync(RealTime);

            refused.Single().Error.Should().BeOfType<TimeoutException>("the residual: senders enough to fill the container refuse every other sender's message while their lookups run")
                .Which.Message.Should().Contain("capacity exhausted").And.Contain("all mailboxes together");
            calls.Labels.Should().NotContain("alice-1");

            never.TrySetCanceled();
            SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
            (await unpacker.UnpackAllAsync(scope, new[] { Message(TestJwe.Authcrypt(AliceKid, "alice-1"), "alice-1") }, "request-alice-2", default).WaitAsync(RealTime))
                .Single().Succeeded.Should().BeTrue();
        }
        finally
        {
            never.TrySetCanceled();
        }
    }

    /// <summary>
    /// ADV2-2 residual R-2 (#111): messages without a named sender share one partition of 16 per
    /// (mediator, mailbox). With 16 such lookups running in Bob's mailbox, a seventeenth anonymous
    /// message there is refused; an anonymous message in another mailbox and a named sender's message
    /// in Bob's mailbox are not.
    /// </summary>
    [Fact]
    public async Task ADV2_2_residual_anonymous_partition()
    {
        var time = new ManualTimeProvider();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("junk", StringComparison.Ordinal) ? never.Task : Task.FromResult(UnpackFakes.Opened(label));
        }, UnpackTimeout, time);
        var bob = new DeliveryScope(FakePickupExchange.M, FakePickupExchange.L);
        var carol = new DeliveryScope(FakePickupExchange.M, "did:example:carol");
        const int deliveries = UnpackReservations.MaxPerAnonymous / DeliveredMessageUnpacker.FetchConcurrency;
        try
        {
            var stalled = Enumerable.Range(0, deliveries)
                .Select(d => unpacker.UnpackAllAsync(bob,
                    Enumerable.Range(0, DeliveredMessageUnpacker.FetchConcurrency).Select(i => Message(TestJwe.Anoncrypt($"junk-{d}-{i}"), $"junk-{d}-{i}")).ToArray(),
                    "request-" + d, default))
                .ToArray();
            await calls.WaitForAsync(UnpackReservations.MaxPerAnonymous, RealTime);
            time.Advance(UnpackTimeout);
            foreach (var delivery in stalled)
                (await delivery.WaitAsync(RealTime)).Should().OnlyContain(o => !o.Succeeded);
            unpacker.Reserved.Should().Be(UnpackReservations.MaxPerAnonymous);

            var inBob = await unpacker.UnpackAllAsync(bob, new[]
            {
                Message(TestJwe.Anoncrypt("anonymous-for-bob"), "anonymous-for-bob"),
                Message(TestJwe.Authcrypt(AliceKid, "alice-for-bob"), "alice-for-bob"),
            }, "request-bob", default).WaitAsync(RealTime);
            var inCarol = await unpacker.UnpackAllAsync(carol, new[] { Message(TestJwe.Anoncrypt("anonymous-for-carol"), "anonymous-for-carol") }, "request-carol", default).WaitAsync(RealTime);

            inBob[0].Error.Should().BeOfType<TimeoutException>("the residual: Bob's anonymous partition is full").Which.Message.Should().Contain("capacity exhausted");
            inBob[1].Succeeded.Should().BeTrue("a named sender has a partition of its own");
            inCarol.Single().Succeeded.Should().BeTrue("another mailbox has an anonymous partition of its own");
            calls.Labels.Should().NotContain("anonymous-for-bob");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const string MalloryKid = "did:example:mallory#key-x25519-1";

    private static DeliveredInput Message(string packed, string id) => new(id, TestJwe.Bytes(packed));

    private static async Task<DeliveredUnpack> StallUntilCancelled(string text, CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}
