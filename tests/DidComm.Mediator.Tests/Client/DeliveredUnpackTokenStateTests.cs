using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// How the unpacker tells a message's own failure from the end of its delivery (#111): by whether
/// the delivery is being ended when the worker observes the outcome, never by the type of what was
/// thrown. Host code may report its cancellation as a failure of its own (a resolver that maps every
/// error, its cancellation included, to "notFound"), and may do so from inside a cancellation
/// callback, which runs before the delivery's own.
/// </summary>
/// <remarks>
/// The lookups that end inside a callback register it only once the test has seen
/// <c>UnpackAllAsync</c> return and the lookups are parked (<see cref="CallbackLookups"/>,
/// <see cref="RegistrationGate"/>), so the order of the callbacks is fixed. Every cancellation and
/// every move of the clock that ends a delivery is made from a thread-pool thread, where
/// continuations run inline as they do in a host; the test's own thread has a synchronization
/// context that would queue them. Real time is only a hang guard.
/// </remarks>
public sealed class DeliveredUnpackTokenStateTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    // As many messages of one delivery as are opened at the same time (sixteen: #111, owner decision G).
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;

    // ---- a failure reported after the delivery's token was cancelled --------------------------------

    /// <summary>
    /// The caller cancels while a message is being opened, and its lookup reports the cancellation
    /// as a resolution failure from inside its cancellation callback. The delivery ends cancelled,
    /// as for any caller's cancellation, and the message is not remembered.
    /// </summary>
    [Fact]
    public async Task A_failure_reported_when_the_caller_cancels_leaves_the_result_cancelled_and_nothing_remembered()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        var unpacker = new DeliveredMessageUnpacker(CallbackLookups.ReportsCancellationAsFailure(calls, register), Budget, time);
        using var caller = new CancellationTokenSource();

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message("alice-1") }, "request-1", caller.Token);
        await register.OpenAsync(1, RealTime); // the worker waits on its call: the lookup's callback is registered after the worker's
        await calls.WaitForAsync(1, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Run(caller.Cancel).WaitAsync(RealTime);
        await Ended(delivery);

        delivery.Status.Should().Be(TaskStatus.Canceled, "the caller cancelled: what its lookup reported then is not the message's own failure ({0})", Describe(delivery, unpacker));
        unpacker.Memory.Count.Should().Be(0, "a message still running when the caller cancelled is not remembered");
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// The budget ends for a message whose code started 14 s into it, and its lookup reports the
    /// cancellation as a resolution failure from inside its cancellation callback. The message's
    /// outcome is the delivery's timeout, not that failure, and it is not remembered: it started
    /// later than the start allowance, so it did not have the whole budget.
    /// </summary>
    [Fact]
    public async Task A_failure_reported_when_the_budget_ends_for_a_late_started_message_is_a_timeout_and_is_not_remembered()
    {
        var late = TimeSpan.FromSeconds(14);
        var time = UnpackClock.SteppingAfterTheFirstBudget(new ManualTimeProvider(), late);
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        var unpacker = new DeliveredMessageUnpacker(CallbackLookups.ReportsCancellationAsFailure(calls, register), Budget, time);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message("alice-1") }, "request-1", default);
        await register.OpenAsync(1, RealTime);
        await calls.WaitForAsync(1, RealTime);
        await Task.Run(() => time.Inner.Advance(Budget - late)).WaitAsync(RealTime);
        var outcomes = await delivery.WaitAsync(RealTime);

        late.Should().BeGreaterThan(unpacker.StartAllowance);
        outcomes.Single().Error.Should().BeOfType<TimeoutException>("the budget ended: what its lookup reported then is not the message's own failure")
            .Which.Message.Should().Contain("ran out while it was being opened");
        unpacker.Memory.Count.Should().Be(0, "its code started {0} into the budget, later than the start allowance of {1}", late, unpacker.StartAllowance);
    }

    /// <summary>
    /// The budget ends for a message whose code started with it, and its lookup reports the
    /// cancellation as a resolution failure from inside its cancellation callback. The message's
    /// outcome is the delivery's timeout, and it is remembered: it had the whole budget (S2).
    /// </summary>
    [Fact]
    public async Task A_failure_reported_when_the_budget_ends_for_a_message_that_had_the_whole_budget_is_a_timeout_and_is_remembered()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        var unpacker = new DeliveredMessageUnpacker(CallbackLookups.ReportsCancellationAsFailure(calls, register), Budget, time);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message("alice-1") }, "request-1", default);
        await register.OpenAsync(1, RealTime);
        await calls.WaitForAsync(1, RealTime);
        await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes.Single().Error.Should().BeOfType<TimeoutException>("the budget ended: what its lookup reported then is not the message's own failure")
            .Which.Message.Should().Contain("ran out while it was being opened");
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Single().Should().BeTrue("it started with the budget and had not opened when the budget ended");
    }

    /// <summary>
    /// Host code that watches the caller's own token (the one given to <c>UnpackAllAsync</c>), or the
    /// owner's shutdown token, instead of the token it is handed: its callback there runs before the
    /// delivery's token is cancelled. What it reports from that callback is not the message's own
    /// failure either: the delivery ends cancelled and nothing is remembered.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failure_reported_from_a_callback_on_the_callers_token_or_on_the_shutdown_token_leaves_the_result_cancelled_and_nothing_remembered(bool byShutdown)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        using var caller = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        var watched = byShutdown ? shutdown : caller;
        var unpacker = new DeliveredMessageUnpacker(CallbackLookups.ReportsCancellationAsFailure(calls, register, on: _ => watched.Token), Budget, time, shutdown.Token);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message("alice-1") }, "request-1", caller.Token);
        await register.OpenAsync(1, RealTime);
        await calls.WaitForAsync(1, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Run(watched.Cancel).WaitAsync(RealTime);
        await Ended(delivery);

        delivery.Status.Should().Be(TaskStatus.Canceled, "the {0}: what the lookup reported from its callback on that token is not the message's own failure ({1})", byShutdown ? "owner shut down" : "caller cancelled", Describe(delivery, unpacker));
        unpacker.Memory.Count.Should().Be(0);
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    // ---- the caller's cancellation and the shutdown ---------------------------------------------------

    /// <summary>
    /// The caller's cancellation and the owner's shutdown, 10 s into a delivery of seventeen (one
    /// more than are opened at once) whose first sixteen messages have been opening since its
    /// start: the result is cancelled with that token, nothing is remembered, and the seventeenth
    /// message never starts; whatever the lookups do when their token is cancelled. A lookup still
    /// running keeps its reservation until it ends. (Each message names a sender of its own, so no
    /// partition's bound stands between the seventeenth and a worker: it waits for a worker only.)
    /// </summary>
    [Theory]
    [InlineData(false, WhenCancelled.Ends)]
    [InlineData(false, WhenCancelled.RunsOn)]
    [InlineData(false, WhenCancelled.ReportsAFailure)]
    [InlineData(true, WhenCancelled.Ends)]
    [InlineData(true, WhenCancelled.RunsOn)]
    [InlineData(true, WhenCancelled.ReportsAFailure)]
    public async Task The_callers_cancellation_and_the_shutdown_cancel_the_result_and_remember_nothing(bool byShutdown, WhenCancelled whenCancelled)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        var host = NeverOpens.Lookup(whenCancelled, calls, register, never.Task);
        var unpacker = new DeliveredMessageUnpacker(host, Budget, time, shutdown.Token);
        var ids = Enumerable.Range(1, AtOnce + 1).Select(i => "alice-" + i).ToArray();
        try
        {
            var delivery = unpacker.UnpackAllAsync(Bob, ids.Select(FromItsOwnSender).ToArray(), "request-1", caller.Token);
            await register.OpenAsync(whenCancelled == WhenCancelled.ReportsAFailure ? AtOnce : 0, RealTime);
            await calls.WaitForAsync(AtOnce, RealTime);
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Run((byShutdown ? shutdown : caller).Cancel).WaitAsync(RealTime);

            var cancelled = (await FluentActions.Awaiting(() => delivery.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>()).Which;
            cancelled.CancellationToken.Should().Be(byShutdown ? shutdown.Token : caller.Token);
            unpacker.Memory.Count.Should().Be(0, "neither the caller's cancellation nor the shutdown says anything about a message");
            calls.Count.Should().Be(AtOnce, "the seventeenth message never started");
            calls.Labels.Should().NotContain(ids[^1]);
            calls.Tokens.Should().OnlyContain(t => t.IsCancellationRequested, "the host code opening the delivery is told to stop");
            if (whenCancelled == WhenCancelled.RunsOn)
                unpacker.Reserved.Should().Be(AtOnce, "a lookup still running keeps its reservation");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue("every reservation came back and nothing is left behind");
    }

    // ---- no further message once the delivery is being ended ----------------------------------------

    /// <summary>
    /// A worker takes no further message once the delivery is being ended. The first message opens
    /// from inside a cancellation callback, so its worker records the success while the delivery is
    /// still open: when the budget ends (the callback is on the token the lookup was handed), and
    /// when the caller cancels (the callback is on the caller's own token, which runs before the
    /// delivery's token is cancelled). The seventeenth message (one more than are opened at once),
    /// not valid UTF-8, was waiting for a worker; a worker that took it would record it as
    /// malformed at once. No worker takes it: one outcome was recorded by a worker, and the
    /// seventeenth message is out of time before its turn. (The callback stays inside the
    /// cancellation until that success is recorded, so the delivery is still open then, whichever
    /// thread the worker runs on.)
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_worker_takes_no_further_message_once_the_delivery_is_being_ended(bool byTheCaller)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        using var caller = new CancellationTokenSource();
        DeliveredMessageUnpacker? opening = null;
        var opensWhenCancelled = CallbackLookups.OpensWhenCancelled(calls, register, recorded: () => opening!.Settled >= 1, byTheCaller ? _ => caller.Token : null);
        var unpacker = opening = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label == "opens-0")
                return await opensWhenCancelled(packed, ct).ConfigureAwait(false);
            using (calls.Enter(label, ct))
                return await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false);
        }, Budget, time);
        // opens-0 and fifteen that stall hold the sixteen workers; the one that is not valid UTF-8 waits.
        var messages = Enumerable.Range(1, AtOnce - 1).Select(i => "stall-" + i).Prepend("opens-0").Select(Message)
            .Append(new DeliveredInput("not-utf8", new byte[] { 0xC3, 0x28, 0xFF }))
            .ToArray();
        messages.Should().HaveCount(AtOnce + 1);

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", caller.Token);
        await register.OpenAsync(1, RealTime);
        await calls.WaitForAsync(AtOnce, RealTime);
        await Task.Run(() =>
        {
            if (byTheCaller)
                caller.Cancel();
            else
                time.Advance(Budget);
        }).WaitAsync(RealTime);
        await Ended(delivery);

        unpacker.Settled.Should().Be(1, "the first message's success was recorded by its worker while the delivery was being ended, and that worker took nothing more");
        if (byTheCaller)
        {
            delivery.Status.Should().Be(TaskStatus.Canceled);
        }
        else
        {
            var outcomes = await delivery;
            outcomes[0].Succeeded.Should().BeTrue("it opened inside the budget's cancellation, before the delivery was closed");
            outcomes[AtOnce].Error.Should().BeOfType<TimeoutException>("no worker took it once the delivery's token was cancelled").Which.Message.Should().Contain("before its turn came");
            outcomes.Skip(1).Take(AtOnce - 1).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// A delivery whose only message opens from inside a cancellation callback: when the budget
    /// ends (the callback is on the token the lookup was handed), and when the caller cancels (the
    /// callback is on the caller's own token). Its worker records the success while the delivery is
    /// still open, which ends the delivery there, inside that cancellation: the result is the
    /// success, nothing throws out of the cancellation, and no timer, reservation or opening
    /// delivery is left behind.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_delivery_whose_last_message_opens_inside_the_cancellation_ends_there_with_its_outcome_and_leaves_nothing_behind(bool byTheCaller)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        using var caller = new CancellationTokenSource();
        DeliveredMessageUnpacker? opening = null;
        var unpacker = opening = new DeliveredMessageUnpacker(
            CallbackLookups.OpensWhenCancelled(calls, register, recorded: () => opening!.Settled >= 1, byTheCaller ? _ => caller.Token : null), Budget, time);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message("alice-1") }, "request-1", caller.Token);
        await register.OpenAsync(1, RealTime);
        await calls.WaitForAsync(1, RealTime);
        var ending = await Record.ExceptionAsync(() => Task.Run(() =>
        {
            if (byTheCaller)
                caller.Cancel();
            else
                time.Advance(Budget);
        }).WaitAsync(RealTime));
        var outcomes = await delivery.WaitAsync(RealTime);

        ending.Should().BeNull("nothing escapes from the cancellation the delivery ended in");
        outcomes.Single().Succeeded.Should().BeTrue("the message opened before the delivery was closed");
        unpacker.Memory.Count.Should().Be(0);
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0 && time.ScheduledTimers == 0, RealTime).Should().BeTrue();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static DeliveredInput Message(string id) => new(id, TestJwe.Bytes(TestJwe.Anoncrypt(id)));

    // A message naming a sender no other message names: it has a partition to itself.
    private static DeliveredInput FromItsOwnSender(string id) => new(id, TestJwe.Bytes(TestJwe.Authcrypt($"did:example:sender-of-{id}#key-1", id)));

    // What a delivery that did not end cancelled ended with, for a failing assertion's text.
    private static string Describe(Task<IReadOnlyList<DeliveredOutcome>> delivery, DeliveredMessageUnpacker unpacker)
        => delivery.IsCompletedSuccessfully
            ? $"it ended with {string.Join(", ", delivery.Result.Select(o => o.Succeeded ? "a success" : o.Error!.GetType().Name))}; remembered: {unpacker.Memory.Count}"
            : $"it ended {delivery.Status}; remembered: {unpacker.Memory.Count}";

    // Wait (bounded) for the delivery to end, however it ends.
    private static Task Ended(Task delivery) => delivery.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
}
