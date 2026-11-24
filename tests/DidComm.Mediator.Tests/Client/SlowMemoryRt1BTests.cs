using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111 red-team pass 1, RT1-B: what the slow memory remembers (I-9). A message is remembered only
/// for its own slowness: its host code ran for at least the slow threshold τ and did not open. Time
/// its work item spent waiting for a pool thread before any host code ran is not the message's own.
/// Every timing is driven by the injected <see cref="ManualTimeProvider"/>; real time is only a hang
/// guard. The pool is kept busy only while the manual clock moves, then released.
/// </summary>
public sealed class SlowMemoryRt1BTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Lookup = TimeSpan.FromSeconds(2); // the honest sender's DID resolves in 2 s
    private static readonly TimeSpan HostRan = TimeSpan.FromMilliseconds(300); // how long its host code ran before the cut
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private const string AliceKid = "did:example:alice#key-x25519-1";

    /// <summary>
    /// I-9: a message whose host code ran for less than τ is not remembered, even when its work item
    /// waited for a pool thread for most of the budget and the budget then ended it. Here every pool
    /// thread is busy and work is queued in front while the clock moves 19.7 s; the honest message's
    /// host code then runs 0.3 s (under τ = 0.6 s) before the budget ends.
    /// </summary>
    [Fact(Skip = "RT1 defect: time queued for a pool thread counts toward the slow threshold")]
    public async Task A_message_whose_host_code_ran_under_the_slow_threshold_is_not_remembered_after_waiting_for_a_pool_thread()
    {
        var time = new ManualTimeProvider();
        var unpacker = new DeliveredMessageUnpacker(Honest(time, out var entered), Budget, time);
        unpacker.SlowThreshold.Should().Be(TimeSpan.FromMilliseconds(600));
        var honest = new[] { new DeliveredInput("alice-1", TestJwe.Bytes(TestJwe.Authcrypt(AliceKid, "alice-1"))) };

        Task<IReadOnlyList<DeliveredOutcome>> delivery;
        using (var floor = new PoolFloor(parked: 0))
        using (var busy = new BusyPool(inFront: 2_000))
        {
            delivery = StartOnOwnThread(() => unpacker.UnpackAllAsync(Bob, honest, "request-1", default));
            time.Advance(Budget - HostRan);
            entered.Task.IsCompleted.Should().BeFalse("the work item is still queued behind the pool's work");
        }

        var enteredAt = await entered.Task.WaitAsync(RealTime);
        time.Advance(HostRan);
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes.Single().Succeeded.Should().BeFalse("the budget ended while its 2 s lookup was running");
        time.GetElapsedTime(enteredAt).Should().Be(HostRan);
        HostRan.Should().BeLessThan(unpacker.SlowThreshold);
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Should().Equal(new[] { false },
            "its host code ran {0} ms, under the slow threshold of {1} ms; remembered, it would not be opened again for {2} min",
            HostRan.TotalMilliseconds, unpacker.SlowThreshold.TotalMilliseconds, unpacker.Memory.FirstPause.TotalMinutes);
    }

    /// <summary>
    /// I-9 across mailboxes: lookups in other mailboxes whose threads stay blocked (fewer than the
    /// container's bound, so this message is never refused) make the pool's threads busy; an honest
    /// message in this mailbox whose host code then runs 0.3 s before its budget ends is not
    /// remembered as slow.
    /// </summary>
    [Fact(Skip = "RT1 defect: time queued for a pool thread counts toward the slow threshold")]
    public async Task A_message_whose_host_code_ran_under_the_slow_threshold_is_not_remembered_while_other_mailboxes_lookups_hold_the_pool()
    {
        const int mailboxes = 15; // 15 × 4 = 60 blocked lookups, under C = 64
        const int blocked = mailboxes * DeliveredMessageUnpacker.FetchConcurrency;
        using var floor = new PoolFloor(parked: 0);
        ThreadPool.GetMinThreads(out var minimum, out _);
        Math.Max(minimum, ThreadPool.ThreadCount).Should().BeLessThan(blocked - 8, "precondition: more blocked lookups than pool threads");

        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        using var gate = new ManualResetEventSlim(false);
        var honest = Honest(time, out var entered);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            if (TestJwe.LabelOf(packed) == "alice-1")
                return honest(packed, ct);
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                gate.Wait(RealTime); // blocks its thread and ignores its token (bounded so a red run cannot hang)
            throw new InvalidOperationException("did not open");
        }, Budget, time);

        Task<IReadOnlyList<DeliveredOutcome>>[] others;
        Task<IReadOnlyList<DeliveredOutcome>> delivery;
        try
        {
            others = StartOnOwnThread(() => Enumerable.Range(0, mailboxes)
                .Select(m => unpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, $"did:example:tenant-{m}"),
                    Enumerable.Range(0, DeliveredMessageUnpacker.FetchConcurrency)
                        .Select(i => new DeliveredInput($"t{m}-{i}", TestJwe.Bytes(TestJwe.Anoncrypt($"t{m}-{i}")))).ToArray(),
                    $"request-t{m}", default))
                .ToArray());
            delivery = StartOnOwnThread(() => unpacker.UnpackAllAsync(Bob,
                new[] { new DeliveredInput("alice-1", TestJwe.Bytes(TestJwe.Authcrypt(AliceKid, "alice-1"))) }, "request-1", default));
            unpacker.Reserved.Should().BeLessThanOrEqualTo(UnpackReservations.MaxReserved);
            time.Advance(Budget - HostRan);
            entered.Task.IsCompleted.Should().BeFalse("the work item is still queued behind the blocked lookups");
        }
        finally
        {
            gate.Set();
        }

        var enteredAt = await entered.Task.WaitAsync(RealTime);
        time.Advance(HostRan);
        var outcomes = await delivery.WaitAsync(RealTime);
        await Task.WhenAll(others).WaitAsync(RealTime);

        outcomes.Single().Succeeded.Should().BeFalse("the budget ended while its 2 s lookup was running");
        time.GetElapsedTime(enteredAt).Should().Be(HostRan);
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Should().Equal(new[] { false },
            "its host code ran {0} ms, under the slow threshold of {1} ms; remembered, it would not be opened again for {2} min",
            HostRan.TotalMilliseconds, unpacker.SlowThreshold.TotalMilliseconds, unpacker.Memory.FirstPause.TotalMinutes);
    }

    // An honest message: its sender's DID resolves in 2 s on the manual clock, then it opens. The
    // returned task completes with the clock's reading when its host code starts.
    private static Func<string, CancellationToken, Task<DeliveredUnpack>> Honest(ManualTimeProvider time, out TaskCompletionSource<long> entered)
    {
        var started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = started;
        return async (packed, ct) =>
        {
            started.TrySetResult(time.GetTimestamp());
            await Task.Delay(Lookup, time, ct).ConfigureAwait(false);
            return UnpackFakes.Opened(TestJwe.LabelOf(packed));
        };
    }

    // Runs the start on a thread of its own (not a pool thread), so its work items go to the pool's
    // global queue, behind what is already queued there.
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

    // Every pool thread busy and work queued in front, until disposed.
    private sealed class BusyPool : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        internal BusyPool(int inFront)
        {
            var items = ThreadPool.ThreadCount + inFront;
            for (var i = 0; i < items; i++)
                ThreadPool.UnsafeQueueUserWorkItem(static release => release.Wait(RealTime), _release, preferLocal: false);
            SpinWait.SpinUntil(() => ThreadPool.PendingWorkItemCount >= inFront / 2, RealTime).Should().BeTrue("the pool is busy");
        }

        public void Dispose() => _release.Set();
    }
}
