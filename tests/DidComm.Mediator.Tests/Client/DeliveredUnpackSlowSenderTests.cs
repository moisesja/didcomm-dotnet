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
/// The unpacker of #111 (I-1..I-14): one budget per delivery with its start rule and no time limit
/// per message (owner decision E), the memory of messages that did not open and what it holds back,
/// and the reservations of host calls per container, per sender and per mailbox. Every timing is
/// driven by the injected <see cref="ManualTimeProvider"/> and rendezvous on host calls and specific
/// timers (L-065); real time is only a hang guard.
/// </summary>
public sealed class DeliveredUnpackSlowSenderTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private const string SamKid = "did:example:sam#key-x25519-1";

    // ---- one budget, no limit per message (I-5, I-6) ------------------------------------------

    /// <summary>
    /// T2: ninety-nine messages that fail at once, then an honest message last whose sender's DID
    /// takes 15 s to resolve. It is taken with the whole budget left and has no time limit of its
    /// own, so it opens.
    /// </summary>
    [Fact]
    public async Task An_honest_message_last_behind_fast_failures_has_the_time_left_and_opens()
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
        outcomes[99].Succeeded.Should().BeTrue("taken last with {0} left, it runs until it opens", Budget);
    }

    /// <summary>
    /// T3 (I-6): a message taken when less than the floor of the budget is left does not start; one
    /// taken with exactly the floor left does. Six messages under 20 s (floor 5 s), four of them
    /// started at once. One fails on its own at 15 s, and its worker takes the fifth message with
    /// exactly the floor left: it starts. Another fails on its own one tick later, and its worker
    /// takes the sixth with one tick less than the floor left: it fails as out of time before its
    /// turn, and its host code never runs. (The failures are timed by timers of the manual clock:
    /// <c>Task.Delay</c> rounds to milliseconds.)
    /// </summary>
    [Fact]
    public async Task A_message_taken_with_less_than_the_floor_left_does_not_start()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var cutoff = Budget - TimeSpan.FromSeconds(5);
        var failsAfter = new Dictionary<string, TimeSpan> { ["s-0"] = cutoff, ["s-1"] = cutoff + TimeSpan.FromTicks(1) };
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            if (failsAfter.TryGetValue(text, out var after))
            {
                var due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timer = time.CreateTimer(_ => due.TrySetResult(), null, after, Timeout.InfiniteTimeSpan);
                using (calls.Enter(text, ct))
                    await due.Task;
                throw new CryptoException("JWE could not be decrypted.");
            }
            using (calls.Enter(text, ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, time);
        var payloads = Enumerable.Range(0, 6).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("s-" + i)).ToArray();
        unpacker.Floor.Should().Be(TimeSpan.FromSeconds(5));

        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        await calls.WaitForAsync(4, RealTime);
        time.Advance(cutoff); // 15 s: s-0 fails; s-4 is taken with exactly the floor left
        await calls.WaitForAsync(5, RealTime);
        time.Advance(TimeSpan.FromTicks(1)); // one tick later: s-1 fails; s-5 is taken
        SpinWait.SpinUntil(() => unpacker.Settled >= 3 || calls.Count > 5, RealTime).Should().BeTrue("s-0, s-1 and s-5 have their outcomes");

        calls.Count.Should().Be(5, "the sixth message was taken with less than the floor left");
        calls.Labels.Should().NotContain("s-5");
        time.Advance(TimeSpan.FromSeconds(5));
        var outcomes = await batch.WaitAsync(RealTime);
        outcomes[5].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        outcomes[4].Error.Should().BeOfType<TimeoutException>("taken with exactly the floor left, it started").Which.Message.Should().Contain("while it was being opened");
    }

    // ---- what is remembered (I-9): S1, a failure of its own ----------------------------------

    /// <summary>
    /// T4 (S1; owner decision C): a message whose host call fails on its own is remembered whatever
    /// the duration, one failing after 1 tick and one after 4.9 s alike; at its next delivery each is
    /// held back without being opened.
    /// </summary>
    [Fact]
    public async Task A_failure_is_remembered_whatever_its_duration()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            var delay = Task.Delay(label == "slow" ? TimeSpan.FromSeconds(4.9) : TimeSpan.FromTicks(1), time, ct);
            using (calls.Enter(label, ct))
                await delay;
            throw new CryptoException("JWE could not be decrypted.");
        }, Budget, time);
        var messages = new[] { Message(TestJwe.Authcrypt(AliceKid, "slow"), "slow"), Message(TestJwe.Authcrypt(AliceKid, "quick"), "quick") };

        var first = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(2, RealTime);
        time.Advance(TimeSpan.FromTicks(1));
        time.Advance(TimeSpan.FromSeconds(4.9) - TimeSpan.FromTicks(1));
        (await first.WaitAsync(RealTime)).Should().OnlyContain(o => o.Error is CryptoException);

        unpacker.Memory.HeldBack(Bob, new[] { "slow", "quick" }).Should().Equal(true, true);

        var again = await unpacker.UnpackAllAsync(Bob, messages, "request-2", default).WaitAsync(RealTime);
        again.Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
        calls.Count.Should().Be(2, "a remembered message is not opened again");
    }

    /// <summary>
    /// S1 for a message that started late: the four messages in front of it fail on their own 14 s
    /// into the budget, it starts then and fails on its own 2 s later. A failure of its own is
    /// remembered however late the message started.
    /// </summary>
    [Fact]
    public async Task A_failure_of_a_message_that_started_late_is_remembered()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            var delay = Task.Delay(TimeSpan.FromSeconds(label == "late-4" ? 2 : 14), time, ct);
            using (calls.Enter(label, ct))
                await delay;
            throw new CryptoException("JWE could not be decrypted.");
        }, Budget, time);
        var ids = new[] { "front-0", "front-1", "front-2", "front-3", "late-4" };

        var delivery = unpacker.UnpackAllAsync(Bob, ids.Select(id => Message(TestJwe.Anoncrypt(id), id)).ToArray(), "request-1", default);
        await calls.WaitForAsync(4, RealTime);
        time.Advance(TimeSpan.FromSeconds(14));
        await calls.WaitForAsync(5, RealTime);
        time.Advance(TimeSpan.FromSeconds(2));
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes.Should().OnlyContain(o => o.Error is CryptoException);
        TimeSpan.FromSeconds(14).Should().BeGreaterThan(unpacker.StartAllowance);
        unpacker.Memory.HeldBack(Bob, ids).Should().OnlyContain(h => h, "each failed on its own; the fifth 14 s into the budget, which does not matter for a failure of its own");
    }

    /// <summary>
    /// S1 needs the message's code to have run: a host call whose work item fails before the unpack
    /// step is called (the host's clock throws as the work item reads it) is a failure, and is not
    /// remembered.
    /// </summary>
    [Fact]
    public async Task A_failure_of_a_call_whose_code_never_started_is_not_remembered()
    {
        var time = new HostCallsReadingThrows(new ManualTimeProvider());
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);

        var outcomes = await unpacker.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-1", default).WaitAsync(RealTime);

        outcomes.Single().Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("the host's clock failed");
        calls.Count.Should().Be(0, "the unpack step was never called");
        unpacker.Memory.Count.Should().Be(0, "its code never ran");
        unpacker.Reserved.Should().Be(0);
    }

    // ---- what is remembered (I-9): S2, it had the whole budget --------------------------------

    /// <summary>
    /// S2 and its boundary: a message that never opens, alone in its delivery, whose code starts a
    /// given time into the budget (the clock moves on between the budget's start and the start of
    /// its host call, as when the call waits for a thread). When the budget ends it, it is
    /// remembered if its code started within the start allowance (F / 2 = 2.5 s) of the budget's
    /// start, the allowance itself included, and not if it started one tick later than that.
    /// </summary>
    [Theory]
    [InlineData(0L, true)]
    [InlineData(1L, true)]
    [InlineData(25_000_000L, true)] // exactly the start allowance
    [InlineData(25_000_001L, false)] // one tick more
    [InlineData(140_000_000L, false)] // 14 s
    public async Task A_never_opening_message_is_remembered_at_the_budgets_end_only_if_its_code_started_within_the_start_allowance(long startedTicksIntoTheBudget, bool remembered)
    {
        var late = TimeSpan.FromTicks(startedTicksIntoTheBudget);
        var time = UnpackClock.SteppingAfterTheFirstBudget(new ManualTimeProvider(), late);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);
        unpacker.StartAllowance.Should().Be(TimeSpan.FromTicks(25_000_000), "F / 2, an eighth of the budget");

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        time.Inner.Advance(Budget - late);
        (await delivery.WaitAsync(RealTime)).Single().Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");

        unpacker.Memory.HeldBack(Bob, new[] { "q1" }).Single().Should().Be(remembered, "its code started {0} into the budget; the start allowance is {1}", late, unpacker.StartAllowance);
    }

    /// <summary>
    /// S2 on a clock that steps back: the reading taken when a message's code starts is below the
    /// reading taken at the budget's start. When the budget ends the message, it is not remembered.
    /// </summary>
    [Fact]
    public async Task A_never_opening_message_whose_start_reading_is_below_the_budgets_start_is_not_remembered()
    {
        var time = new UnpackClock(new ManualTimeProvider());
        time.AfterTimerCreated = n =>
        {
            if (n == 1)
                time.Shift(TimeSpan.FromSeconds(-1)); // the host's clock steps back right after the budget started
        };
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message(TestJwe.Anoncrypt("q1"), "q1") }, "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        time.Inner.Advance(Budget);
        (await delivery.WaitAsync(RealTime)).Single().Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");

        unpacker.Memory.Count.Should().Be(0, "a start reading below the budget's start says nothing about when the message started");
    }

    /// <summary>
    /// S2, fair to late starts, across two deliveries: four messages that fail on their own 14 s
    /// into the budget, then one that never opens. It starts 14 s into the budget, and when the
    /// budget ends it gets a timeout and is not remembered (it did not have the whole budget), while
    /// the four in front are (S1). At the next delivery of the same messages those four are held
    /// back, so it starts first, with the budget; it still does not open, and is remembered when
    /// the budget ends. Whatever its lookup does when its token is cancelled.
    /// </summary>
    [Theory]
    [InlineData(WhenCancelled.Ends)]
    [InlineData(WhenCancelled.RunsOn)]
    [InlineData(WhenCancelled.ReportsAFailure)]
    public async Task A_message_that_started_late_behind_others_is_not_remembered_at_the_budgets_end_and_is_at_the_next_delivery_where_it_starts_first(WhenCancelled whenCancelled)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Each of the four in front ends when the test says so, on the thread that says so.
        var inFront = Enumerable.Range(0, 4).ToDictionary(i => "front-" + i, _ => new TaskCompletionSource<DeliveredUnpack>());
        var late = NeverOpens.Lookup(whenCancelled, calls, register, never.Task);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (!inFront.TryGetValue(label, out var ends))
                return late(packed, ct);
            calls.Enter(label, ct);
            return ends.Task;
        }, Budget, time);
        var messages = inFront.Keys.Append("late-4").Select(id => Message(TestJwe.Anoncrypt(id), id)).ToArray();
        var ids = messages.Select(m => m.Id).ToArray();
        try
        {
            var first = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
            await calls.WaitForAsync(4, RealTime);
            time.Advance(TimeSpan.FromSeconds(14));
            // The four in front fail on their own, from a pool thread: a worker takes the fifth
            // message there and waits on it before its lookup is let register its callback.
            await Task.Run(() =>
            {
                foreach (var ends in inFront.Values)
                    ends.SetException(new CryptoException("JWE could not be decrypted."));
            }).WaitAsync(RealTime);
            SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue("the four in front have their outcomes");
            await register.OpenAsync(whenCancelled == WhenCancelled.ReportsAFailure ? 1 : 0, RealTime);
            await calls.WaitForAsync(5, RealTime);
            await Task.Run(() => time.Advance(TimeSpan.FromSeconds(6))).WaitAsync(RealTime); // the budget's end
            var outcomes = await first.WaitAsync(RealTime);

            outcomes[4].Error.Should().BeOfType<TimeoutException>("the budget ended it").Which.Message.Should().Contain("ran out while it was being opened");
            outcomes.Take(4).Should().OnlyContain(o => o.Error is CryptoException);
            unpacker.Memory.HeldBack(Bob, ids).Should().Equal(new[] { true, true, true, true, false },
                "the four failed on their own; the fifth started 14 s into the budget, later than the start allowance of {0}, so it did not have the whole budget", unpacker.StartAllowance);

            var second = unpacker.UnpackAllAsync(Bob, messages, "request-2", default);
            await calls.WaitForAsync(6, RealTime);
            await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
            var again = await second.WaitAsync(RealTime);

            again.Take(4).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
            again[4].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");
            calls.Labels.Skip(4).Should().Equal("late-4", "late-4");
            unpacker.Memory.HeldBack(Bob, new[] { "late-4" }).Single().Should().BeTrue("this time it started with the budget, and still did not open");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// I-9 with I-3: a delivery whose budget is over before any of its messages is taken (the clock
    /// moves past it right after the budget started) runs no host code, and nothing is remembered.
    /// </summary>
    [Fact]
    public async Task A_delivery_whose_budget_ended_before_its_messages_were_taken_runs_no_host_code_and_remembers_nothing()
    {
        var time = UnpackClock.SteppingAfterTheFirstBudget(new ManualTimeProvider(), Budget);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(Stall(calls), Budget, time);
        var messages = Enumerable.Range(0, 5).Select(i => Message(TestJwe.Anoncrypt("q" + i), "q" + i)).ToArray();

        var outcomes = await unpacker.UnpackAllAsync(Bob, messages, "request-1", default).WaitAsync(RealTime);

        outcomes.Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("before its turn came"));
        calls.Count.Should().Be(0);
        unpacker.Memory.Count.Should().Be(0);
        unpacker.Reserved.Should().Be(0);
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
    /// not remembered, however long it ran; one that started with the budget and is still running
    /// when the budget itself ends is (S2).
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
        byBudget.Memory.HeldBack(Bob, new[] { "q1" }).Single().Should().BeTrue("it started with the budget and was still running when the budget ended");
    }

    /// <summary>
    /// I-5 with the memory's clock readings: a host clock whose readings throw from the moment the
    /// budget ends still lets the budget end the delivery (nothing is remembered: the memory's own
    /// reading failed, so its strike was not made).
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
    /// T5 (I-13): four remembered messages ahead of four others. They fail at once without a host
    /// call or a reservation and are never handed to a worker; the four others start at once and run
    /// until the budget ends.
    /// </summary>
    [Fact]
    public async Task Held_back_messages_make_no_host_call_and_take_no_reservation()
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
        unpacker.Settled.Should().Be(0, "a held-back message is not handed to a worker");

        time.Advance(Budget);
        var outcomes = await delivery.WaitAsync(RealTime);
        outcomes.Take(4).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before") && o.Error.Message.Contains("'request-1'"));
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
        (await delivery).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
        calls.Count.Should().Be(0);
    }

    /// <summary>
    /// The cost of having no time limit per message, and what the memory makes of it (I-13, S2):
    /// twenty messages that never open ahead of an honest one. Each delivery starts four of them;
    /// they keep the four workers for the whole budget and are remembered when it ends, and every
    /// message behind them, the honest one included, is out of time before its turn. Each next
    /// delivery holds back what was learned, so after five deliveries all twenty are held back, and
    /// the sixth opens the honest message without opening any of them again.
    /// </summary>
    [Fact]
    public async Task Twenty_stalling_first_are_learned_four_per_delivery_and_then_the_honest_one_opens()
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
        var stalling = Enumerable.Range(0, 20).Select(i => (string?)("stall-" + i)).ToArray();
        var messages = stalling.Select(id => Message(TestJwe.Anoncrypt(id!), id!))
            .Append(Message(TestJwe.Authcrypt(AliceKid, "honest"), "honest"))
            .ToArray();

        for (var delivery = 1; delivery <= 5; delivery++)
        {
            var batch = unpacker.UnpackAllAsync(Bob, messages, "request-" + delivery, default);
            await calls.WaitForAsync(4 * delivery, RealTime);
            time.Advance(Budget);
            var outcomes = await batch.WaitAsync(RealTime);

            outcomes[20].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
            unpacker.Memory.HeldBack(Bob, stalling).Count(h => h).Should().Be(4 * delivery, "four more were learned by delivery {0}", delivery);
        }
        var last = await unpacker.UnpackAllAsync(Bob, messages, "request-6", default).WaitAsync(RealTime);

        last.Take(20).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
        last[20].Succeeded.Should().BeTrue("every message in front of it is held back, so the honest one gets its turn");
        calls.Labels.Should().HaveCount(21).And.OnlyHaveUniqueItems("no message was opened twice");
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
    /// and every kind of message — opens, fails at once, fails after a second, honours its token,
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

        outcomes.Should().NotContain(o => o.Error is TimeoutException, "nothing in the delivery was refused or ran out of time");
        outcomes.Take(10).Should().OnlyContain(o => o.Error is CryptoException);
        outcomes[10].Succeeded.Should().BeTrue("Sam's message is never refused for its partition inside one delivery");
        unpacker.Memory.HeldBack(Bob, junk.Select(m => m.Id).ToArray()).Should().OnlyContain(h => h, "each failed on its own");

        var second = unpacker.UnpackAllAsync(Bob, junk.Append(Message(TestJwe.Authcrypt(SamKid, "sam-2"), "sam-2")).ToArray(), "request-2", default);
        await calls.WaitForAsync(12, RealTime);
        time.Advance(lookup);
        var again = await second.WaitAsync(RealTime);

        again.Take(10).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("did not open when it was delivered before"));
        again[10].Succeeded.Should().BeTrue();
        calls.Labels.Skip(11).Should().Equal("sam-2");
        held.RunAll();
        unpacker.Reserved.Should().Be(0);
    }

    /// <summary>
    /// D-13 for messages that open: six messages of one sender that open at once, in one delivery,
    /// with the release continuations held back. Each worker gives the reservation of the call it
    /// waited for back itself, before its next take, so the fifth and the sixth are not refused for
    /// the sender's partition of four; the held continuations then change nothing.
    /// </summary>
    [Fact]
    public async Task A_senders_partition_never_refuses_inside_one_delivery_because_of_its_own_opened_calls()
    {
        var time = new ManualTimeProvider();
        var held = new HeldScheduler();
        var unpacker = new DeliveredMessageUnpacker((packed, _) => Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed))), Budget, time, reservations: new UnpackReservations(held));
        var messages = Enumerable.Range(0, 6).Select(i => Message(TestJwe.Authcrypt(SamKid, "sam-" + i), "sam-" + i)).ToArray();

        var outcomes = await unpacker.UnpackAllAsync(Bob, messages, "request-1", default).WaitAsync(RealTime);

        outcomes.Should().OnlyContain(o => o.Succeeded, "no message is refused for its sender's partition because of calls of its own delivery that have ended");
        unpacker.Reserved.Should().Be(0, "each worker gave its reservation back itself");
        SpinWait.SpinUntil(() => held.Held == messages.Length, RealTime).Should().BeTrue("every release continuation is still held");
        held.RunAll();
        unpacker.Reserved.Should().Be(0, "a reservation is given back once");
        unpacker.Reservations.Partitions.Should().Be(0);
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
    /// A manual clock whose second reading after its first timer was created throws, once. In a
    /// delivery of one message that reading is the host call's own, made as its work item starts
    /// (the first is its worker's, for the start rule).
    /// </summary>
    private sealed class HostCallsReadingThrows(ManualTimeProvider inner) : TimeProvider
    {
        private int _timers;
        private int _readings;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp()
        {
            if (Volatile.Read(ref _timers) > 0 && Interlocked.Increment(ref _readings) == 2)
                throw new InvalidOperationException("the host's clock failed");
            return inner.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
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
