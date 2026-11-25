using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-1 for #111 at the unpacker: the memory at its real bounds (256 entries per mailbox, 256
/// mailboxes), with what an eviction costs measured as the extra openings at the next delivery; the
/// pause doubling to its cap and a message forgotten once it opens; which outcomes are remembered
/// (a failure of its own whatever its duration, malformed messages, refusals, messages not
/// started, the caller's cancellation and the shutdown); and an honest sender whose real message
/// follows messages naming its key. Manual clocks only.
/// </summary>
public sealed class SlowMemoryQa1Tests
{
    private const string MalloryKid = "did:example:mallory#key-x25519-1";
    private const string SamKid = "did:example:sam#key-x25519-1";
    private static readonly TimeSpan B = Qa1.Budget;
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
    private const int Q = SlowMemory.EntriesPerPartition;
    private const int R = SlowMemory.MaxPartitions;

    // As many messages of one delivery as are opened at the same time, and a named sender's bound
    // (sixteen each: #111, owner decision G).
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;
    private const int PerSender = UnpackReservations.MaxPerSender;

    // ---- eviction at the real bounds (I-11, R-4) ---------------------------------------------------

    /// <summary>
    /// Q + 1 and 2Q entries in one mailbox: the least recently seen go. Replaying every id costs
    /// exactly one opening per evicted entry (1 and 256), and nothing else is opened.
    /// </summary>
    [Theory]
    [InlineData(Q + 1, 1)]
    [InlineData(2 * Q, Q)]
    public async Task Beyond_a_mailboxs_bound_the_least_recently_seen_go_and_each_costs_one_opening(int struck, int evicted)
    {
        var (unpacker, calls) = Opening();
        var ids = Enumerable.Range(0, struck).Select(i => "q" + i).ToArray();
        foreach (var id in ids)
            unpacker.Memory.Strike(Qa1.Bob, id);

        unpacker.Memory.Count.Should().Be(Q);
        await ReplayAsync(unpacker, Qa1.Bob, ids);

        calls.Count.Should().Be(evicted);
        calls.Labels.Should().BeEquivalentTo(ids.Take(evicted), "the oldest are the ones evicted");
    }

    /// <summary>
    /// A delivery refreshes what it sees: of 256 remembered, the first 128 are delivered again (held
    /// back), then 128 new ones are struck. The 128 not seen go; replaying all 384 opens exactly
    /// those 128.
    /// </summary>
    [Fact]
    public async Task Entries_seen_in_a_delivery_outlive_eviction_and_only_the_unseen_cost_an_opening()
    {
        var (unpacker, calls) = Opening();
        var first = Enumerable.Range(0, Q).Select(i => "q" + i).ToArray();
        foreach (var id in first)
            unpacker.Memory.Strike(Qa1.Bob, id);
        await ReplayAsync(unpacker, Qa1.Bob, first.Take(128).ToArray());
        calls.Count.Should().Be(0);
        var added = Enumerable.Range(Q, 128).Select(i => "q" + i).ToArray();
        foreach (var id in added)
            unpacker.Memory.Strike(Qa1.Bob, id);

        await ReplayAsync(unpacker, Qa1.Bob, first.Concat(added).ToArray());

        calls.Labels.Should().BeEquivalentTo(first.Skip(128));
    }

    /// <summary>
    /// R + 1 mailboxes: the least recently used one goes, whole. A delivery to mailbox 0 just before
    /// the 257th mailbox's first strike keeps mailbox 0, so mailbox 1 goes; replaying every
    /// mailbox opens exactly mailbox 1's four messages.
    /// </summary>
    [Fact]
    public async Task Beyond_the_bound_on_mailboxes_the_least_recently_used_goes_and_costs_its_own_openings_only()
    {
        var (unpacker, calls) = Opening();
        var scopes = Enumerable.Range(0, R + 1).Select(i => new DeliveryScope(FakePickupExchange.M, "did:example:local-" + i)).ToArray();
        var ids = Enumerable.Range(0, 4).Select(i => "q" + i).ToArray();
        for (var s = 0; s < R; s++)
        {
            foreach (var id in ids)
                unpacker.Memory.Strike(scopes[s], id);
        }
        await ReplayAsync(unpacker, scopes[0], ids);
        foreach (var id in ids)
            unpacker.Memory.Strike(scopes[R], id);

        unpacker.Memory.Partitions.Should().Be(R);
        foreach (var scope in scopes)
            await ReplayAsync(unpacker, scope, ids);
        calls.Count.Should().Be(4);
        unpacker.Memory.HeldBack(scopes[1], ids).Should().OnlyContain(h => !h);
    }

