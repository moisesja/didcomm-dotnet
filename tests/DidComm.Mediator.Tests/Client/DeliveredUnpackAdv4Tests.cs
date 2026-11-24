using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// ADV-4, the last red-team pass of #96, scoped to the unpacker fixes of fix round 3
/// (<c>d43b871</c>): every host unpack on a thread-pool work item of its own (ADV3-4), and the
/// channel's shutdown linked to every delivery's budget (ADV3-7). Manual clocks and rendezvous only
/// (L-044); the two garbage-collection checks wait, bounded, for an object to become unreachable.
/// </summary>
public sealed class DeliveredUnpackAdv4Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Unreachable = TimeSpan.FromSeconds(3);
    private static readonly AsyncLocal<string?> Ambient = new();

    // ---- ADV3-4 -------------------------------------------------------------------------------

    /// <summary>
    /// ADV4-2 (fixed by #111, I-4). An unpack let go at the budget keeps only its own bounded values:
    /// its work item holds the unpack step, the packed text and the share's token, and nothing it
    /// references holds the delivery. While a lookup still blocks its thread after the delivery's
    /// result was returned and let go, another message's bytes and a message opened in the same
    /// delivery are unreachable.
    /// </summary>
    [Fact]
    public void ADV4_2_an_unpack_that_blocks_its_thread_keeps_nothing_of_its_delivery_reachable_after_the_result_was_returned()
    {
        // The blocked lookup parks a pool thread: raise the floor first (L-065).
        using var floor = new PoolFloor(parked: 1);
        var clock = new CountingTimeProvider(new ManualTimeProvider());
        using var gate = new ManualResetEventSlim(false);
        using var entered = new SemaphoreSlim(0);
        var unpacker = new DeliveredMessageUnpacker((text, _) =>
        {
            if (text.StartsWith("blocks", StringComparison.Ordinal))
            {
                entered.Release();
                gate.Wait(RealTime); // a DID lookup that blocks its thread (bounded only so a red run cannot hang)
            }
            return Task.FromResult(Opened(text));
        }, Budget, clock);
        bool keptWhileBlocked;
        (WeakReference Payload, WeakReference Opened) refs;
        try
        {
            refs = OpenAndLetGo(unpacker, clock, entered);
            SpinWait.SpinUntil(() => clock.LiveTimers == 0, RealTime).Should().BeTrue("every timer of the delivery was disposed");
            keptWhileBlocked = !BecomesUnreachable(refs.Payload) || !BecomesUnreachable(refs.Opened);
        }
        finally
        {
            gate.Set();
        }

        // The control: once the blocked thread returns, nothing else holds them.
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
        BecomesUnreachable(refs.Payload).Should().BeTrue("the test itself holds nothing of the delivery");
        BecomesUnreachable(refs.Opened).Should().BeTrue();

        keptWhileBlocked.Should().BeFalse(
            "an unpack let go keeps only its own bounded values, not the delivery: another message's bytes and an opened message stayed reachable while a lookup blocked");
    }

    // A delivery of one message whose lookup blocks and three honest ones: opened, its budget runs
    // out, its result is returned and let go. Only weak references leave this frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Payload, WeakReference Opened) OpenAndLetGo(DeliveredMessageUnpacker unpacker, CountingTimeProvider clock, SemaphoreSlim entered)
    {
        var honest = Encoding.UTF8.GetBytes("honest-1");
        var payloads = new ReadOnlyMemory<byte>[] { Encoding.UTF8.GetBytes("blocks-0"), honest, Encoding.UTF8.GetBytes("honest-2"), Encoding.UTF8.GetBytes("honest-3") };
        var settled = unpacker.Settled;
        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        entered.Wait(RealTime).Should().BeTrue();
        SpinWait.SpinUntil(() => unpacker.Settled == settled + 3, RealTime).Should().BeTrue("the honest messages opened");
        clock.Inner.Advance(Budget);
        batch.Wait(RealTime).Should().BeTrue();
        var outcomes = batch.Result;
        outcomes[0].Error.Should().BeOfType<TimeoutException>();
        outcomes[1].Succeeded.Should().BeTrue();
        unpacker.Reserved.Should().Be(1, "the blocked lookup keeps its reservation");
        return (new WeakReference(honest), new WeakReference(outcomes[1].Unpacked));
    }

    /// <summary>
    /// ADV3-4 held (regression), and the ExecutionContext question (L-059): host code sees the
    /// caller's ambient values (as it did on the caller's stack), and what host code sets on its
    /// thread reaches neither the caller nor the next message the same worker opens.
    /// </summary>
    [Fact]
    public async Task Host_code_sees_the_callers_ambient_values_and_what_it_sets_reaches_neither_the_caller_nor_the_next_message()
    {
        var time = new ManualTimeProvider();
        var seen = new ConcurrentQueue<(string Text, string? Value)>();
        var unpacker = new DeliveredMessageUnpacker((text, _) =>
        {
            seen.Enqueue((text, Ambient.Value));
            Ambient.Value = "set-by-" + text; // written synchronously, before the task is returned
            return Task.FromResult(Opened(text));
        }, Budget, time);

        Ambient.Value = "caller";
        var outcomes = await unpacker.UnpackAllAsync(Payloads(9, "m-"), "request-1", default).WaitAsync(RealTime);

        outcomes.Should().OnlyContain(o => o.Succeeded);
        outcomes.Select(o => o.Unpacked!.Message.Id).Should().Equal(Enumerable.Range(0, 9).Select(i => $"m-{i}"), "outcomes stay in delivery order");
        Ambient.Value.Should().Be("caller");
        seen.Should().HaveCount(9).And.OnlyContain(s => s.Value == "caller", "each message's host code starts from the caller's context, whichever worker opens it");
    }

    // ---- ADV3-7 -------------------------------------------------------------------------------

    /// <summary>
    /// ADV4-3 (fixed by #111, I-3). A host call is created only under the delivery's lock while the
    /// delivery is open, with its share's token. The shutdown lands while the fifth message's worker
    /// creates its share (the timer creation is the rendezvous): the delivery ends cancelled, and the
    /// fifth message's host code never starts.
    /// </summary>
    [Fact]
    public async Task ADV4_3_a_message_whose_worker_was_starting_it_when_the_shutdown_came_never_has_its_host_code_started()
    {
        var clock = new CountingTimeProvider(new ManualTimeProvider());
        using var shutdown = new CancellationTokenSource();
        var ended = 0;
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            if (text == "m-4")
            {
                late.TrySetResult(Volatile.Read(ref ended) == 1);
                return Opened(text);
            }
            if (text == "m-0")
                return Opened(text);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a lookup that honours its token
            throw new InvalidOperationException("unreachable");
        }, Budget, clock, shutdown.Token);
        // Timer 1 is the budget, 2 to 5 the first four shares; timer 6 is the share of the fifth
        // message (m-4), which a worker takes once m-0 has opened: the container goes away while
        // that worker creates it.
        clock.OnCreate = n =>
        {
            if (n != 6)
                return;
            shutdown.Cancel();
            Volatile.Write(ref ended, 1);
        };

        var batch = unpacker.UnpackAllAsync(Payloads(5, "m-"), "request-1", default);
        await FluentActions.Awaiting(() => batch.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>("the shutdown ended the delivery");

        bool? startedAfterTheEnd;
        try
        {
            startedAfterTheEnd = await late.Task.WaitAsync(Unreachable);
        }
        catch (TimeoutException)
        {
            startedAfterTheEnd = null; // never started: what the summary promises
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0 && clock.LiveTimers == 0, RealTime).Should().BeTrue("nothing is left behind either way");

        startedAfterTheEnd.Should().NotBe(true, "a delivery the shutdown ended starts no host code; m-4's was started after it ended");
    }

    /// <summary>
    /// ADV3-7 held (regression): host code that blocks its thread through the shutdown and then
    /// throws on that pool thread is observed — no <see cref="TaskScheduler.UnobservedTaskException"/>
    /// — gives its reservation back, and leaves no timer or opening delivery behind.
    /// </summary>
    [Fact]
    public async Task Host_code_that_throws_on_its_thread_after_the_shutdown_is_observed_and_gives_its_slot_back()
    {
        // The two unpacks below park two pool threads. Raise the floor first, or on a 4-vCPU runner
        // (the test host holds threads of its own) nothing else in the process, this test's own
        // steps included, runs until new threads arrive, which can take longer than the gate's
        // 10 s hang guard (#120's windows failure; #98).
        using var floor = MessagePickupLive.RaiseThreadPoolFloor(2);
        var marker = "adv4-unobserved-" + Guid.NewGuid().ToString("N");
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == marker))
                Interlocked.Increment(ref unobserved);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var time = new ManualTimeProvider();
            using var shutdown = new CancellationTokenSource();
            using var gate = new ManualResetEventSlim(false);
            using var entered = new SemaphoreSlim(0);
            var guardFired = 0;
            var unpacker = new DeliveredMessageUnpacker((_, _) =>
            {
                entered.Release();
                // Blocks its thread and ignores its token. RealTime is only a hang guard: the test
                // lets it go (gate.Set below); a guard that fires is counted, never ignored.
                if (!gate.Wait(RealTime))
                    Interlocked.Increment(ref guardFired);
                throw new InvalidOperationException(marker);
            }, Budget, time, shutdown.Token);

            var batch = unpacker.UnpackAllAsync(Payloads(2, "m-"), "request-1", default);
            for (var i = 0; i < 2; i++)
                (await entered.WaitAsync(RealTime)).Should().BeTrue();
            // Cancelled synchronously, as the channel's Dispose cancels its shutdown: the delivery
            // ends inside this call. CancelAsync would run the callbacks on a pool work item, and on
            // a busy pool the host code's 10 s gate could time out first and complete the delivery
            // normally (#120: a windows CI run, reproduced by starving the pool).
            shutdown.Cancel();
            Volatile.Read(ref guardFired).Should().Be(0, "the shutdown was decided while both unpacks were still blocked, not after their 10 s hang guard let them go");
            await FluentActions.Awaiting(() => batch.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>();
            unpacker.Reserved.Should().Be(2);

            gate.Set();
            SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
        }
        finally
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            TaskScheduler.UnobservedTaskException -= handler;
        }
        Volatile.Read(ref unobserved).Should().Be(0);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // Whether nothing holds the target any more: collected within a bounded wait (queued disposals of
    // the delivery's timers may still be running when the check starts).
    private static bool BecomesUnreachable(WeakReference weak)
        => SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !weak.IsAlive;
        }, Unreachable);

    private static ReadOnlyMemory<byte>[] Payloads(int count, string prefix)
        => Enumerable.Range(0, count).Select(i => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(prefix + i)).ToArray();

    private static DeliveredUnpack Opened(string text)
    {
        var message = new MessageBuilder().WithId(text).WithType("https://example.org/notes/1.0/note").WithBody(new System.Text.Json.Nodes.JsonObject()).Build();
        var result = new UnpackResult(message, new[] { DidComm.Jose.EnvelopeKind.Plaintext }, false, false, false, false, null, null, null, null, null, null, Array.Empty<string>(), null);
        return new DeliveredUnpack(result, text, 0);
    }
}
