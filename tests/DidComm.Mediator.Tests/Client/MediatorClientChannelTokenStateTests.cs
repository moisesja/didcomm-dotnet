using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The request path of the real channel with host code (a transport, an endpoint lookup) that
/// reports the cancellation of its token as a failure of its own, from inside a cancellation
/// callback (#111). A call whose caller cancels, whose
/// <see cref="MediatorClientOptions.RequestTimeout"/> passes or whose container is disposed while
/// that host code runs ends as the cancellation, the timeout or the disposal does, whatever the host
/// code reported then. A failure the host code reports while none of the three has happened is
/// passed on as it is.
/// </summary>
/// <remarks>
/// The set-up of every test: the call is started and returns once it waits on its host code; only
/// then does the host code register its callback, so that callback runs before the channel's own.
/// Whatever ends the call is done from a thread-pool thread, where continuations run inline as they
/// do in a host. Real time is only a hang guard.
/// </remarks>
public sealed class MediatorClientChannelTokenStateTests : IAsyncLifetime
{
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    /// <summary>The call under test, named by the host code it waits on.</summary>
    public enum Step
    {
        /// <summary><c>ResolveRouteAsync</c>: the mediator's endpoint is looked up.</summary>
        EndpointLookup,

        /// <summary><c>ExchangeAsync</c>: a request is sent.</summary>
        Send,

        /// <summary><c>ExchangeAllowingNoAnswerAsync</c> with an answer wait: a request the mediator may leave unanswered is sent.</summary>
        SendAllowingNoAnswer,

        /// <summary><c>SendOneWayAsync</c>: a message no answer is expected to is sent.</summary>
        OneWaySend,
    }

    /// <summary>What ends a call whose host code is still running.</summary>
    public enum Ending
    {
        /// <summary>The caller cancels.</summary>
        CallerCancels,

        /// <summary>The call's deadline (<see cref="MediatorClientOptions.RequestTimeout"/>) passes.</summary>
        RequestTimesOut,

        /// <summary>The container is disposed.</summary>
        ContainerDisposed,
    }

    // ---- a call ended while its host code runs -----------------------------------------------------

    /// <summary>
    /// The caller cancels, the deadline passes or the container is disposed while the call's host
    /// code runs; the host code reports the cancellation of the token it was handed as a failure of
    /// its own, from its callback there. The call ends as the cancellation (with the caller's
    /// token), the timeout or the disposal does.
    /// </summary>
    [Theory]
    [InlineData(Step.EndpointLookup, Ending.CallerCancels)]
    [InlineData(Step.EndpointLookup, Ending.RequestTimesOut)]
    [InlineData(Step.EndpointLookup, Ending.ContainerDisposed)]
    [InlineData(Step.Send, Ending.CallerCancels)]
    [InlineData(Step.Send, Ending.RequestTimesOut)]
    [InlineData(Step.Send, Ending.ContainerDisposed)]
    [InlineData(Step.SendAllowingNoAnswer, Ending.CallerCancels)]
    [InlineData(Step.SendAllowingNoAnswer, Ending.RequestTimesOut)]
    [InlineData(Step.SendAllowingNoAnswer, Ending.ContainerDisposed)]
    [InlineData(Step.OneWaySend, Ending.CallerCancels)]
    [InlineData(Step.OneWaySend, Ending.RequestTimesOut)]
    [InlineData(Step.OneWaySend, Ending.ContainerDisposed)]
    public async Task A_call_ended_while_its_host_code_runs_ends_as_what_ended_it_whatever_the_host_code_reported(Step step, Ending ending)
    {
        var time = new ManualTimeProvider();
        var host = new HeldHostCode { ReportsWhenCancelled = () => FailureOf(step) };
        using var caller = new CancellationTokenSource();
        var (channel, send) = ChannelOver(time, host);

        var (call, request) = Start(channel, step, caller.Token);
        await host.ParkAsync();
        await Task.Run(() => End(ending, caller, time, channel)).WaitAsync(PickupTest.Timeout);

        var ended = await EndOf(call);
        host.Reported.Should().Be(1, "the host code reported the cancellation of its token as a failure of its own");
        ShouldHaveEndedAs(ended, ending, step, caller.Token, request);
        channel.PendingCount.Should().Be(0);
        time.ScheduledTimers.Should().Be(0, "the call's deadline was released");
        if (step == Step.EndpointLookup)
            send.Count.Should().Be(0, "nothing is sent before the endpoint is found");
        await channel.DisposeAsync();
    }