    /// <summary>A mailbox whose last entry is forgotten frees its place: with 256 mailboxes kept, emptying one and striking in a new one evicts none of the others.</summary>
    [Fact]
    public void A_mailbox_emptied_by_forgetting_frees_its_place_without_evicting_another()
    {
        var memory = new SlowMemory(new ManualTimeProvider(), 30 * B, 180 * B, Q, R);
        var scopes = Enumerable.Range(0, R + 1).Select(i => new DeliveryScope(FakePickupExchange.M, "did:example:local-" + i)).ToArray();
        for (var s = 0; s < R; s++)
            memory.Strike(scopes[s], "q");

        memory.Forget(scopes[0], "q");
        memory.Strike(scopes[R], "q");

        memory.Partitions.Should().Be(R);
        Enumerable.Range(1, R).Should().OnlyContain(s => memory.HeldBack(scopes[s], new[] { "q" })[0]);
    }

    /// <summary>
    /// Ten thousand new ids struck in one mediator's partition for Bob: that partition stays at 256
    /// entries, and another mediator's 256 entries for Bob all survive.
    /// </summary>
    [Fact]
    public void Id_churn_in_one_mediators_partition_never_evicts_another_mediators_entries()
    {
        var memory = new SlowMemory(new ManualTimeProvider(), 30 * B, 180 * B, Q, R);
        var honest = new DeliveryScope("did:example:mediator-y", FakePickupExchange.L);
        var churning = new DeliveryScope("did:example:mediator-x", FakePickupExchange.L);
        var kept = Enumerable.Range(0, Q).Select(i => (string?)("y-" + i)).ToArray();
        foreach (var id in kept)
            memory.Strike(honest, id);

        for (var i = 0; i < 10_000; i++)
            memory.Strike(churning, "x-" + i);

        memory.Count.Should().Be(2 * Q);
        memory.HeldBack(honest, kept).Should().OnlyContain(h => h);
    }

    /// <summary>
    /// Null and empty ids at the memory: a null id is never remembered or held back; an empty id is
    /// an id like any other (the fetch path never hands one over, being unusable there).
    /// </summary>
    [Fact]
    public void A_null_id_is_never_remembered_and_an_empty_id_is_an_exact_id()
    {
        var memory = new SlowMemory(new ManualTimeProvider(), 30 * B, 180 * B, Q, R);

        memory.Strike(Qa1.Bob, null);
        memory.Strike(Qa1.Bob, "");

        memory.Count.Should().Be(1);
        memory.HeldBack(Qa1.Bob, new string?[] { "", null, " " }).Should().Equal(true, false, false);
        memory.Forget(Qa1.Bob, "");
        memory.Partitions.Should().Be(0);
    }

    // ---- the pause (D-2, I-12) -----------------------------------------------------------------

