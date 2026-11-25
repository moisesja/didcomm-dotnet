using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111 red-team pass 2, RT2-B: what the memory remembers after fix round 1 (owner decision C,
/// "every failure, fair to late starts"; the pause cap of Q budgets; RT1B-2's single strike per
/// pause). Every timing is driven by a manual clock; real time is only a hang guard.
/// </summary>
public sealed class SlowMemoryRt2BTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private const string AliceKid = "did:example:alice#key-x25519-1";

    /// <summary>
    /// I-9 (as amended by decision C): nothing is written for a message still running when the
    /// caller cancels. The host's lookup ends at once when its token is cancelled and reports that
    /// as a resolution failure (a resolver that maps every error, its cancellation included, to
    /// "notFound"), from inside its cancellation callback. The message had been opening for 1 s
    /// when the caller cancelled. It is not remembered, and the delivery ends cancelled.
    /// </summary>
    /// <remarks>
    /// The lookup registers its callback once <c>UnpackAllAsync</c> has returned, so it runs before
    /// the worker's and the delivery's; the caller cancels from a pool thread, where continuations
    /// run inline. (As first written, the lookup's continuation hopped threads and the order of two
    /// registrations decided the outcome.)
    /// </remarks>
    [Fact]
    public async Task A_message_running_when_the_caller_cancels_is_not_remembered_when_its_lookup_reports_the_cancellation_as_a_failure()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(CallbackLookups.ReportsCancellationAsFailure(calls, register.Task), Budget, time);
        using var caller = new CancellationTokenSource();

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Alice("alice-1") }, "request-1", caller.Token);
        register.SetResult();
        await calls.WaitForAsync(1, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Run(caller.Cancel).WaitAsync(RealTime);
        var ended = await Task.WhenAny(delivery, Task.Delay(RealTime));

        ended.Should().BeSameAs(delivery, "the delivery ends when the caller cancels");
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Should().Equal(new[] { false },
            "it was running when the caller cancelled (the delivery ended {0}); remembered, it would not be opened again for {1} min",
            delivery.Status, unpacker.Memory.FirstPause.TotalMinutes);
        delivery.Status.Should().Be(TaskStatus.Canceled);
    }

    /// <summary>
    /// I-9 (as amended by decision C): nothing is written for a message still running when the
    /// caller cancels, in a delivery of four. Each host lookup ends at once when its token is
    /// cancelled and reports that as a resolution failure, from inside its cancellation callback
    /// (registered once <c>UnpackAllAsync</c> has returned; the caller cancels from a pool thread).
    /// None of the four is remembered, and the delivery ends cancelled.
    /// </summary>
    [Fact]
    public async Task Messages_running_when_the_caller_cancels_are_not_remembered_when_their_lookups_report_the_cancellation_as_a_failure()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(CallbackLookups.ReportsCancellationAsFailure(calls, register.Task), Budget, time);
        using var caller = new CancellationTokenSource();
        var ids = new[] { "alice-1", "alice-2", "alice-3", "alice-4" };

        var delivery = unpacker.UnpackAllAsync(Bob, ids.Select(Alice).ToArray(), "request-1", caller.Token);
        register.SetResult();
        await calls.WaitForAsync(ids.Length, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Run(caller.Cancel).WaitAsync(RealTime);
        var ended = await Task.WhenAny(delivery, Task.Delay(RealTime));

        ended.Should().BeSameAs(delivery, "the delivery ends when the caller cancels");
        unpacker.Memory.Count.Should().Be(0, "all four were running when the caller cancelled (the delivery ended {0})", delivery.Status);
        delivery.Status.Should().Be(TaskStatus.Canceled);
    }

    /// <summary>
    /// The XML's promise on clocks (PickupClient.FetchAsync: "A clock that steps back never holds a
    /// message back longer"): an honest message whose sender's DID host is unreachable from minute
    /// 0 to minute 11 fails at minute 0 (paused 10 min) and at minute 10 (paused 20 min, so opened
    /// again at minute 30, when it opens). With the host clock stepping back one minute at minute
    /// 10.5, it is held back no longer than that: at minute 31 it is not held back.
    /// </summary>
    [Fact(Skip = "RT2 defect: a clock that steps back can hold a message back longer than without the step")]
    public void A_clock_that_steps_back_never_holds_a_message_back_longer()
    {
        var clock = new Qa1Clock();
        var memory = new SlowMemory(clock, TimeSpan.FromTicks(Budget.Ticks * 30), TimeSpan.FromTicks(Budget.Ticks * SlowMemory.EntriesPerPartition), SlowMemory.EntriesPerPartition, SlowMemory.MaxPartitions);
        var id = new[] { "alice-1" };

        memory.Strike(Bob, "alice-1"); // minute 0: it fails (its sender's host is unreachable)
        clock.Inner.Advance(TimeSpan.FromMinutes(10));
        memory.HeldBack(Bob, id).Should().Equal(new[] { false }, "its first pause has ended");
        memory.Strike(Bob, "alice-1"); // minute 10: it fails again, paused 20 min (until minute 30)
        clock.Inner.Advance(TimeSpan.FromSeconds(30));
        clock.Step(TimeSpan.FromMinutes(-1)); // minute 10.5: the host clock steps back one minute
        var openedAgain = !memory.HeldBack(Bob, id)[0]; // a reading below the strike's is due (I-12)
        if (openedAgain)
            memory.Strike(Bob, "alice-1"); // opened once more, it fails (its sender's host is unreachable until minute 11)

        clock.Inner.Advance(TimeSpan.FromMinutes(20.5)); // minute 31: without the step it was opened at minute 30
        memory.HeldBack(Bob, id).Should().Equal(new[] { false },
            "without the step it is opened again at minute 30; with it (opened again at minute 10.5: {0}) it is held back until minute {1}",
            openedAgain, 10.5 + memory.PauseAfter(3).TotalMinutes);
    }

    private static DeliveredInput Alice(string id) => new(id, TestJwe.Bytes(TestJwe.Authcrypt(AliceKid, id)));
}