    /// <summary>
    /// Two of the three endings are true by the time the host code reports its failure: the first is
    /// made from a thread-pool thread, the second by the host code's callback just before it reports.
    /// The call ends as the first of disposal, the caller's cancellation and the timeout that is
    /// true, in that order, whichever happened first.
    /// </summary>
    [Theory]
    [MemberData(nameof(TwoEndings))]
    public async Task With_two_endings_true_when_the_host_code_reports_the_call_ends_as_disposal_then_cancellation_then_timeout(Step step, Ending first, Ending second, Ending reported)
    {
        var time = new ManualTimeProvider();
        var host = new HeldHostCode { ReportsWhenCancelled = () => FailureOf(step) };
        using var caller = new CancellationTokenSource();
        var (channel, _) = ChannelOver(time, host);
        host.BeforeItReports = _ => End(second, caller, time, channel);

        var (call, request) = Start(channel, step, caller.Token);
        await host.ParkAsync();
        await Task.Run(() => End(first, caller, time, channel)).WaitAsync(PickupTest.Timeout);

        var ended = await EndOf(call);
        host.Reported.Should().Be(1, "the host code reported the cancellation of its token as a failure of its own");
        ShouldHaveEndedAs(ended, reported, step, caller.Token, request);
        channel.PendingCount.Should().Be(0);
        await channel.DisposeAsync();
    }

    /// <summary>Each step with each pair of endings, in both orders, and the one that is reported.</summary>
    public static TheoryData<Step, Ending, Ending, Ending> TwoEndings()
    {
        var rows = new TheoryData<Step, Ending, Ending, Ending>();
        foreach (var step in Enum.GetValues<Step>())
        {
            rows.Add(step, Ending.RequestTimesOut, Ending.CallerCancels, Ending.CallerCancels);
            rows.Add(step, Ending.CallerCancels, Ending.RequestTimesOut, Ending.CallerCancels);
            rows.Add(step, Ending.RequestTimesOut, Ending.ContainerDisposed, Ending.ContainerDisposed);
            rows.Add(step, Ending.ContainerDisposed, Ending.RequestTimesOut, Ending.ContainerDisposed);
            rows.Add(step, Ending.CallerCancels, Ending.ContainerDisposed, Ending.ContainerDisposed);
            rows.Add(step, Ending.ContainerDisposed, Ending.CallerCancels, Ending.ContainerDisposed);
        }
        return rows;
    }