    /// <summary>
    /// D-2 through real strikes: a message that never opens is held back for 10, 20, 40 and 80
    /// minutes, then the cap of 256 budgets (QA1-1: Q·B, about 85 minutes), and the cap again: never a
    /// tick longer, never a tick shorter. When it opens at the next delivery it is forgotten, so the
    /// next time it is slow its pause starts again at 10 minutes.
    /// </summary>
    [Fact]
    public async Task The_pause_doubles_to_its_cap_and_a_message_that_opens_is_forgotten()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var opens = false;
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                return Volatile.Read(ref opens) ? UnpackFakes.Opened("q1") : await UnpackFakes.StallUntilCancelled(ct);
        }, B, time);
        var message = new[] { Qa1.Message(Qa1.Anon("q1"), "q1") };
        async Task<bool> DeliverAsync()
        {
            var before = calls.Count;
            var delivery = unpacker.UnpackAllAsync(Qa1.Bob, message, "request", default);
            // Held back: decided at once, with no host call. (A message that opens at once can also
            // be decided by the time the call above returns; its host call was made then.)
            if (delivery.IsCompleted && calls.Count == before)
                return false;
            await calls.WaitForAsync(before + 1, RealTime);
            if (!Volatile.Read(ref opens))
                time.Advance(B);
            (await delivery.WaitAsync(RealTime)).Single().Succeeded.Should().Be(Volatile.Read(ref opens));
            return true;
        }

        foreach (var pause in new[] { 30 * B, 60 * B, 120 * B, 240 * B, 256 * B, 256 * B })
        {
            (await DeliverAsync()).Should().BeTrue("its pause has ended");
            (await DeliverAsync()).Should().BeFalse("its pause of {0} has started", pause);
            time.Advance(pause - TimeSpan.FromTicks(1));
            (await DeliverAsync()).Should().BeFalse("one tick before its pause of {0} ends", pause);
            time.Advance(TimeSpan.FromTicks(1));
        }
        Volatile.Write(ref opens, true);
        (await DeliverAsync()).Should().BeTrue();
        unpacker.Memory.Count.Should().Be(0);
        unpacker.Memory.Partitions.Should().Be(0);

        Volatile.Write(ref opens, false);
        (await DeliverAsync()).Should().BeTrue();
        time.Advance(30 * B - TimeSpan.FromTicks(1));
        (await DeliverAsync()).Should().BeFalse();
        time.Advance(TimeSpan.FromTicks(1));
        (await DeliverAsync()).Should().BeTrue("its pause started again at the first one");
    }

    // ---- what is remembered (I-9) ----------------------------------------------------------------

    /// <summary>
    /// I-9 as amended (owner decision C): a message whose host call fails on its own is remembered
    /// whatever the duration: at once, a tick after it started, at the former slow threshold (0.6 s)
    /// and a tick either side of it, and after 4.9 s.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(5_999_999L)]
    [InlineData(6_000_000L)]
    [InlineData(6_000_001L)]
    [InlineData(49_000_000L)]
    public async Task A_failure_is_remembered_whatever_its_duration(long ticks)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var fail = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            return fail.Task;
        }, B, time);

        var delivery = unpacker.UnpackAllAsync(Qa1.Bob, new[] { Qa1.Message(Qa1.Anon("q1"), "q1") }, "request", default);
        await calls.WaitForAsync(1, RealTime);
        time.Advance(TimeSpan.FromTicks(ticks));
        fail.SetException(new CryptoException("JWE could not be decrypted."));
        (await delivery.WaitAsync(RealTime)).Single().Error.Should().BeOfType<CryptoException>();

        unpacker.Memory.HeldBack(Qa1.Bob, new[] { "q1" }).Single().Should().BeTrue("it failed {0} ticks after it started", ticks);
    }

    /// <summary>
    /// I-9: a message that is not valid UTF-8, or is empty, fails as malformed with no host call and
    /// is never remembered, while a message in the same delivery that ran until the budget ended is.
    /// </summary>
    [Fact]
    public async Task A_malformed_message_is_never_remembered()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, B, time);
        var messages = new[]
        {
            new DeliveredInput("not-utf8", new byte[] { 0xFF, 0xFE, 0x7B }),
            new DeliveredInput("empty", ReadOnlyMemory<byte>.Empty),
            Qa1.Message(Qa1.Anon("slow"), "slow"),
        };

        var delivery = unpacker.UnpackAllAsync(Qa1.Bob, messages, "request", default);
        await calls.WaitForAsync(1, RealTime);
        time.Advance(B);
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes[0].Error.Should().BeOfType<MalformedMessageException>();
        outcomes[1].Error.Should().BeOfType<MalformedMessageException>();
        calls.Labels.Should().Equal("slow");
        unpacker.Memory.HeldBack(Qa1.Bob, new[] { "not-utf8", "empty", "slow" }).Should().Equal(false, false, true);
    }

    /// <summary>
    /// I-9: a message refused for its sender's partition, and a message that did not start because
    /// the budget ended before its turn, are never remembered; the messages that ran for the whole
    /// budget are. Mallory's sixteen lookups (her bound) ignore their token and still run; her
    /// seventeenth message is refused. Behind it are thirteen more messages than are opened at
    /// once: without a time limit per message (#111, owner decision E) the sixteen that start keep
    /// the workers until the budget ends, so the thirteen behind them do not start.
    /// </summary>
    [Fact]
    public async Task A_refusal_and_a_message_not_started_are_never_remembered()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            using (calls.Enter(label, ct))
            {
                if (label.StartsWith("mallory", StringComparison.Ordinal))
                    return await never.Task;
                return await UnpackFakes.StallUntilCancelled(ct);
            }
        }, B, time);
        try
        {
            PerSender.Should().BeLessThanOrEqualTo(AtOnce, "Mallory's first delivery starts all of its messages at once");
            var first = unpacker.UnpackAllAsync(Qa1.Bob, Enumerable.Range(0, PerSender).Select(i => Qa1.Message(Qa1.From(MalloryKid, "mallory-" + i), "mallory-" + i)).ToArray(), "request-1", default);
            await calls.WaitForAsync(PerSender, RealTime);
            time.Advance(B);
            await first.WaitAsync(RealTime);

            const string refused = "mallory-refused"; // her seventeenth
            const int behind = 13; // how many wait behind the sixteen that start
            var messages = new[] { Qa1.Message(Qa1.From(MalloryKid, refused), refused) }
                .Concat(Enumerable.Range(0, AtOnce + behind).Select(i => Qa1.Message(Qa1.Anon("s-" + i), "s-" + i))).ToArray();
            var second = unpacker.UnpackAllAsync(Qa1.Bob, messages, "request-2", default);
            await calls.WaitForAsync(PerSender + AtOnce, RealTime);
            time.Advance(B);
            var outcomes = await second.WaitAsync(RealTime);

            outcomes[0].Error!.Message.Should().Contain("capacity exhausted");
            outcomes.Skip(1).Take(AtOnce).Should().OnlyContain(o => o.Error!.Message.Contains("ran out while it was being opened"));
            outcomes.Skip(1 + AtOnce).Should().HaveCount(behind).And.OnlyContain(o => o.Error!.Message.Contains("before its turn came"));
            calls.Count.Should().Be(PerSender + AtOnce, "the {0} of the first delivery and the {1} that started in the second", PerSender, AtOnce);
            calls.Labels.Should().NotContain(refused, "the refused message never started");
            unpacker.Memory.HeldBack(Qa1.Bob, new[] { refused }).Single().Should().BeFalse("a refusal is not remembered");
            unpacker.Memory.HeldBack(Qa1.Bob, Enumerable.Range(AtOnce, behind).Select(i => (string?)("s-" + i)).ToArray()).Should().OnlyContain(h => !h, "a message that did not start is not remembered");
            unpacker.Memory.HeldBack(Qa1.Bob, Enumerable.Range(0, AtOnce).Select(i => (string?)("s-" + i)).ToArray()).Should().OnlyContain(h => h, "the {0} that ran for the whole budget are", AtOnce);
        }
        finally
        {
            never.TrySetCanceled();
        }
    }

    /// <summary>
    /// I-9 with sixteen messages (as many as are opened at once) still running, fourteen of them
    /// for 10 s and two for 7 s, and two that failed after 3 s before: when the caller cancels, or
    /// the owner shuts down, the two that failed slowly stay remembered (decided before) and none of
    /// the sixteen running is. Two more messages than are opened at once, so the last two start only
    /// once the two that fail have their outcomes and their workers are free.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_callers_cancellation_or_the_shutdown_remembers_none_of_the_messages_running(bool shutdown)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        using var stop = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label.StartsWith("quick", StringComparison.Ordinal))
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(3), time, ct);
                using (calls.Enter(label, ct))
                    await delay;
                throw new CryptoException("JWE could not be decrypted.");
            }
            using (calls.Enter(label, ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, B, time, shutdown ? stop.Token : default);
        var messages = new[] { "quick-0", "quick-1" }.Concat(Enumerable.Range(0, AtOnce).Select(i => "run-" + i)).Select(l => Qa1.Message(Qa1.Anon(l), l)).ToArray();

        var delivery = unpacker.UnpackAllAsync(Qa1.Bob, messages, "request", shutdown ? default : caller.Token);
        // The two that fail and the first fourteen of the others hold the sixteen workers.
        await calls.WaitForAsync(AtOnce, RealTime);
        time.Advance(TimeSpan.FromSeconds(3));
        // The last two started: the workers that took them had recorded the two failures first.
        await calls.WaitForAsync(AtOnce + 2, RealTime);
        time.Advance(TimeSpan.FromSeconds(7));
        if (shutdown)
            stop.Cancel();
        else
            caller.Cancel();
        var ended = await Record.ExceptionAsync(() => delivery.WaitAsync(RealTime));

        ended.Should().BeAssignableTo<OperationCanceledException>();
        unpacker.Memory.HeldBack(Qa1.Bob, messages.Select(m => m.Id).ToArray()).Should().Equal(new[] { true, true }.Concat(Enumerable.Repeat(false, AtOnce)));
    }

    // ---- D-13: an honest slow sender after messages naming its key ---------------------------------

    /// <summary>
    /// QA1-5 restated (#111 owner decision E). It pinned a residual of the per-message time limit:
    /// messages naming Sam's key were cut at their share and still held Sam's partition when Sam's
    /// real message was taken, which was then refused once. Without that limit nothing is cut: the
    /// sixteen messages naming Sam's key (Sam's bound, and as many as are opened at once; Sam's
    /// lookups take 5.5 s) fill Sam's partition and fail on their own, their workers give their
    /// reservations back before taking the next message, and Sam's real message, taken after
    /// eleven that fail at once, is not refused and opens in the same delivery.
    /// </summary>
    [Fact]
    public async Task A_slow_senders_real_message_after_messages_naming_its_key_is_not_refused_and_opens_in_the_same_delivery()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var lookup = TimeSpan.FromSeconds(5.5);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label.StartsWith("fast", StringComparison.Ordinal))
                throw new CryptoException("JWE could not be decrypted.");
            var delay = Task.Delay(lookup, time, ct);
            using (calls.Enter(label, ct))
                await delay;
            return label == "sam" ? UnpackFakes.Opened(label) : throw new CryptoException("JWE could not be decrypted.");
        }, B, time);
        PerSender.Should().Be(AtOnce, "the messages naming Sam's key fill Sam's partition and hold every worker");
        var messages = Enumerable.Range(0, PerSender).Select(i => Qa1.Message(Qa1.From(SamKid, "named-" + i), "named-" + i))
            .Concat(Enumerable.Range(0, 11).Select(i => Qa1.Message(Qa1.Anon("fast-" + i), "fast-" + i)))
            .Append(Qa1.Message(Qa1.From(SamKid, "sam"), "sam"))
            .ToArray();

        var delivery = unpacker.UnpackAllAsync(Qa1.Bob, messages, "request", default);
        await calls.WaitForAsync(PerSender, RealTime);
        unpacker.Reserved.Should().Be(PerSender, "Sam's partition is full");
        calls.Labels.Should().NotContain("sam", "Sam's real message waits for a worker");
        time.Advance(lookup); // 5.5 s: the sixteen naming Sam's key fail on their own
        await calls.WaitForAsync(PerSender + 1, RealTime);
        time.Advance(lookup); // 11 s: Sam's own lookup ends
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes.Take(PerSender + 11).Should().OnlyContain(o => o.Error is CryptoException, "nothing was cut or refused");
        outcomes[PerSender + 11].Succeeded.Should().BeTrue("Sam's partition held none of the finished lookups when its real message was taken");
        calls.Labels.Should().HaveCount(PerSender + 1).And.Contain("sam");
        unpacker.Memory.HeldBack(Qa1.Bob, new[] { "sam" }).Single().Should().BeFalse("a message that opened is not remembered");
        unpacker.Reserved.Should().Be(0);
    }

    // ---- helpers -------------------------------------------------------------------------------

    // An unpacker whose host opens every message at once; the log counts the openings.
    private static (DeliveredMessageUnpacker Unpacker, HostCallLog Calls) Opening()
    {
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            using (calls.Enter(label, ct))
                return Task.FromResult(UnpackFakes.Opened(label));
        }, B, new ManualTimeProvider());
        return (unpacker, calls);
    }

    // Deliver `ids` once (each opens at once unless held back, and is then forgotten) and wait for
    // the outcomes.
    private static async Task ReplayAsync(DeliveredMessageUnpacker unpacker, DeliveryScope scope, IReadOnlyList<string> ids)
    {
        var outcomes = await unpacker.UnpackAllAsync(scope, ids.Select(id => Qa1.Message(Qa1.Anon(id), id)).ToArray(), "replay", default).WaitAsync(RealTime);
        outcomes.Should().OnlyContain(o => o.Succeeded || (o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before")));
    }
}
