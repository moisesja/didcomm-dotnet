using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111 red-team pass 1, RT1-B: what the memory remembers (I-9, owner decision C: "every failure,
/// fair to late starts", under owner decision E). A message the budget ends without opening is
/// remembered only when its own code had the whole budget (S2); time its work item spent waiting
/// for a pool thread before any host code ran is not the message's own.
/// Every timing is driven by the injected <see cref="ManualTimeProvider"/>; real time is only a hang
/// guard. A late start is forced by a gate on the work item's own thread, never by keeping the pool
/// busy: whether a pool thread is free makes no difference to it.
/// </summary>
/// <remarks>
/// <strong>The gate is each test's own</strong> (<see cref="LateStart"/>). It is the value that
/// flows, in the execution context, into the work items of the delivery the test starts, and it
/// holds the first pool thread that switches into that context: the message's host call, before any
/// of its code runs. A thread that switches into another delivery's context meets that delivery's
/// gate, never this one. That matters because a delivery leaves continuations to run in its
/// context after its result is set (queued to the pool when its budget ends), and the pool may get
/// to them after the next test has begun.
/// </remarks>
public sealed class SlowMemoryRt1BTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Lookup = TimeSpan.FromSeconds(2); // the honest sender's DID resolves in 2 s
    private static readonly TimeSpan HostRan = TimeSpan.FromMilliseconds(300); // how long its host code ran before the budget ended
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private const string AliceKid = "did:example:alice#key-x25519-1";

    /// <summary>
    /// I-9 (S2): a message whose host code started late is not remembered when the budget ends it:
    /// its work item waited for a pool thread for most of the budget. It waits for its thread while
    /// the clock moves 19.7 s (forced by a gate on that thread before its code runs, so no other
    /// test's threads are held); the honest message's host code then runs for the 0.3 s of the
    /// budget that are left.
    /// </summary>
    [Fact]
    public Task A_message_whose_host_code_started_late_is_not_remembered_after_waiting_for_a_pool_thread()
        => LateStartAsync(otherMailboxes: 0);

    /// <summary>
    /// I-9 (S2) across mailboxes: lookups in fifteen other mailboxes whose threads stay blocked
    /// (sixteen each, fewer in all than the container's bound, so this message is never refused) hold
    /// pool threads; the honest message's work item waits for one while the clock moves 19.7 s
    /// (forced by a gate on its thread before its code runs, so the wait does not depend on how many
    /// threads the pool has), then its host code runs for the 0.3 s of the budget that are left. It
    /// is not remembered.
    /// </summary>
    [Fact]
    public Task A_message_whose_host_code_started_late_is_not_remembered_while_other_mailboxes_lookups_hold_the_pool()
        => LateStartAsync(otherMailboxes: 15);

    /// <summary>
    /// The gate holds the message's own work item whatever else the pool runs meanwhile. Forced
    /// here: an earlier delivery, marked with a gate of its own as every delivery of this class is,
    /// ends at its budget while its lookup is still suspended; the lookup resumes, on a pool thread
    /// and in that delivery's execution context, once this test's gate exists and before its
    /// delivery starts. It runs on and does not take this test's gate, which then holds the honest
    /// message's work item as in the two tests above (the same steps, with and without the other
    /// mailboxes' blocked lookups).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public async Task A_continuation_an_earlier_delivery_left_to_run_does_not_take_the_gate_of_the_next(int otherMailboxes)
    {
        var earlierTime = new ManualTimeProvider();
        var earlierGate = new LateStart();
        earlierGate.Release(); // it holds nothing back: the earlier delivery's work item passes at once
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlierUnpacker = new DeliveredMessageUnpacker(async (_, ct) =>
        {
            started.TrySetResult();
            await resume.Task.ConfigureAwait(false); // resumes on a pool thread, in its delivery's execution context
            resumed.TrySetResult();
            throw new OperationCanceledException(ct);
        }, Budget, earlierTime);
        var earlier = StartOnOwnThread(() =>
        {
            Marked.Value = earlierGate;
            return earlierUnpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, "did:example:earlier"),
                new[] { new DeliveredInput("earlier-1", TestJwe.Bytes(TestJwe.Anoncrypt("earlier-1"))) }, "request-0", default);
        });
        await started.Task.WaitAsync(RealTime);
        earlierTime.Advance(Budget);
        (await earlier.WaitAsync(RealTime)).Single().Succeeded.Should().BeFalse("the earlier delivery ended at its budget, its lookup still suspended");

        await LateStartAsync(otherMailboxes, async gate =>
        {
            resume.TrySetResult();
            var first = await Task.WhenAny(resumed.Task, gate.Parked).WaitAsync(RealTime);
            first.Should().BeSameAs(resumed.Task,
                "what an earlier delivery left to run switches into that delivery's context and runs on; this test's gate holds only a thread that switches into its own");
            earlierGate.SwitchesInto.Should().BeGreaterThanOrEqualTo(2, "a pool thread switched into the earlier delivery's context for its work item, and again for what it left to run");
            gate.SwitchesInto.Should().Be(0, "no thread has switched into this test's context yet");
        });

        SpinWait.SpinUntil(() => earlierUnpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    // The steps of the tests above. `otherMailboxes` mailboxes each have a delivery of as many
    // lookups as are opened at once, which block their threads until the test ends. The honest
    // message's delivery is then started with the test's gate as its mark: the gate holds its work
    // item while the clock moves to 0.3 s before the budget's end. `whileTheGateWaits` runs once the
    // gate exists and before that delivery starts.
    private static async Task LateStartAsync(int otherMailboxes, Func<LateStart, Task>? whileTheGateWaits = null)
    {
        var blocked = otherMailboxes * DeliveredMessageUnpacker.FetchConcurrency;
        using var floor = new PoolFloor(parked: blocked + 1);
        var late = new LateStart();
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        using var blocking = new ManualResetEventSlim(false);
        var honest = Honest(time, late, out var entered);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            if (TestJwe.LabelOf(packed) == "alice-1")
                return honest(packed, ct);
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                blocking.Wait(RealTime); // blocks its thread and ignores its token (bounded so a red run cannot hang)
            throw new InvalidOperationException("did not open");
        }, Budget, time);

        Task<IReadOnlyList<DeliveredOutcome>>[] others;
        Task<IReadOnlyList<DeliveredOutcome>> delivery;
        try
        {
            others = StartOnOwnThread(() => Enumerable.Range(0, otherMailboxes)
                .Select(m => unpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, $"did:example:tenant-{m}"),
                    Enumerable.Range(0, DeliveredMessageUnpacker.FetchConcurrency)
                        .Select(i => new DeliveredInput($"t{m}-{i}", TestJwe.Bytes(TestJwe.Anoncrypt($"t{m}-{i}")))).ToArray(),
                    $"request-t{m}", default))
                .ToArray());
            await calls.WaitForAsync(blocked, RealTime);
            if (whileTheGateWaits is not null)
                await whileTheGateWaits(late);
            late.Parked.IsCompleted.Should().BeFalse("nothing runs in this test's context yet");

            delivery = StartOnOwnThread(() =>
            {
                Marked.Value = late; // flows into the honest message's work items only
                return unpacker.UnpackAllAsync(Bob,
                    new[] { new DeliveredInput("alice-1", TestJwe.Bytes(TestJwe.Authcrypt(AliceKid, "alice-1"))) }, "request-1", default);
            });
            unpacker.Reserved.Should().Be(blocked + 1, "the honest message is never refused");
            await late.Parked.WaitAsync(RealTime); // its work item is on its thread, held before any of its code
            time.Advance(Budget - HostRan);
            entered.Task.IsCompleted.Should().BeFalse("its code has not started");
        }
        finally
        {
            late.Release();
            blocking.Set();
        }

        var (enteredAt, heldByTheGate) = await entered.Task.WaitAsync(RealTime);
        heldByTheGate.Should().BeTrue("the thread the gate held is the one that then ran the honest message's host code");
        time.Advance(HostRan);
        var outcomes = await delivery.WaitAsync(RealTime);
        await Task.WhenAll(others).WaitAsync(RealTime);

        outcomes.Single().Succeeded.Should().BeFalse("the budget ended while its 2 s lookup was running");
        time.GetElapsedTime(enteredAt).Should().Be(HostRan);
        (Budget - HostRan).Should().BeGreaterThan(unpacker.StartAllowance, "its code started later than the start allowance");
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Should().Equal(new[] { false },
            "its host code started {0} s into the {1} s budget; remembered, it would not be opened again for {2} min",
            (Budget - HostRan).TotalSeconds, Budget.TotalSeconds, unpacker.Memory.FirstPause.TotalMinutes);
    }

    // The mark of a delivery started by a test of this class: the test's gate. It flows, in the
    // execution context, into the work items that delivery starts; the handler runs on a thread
    // whose context is switched (never when the value is set), and tells the gates concerned.
    private static readonly AsyncLocal<LateStart?> Marked = new(OnMarkedContextSwitched);

    private static void OnMarkedContextSwitched(AsyncLocalValueChangedArgs<LateStart?> args)
    {
        if (!args.ThreadContextChanged)
            return;
        try
        {
            args.PreviousValue?.SwitchedAwayFrom();
            args.CurrentValue?.SwitchedInto();
        }
        catch
        {
            // Never thrown into the runtime's context switch (an exception here ends the process).
        }
    }

    /// <summary>
    /// One test's gate on a late start. It holds the first thread that switches into the execution
    /// context carrying it (a host call's work item, after the task's own cancellation check and
    /// before its delegate runs) until the test releases it; later switches into that context pass.
    /// Only a context that carries this gate reaches it.
    /// </summary>
    private sealed class LateStart
    {
        // The gate that held the thread's present stay in a marked context, if one did.
        [ThreadStatic]
        private static LateStart? t_holding;

        private readonly ManualResetEventSlim _released = new(false);
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;
        private int _switches;

        /// <summary>Completes once a thread has switched into this gate's context and is held here.</summary>
        public Task Parked => _parked.Task;

        /// <summary>Whether the caller runs on the thread this gate held, before that thread left the gate's context: in the very work item the gate held.</summary>
        public bool HeldTheCaller => ReferenceEquals(t_holding, this);

        /// <summary>How many times a thread has switched into this gate's context.</summary>
        public int SwitchesInto => Volatile.Read(ref _switches);

        /// <summary>Let the held thread go on (and any thread that reaches the gate later pass at once).</summary>
        public void Release() => _released.Set();

        public void SwitchedInto()
        {
            Interlocked.Increment(ref _switches);
            if (Interlocked.Exchange(ref _used, 1) != 0)
                return;
            t_holding = this;
            _parked.TrySetResult();
            _released.Wait(RealTime); // the work item waits for its thread (bounded so a red run cannot hang)
        }

        public void SwitchedAwayFrom()
        {
            if (ReferenceEquals(t_holding, this))
                t_holding = null;
        }
    }

    /// <summary>
    /// D-2: the pause doubles each time a message is slow again. One slow episode seen by deliveries
    /// of the same message that run at the same time (two fetch loops on one mailbox, or a fetch
    /// beside a live stream) is one strike: after it the message is paused for the first pause, not
    /// for a doubled one.
    /// </summary>
    [Fact]
    public async Task One_slow_episode_seen_by_concurrent_deliveries_of_the_same_message_pauses_it_for_the_first_pause_only()
    {
        const int concurrent = 4;
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, time);
        var message = new[] { new DeliveredInput("alice-1", TestJwe.Bytes(TestJwe.Authcrypt(AliceKid, "alice-1"))) };

        var deliveries = Enumerable.Range(0, concurrent).Select(d => unpacker.UnpackAllAsync(Bob, message, $"request-{d}", default)).ToArray();
        await calls.WaitForAsync(concurrent, RealTime);
        time.Advance(Budget);
        await Task.WhenAll(deliveries).WaitAsync(RealTime);

        time.Advance(unpacker.Memory.FirstPause);
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Should().Equal(new[] { false },
            "one slow episode is one strike, so the first pause ({0} min) has ended; {1} concurrent deliveries paused it for {2} min",
            unpacker.Memory.FirstPause.TotalMinutes, concurrent, unpacker.Memory.PauseAfter(concurrent).TotalMinutes);
    }

    // An honest message: its sender's DID resolves in 2 s on the manual clock, then it opens. The
    // returned task completes when its host code starts, with the clock's reading then and whether
    // that code runs in the work item `gate` held.
    private static Func<string, CancellationToken, Task<DeliveredUnpack>> Honest(ManualTimeProvider time, LateStart gate, out TaskCompletionSource<(long At, bool HeldByTheGate)> entered)
    {
        var started = new TaskCompletionSource<(long At, bool HeldByTheGate)>(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = started;
        return async (packed, ct) =>
        {
            started.TrySetResult((time.GetTimestamp(), gate.HeldTheCaller));
            await Task.Delay(Lookup, time, ct).ConfigureAwait(false);
            return UnpackFakes.Opened(TestJwe.LabelOf(packed));
        };
    }

    // Runs the start on a thread of its own (not a pool thread), so an execution-context value set
    // there (Marked) flows into the work items it starts and never into the test's own context.
    private static T StartOnOwnThread<T>(Func<T> start)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = start();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.Start();
        thread.Join(RealTime).Should().BeTrue();
        if (error is not null)
            throw error;
        return result;
    }
}