    /// <summary>
    /// The answer to a request arrives while its send still runs, and wins. The call is then ended,
    /// and the transport reports the cancellation of its token as a failure of its own. The answer
    /// holds: the call returns it.
    /// </summary>
    [Theory]
    [InlineData(false, Ending.CallerCancels)]
    [InlineData(false, Ending.RequestTimesOut)]
    [InlineData(false, Ending.ContainerDisposed)]
    [InlineData(true, Ending.CallerCancels)]
    [InlineData(true, Ending.RequestTimesOut)]
    [InlineData(true, Ending.ContainerDisposed)]
    public async Task An_answer_that_won_while_the_send_ran_holds_whatever_the_transport_reports_when_the_call_is_ended(bool allowingNoAnswer, Ending ending)
    {
        var time = new ManualTimeProvider();
        var host = new HeldHostCode { ReportsWhenCancelled = () => FailureOf(Step.Send) };
        using var caller = new CancellationTokenSource();
        var (channel, _) = ChannelOver(time, host);

        var (call, request) = Start(channel, allowingNoAnswer ? Step.SendAllowingNoAnswer : Step.Send, caller.Token);
        var answer = await _w.SnapshotAsync(_w.Status(request!.Id));
        await host.ParkAsync();
        channel.AsCorrelator().OnInbound(answer).Should().BeTrue("a request is registered before it is sent, so its answer can arrive while the send runs");
        await Task.Run(() => End(ending, caller, time, channel)).WaitAsync(PickupTest.Timeout);

        var ended = await EndOf(call);
        host.Reported.Should().Be(1, "the transport reported the cancellation of its token as a failure of its own");
        ended.Should().BeNull("the answer had won before the call was ended");
        (await (Task<InboundMessageSnapshot?>)call).Should().BeSameAs(answer);
        channel.PendingCount.Should().Be(0);
        await channel.DisposeAsync();
    }

    // ---- a failure observed before the deadline's token is cancelled --------------------------------

    /// <summary>
    /// The host code watches the caller's own token, not the one it was handed, and reports its
    /// cancellation as a failure of its own from its callback there. That callback was registered
    /// after the call's, so it runs first: the caller has cancelled, and the token the host code was
    /// handed is not cancelled yet. The call ends cancelled, with the caller's token.
    /// </summary>
    [Theory]
    [InlineData(Step.EndpointLookup)]
    [InlineData(Step.Send)]
    [InlineData(Step.SendAllowingNoAnswer)]
    [InlineData(Step.OneWaySend)]
    public async Task A_call_whose_host_code_reports_from_a_callback_on_the_callers_own_token_ends_cancelled(Step step)
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var handedWasCancelled = true;
        var host = new HeldHostCode
        {
            ReportsWhenCancelled = () => FailureOf(step),
            Watches = _ => caller.Token,
            BeforeItReports = handed => handedWasCancelled = handed.IsCancellationRequested,
        };
        var (channel, _) = ChannelOver(time, host);

        var (call, request) = Start(channel, step, caller.Token);
        await host.ParkAsync();
        await Task.Run(caller.Cancel).WaitAsync(PickupTest.Timeout);

