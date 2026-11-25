using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-3 red-team pass on the per-delivery budget of fix round 2 (#96, owner decision 2-A), and
/// since #111 the reservations of host unpack calls and one budget with no time limit per message
/// (owner decision E): the bound on host calls, host code that blocks, and the timer and token
/// handling under races. Every timing is driven by the injected
/// <see cref="ManualTimeProvider"/> and rendezvous on scheduled timers (L-044); nothing sleeps.
/// </summary>
public sealed class DeliveredUnpackAdv3Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// ADV3-2 (fixed by #111, I-1): the bound on host unpack calls is a reservation, taken with its
    /// check in one step before the call exists. Twenty deliveries of four messages whose lookups
    /// ignore cancellation are opened together, each from its own mailbox (so no partition bound
    /// refuses anything): exactly 64 calls start and stay reserved, exactly 16 messages are refused
    /// for the container, and never more than 64 calls run.
    /// </summary>
    [Fact]
    public async Task ADV3_2_deliveries_opened_together_reserve_at_most_the_containers_bound()
    {
        var time = new ManualTimeProvider();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((text, ct) =>
        {
            calls.Enter(text, ct);
            return never.Task; // ignores its token
        }, Budget, time);
        const int deliveries = UnpackReservations.MaxReserved / DeliveredMessageUnpacker.FetchConcurrency + 4;
        try
        {
            var batches = Enumerable.Range(0, deliveries)
                .Select(d => unpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, $"did:example:local-{d}"),
                    Payloads(DeliveredMessageUnpacker.FetchConcurrency, $"mallory-{d}-").Select((p, i) => new DeliveredInput($"q-{d}-{i}", p)).ToArray(),
                    $"request-{d}", default))
                .ToArray();
            await calls.WaitForAsync(UnpackReservations.MaxReserved, RealTime);
            time.Advance(Budget);
            var outcomes = new List<DeliveredOutcome>();
            foreach (var batch in batches)
                outcomes.AddRange(await batch.WaitAsync(RealTime));

            unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved, "every call started is reserved until it ends");
            outcomes.Count(o => o.Error is TimeoutException t && t.Message.Contains("capacity exhausted")).Should().Be(deliveries * DeliveredMessageUnpacker.FetchConcurrency - UnpackReservations.MaxReserved);
            calls.Count.Should().Be(UnpackReservations.MaxReserved, "a refused message never starts");
            calls.MaxRunning.Should().BeLessThanOrEqualTo(UnpackReservations.MaxReserved);
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("each reservation is given back when its call ends");
    }

    /// <summary>
    /// ADV3-3 (fixed by #111): ninety-nine messages that fail at once and an honest message first
    /// whose sender's DID takes 2 s to resolve, in one delivery of a hundred under the default 20 s
    /// budget. A message has no time limit of its own (owner decision E; before, a share of the
    /// budget sized from the count, 0.8 s here), so it opens; alone, it opens too.
    /// </summary>
    [Fact]
    public async Task ADV3_3_junk_that_fails_at_once_leaves_an_honest_message_first_the_time_to_open()
    {
        var time = new ManualTimeProvider();
        var resolution = TimeSpan.FromSeconds(2);
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            if (text.StartsWith("junk", StringComparison.Ordinal))
                throw new CryptoException("JWE could not be decrypted.");
            await Task.Delay(resolution, time, ct); // the honest sender's DID resolves in 2 s
            return Opened(text);
        }, Budget, time);

        // Control: alone, the honest message opens. The clock moves once its own lookup's timer exists.
        var mark = time.Mark();
        var alone = unpacker.UnpackAllAsync(Payloads(1, "alice-"), "request-1", default);
        await time.WaitForTimerAsync(resolution, mark, RealTime);
        time.Advance(resolution);
        (await alone.WaitAsync(RealTime)).Single().Succeeded.Should().BeTrue();

        var padded = Payloads(99, "junk-").Prepend((ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("alice-0")).ToArray();
        mark = time.Mark();
        var batch = unpacker.UnpackAllAsync(padded, "request-2", default);
        await time.WaitForTimerAsync(resolution, mark, RealTime);
        time.Advance(resolution);
        var outcomes = await batch.WaitAsync(RealTime);

        outcomes.Skip(1).Should().OnlyContain(o => o.Error is CryptoException, "the junk failed at once");
        outcomes[0].Succeeded.Should().BeTrue("it runs until it opens: its {0} lookup fits in the budget of {1}", resolution, Budget);
    }

    /// <summary>
    /// ADV3-4 (fixed in #96 fix round 3: every unpack runs on a thread-pool work item of its own;
    /// since #111 its call is reserved before it starts). An unpack whose host code blocks its thread
    /// and ignores its token: the caller of <c>UnpackAllAsync</c> holds the delivery's task while the
    /// host code still blocks, the budget decides the message's outcome (out of time) while it still
    /// blocks, and the call keeps its reservation until its thread comes back. Checked against
    /// <see cref="PickupClient.FetchAsync"/>'s remarks ("a fetch ends within the budget even when some
    /// senders' DIDs resolve slowly or never") and <see cref="DeliveredMessageUnpacker"/>'s ("the
    /// returned task is complete when the last message has ended or, at the latest, when the budget
    /// runs out").
    /// </summary>
    [Fact]
    public async Task ADV3_4_an_unpack_that_blocks_its_thread_leaves_the_caller_its_task_and_the_budget_its_outcome_and_keeps_its_reservation()
    {
        using var floor = new PoolFloor(parked: 1); // the unpack below parks a pool thread on the gate
        var time = new ManualTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker((text, _) =>
        {
            entered.TrySetResult();
            gate.Wait(); // a resolver that blocks its thread and ignores the token
            return Task.FromResult(Opened(text));
        }, Budget, time);
        // Not Task.Run, which would unwrap: `call` completes when the caller holds the delivery's task.
        var call = Task.Factory.StartNew(() => unpacker.UnpackAllAsync(Payloads(1, "mallory-"), "request-1", default),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(RealTime);
            // Fix round 3: awaited (bounded) rather than read after the budget, a rendezvous instead
            // of a race with the caller's thread; before the fix the caller stays blocked here.
            var delivery = await call.WaitAsync(RealTime);
            await time.WaitForScheduledTimersAsync(1, RealTime); // the budget
            time.Advance(Budget); // the budget fires: the outcome is decided

            call.IsCompleted.Should().BeTrue("the delivery's budget ran out, so the caller must hold its (failed) outcome; it is still blocked inside UnpackAllAsync");
            (await delivery.WaitAsync(RealTime)).Single().Error.Should().BeOfType<TimeoutException>("the budget decides the outcome while the resolver still blocks its thread");
            unpacker.Reserved.Should().Be(1, "an unpack still running after its budget keeps its reservation");
        }
        finally
        {
            gate.Set();
            await (await call.WaitAsync(RealTime)).WaitAsync(RealTime);
        }
    }

    /// <summary>
    /// Held (passes): the caller cancels at the instant the budget ends, and a callback host code
    /// registered on its token throws — the call ends cancelled, nothing escapes onto the timer
    /// thread or into the caller's <c>Cancel()</c>, and every reservation comes back.
    /// </summary>
    [Fact]
    public async Task Budget_and_caller_ending_together_with_a_throwing_host_callback_end_cleanly()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using var registered = new SemaphoreSlim(0);
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            ct.Register(() => throw new InvalidOperationException("host callback"));
            var stall = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            registered.Release();
            await stall;
            return Opened(text);
        }, Budget, time);
        // The caller cancels from a timer of its own, due at the same instant as the budget and
        // created first, so it fires first: its Cancel() runs the budget's cancellation, and the
        // throwing host callbacks, inline.
        using var callerTimer = time.CreateTimer(_ => caller.Cancel(), null, Budget, Timeout.InfiniteTimeSpan);
        var batch = unpacker.UnpackAllAsync(Payloads(4, "m-"), "request-1", caller.Token);
        for (var i = 0; i < 4; i++)
            (await registered.WaitAsync(RealTime)).Should().BeTrue("each host call registered its callback");
        time.ScheduledTimers.Should().Be(2, "the caller's timer and the budget");

        FluentActions.Invoking(() => { time.Advance(Budget); }).Should().NotThrow("nothing a host callback throws escapes into the caller's Cancel() or onto a timer");

        await FluentActions.Awaiting(() => batch.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// Held (passes): a host callback on the call's token completes the unpack <em>successfully</em>
    /// inside the budget's own <c>Cancel()</c>. Whichever comes first, that result or the delivery's
    /// end, each message has exactly one outcome; the message that waited for a worker is not
    /// started; and no reservation or timer is left behind (the budget may be disposed inside its
    /// own <c>Cancel()</c>).
    /// </summary>
    [Fact]
    public async Task An_unpack_that_completes_inside_the_budgets_cancel_is_recorded_once_and_leaves_nothing_behind()
    {
        var time = new ManualTimeProvider();
        using var registered = new SemaphoreSlim(0);
        var started = 0;
        var unpacker = new DeliveredMessageUnpacker((text, ct) =>
        {
            Interlocked.Increment(ref started);
            var done = new TaskCompletionSource<DeliveredUnpack>();
            // Completes synchronously, on the budget's timer thread, when the budget runs out.
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                ct.Register(() => done.TrySetResult(Opened(text)));
                registered.Release();
            });
            return done.Task;
        }, Budget, time);
        var payloads = Payloads(5, "m-"); // four at once; the fifth waits for a worker
        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);

        for (var i = 0; i < 4; i++)
            (await registered.WaitAsync(RealTime)).Should().BeTrue("each host call registered its callback");
        time.ScheduledTimers.Should().Be(1, "the budget");
        time.Advance(Budget);
        var outcomes = await batch.WaitAsync(RealTime);

        outcomes.Should().HaveCount(5);
        // Whichever won (the late result or the budget), each message has exactly one outcome.
        outcomes.Should().OnlyContain(o => o.Succeeded || o.Error is TimeoutException);
        outcomes[4].Error.Should().BeOfType<TimeoutException>("the delivery's token was cancelled before a worker was free for it");
        Volatile.Read(ref started).Should().Be(4);
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// Held (passes): thousands of deliveries with every kind of message — opens at once, fails at
    /// once, throws synchronously, honours its token, ignores its token and ends later, plus callers
    /// that cancel mid-delivery — leave the reserved count at exactly zero once the late unpacks
    /// end (it never drifts up or down), and no timer scheduled.
    /// </summary>
    [Fact]
    public async Task Thousands_of_mixed_deliveries_leave_no_reservation_and_no_timer()
    {
        var time = new ManualTimeProvider();
        var late = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            switch (text[0])
            {
                case 'o': return Opened(text);
                case 'f': throw new MalformedMessageException("bad");
                case 's': await Task.Delay(Timeout.InfiniteTimeSpan, ct); break;
                case 'l': return await late.Task; // ignores its token, ends when released
            }
            return Opened(text);
        }, Budget, time);
        var kinds = new[] { "o", "f", "s", "l" };
        var random = new Random(96);
        var maxReserved = 0;

        for (var round = 0; round < 300; round++)
        {
            using var cancel = new CancellationTokenSource();
            var batches = new List<(Task<IReadOnlyList<DeliveredOutcome>> Task, bool Cancelled)>();
            for (var d = 0; d < 6; d++)
            {
                var count = random.Next(1, 13);
                var payloads = Enumerable.Range(0, count).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(kinds[random.Next(kinds.Length)] + "-" + round + "-" + d + "-" + i)).ToArray();
                var cancelled = d == 5;
                batches.Add((unpacker.UnpackAllAsync(payloads, "request", cancelled ? cancel.Token : default), cancelled));
            }
            if (round % 3 == 0)
                cancel.Cancel();
            // Run every budget out.
            for (var step = 0; step < 4 && batches.Any(b => !b.Task.IsCompleted); step++)
            {
                time.Advance(Budget);
                await Task.Yield();
            }
            foreach (var (task, cancelled) in batches)
            {
                // A cancelled caller's delivery may have ended on its own first (its unpacks run on
                // the pool): either way it is cancelled, or it has one outcome per message (L-044).
                await ((Task)task).ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
                if (cancelled && task.IsCanceled)
                    continue;
                task.IsCompletedSuccessfully.Should().BeTrue();
                (await task).Should().NotContainNulls();
            }
            maxReserved = Math.Max(maxReserved, unpacker.Reserved);
            if (round % 50 == 49)
            {
                late.TrySetResult(Opened("late"));
                SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every unpack let go gave its reservation back once it ended");
                late = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        late.TrySetResult(Opened("late"));
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
        maxReserved.Should().BeGreaterThan(0, "the run did let unpacks go");
    }

    private static ReadOnlyMemory<byte>[] Payloads(int count, string prefix)
        => Enumerable.Range(0, count).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(prefix + i)).ToArray();

    private static DeliveredUnpack Opened(string text)
    {
        var message = new MessageBuilder().WithId(text).WithType("https://example.org/notes/1.0/note").WithBody(new System.Text.Json.Nodes.JsonObject()).Build();
        var result = new UnpackResult(message, new[] { DidComm.Jose.EnvelopeKind.Plaintext }, false, false, false, false, null, null, null, null, null, null, Array.Empty<string>(), null);
        return new DeliveredUnpack(result, text, 0);
    }
}
