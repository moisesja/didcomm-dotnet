using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2): the request path of the real channel (an endpoint lookup, a send, a
/// send that allows no answer, a one-way send) in the orderings
/// <see cref="MediatorClientChannelTokenStateTests"/> does not set up: a failure of the host code's
/// own that races with what ends the call; a call whose token is combined from two caller tokens;
/// what the log keeps of what the host code reported while its call was being ended; and an answer
/// that won before the send failed on its own.
/// </summary>
/// <remarks>
/// As there: a call is started and returns once it waits on its host code, and only then does the
/// host code register its callback, so that callback runs before the channel's own. Whatever ends a
/// call is done from a thread-pool thread. Real time is only a hang guard.
/// </remarks>
public sealed class MediatorClientChannelTokenStateRt3Tests : IAsyncLifetime
{
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    /// <summary>The call under test, named by the host code it waits on.</summary>
    public enum Step
    {
        /// <summary><c>ResolveRouteAsync</c>.</summary>
        EndpointLookup,

        /// <summary><c>ExchangeAsync</c>.</summary>
        Send,

        /// <summary><c>ExchangeAllowingNoAnswerAsync</c> with an answer wait.</summary>
        SendAllowingNoAnswer,

        /// <summary><c>SendOneWayAsync</c>.</summary>
        OneWaySend,
    }

    /// <summary>What ends a call whose host code is still running.</summary>
    public enum Ending
    {
        /// <summary>The caller cancels.</summary>
        CallerCancels,

        /// <summary>The call's deadline passes.</summary>
        RequestTimesOut,

        /// <summary>The container is disposed.</summary>
        ContainerDisposed,
    }

    // ---- what held ----------------------------------------------------------------------------------

    /// <summary>
    /// The host code fails on its own on one thread while another thread ends the call (the caller
    /// cancels, the deadline passes or the container is disposed), fifty times per case, in whatever
    /// order the two land. Every call ends, as its own failure or as what ended it and as nothing
    /// else; no request is left counted, and no deadline is left scheduled.
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
    public async Task A_failure_of_its_own_that_races_with_what_ends_the_call_ends_it_as_one_or_the_other_and_leaves_no_request_counted(Step step, Ending ending)
    {
        var own = 0;
        var ended = 0;
        for (var round = 0; round < 50; round++)
        {
            var time = new ManualTimeProvider();
            var host = new HeldHostCode();
            using var caller = new CancellationTokenSource();
            var (channel, _) = ChannelOver(time, logs: null, host);
            var failure = FailureOf(step);

            var (call, _) = Start(channel, step, caller.Token);
            await host.ParkAsync();
            using var go = new ManualResetEventSlim();
            var failing = Task.Run(() =>
            {
                go.Wait();
                host.EndWith(failure);
            });
            var ending1 = Task.Run(() =>
            {
                go.Wait();
                End(ending, caller, time, channel);
            });
            go.Set();
            await Task.WhenAll(failing, ending1).WaitAsync(PickupTest.Timeout);

            var result = await EndOf(call);
            if (ReferenceEquals(result, failure))
            {
                own++;
            }
            else
            {
                ended++;
                ShouldHaveEndedAs(result, ending, caller.Token);
            }
            channel.PendingCount.Should().Be(0, "no request is left counted");
            await channel.DisposeAsync();
            SpinWait.SpinUntil(() => time.ScheduledTimers == 0, PickupTest.Timeout).Should().BeTrue("the call's deadline was released");
        }
        (own + ended).Should().Be(50);
    }

    // ---- a call whose token is combined from two caller tokens -----------------------------------------

    /// <summary>
    /// The call's token is cancelled by either of two caller tokens (the combination a compiler
    /// makes for an enumeration given two tokens, which a live stream hands to its requests). The
    /// host code watches one of the two and reports its cancellation as a failure of its own from
    /// its callback there. The call ends cancelled, as when the host code watches the call's token
    /// itself.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: the call ends with what the host code reported.
    /// </remarks>
    [Theory(Skip = "RT3: whether a call whose token is combined from two caller tokens ends cancelled when its host code reports the cancellation of one of them as a failure")]
    [InlineData(Step.EndpointLookup)]
    [InlineData(Step.Send)]
    [InlineData(Step.SendAllowingNoAnswer)]
    [InlineData(Step.OneWaySend)]
    public async Task A_call_whose_token_is_combined_from_two_ends_cancelled_when_its_host_code_reports_the_cancellation_of_one_of_them(Step step)
    {
        var time = new ManualTimeProvider();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(first.Token, second.Token);
        var host = new HeldHostCode { ReportsWhenCancelled = () => FailureOf(step), Watches = _ => first.Token };
        var (channel, _) = ChannelOver(time, logs: null, host);

        var (call, _) = Start(channel, step, combined.Token);
        await host.ParkAsync();
        await Task.Run(first.Cancel).WaitAsync(PickupTest.Timeout);

        var result = await EndOf(call);
        host.Reported.Should().Be(1, "the host code reported the caller's cancellation as a failure of its own");
        result.Should().BeAssignableTo<OperationCanceledException>("the caller cancelled the call (it ended with {0})", result?.GetType().Name ?? "a result");
        channel.PendingCount.Should().Be(0);
        await channel.DisposeAsync();
    }

    // ---- what the log keeps -----------------------------------------------------------------------------

    /// <summary>
    /// The call is ended (the caller cancels, its deadline passes or the container is disposed) and
    /// its host code reports the cancellation of its token as a failure of its own; the call ends
    /// as what ended it. One Debug line of the channel names the type of what the host code
    /// reported, the step and, for a send, the request's id.
    /// </summary>
    [Theory]
    [MemberData(nameof(EachStepEndedEachWay))]
    public async Task What_the_host_code_reported_while_its_call_was_being_ended_is_named_in_the_log(Step step, Ending ending)
    {
        var (logs, request, reported) = await EndWhileTheHostCodeReportsAsync(step, ending);

        var named = reported.GetType().Name;
        var line = logs.Of<MediatorClientChannel>()
            .Should().ContainSingle(l => Equals(l.State.GetValueOrDefault("ExceptionType"), named),
                "one line names what the host code reported ({0}); the lines written: {1}", named, string.Join(" | ", logs.Entries.Select(e => e.Formatted)))
            .Subject;
        line.Level.Should().Be(LogLevel.Debug);
        line.Formatted.Should().Contain(named).And.Contain("while the call was being ended");
        if (step == Step.EndpointLookup)
        {
            line.Formatted.Should().Contain("the lookup step");
            line.State.Should().NotContainKey("RequestId", "an endpoint lookup has no request");
        }
        else
        {
            line.Formatted.Should().Contain("the send step").And.Contain(request!.Id);
            line.State.GetValueOrDefault("RequestId").Should().Be(request.Id, "the line names the request");
        }
    }

    /// <summary>
    /// The same calls. No log line, in its text, in any of its values or in an exception handed to
    /// the logger, carries the text of what the host code reported: only its type is kept.
    /// </summary>
    [Theory]
    [MemberData(nameof(EachStepEndedEachWay))]
    public async Task No_log_line_carries_the_text_of_what_the_host_code_reported_while_its_call_was_being_ended(Step step, Ending ending)
    {
        var (logs, _, reported) = await EndWhileTheHostCodeReportsAsync(step, ending);

        logs.Entries.Should().NotBeEmpty("the call wrote its lines");
        foreach (var entry in logs.Entries)
        {
            entry.Exception.Should().BeNull("no exception is handed to the logger");
            entry.Everything.Should().NotContain(reported.Message, "the text of what the host code reported is not logged");
            if (reported is DidResolutionException resolution)
                entry.Everything.Should().NotContain(resolution.Did).And.NotContain(resolution.Reason);
        }
    }

    /// <summary>
    /// The call's deadline passes while its host code runs on and reports nothing. The line is
    /// written all the same: what the channel observed then is the cancellation of its own wait on
    /// the host code, and the line names that.
    /// </summary>
    [Theory]
    [InlineData(Step.EndpointLookup)]
    [InlineData(Step.Send)]
    [InlineData(Step.SendAllowingNoAnswer)]
    [InlineData(Step.OneWaySend)]
    public async Task For_host_code_that_reports_nothing_the_line_names_the_cancellation_of_the_channels_own_wait(Step step)
    {
        var time = new ManualTimeProvider();
        var logs = new ClientLogCapture();
        var host = new HeldHostCode();
        using var caller = new CancellationTokenSource();
        var (channel, _) = ChannelOver(time, logs, host);

        var (call, _) = Start(channel, step, caller.Token);
        await host.ParkAsync();
        await Task.Run(() => End(Ending.RequestTimesOut, caller, time, channel)).WaitAsync(PickupTest.Timeout);

        (await EndOf(call)).Should().BeOfType<TimeoutException>("the call's deadline passed");
        var line = logs.Of<MediatorClientChannel>()
            .Should().ContainSingle(l => l.Formatted.Contains("while the call was being ended", StringComparison.Ordinal)).Subject;
        line.State.GetValueOrDefault("ExceptionType").Should().BeOneOf(
            new object[] { nameof(OperationCanceledException), nameof(TaskCanceledException) }, "the channel's own wait was cancelled");
        await channel.DisposeAsync();
    }

    /// <summary>Each step ended each of the three ways.</summary>
    public static TheoryData<Step, Ending> EachStepEndedEachWay()
    {
        var rows = new TheoryData<Step, Ending>();
        foreach (var step in Enum.GetValues<Step>())
        {
            foreach (var ending in Enum.GetValues<Ending>())
                rows.Add(step, ending);
        }
        return rows;
    }

    // A call of a step, ended while its host code runs; the host code reports the cancellation of
    // the token it was handed as a failure of its own. Returns once the call has ended as what
    // ended it: the lines written, the request (none for an endpoint lookup) and what was reported.
    private async Task<(ClientLogCapture Logs, Message? Request, Exception Reported)> EndWhileTheHostCodeReportsAsync(Step step, Ending ending)
    {
        var time = new ManualTimeProvider();
        var logs = new ClientLogCapture();
        var reported = FailureOf(step);
        var host = new HeldHostCode { ReportsWhenCancelled = () => reported };
        using var caller = new CancellationTokenSource();
        var (channel, _) = ChannelOver(time, logs, host);

        var (call, request) = Start(channel, step, caller.Token);
        await host.ParkAsync();
        await Task.Run(() => End(ending, caller, time, channel)).WaitAsync(PickupTest.Timeout);

        ShouldHaveEndedAs(await EndOf(call), ending, caller.Token);
        host.Reported.Should().Be(1, "the host code reported the cancellation of its token as a failure of its own");
        await channel.DisposeAsync();
        return (logs, request, reported);
    }

    // ---- an answer that won, and a send that then fails on its own -----------------------------------

    /// <summary>
    /// The answer to a request arrives while its send still runs, and wins; the send then fails on
    /// its own, while nothing has ended the call. The call returns the answer, as it does when the
    /// transport reports a failure while the call is being ended.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2, and by the code the same before the round (the send's
    /// failure was passed on then too): the call ends with the send's failure and the answer is not
    /// returned.
    /// </remarks>
    [Theory(Skip = "RT3: whether an answer that won is returned when the send then fails on its own while nothing has ended the call")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_answer_that_won_while_the_send_ran_is_returned_when_the_send_then_fails_on_its_own(bool allowingNoAnswer)
    {
        var time = new ManualTimeProvider();
        var host = new HeldHostCode();
        using var caller = new CancellationTokenSource();
        var (channel, _) = ChannelOver(time, logs: null, host);

        var (call, request) = Start(channel, allowingNoAnswer ? Step.SendAllowingNoAnswer : Step.Send, caller.Token);
        var answer = await _w.SnapshotAsync(_w.Status(request!.Id));
        await host.ParkAsync();
        channel.AsCorrelator().OnInbound(answer).Should().BeTrue("a request is registered before it is sent, so its answer can arrive while the send runs");
        await Task.Run(() => host.EndWith(FailureOf(Step.Send))).WaitAsync(PickupTest.Timeout);

        var result = await EndOf(call);
        result.Should().BeNull("the answer had won before the send failed (the call ended with {0})", result?.GetType().Name ?? "a result");
        (await (Task<InboundMessageSnapshot?>)call).Should().BeSameAs(answer);
        channel.PendingCount.Should().Be(0);
        await channel.DisposeAsync();
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// Host code the channel waits on (a send, an endpoint lookup), held until something ends it.
    /// Once the test lets it (<see cref="ParkAsync"/>), it registers a callback on the token it
    /// watches, which reports <see cref="ReportsWhenCancelled"/>, and parks.
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

        /// <summary>How many times its callback reported its failure.</summary>
        public int Reported => Volatile.Read(ref _reported);

        public async Task RunAsync(CancellationToken handed)
        {
            await _mayRegister.Task.ConfigureAwait(false);
            if (ReportsWhenCancelled is { } failure)
            {
                Watches(handed).UnsafeRegister(_ =>
                {
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
    private (MediatorClientChannel Channel, FakeSend Send) ChannelOver(ManualTimeProvider time, ILoggerFactory? logs, params HeldHostCode[] hosts)
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
        var channel = Create(_w, send, time: time, logs: logs, resolveEndpoint: async (_, ct) =>
        {
            await Next().RunAsync(ct).ConfigureAwait(false);
            return Https;
        });
        return (channel, send);
    }

    // Start the call of a step. It returns once it waits on its host code.
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

    // What the call ended with (null: it returned). The call must have ended: a hang fails here.
    private static async Task<Exception?> EndOf(Task call)
    {
        var ended = await Record.ExceptionAsync(() => call.WaitAsync(PickupTest.Timeout));
        call.IsCompleted.Should().BeTrue("the call ended");
        return ended;
    }

    private static void ShouldHaveEndedAs(Exception? ended, Ending ending, CancellationToken caller)
    {
        switch (ending)
        {
            case Ending.CallerCancels:
                ended.Should().BeAssignableTo<OperationCanceledException>("the caller cancelled the call")
                    .Which.CancellationToken.Should().Be(caller, "the cancellation is the caller's");
                break;
            case Ending.RequestTimesOut:
                ended.Should().BeOfType<TimeoutException>("the call's RequestTimeout passed");
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