        var ended = await EndOf(call);
        host.Reported.Should().Be(1, "the host code reported the caller's cancellation as a failure of its own");
        handedWasCancelled.Should().BeFalse("the set-up: the token the host code was handed was not cancelled yet when it reported");
        ShouldHaveEndedAs(ended, Ending.CallerCancels, step, caller.Token, request);
        channel.PendingCount.Should().Be(0);
        time.ScheduledTimers.Should().Be(0, "the call's deadline was released");
        await channel.DisposeAsync();
    }

    /// <summary>
    /// Two calls of one caller are in flight when the caller cancels, or the container is disposed.
    /// The call started second is reached first; its host code, from its cancellation callback,
    /// fails the first call's host code too (one connection carries both), before the token that
    /// first call's host code was handed is cancelled. Both calls end as the cancellation, or the
    /// disposal, does.
    /// </summary>
    [Theory]
    [InlineData(Step.EndpointLookup, Ending.CallerCancels)]
    [InlineData(Step.EndpointLookup, Ending.ContainerDisposed)]
    [InlineData(Step.Send, Ending.CallerCancels)]
    [InlineData(Step.Send, Ending.ContainerDisposed)]
    [InlineData(Step.SendAllowingNoAnswer, Ending.CallerCancels)]
    [InlineData(Step.SendAllowingNoAnswer, Ending.ContainerDisposed)]
    [InlineData(Step.OneWaySend, Ending.CallerCancels)]
    [InlineData(Step.OneWaySend, Ending.ContainerDisposed)]
    public async Task A_call_failed_by_another_calls_host_code_as_both_are_ended_ends_as_what_ended_them(Step step, Ending ending)
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var firstHandedWasCancelled = true;
        var first = new HeldHostCode();
        var second = new HeldHostCode { ReportsWhenCancelled = () => FailureOf(step) };
        second.BeforeItReports = _ =>
        {
            firstHandedWasCancelled = first.Handed.IsCancellationRequested;
            first.EndWith(FailureOf(step));
        };
        var (channel, _) = ChannelOver(time, first, second);

        var (firstCall, firstRequest) = Start(channel, step, caller.Token);
        var (secondCall, secondRequest) = Start(channel, step, caller.Token);
        await first.ParkAsync();
        await second.ParkAsync();
        await Task.Run(() => End(ending, caller, time, channel)).WaitAsync(PickupTest.Timeout);

        var firstEnded = await EndOf(firstCall);
        var secondEnded = await EndOf(secondCall);
        second.Reported.Should().Be(1, "the second call's host code reported the cancellation of its token as a failure of its own");
        firstHandedWasCancelled.Should().BeFalse("the set-up: the token the first call's host code was handed was not cancelled yet when that host code was failed");
        ShouldHaveEndedAs(firstEnded, ending, step, caller.Token, firstRequest);
        ShouldHaveEndedAs(secondEnded, ending, step, caller.Token, secondRequest);
        channel.PendingCount.Should().Be(0);
        time.ScheduledTimers.Should().Be(0, "both deadlines were released");
        await channel.DisposeAsync();
    }

    // ---- a failure of the call's own ---------------------------------------------------------------

    /// <summary>
    /// The host code fails on its own while it runs (a failure of its own kind, or a cancellation of
    /// a wait of its own): the caller has not cancelled, the deadline has not passed and the
    /// container is not disposed. The call ends with that very exception.
    /// </summary>
    [Theory]
    [InlineData(Step.EndpointLookup, false)]
    [InlineData(Step.EndpointLookup, true)]
    [InlineData(Step.Send, false)]
    [InlineData(Step.Send, true)]
    [InlineData(Step.SendAllowingNoAnswer, false)]
    [InlineData(Step.SendAllowingNoAnswer, true)]
    [InlineData(Step.OneWaySend, false)]
    [InlineData(Step.OneWaySend, true)]
    public async Task A_failure_the_host_code_reports_while_nothing_has_ended_the_call_is_passed_on_as_it_is(Step step, bool aCancellationOfItsOwn)
    {
        var time = new ManualTimeProvider();
        var host = new HeldHostCode();
        using var caller = new CancellationTokenSource();
        var (channel, _) = ChannelOver(time, host);
        var failure = aCancellationOfItsOwn ? new OperationCanceledException("The host code gave up on a wait of its own.") : FailureOf(step);

        var (call, _) = Start(channel, step, caller.Token);
        await host.ParkAsync();
        await Task.Run(() => host.EndWith(failure)).WaitAsync(PickupTest.Timeout);

        var ended = await EndOf(call);
        ended.Should().BeSameAs(failure, "nothing had ended the call when its host code failed: the failure is the call's own");
        host.Handed.IsCancellationRequested.Should().BeFalse("the token the host code was handed was never cancelled");
        channel.IsDisposed.Should().BeFalse();
        channel.PendingCount.Should().Be(0);
        time.ScheduledTimers.Should().Be(0, "the call's deadline was released");
        await channel.DisposeAsync();
    }

    /// <summary>
    /// Two calls are in flight; the deadline of the one started first passes, and its host code, from
    /// its cancellation callback, fails the other call's host code too (one connection carries both).
    /// Nothing has ended that other call: its deadline is still to come. It ends with its host
    /// code's failure, and the first call with its timeout.
    /// </summary>
    [Theory]
    [InlineData(Step.EndpointLookup)]
    [InlineData(Step.Send)]
    [InlineData(Step.SendAllowingNoAnswer)]
    [InlineData(Step.OneWaySend)]
    public async Task A_call_failed_by_another_calls_host_code_while_only_that_other_call_times_out_keeps_its_failure(Step step)
    {
        var time = new ManualTimeProvider();
        var failure = FailureOf(step);
        var own = new HeldHostCode();
        var other = new HeldHostCode { ReportsWhenCancelled = () => FailureOf(step), BeforeItReports = _ => own.EndWith(failure) };
        var (channel, _) = ChannelOver(time, other, own);
        var lead = channel.Options.RequestTimeout / 3;

        var (otherCall, otherRequest) = Start(channel, step, CancellationToken.None);
        time.Advance(lead).Should().Be(0, "the other call's deadline is still to come");
        var (call, _) = Start(channel, step, CancellationToken.None);
        await other.ParkAsync();
        await own.ParkAsync();
        var fired = await Task.Run(() => time.Advance(channel.Options.RequestTimeout - lead)).WaitAsync(PickupTest.Timeout);

        fired.Should().Be(1, "only the deadline of the call started first has passed");
        (await EndOf(call)).Should().BeSameAs(failure, "nothing had ended this call when its host code failed");
        ShouldHaveEndedAs(await EndOf(otherCall), Ending.RequestTimesOut, step, CancellationToken.None, otherRequest);
        channel.PendingCount.Should().Be(0);
        await channel.DisposeAsync();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>
    /// Host code the channel waits on (a send, an endpoint lookup), held until something ends it. It
    /// never ends on its own. Once the test lets it (<see cref="ParkAsync"/>), it registers a callback
    /// on the token it watches, which reports <see cref="ReportsWhenCancelled"/>, and parks; the
    /// channel's continuation then runs on the thread that ends it.
    /// </summary>
    private sealed class HeldHostCode
    {
        private readonly TaskCompletionSource _mayRegister = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _ends = new(); // its continuations run inline, on the thread that ends it
        private int _reported;

        /// <summary>What it reports, from its callback, when the token it watches is cancelled; <c>null</c>: it registers nothing and ignores its token.</summary>
        public Func<Exception>? ReportsWhenCancelled { get; init; }

        /// <summary>The token it watches, given the one it was handed; the one it was handed by default.</summary>
        public Func<CancellationToken, CancellationToken> Watches { get; init; } = handed => handed;

        /// <summary>Runs inside its callback, before it reports; it gets the token the host code was handed.</summary>
        public Action<CancellationToken>? BeforeItReports { get; set; }

        /// <summary>The token the channel handed it.</summary>
        public CancellationToken Handed { get; private set; }

        /// <summary>How many times its callback reported its failure.</summary>
        public int Reported => Volatile.Read(ref _reported);

        public async Task RunAsync(CancellationToken handed)
        {
            Handed = handed;
            await _mayRegister.Task.ConfigureAwait(false);
            if (ReportsWhenCancelled is { } failure)
            {
                Watches(handed).UnsafeRegister(_ =>
                {
                    BeforeItReports?.Invoke(handed);
                    if (_ends.TrySetException(failure()))
                        Interlocked.Increment(ref _reported);
                }, null);
            }
            await new ParkedAwaitable<bool>(_ends.Task, () => _parked.TrySetResult());
        }

        /// <summary>Let it register its callback, and wait (bounded, in real time) until it is parked.</summary>
        public async Task ParkAsync()
        {
            _mayRegister.TrySetResult();
            await _parked.Task.WaitAsync(PickupTest.Timeout);
        }

        /// <summary>End it with <paramref name="failure"/>, on the calling thread.</summary>
        public bool EndWith(Exception failure) => _ends.TrySetException(failure);
    }

    // A channel whose sends and endpoint lookups are the given host code, one per call in the order
    // the calls are made.
    private (MediatorClientChannel Channel, FakeSend Send) ChannelOver(ManualTimeProvider time, params HeldHostCode[] hosts)
    {
        var taken = -1;
        HeldHostCode Next() => hosts[Interlocked.Increment(ref taken)];
        var send = new FakeSend
        {
            Respond = async (_, options, ct) =>
            {
                await Next().RunAsync(ct).ConfigureAwait(false);
                return FakeSend.Accepted(options.ServiceEndpointOverride!);
            },
        };
        var channel = Create(_w, send, time: time, resolveEndpoint: async (_, ct) =>
        {
            await Next().RunAsync(ct).ConfigureAwait(false);
            return Https;
        });
        return (channel, send);
    }

    // Start the call of a step. It returns once it waits on its host code: a callback the host code
    // registers afterwards is registered after the channel's own wait.
    private (Task Call, Message? Request) Start(MediatorClientChannel channel, Step step, CancellationToken ct)
    {
        Message? request = null;
        Task call;
        switch (step)
        {
            case Step.EndpointLookup:
                call = channel.ResolveRouteAsync(_w.Mediator.Did, null, requireWebSocket: false, ct);
                break;
            case Step.Send:
                request = _w.Request();
                call = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, ct);
                break;
            case Step.SendAllowingNoAnswer:
                request = _w.Request(MessagePickup.MessagesReceivedType);
                call = channel.ExchangeAllowingNoAnswerAsync(WebSocketRoute, request, DeliveryReader.MessagesReceivedSpec, _w.Mediator.Did, _w.Local.Did, channel.Options.RequestTimeout / 2, ct);
                break;
            default:
                request = _w.Request(MessagePickup.MessagesReceivedType);
                call = channel.SendOneWayAsync(WebSocketRoute, request, "messages-received", _w.Mediator.Did, _w.Local.Did, ct);
                break;
        }
        call.IsCompleted.Should().BeFalse("the call waits on its host code");
        // Observed here: a test that fails part-way must not leave an unobserved fault for the
        // parallel unobserved-exception tests to count.
        call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (call, request);
    }

    private static void End(Ending ending, CancellationTokenSource caller, ManualTimeProvider time, MediatorClientChannel channel)
    {
        switch (ending)
        {
            case Ending.CallerCancels:
                caller.Cancel();
                break;
            case Ending.RequestTimesOut:
                time.Advance(channel.Options.RequestTimeout);
                break;
            default:
                channel.Dispose();
                break;
        }
    }

    // What the call ended with (null: it returned). The call must have ended: a hang fails here, and
    // the hang guard's TimeoutException is never taken for the call's own.
    private static async Task<Exception?> EndOf(Task call)
    {
        var ended = await Record.ExceptionAsync(() => call.WaitAsync(PickupTest.Timeout));
        call.IsCompleted.Should().BeTrue("the call ended");
        return ended;
    }

    private static void ShouldHaveEndedAs(Exception? ended, Ending ending, Step step, CancellationToken caller, Message? request)
    {
        switch (ending)
        {
            case Ending.CallerCancels:
                ended.Should().BeAssignableTo<OperationCanceledException>("the caller cancelled the call")
                    .Which.CancellationToken.Should().Be(caller, "the cancellation is the caller's");
                break;
            case Ending.RequestTimesOut:
                var text = ended.Should().BeOfType<TimeoutException>("the call's RequestTimeout passed").Which.Message;
                if (step == Step.EndpointLookup)
                    text.Should().Contain("Finding the mediator's endpoint");
                else
                    text.Should().Contain(request!.Id, "the timeout names the request");
                break;
            default:
                ended.Should().BeOfType<ObjectDisposedException>("the container was disposed");
                break;
        }
    }

    // What the host code of a step reports: a lookup a resolution failure, a send a transport failure.
    private static Exception FailureOf(Step step)
        => step == Step.EndpointLookup
            ? new DidResolutionException("did:example:mediator", "notFound")
            : new TransportException("The connection was closed.");
}
