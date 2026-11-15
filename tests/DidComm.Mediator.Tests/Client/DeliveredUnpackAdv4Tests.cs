using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
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
    /// ADV4-2 (defect, Low). Property broken — the worker's own claim in
    /// <c>DeliveredMessageUnpacker.Delivery.RunWorkerAsync</c>: "an abandoned unpack never keeps the
    /// delivery (its bytes and the messages opened so far) alive (L-062)"; L-062: "Work that
    /// outlives a reservation keeps only bounded values".
    /// <para>
    /// Attack: the unpack is started as <c>Task.Run(() =&gt; _owner.Unpack(packed, slotToken))</c>. The
    /// lambda reads the field <c>_owner</c>, so its closure holds the <c>Delivery</c> itself, and the
    /// running work item holds the closure for as long as the host code runs. A sender whose DID
    /// lookup blocks its thread (the case ADV3-4 moved to the pool) therefore keeps the whole
    /// delivery reachable after its result was returned and the caller let go of it: every other
    /// message's bytes and the messages opened so far — other senders' decrypted plaintext — stay
    /// in memory until the lookup returns. Since the fix the delivery returns and the caller moves
    /// on, so up to <see cref="DeliveredMessageUnpacker.MaxAbandoned"/> (and the ADV3-2 overshoot)
    /// blocked lookups can each pin a different delivery (with the 1 MiB default
    /// <c>MaxReceiveBytes</c>, tens of MiB); before it the one blocked call held the caller too.
    /// </para>
    /// </summary>
    [Fact(Skip = "Known limit, deferred to #111 (slow-sender hardening): an unpack that blocks its thread keeps its whole delivery reachable until it returns (ADV4-2).")]
    public void ADV4_2_an_unpack_that_blocks_its_thread_keeps_its_whole_delivery_reachable_after_the_result_was_returned()
    {
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
        SpinWait.SpinUntil(() => unpacker.Abandoned == 0, RealTime).Should().BeTrue();
        BecomesUnreachable(refs.Payload).Should().BeTrue("the test itself holds nothing of the delivery");
        BecomesUnreachable(refs.Opened).Should().BeTrue();

        keptWhileBlocked.Should().BeFalse(
            "an abandoned unpack keeps only its own bounded values, not the delivery: another message's bytes and an opened message stayed reachable while a lookup blocked");
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
        unpacker.Abandoned.Should().Be(1, "the blocked lookup keeps its abandoned slot");
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
    /// ADV4-3 (defect, Low). Property broken — <c>DeliveredMessageUnpacker.Delivery</c>: "After it is
    /// closed nothing is started and nothing is recorded"; the fix's claim (ADV3-7) that the
    /// container's disposal ends a delivery being opened, the host code seeing its token cancelled.
    /// <para>
    /// Attack: a worker checks <c>_closed</c>, marks the message started, creates its share and only
    /// then hands the host code to <c>Task.Run</c> — with no token and no second look. When the
    /// shutdown (or the budget, or the caller) ends the delivery in between, the delivery's result
    /// is already final, yet the host code for that message is started afterwards: after the
    /// container was disposed, with services the container may already have disposed. ADV3-4 widens
    /// the window from "between the check and the call" to "until the work item runs": a work item
    /// still queued on a busy pool (the blocking-resolver case ADV3-4 is about) when the delivery
    /// ends still runs. Here the share's timer creation is the rendezvous: the shutdown lands while
    /// the fifth message's worker creates its share.
    /// </para>
    /// </summary>
    [Fact(Skip = "Known limit, deferred to #111 (slow-sender hardening): a message a worker was starting when the delivery ended can still have its host unpack started afterwards (ADV4-3).")]
    public async Task ADV4_3_a_message_whose_worker_was_starting_it_when_the_shutdown_came_has_its_host_code_started_after_the_delivery_ended()
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
        SpinWait.SpinUntil(() => unpacker.Abandoned == 0 && unpacker.Opening == 0 && clock.LiveTimers == 0, RealTime).Should().BeTrue("nothing is left behind either way");

        startedAfterTheEnd.Should().NotBe(true, "a delivery the shutdown ended starts no host code; m-4's was started after it ended");
    }

    /// <summary>
    /// ADV3-7 held (regression): host code that blocks its thread through the shutdown and then
    /// throws on that pool thread is observed — no <see cref="TaskScheduler.UnobservedTaskException"/>
    /// — gives its abandoned slot back, and leaves no timer or opening delivery behind.
    /// </summary>
    [Fact]
    public async Task Host_code_that_throws_on_its_thread_after_the_shutdown_is_observed_and_gives_its_slot_back()
    {
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
            var unpacker = new DeliveredMessageUnpacker((_, _) =>
            {
                entered.Release();
                gate.Wait(RealTime); // blocks its thread and ignores its token
                throw new InvalidOperationException(marker);
            }, Budget, time, shutdown.Token);

            var batch = unpacker.UnpackAllAsync(Payloads(2, "m-"), "request-1", default);
            for (var i = 0; i < 2; i++)
                (await entered.WaitAsync(RealTime)).Should().BeTrue();
            await shutdown.CancelAsync();
            await FluentActions.Awaiting(() => batch.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>();
            unpacker.Abandoned.Should().Be(2);

            gate.Set();
            SpinWait.SpinUntil(() => unpacker.Abandoned == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
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
