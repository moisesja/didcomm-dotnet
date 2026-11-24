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
/// ADV-3 red-team pass on the per-delivery budget of fix round 2 (#96, owner decision 2-A and the
/// equal share of <c>2e45df7</c>), and since #111 the reservations of host unpack calls and the
/// share of the time left: the share arithmetic, the bound on host calls, host code that blocks, and
/// the timer and token handling under races. Every timing is driven by the injected
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
    /// ADV3-3 (fixed by #111, I-6, D-5): ninety-nine messages that fail at once and an honest message
    /// first whose sender's DID takes 2 s to resolve, in one delivery of a hundred under the default
    /// 20 s budget. The honest message's share is at least the floor (5 s), not the static share
    /// (0.8 s), so it opens; alone, it opens too.
    /// </summary>
    [Fact]
    public async Task ADV3_3_junk_that_fails_at_once_leaves_an_honest_message_first_at_least_the_floor()
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
        outcomes[0].Succeeded.Should().BeTrue(
            "its share is at least the floor of {0}, which its {1} lookup fits in, not the static share of {2}",
            unpacker.Floor, resolution, DeliveredMessageUnpacker.ShareOf(Budget, padded.Length));
    }

    /// <summary>
    /// ADV3-4 (defect, Low; fixed in fix round 3: every unpack runs on a thread-pool work item of its own). Property broken — <see cref="PickupClient.FetchAsync"/> remarks:
    /// "So a fetch ends within the budget even when some senders' DIDs resolve slowly or never";
    /// <see cref="DeliveredMessageUnpacker"/>: "The returned task is complete when the last message
    /// has ended or, at the latest, when the budget runs out".
    /// <para>
    /// Attack: the workers start inside <c>UnpackAllAsync</c> on the caller's own stack
    /// (<c>Delivery.Start</c> calls <c>RunWorkerAsync</c>, which calls the unpack synchronously
    /// before its first await), so the unpack's synchronous part — host code: the DID resolver, the
    /// secrets resolver — runs before the caller even holds the task. A resolver that blocks its
    /// thread (sync-over-async HTTP for the attacker's did:webvh, ignoring the token) blocks the
    /// caller's <c>FetchAsync</c> for as long as the attacker holds the connection: the budget fires
    /// and fills in the outcome, but the caller cannot get to it. The blocked unpack also holds no
    /// abandoned slot (it has no task yet), so the bound of 16 does not count it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADV3_4_an_unpack_that_blocks_its_thread_holds_the_caller_past_the_budget_and_takes_no_slot()
    {
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
            await time.WaitForScheduledTimersAsync(2, RealTime); // the budget and the share
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
    /// Held (passes): the share and the budget fire at the same instant (a delivery of up to four
    /// messages: share = budget), the caller cancels at that instant too, and a callback host code
    /// registered on the share's token throws — the call ends cancelled, nothing escapes onto the
    /// timer thread or into the caller's <c>Cancel()</c>, and every reservation comes back.
    /// </summary>
    [Fact]
    public async Task Share_budget_and_caller_ending_together_with_a_throwing_host_callback_end_cleanly()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            ct.Register(() => throw new InvalidOperationException("host callback"));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Opened(text);
        }, Budget, time);
        // The caller cancels from a timer of its own, due at the same instant as the budget and the
        // shares and created first, so it fires first: its Cancel() runs the budget's and the shares'
        // cancellations, and the throwing host callbacks, inline.
        using var callerTimer = time.CreateTimer(_ => caller.Cancel(), null, Budget, Timeout.InfiniteTimeSpan);
        var batch = unpacker.UnpackAllAsync(Payloads(4, "m-"), "request-1", caller.Token);
        await time.WaitForScheduledTimersAsync(6, RealTime);

        FluentActions.Invoking(() => { time.Advance(Budget); }).Should().NotThrow("nothing a host callback throws escapes into the caller's Cancel() or onto a timer");

        await FluentActions.Awaiting(() => batch.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// Held (passes): a host callback on the share's token completes the unpack <em>successfully</em>
    /// inside the share's own <c>Cancel()</c> (registered after the worker's wait, so it runs first).
    /// The message is a success exactly once, the delivery completes, the next message still gets
    /// its own share (the time left), and no reservation or timer is left behind.
    /// </summary>
    [Fact]
    public async Task An_unpack_that_completes_inside_its_shares_cancel_is_recorded_once_and_leaves_nothing_behind()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker((text, ct) =>
        {
            var done = new TaskCompletionSource<DeliveredUnpack>();
            // Completes synchronously, on the share's timer thread, when the share runs out.
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                ct.Register(() => done.TrySetResult(Opened(text)));
            });
            return done.Task;
        }, Budget, time);
        var payloads = Payloads(5, "m-"); // two rounds: four, then one
        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        // The first message's share is half the budget (two rounds left); the other three get all of it.
        var first = unpacker.ShareAt(payloads.Length, 0, TimeSpan.Zero);

        await time.WaitForScheduledTimersAsync(5, RealTime);
        var mark = time.Mark();
        time.Advance(first);
        // The fifth message starts when the first ends, with the time left as its share.
        await time.WaitForTimerAsync(Budget - first, mark, RealTime);
        time.Advance(Budget - first);
        var outcomes = await batch.WaitAsync(RealTime);

        outcomes.Should().HaveCount(5);
        // Whichever won (the late result or the share), each message has exactly one outcome.
        outcomes.Should().OnlyContain(o => o.Succeeded || o.Error is TimeoutException);
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// Held (passes): thousands of deliveries with every kind of message — opens at once, fails at
    /// once, throws synchronously, honours its share, ignores its token and ends later, plus callers
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
            // Run every share and budget out.
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
