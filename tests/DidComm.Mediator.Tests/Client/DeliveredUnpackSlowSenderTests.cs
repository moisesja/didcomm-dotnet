using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The unpacker of #111 (I-1..I-14): the share of the time left with its floor and start rule, the
/// memory of messages slow to open and what it holds back, and the reservations of host calls per
/// container, per sender and per mailbox. Every timing is driven by the injected
/// <see cref="ManualTimeProvider"/> and rendezvous on host calls and specific timers (L-065); real
/// time is only a hang guard.
/// </summary>
public sealed class DeliveredUnpackSlowSenderTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const string SamKid = "did:example:sam#key-x25519-1";

    // ---- the share (I-6, I-7) ---------------------------------------------------------------

    /// <summary>
    /// T2 (I-7): ninety-nine messages that fail at once, then an honest message last whose sender's
    /// DID takes 15 s to resolve. It is taken with the whole budget left and nothing behind it, so its
    /// share is the time left (20 s), not the static share or the floor, and it opens.
    /// </summary>
    [Fact]
    public async Task Formula_honest_last_gets_the_time_left()
    {
        var time = new ManualTimeProvider();
        var resolution = TimeSpan.FromSeconds(15);
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            if (text.StartsWith("junk", StringComparison.Ordinal))
                throw new CryptoException("JWE could not be decrypted.");
            await Task.Delay(resolution, time, ct);
            return UnpackFakes.Opened(text);
        }, Budget, time);
        var payloads = Enumerable.Range(0, 99).Select(i => "junk-" + i).Append("alice-0").Select(t => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(t)).ToArray();

        var mark = time.Mark();
        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        await time.WaitForTimerAsync(resolution, mark, RealTime);
        time.Advance(resolution);
        var outcomes = await batch.WaitAsync(RealTime);

        outcomes.Take(99).Should().OnlyContain(o => o.Error is CryptoException);
        outcomes[99].Succeeded.Should().BeTrue("taken last with {0} left, its share is the time left, not {1}", Budget, DeliveredMessageUnpacker.ShareOf(Budget, payloads.Length));
    }

    /// <summary>
    /// T3 (I-6): a message taken when less than the floor of the budget is left does not start; one
    /// taken with exactly the floor left does. Seventeen stalling messages under 20 s (floor 5 s):
    /// rounds start at 0, 5 and 10 s; one of the third round fails on its own at 12 s, so its worker
    /// starts the thirteenth message then (share 5 s, to 17 s); the rest of the third round end at
    /// 15 s and their workers start the last three with exactly 5 s left. At 17 s the thirteenth
    /// message's worker takes the seventeenth with 3 s left: it fails as out of time before its
    /// turn, and its host code never runs.
    /// </summary>
    [Fact]
    public async Task A_message_taken_with_less_than_the_floor_left_does_not_start()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            if (text == "s-8")
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(2), time, ct);
                using (calls.Enter(text, ct))
                    await delay;
                throw new CryptoException("JWE could not be decrypted.");
            }
            using (calls.Enter(text, ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, time);
        var payloads = Enumerable.Range(0, 17).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("s-" + i)).ToArray();
        unpacker.Floor.Should().Be(TimeSpan.FromSeconds(5));

        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        await calls.WaitForAsync(4, RealTime);
        time.Advance(TimeSpan.FromSeconds(5));
        await calls.WaitForAsync(8, RealTime);
        time.Advance(TimeSpan.FromSeconds(5));
        await calls.WaitForAsync(12, RealTime);
        time.Advance(TimeSpan.FromSeconds(2)); // 12 s: s-8 fails, s-12 starts
        await calls.WaitForAsync(13, RealTime);
        time.Advance(TimeSpan.FromSeconds(3)); // 15 s: s-13..s-15 start with exactly the floor left
        await calls.WaitForAsync(16, RealTime);
        time.Advance(TimeSpan.FromSeconds(2)); // 17 s: s-12's share ends; s-16 is taken
        SpinWait.SpinUntil(() => unpacker.Settled >= 14 || calls.Count > 16, RealTime).Should().BeTrue();

        calls.Count.Should().Be(16, "the seventeenth message was taken with less than the floor left");
        calls.Labels.Should().NotContain("s-16");
        time.Advance(TimeSpan.FromSeconds(3));
        var outcomes = await batch.WaitAsync(RealTime);
        outcomes[16].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        outcomes.Skip(13).Take(3).Should().OnlyContain(o => o.Error!.Message.Contains("while it was being opened"), "taken with exactly the floor left, they started");
    }

    // ---- what is remembered (I-9) ------------------------------------------------------------

    /// <summary>
    /// T4 (I-9, D-6): a message whose host call failed after 4.9 s is remembered and held back at its
    /// next delivery; one that failed after 0.5 s (under the 0.6 s slow threshold) is not, and is
    /// opened again.
    /// </summary>
    [Fact]
    public async Task A_failure_that_cost_the_slow_threshold_is_remembered_and_a_quicker_one_is_not()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            var delay = Task.Delay(label == "slow" ? TimeSpan.FromSeconds(4.9) : TimeSpan.FromSeconds(0.5), time, ct);
            using (calls.Enter(label, ct))
                await delay;
            throw new CryptoException("JWE could not be decrypted.");
        }, Budget, time);
        unpacker.SlowThreshold.Should().Be(TimeSpan.FromSeconds(0.6));
        var messages = new[] { Message(TestJwe.Authcrypt(AliceKid, "slow"), "slow"), Message(TestJwe.Authcrypt(AliceKid, "quick"), "quick") };

        var first = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(2, RealTime);
        time.Advance(TimeSpan.FromSeconds(0.5));
        time.Advance(TimeSpan.FromSeconds(4.4));
        (await first.WaitAsync(RealTime)).Should().OnlyContain(o => o.Error is CryptoException);

        unpacker.Memory.HeldBack(Bob, new[] { "slow", "quick" }).Should().Equal(true, false);

        var second = unpacker.UnpackAllAsync(Bob, messages, "request-2", default);
        await calls.WaitForAsync(3, RealTime);
        time.Advance(TimeSpan.FromSeconds(0.5));
        var again = await second.WaitAsync(RealTime);
        again[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("slow to open when it was delivered before");
        again[1].Error.Should().BeOfType<CryptoException>("the quick failure is opened again");
        calls.Labels.Count(l => l == "slow").Should().Be(1, "the remembered message was not opened again");
        calls.Labels.Count(l => l == "quick").Should().Be(2);
    }

    /// <summary>
    /// I-9 with I-3: a message whose share ends before its work item runs never has its host code
    /// run, and is not remembered, although its share ran for more than the slow threshold. The
    /// clock jumps past the first message's share right after its start is read, so its work item is
    /// created with a cancelled token. The messages after it run and are remembered when the budget
    /// ends them.
    /// </summary>
    [Fact]
    public async Task A_message_whose_share_ended_before_its_host_call_ran_is_not_remembered()
    {
        var manual = new ManualTimeProvider();
        var calls = new HostCallLog();
        var first = DeliveredMessageUnpacker.ShareOf(Budget, 5); // the first of five gets half the budget
        var time = new JumpAfterTimer(manual, timerNumber: 2, jump: first); // timer 1 is the budget, 2 the first share
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);
        var messages = Enumerable.Range(0, 5).Select(i => Message(TestJwe.Anoncrypt("q" + i), "q" + i)).ToArray();

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(4, RealTime);
        time.Jumped.Should().BeTrue();
        manual.Advance(Budget);
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("its share");
        calls.Labels.Should().NotContain("q0", "its token was cancelled before its work item ran");
        unpacker.Memory.HeldBack(Bob, new[] { "q0", "q1", "q2", "q3", "q4" }).Should().Equal(false, true, true, true, true);
    }

    /// <summary>A remembered message whose pause has ended is opened again; once it opens it is forgotten.</summary>
    [Fact]
    public async Task A_remembered_message_is_opened_after_its_pause_and_forgotten_once_it_opens()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker((packed, _) => Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed))), Budget, time);
        unpacker.Memory.Strike(Bob, "q1");
        unpacker.Memory.FirstPause.Should().Be(30 * Budget);
        time.Advance(unpacker.Memory.FirstPause);

        var outcomes = await unpacker.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-1", default).WaitAsync(RealTime);

        outcomes.Single().Succeeded.Should().BeTrue();
        unpacker.Memory.Count.Should().Be(0);
    }

    /// <summary>
    /// T14 (I-9): a message still running when the caller cancels, or when the owner shuts down, is
    /// not remembered, however long it ran; one still running when the budget itself ends is.
    /// </summary>
    [Fact]
    public async Task Neither_the_callers_cancellation_nor_the_shutdown_remembers_a_message_but_the_budgets_end_does()
    {
        // The caller cancels after 10 s.
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var byCaller = new DeliveredMessageUnpacker(Stall(calls), Budget, time);
        using (var caller = new CancellationTokenSource())
        {
            var delivery = byCaller.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-1", caller.Token);
            await calls.WaitForAsync(1, RealTime);
            time.Advance(TimeSpan.FromSeconds(10));
            caller.Cancel();
            await FluentActions.Awaiting(() => delivery.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>();
        }
        byCaller.Memory.Count.Should().Be(0, "the caller's cancellation says nothing about the message");

        // The owner shuts down after 10 s.
        using var shutdown = new CancellationTokenSource();
        var byShutdown = new DeliveredMessageUnpacker(Stall(calls), Budget, time, shutdown.Token);
        var stopped = byShutdown.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-2", default);
        await calls.WaitForAsync(2, RealTime);
        time.Advance(TimeSpan.FromSeconds(10));
        shutdown.Cancel();
        await FluentActions.Awaiting(() => stopped.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>();
        byShutdown.Memory.Count.Should().Be(0, "the shutdown says nothing about the message");

        // The control: the budget ends.
        var byBudget = new DeliveredMessageUnpacker(Stall(calls), Budget, time);
        var ended = byBudget.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-3", default);
        await calls.WaitForAsync(3, RealTime);
        time.Advance(Budget);
        (await ended.WaitAsync(RealTime)).Single().Error.Should().BeOfType<TimeoutException>();
        byBudget.Memory.HeldBack(Bob, new[] { "q1" }).Single().Should().BeTrue("still running when the budget ended, it was slow");
    }

    /// <summary>
    /// I-5 with the memory's clock readings: a host clock whose readings throw from the moment the
    /// budget ends still lets the budget end the delivery (nothing is remembered: a reading that
    /// failed strikes nothing).
    /// </summary>
    [Fact]
    public async Task A_clock_whose_readings_throw_when_the_budget_ends_still_ends_the_delivery()
    {
        var manual = new ManualTimeProvider();
        var time = new ReadingsThrowWhenArmed(manual);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        time.Armed = true;
        manual.Advance(Budget);
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes.Single().Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        unpacker.Memory.Count.Should().Be(0);
    }

    // ---- held back (I-13) --------------------------------------------------------------------

    /// <summary>
    /// T5 (I-13): four remembered messages ahead of four others. They fail at once without a host call
    /// or a reservation, and the shares are sized from the four others alone: a delivery of four gets
    /// the whole budget each, so nothing is cut at 10 s (what eight would have had).
    /// </summary>
    [Fact]
    public async Task Held_back_messages_make_no_host_call_take_no_reservation_and_are_left_out_of_the_shares()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);
        for (var i = 0; i < 4; i++)
            unpacker.Memory.Strike(Bob, "held-" + i);
        var messages = Enumerable.Range(0, 4).Select(i => Message(TestJwe.Anoncrypt("held-" + i), "held-" + i))
            .Concat(Enumerable.Range(0, 4).Select(i => Message(TestJwe.Anoncrypt("new-" + i), "new-" + i)))
            .ToArray();

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(4, RealTime);

        calls.Labels.Should().BeEquivalentTo(new[] { "new-0", "new-1", "new-2", "new-3" }, "a held-back message runs no host code");
        unpacker.Reserved.Should().Be(4, "a held-back message takes no reservation");
        time.Advance(DeliveredMessageUnpacker.ShareOf(Budget, messages.Length));
        calls.Tokens.Should().OnlyContain(t => !t.IsCancellationRequested, "the shares were sized for the four that are opened");

        time.Advance(Budget - DeliveredMessageUnpacker.ShareOf(Budget, messages.Length));
        var outcomes = await delivery.WaitAsync(RealTime);
        outcomes.Take(4).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("slow to open when it was delivered before") && o.Error.Message.Contains("'request-1'"));
        outcomes.Skip(4).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened"));
    }

    /// <summary>I-13: a delivery made only of remembered messages returns at once, with no timer and no host call.</summary>
    [Fact]
    public async Task A_delivery_of_only_held_back_messages_returns_at_once_without_a_timer()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);
        unpacker.Memory.Strike(Bob, "q1");
        unpacker.Memory.Strike(Bob, "q2");

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1"), Message(TestJwe.Anoncrypt("q2"), "q2") }, "request-1", default);

        delivery.IsCompleted.Should().BeTrue();
        time.ScheduledTimers.Should().Be(0);
        unpacker.Opening.Should().Be(0);
        (await delivery).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("slow to open"));
        calls.Count.Should().Be(0);
    }

    /// <summary>
    /// The same-delivery bound, carried across deliveries (I-6, I-7, I-13): twenty messages that never
    /// open ahead of an honest one. In the first delivery sixteen of them start, each with the floor
    /// of 5 s, and the budget ends before the honest one's turn; all sixteen are remembered. At the
    /// next delivery of the same ids they are held back, and the honest message opens.
    /// </summary>
    [Fact]
    public async Task Twenty_stalling_first_the_honest_one_opens_in_the_next_delivery()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            using (calls.Enter(label, ct))
            {
                if (label == "honest")
                    return UnpackFakes.Opened(label);
                return await UnpackFakes.StallUntilCancelled(ct);
            }
        }, Budget, time);
        var messages = Enumerable.Range(0, 20).Select(i => Message(TestJwe.Anoncrypt("stall-" + i), "stall-" + i))
            .Append(Message(TestJwe.Authcrypt(AliceKid, "honest"), "honest"))
            .ToArray();

        var first = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        for (var round = 1; round <= 4; round++)
        {
            await calls.WaitForAsync(4 * round, RealTime);
            time.Advance(unpacker.Floor);
        }
        var outcomes = await first.WaitAsync(RealTime);

        outcomes[20].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        calls.Count.Should().Be(16, "four rounds of four start, each with the floor");
        unpacker.Memory.HeldBack(Bob, Enumerable.Range(0, 16).Select(i => (string?)("stall-" + i)).ToArray()).Should().OnlyContain(h => h, "all sixteen were slow");

        var settled = unpacker.Settled;
        var second = unpacker.UnpackAllAsync(Bob, messages, "request-2", default);
        await calls.WaitForAsync(20, RealTime);
        time.Advance(unpacker.ShareAt(5, 0, TimeSpan.Zero)); // the first of the four left gives up its worker
        // Its outcome and the honest message's are recorded before the budget is run out.
        SpinWait.SpinUntil(() => unpacker.Settled == settled + 2, RealTime).Should().BeTrue();
        time.Advance(Budget);
        var again = await second.WaitAsync(RealTime);

        again.Take(16).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("slow to open"));
        again[20].Succeeded.Should().BeTrue("the remembered messages are held back, so the honest one gets its turn");
        calls.Labels.Take(16).Should().OnlyHaveUniqueItems();
        calls.Labels.Count(l => l.StartsWith("stall-", StringComparison.Ordinal) && int.Parse(l.AsSpan(6)) < 16).Should().Be(16, "a held-back message is not opened again");
    }

    // ---- the reservations (I-1, I-2, I-14) ---------------------------------------------------

    public static TheoryData<string, string> Anonymous => new()
    {
        { "an ECDH-ES envelope carrying Alice's skid (T9)", TestJwe.EcdhEsWithSkid(AliceKid, "under-test") },
        { "authcrypt naming Alice only in apu (T10)", TestJwe.AuthcryptApuOnly(AliceKid, "under-test") },
        { "anoncrypt", TestJwe.Anoncrypt("under-test") },
        { "a skid that is not a DID URL", TestJwe.Authcrypt("alice#key-1", "under-test") },
    };

    /// <summary>
    /// T9, T10 (I-14): a message that does not name an authcrypt sender in <c>skid</c> counts against
    /// the mailbox's anonymous partition, not against Alice's: with 16 anonymous lookups running it is
    /// refused, while Alice's authcrypt message opens.
    /// </summary>
    [Theory]
    [MemberData(nameof(Anonymous))]
    public async Task A_message_without_a_named_authcrypt_sender_counts_against_the_anonymous_partition(string name, string packed)
    {
        var (unpacker, time, calls, never) = WithLookupsThatNeverEnd();
        try
        {
            await FillAnonymousPartitionAsync(unpacker, time, calls, Bob);

            var outcomes = await unpacker.UnpackAllAsync(Bob, new[] { Message(packed, "under-test"), Message(TestJwe.Authcrypt(AliceKid, "alice-1"), "alice-1") }, "request-1", default).WaitAsync(RealTime);

            outcomes[0].Error.Should().BeOfType<TimeoutException>(name).Which.Message.Should().Contain("capacity exhausted");
            outcomes[1].Succeeded.Should().BeTrue("Alice's partition is empty");
        }
        finally
        {
            never.TrySetCanceled();
        }
    }

    /// <summary>
    /// T11 (I-1, I-2): thousands of deliveries over three mailboxes, with named and anonymous senders
    /// and every kind of message — opens, fails at once, fails after a second, honours its share,
    /// ignores its token and ends later, held back — and callers that cancel mid-delivery. After every
    /// round the reservations held are exactly the lookups still running (no release missed or
    /// doubled); once the late lookups end, nothing is reserved, no partition is left and no timer is
    /// scheduled.
    /// </summary>
    [Fact]
    public async Task Thousands_of_mixed_deliveries_over_mailboxes_and_senders_end_with_nothing_reserved()
    {
        var time = new ManualTimeProvider();
        var late = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRunning = 0;
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            switch (label[0])
            {
                case 'o': return UnpackFakes.Opened(label);
                case 'f': throw new MalformedMessageException("bad");
                case 'd':
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                    throw new CryptoException("JWE could not be decrypted.");
                case 's': return await UnpackFakes.StallUntilCancelled(ct);
                case 'l':
                    // Ignores its token, ends when released; counted while it runs.
                    Interlocked.Increment(ref lateRunning);
                    try
                    {
                        return await late.Task;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref lateRunning);
                    }
            }
            return UnpackFakes.Opened(label);
        }, Budget, time);
        var scopes = new[] { Bob, new DeliveryScope(FakePickupExchange.M, "did:example:carol"), new DeliveryScope("did:example:other-mediator", FakePickupExchange.L) };
        var senders = new string?[] { null, AliceKid, SamKid, "did:example:mallory#key-1" };
        var kinds = new[] { 'o', 'f', 'd', 's', 'l' };
        var random = new Random(111);
        var maxReserved = 0;

        for (var round = 0; round < 300; round++)
        {
            using var cancel = new CancellationTokenSource();
            var batches = new List<(Task<IReadOnlyList<DeliveredOutcome>> Task, bool Cancelled)>();
            for (var d = 0; d < 6; d++)
            {
                var count = random.Next(1, 13);
                var messages = Enumerable.Range(0, count).Select(i =>
                {
                    // Ids repeat across rounds, so remembered messages are held back too.
                    var label = $"{kinds[random.Next(kinds.Length)]}-{random.Next(40)}";
                    var sender = senders[random.Next(senders.Length)];
                    return Message(sender is null ? TestJwe.Anoncrypt(label) : TestJwe.Authcrypt(sender, label), label);
                }).ToArray();
                var cancelled = d == 5;
                batches.Add((unpacker.UnpackAllAsync(scopes[random.Next(scopes.Length)], messages, "request", cancelled ? cancel.Token : default), cancelled));
            }
            if (round % 3 == 0)
                cancel.Cancel();
            for (var step = 0; step < 4 && batches.Any(b => !b.Task.IsCompleted); step++)
            {
                time.Advance(Budget);
                await Task.Yield();
            }
            foreach (var (task, cancelled) in batches)
            {
                // A cancelled caller's delivery may have ended on its own first: either way it is
                // cancelled, or it has one outcome per message.
                await ((Task)task).ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
                if (cancelled && task.IsCanceled)
                    continue;
                task.IsCompletedSuccessfully.Should().BeTrue();
                (await task).Should().NotContainNulls();
            }
            maxReserved = Math.Max(maxReserved, unpacker.Reserved);
            unpacker.Reserved.Should().BeLessThanOrEqualTo(UnpackReservations.MaxReserved);
            // Every other lookup of the round ends on its own (on the pool); the late ones still run.
            unpacker.Reserved.Should().BeGreaterThanOrEqualTo(Volatile.Read(ref lateRunning), "a running lookup keeps its reservation");
            SpinWait.SpinUntil(() => unpacker.Reserved == Volatile.Read(ref lateRunning), RealTime).Should().BeTrue("the reservations held are exactly the lookups still running (round {0})", round);
            if (round % 50 == 49)
            {
                late.TrySetResult(UnpackFakes.Opened("late"));
                SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every reservation was given back once its call ended");
                unpacker.Reservations.Partitions.Should().Be(0);
                late = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        late.TrySetResult(UnpackFakes.Opened("late"));
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && time.ScheduledTimers == 0 && unpacker.Opening == 0, RealTime).Should().BeTrue();
        unpacker.Reservations.Partitions.Should().Be(0);
        maxReserved.Should().BeGreaterThan(0, "the run did let lookups go");
        unpacker.Memory.Count.Should().BeGreaterThan(0, "the run did remember slow messages");
    }

    /// <summary>
    /// D-13: an honest sender, Sam, whose lookups take 3 s, and ten messages naming Sam's <c>skid</c>
    /// that each take 3 s and then fail, ahead of Sam's own message in one delivery. Each worker gives
    /// its finished call's reservation back before its next take, so inside the delivery Sam's
    /// partition is never full of finished calls and nothing is refused, even while the release
    /// continuations are held back. Every failing message is remembered; at the next delivery they
    /// are held back and Sam's next message opens.
    /// </summary>
    [Fact]
    public async Task A_senders_partition_never_refuses_inside_one_delivery_because_of_its_own_finished_calls()
    {
        var time = new ManualTimeProvider();
        var held = new HeldScheduler();
        var calls = new HostCallLog();
        var lookup = TimeSpan.FromSeconds(3);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            var delay = Task.Delay(lookup, time, ct);
            using (calls.Enter(label, ct))
                await delay;
            return label.StartsWith("sam", StringComparison.Ordinal) ? UnpackFakes.Opened(label) : throw new CryptoException("JWE could not be decrypted.");
        }, Budget, time, reservations: new UnpackReservations(held));
        var junk = Enumerable.Range(0, 10).Select(i => Message(TestJwe.Authcrypt(SamKid, "junk-" + i), "junk-" + i)).ToArray();

        var first = unpacker.UnpackAllAsync(Bob, junk.Append(Message(TestJwe.Authcrypt(SamKid, "sam-1"), "sam-1")).ToArray(), "request-1", default);
        foreach (var started in new[] { 4, 8, 11 })
        {
            await Task.WhenAny(calls.WaitForAsync(started, RealTime), first);
            time.Advance(lookup);
        }
        var outcomes = await first.WaitAsync(RealTime);

        outcomes.Should().NotContain(o => o.Error is TimeoutException, "nothing in the delivery was refused or cut");
        outcomes.Take(10).Should().OnlyContain(o => o.Error is CryptoException);
        outcomes[10].Succeeded.Should().BeTrue("Sam's message is never refused for its partition inside one delivery");
        unpacker.Memory.HeldBack(Bob, junk.Select(m => m.Id).ToArray()).Should().OnlyContain(h => h, "each failure cost 3 s, at least the slow threshold");

        var second = unpacker.UnpackAllAsync(Bob, junk.Append(Message(TestJwe.Authcrypt(SamKid, "sam-2"), "sam-2")).ToArray(), "request-2", default);
        await calls.WaitForAsync(12, RealTime);
        time.Advance(lookup);
        var again = await second.WaitAsync(RealTime);

        again.Take(10).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("slow to open"));
        again[10].Succeeded.Should().BeTrue();
        calls.Labels.Skip(11).Should().Equal("sam-2");
        held.RunAll();
        unpacker.Reserved.Should().Be(0);
    }

    /// <summary>
    /// I-1 with host code that blocks its thread and ignores its token: eighty messages from twenty
    /// mailboxes start at most 64 host calls (so at most 64 threads are held), the rest are refused
    /// for the container; while those calls still block after their budgets, another twenty
    /// deliveries start nothing. When the calls return, every reservation comes back.
    /// </summary>
    [Fact]
    public async Task At_most_C_host_calls_run_with_a_blocking_resolver()
    {
        using var floor = new PoolFloor(parked: UnpackReservations.MaxReserved);
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        using var gate = new ManualResetEventSlim(false);
        var unpacker = new DeliveredMessageUnpacker((text, ct) =>
        {
            using (calls.Enter(text, ct))
                gate.Wait(RealTime); // blocks its thread, ignores its token (bounded only so a red run cannot hang)
            return Task.FromResult(UnpackFakes.Opened(text));
        }, Budget, time);
        const int deliveries = UnpackReservations.MaxReserved / DeliveredMessageUnpacker.FetchConcurrency + 4;
        try
        {
            var wave = Wave(unpacker, deliveries, "first");
            await calls.WaitForAsync(UnpackReservations.MaxReserved, RealTime);
            time.Advance(Budget);
            var outcomes = (await Task.WhenAll(wave).WaitAsync(RealTime)).SelectMany(o => o).ToArray();

            outcomes.Count(o => o.Error is TimeoutException t && t.Message.Contains("capacity exhausted")).Should().Be(deliveries * DeliveredMessageUnpacker.FetchConcurrency - UnpackReservations.MaxReserved);
            unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved, "the blocked calls keep their reservations after their budgets");

            var second = Wave(unpacker, deliveries, "second");
            second.Should().OnlyContain(t => t.IsCompleted, "every message of the second wave was refused at once");
            (await Task.WhenAll(second)).SelectMany(o => o).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("capacity exhausted"));
            calls.Count.Should().Be(UnpackReservations.MaxReserved);
            calls.MaxRunning.Should().Be(UnpackReservations.MaxReserved);
        }
        finally
        {
            gate.Set();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("each reservation comes back when its thread returns");
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// A manual clock that, right after its <c>timerNumber</c>-th timer is created, answers the next
    /// reading and then moves the clock on by <c>jump</c>, firing what came due.
    /// </summary>
    private sealed class JumpAfterTimer(ManualTimeProvider inner, int timerNumber, TimeSpan jump) : TimeProvider
    {
        private int _created;
        private int _armed;
        private int _jumped;

        public bool Jumped => Volatile.Read(ref _jumped) != 0;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
        {
            var reading = inner.GetTimestamp();
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                inner.Advance(jump);
                Volatile.Write(ref _jumped, 1);
            }
            return reading;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (Interlocked.Increment(ref _created) == timerNumber)
                Volatile.Write(ref _armed, 1);
            return timer;
        }
    }

    /// <summary>A manual clock whose readings throw once armed; its timers still fire.</summary>
    private sealed class ReadingsThrowWhenArmed(ManualTimeProvider inner) : TimeProvider
    {
        public volatile bool Armed;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
            => Armed ? throw new InvalidOperationException("the host's clock failed") : inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => inner.CreateTimer(callback, state, dueTime, period);
    }

    private static DeliveredInput Message(string packed, string id) => new(id, TestJwe.Bytes(packed));

    private static Func<string, CancellationToken, Task<DeliveredUnpack>> Stall(HostCallLog calls)
        => async (packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        };

    private static Task<IReadOnlyList<DeliveredOutcome>>[] Wave(DeliveredMessageUnpacker unpacker, int deliveries, string name)
        => Enumerable.Range(0, deliveries)
            .Select(d => unpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, $"did:example:{name}-{d}"),
                Enumerable.Range(0, DeliveredMessageUnpacker.FetchConcurrency).Select(i => Message(TestJwe.Anoncrypt($"{name}-{d}-{i}"), $"{name}-{d}-{i}")).ToArray(),
                $"request-{name}-{d}", default))
            .ToArray();

    // An unpacker whose "junk" lookups never end and ignore their token, and whose others open at once.
    private static (DeliveredMessageUnpacker Unpacker, ManualTimeProvider Time, HostCallLog Calls, TaskCompletionSource<DeliveredUnpack> Never) WithLookupsThatNeverEnd()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label.StartsWith("junk", StringComparison.Ordinal) ? never.Task : Task.FromResult(UnpackFakes.Opened(label));
        }, Budget, time);
        return (unpacker, time, calls, never);
    }

    // Sixteen anonymous lookups that never end, let go at their budgets: the mailbox's anonymous
    // partition is full.
    private static async Task FillAnonymousPartitionAsync(DeliveredMessageUnpacker unpacker, ManualTimeProvider time, HostCallLog calls, DeliveryScope scope)
    {
        var stalled = Enumerable.Range(0, UnpackReservations.MaxPerAnonymous / DeliveredMessageUnpacker.FetchConcurrency)
            .Select(d => unpacker.UnpackAllAsync(scope,
                Enumerable.Range(0, DeliveredMessageUnpacker.FetchConcurrency).Select(i => Message(TestJwe.Anoncrypt($"junk-{d}-{i}"), $"junk-{d}-{i}")).ToArray(),
                "request-junk-" + d, default))
            .ToArray();
        await calls.WaitForAsync(UnpackReservations.MaxPerAnonymous, RealTime);
        time.Advance(Budget);
        await Task.WhenAll(stalled).WaitAsync(RealTime);
        unpacker.Reserved.Should().Be(UnpackReservations.MaxPerAnonymous);
    }
}
