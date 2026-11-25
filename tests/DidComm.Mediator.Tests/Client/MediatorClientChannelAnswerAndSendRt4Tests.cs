using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using Xunit;
using Xunit.Abstractions;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT4 (#111): the real channel's request path when an answer, a send that fails on its own and
/// whatever ends the call land together, written without the helpers of
/// <see cref="MediatorClientChannelTokenStateRt3Tests"/>. What is checked for every request: it has
/// one outcome; the call returns an answer only when the channel took that answer for that
/// request, and never another request's; otherwise it ends with its send's own failure or with what
/// ended it; no request is left counted and no deadline scheduled; and no log line carries the text
/// of the send's failure, an identifier of a party or a value from the answer. Also checked: an
/// answer the channel took is always the call's outcome, whatever ends the call (the reproduction
/// of the independent pass, fixed in the round that followed). The sends here never end on their
/// own token (the test ends them); the deadline is on the manual clock, and real time is only a
/// hang guard.
/// </summary>
public sealed class MediatorClientChannelAnswerAndSendRt4Tests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(60);
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    /// <summary>The kind of call.</summary>
    public enum Kind
    {
        /// <summary><c>ExchangeAsync</c> on a WebSocket route.</summary>
        Request,

        /// <summary><c>ExchangeAsync</c> on an HTTP route.</summary>
        RequestOverHttp,

        /// <summary><c>ExchangeAllowingNoAnswerAsync</c> with an answer wait, on a WebSocket route.</summary>
        RequestAllowingNoAnswer,

        /// <summary><c>SendOneWayAsync</c>.</summary>
        OneWay,
    }

    /// <summary>What ends the call from outside, if anything.</summary>
    public enum Ending
    {
        /// <summary>Nothing.</summary>
        Nothing,

        /// <summary>The caller cancels.</summary>
        CallerCancels,

        /// <summary>The call's deadline passes.</summary>
        RequestTimesOut,

        /// <summary>The container is disposed.</summary>
        ContainerDisposed,
    }

    /// <summary>
    /// Three things land together on three threads, forty times per row, in an order that varies
    /// from round to round: an answer naming the request arrives, the send fails on its own, and the
    /// row's ending ends the call. Every call ends. It returns the answer only when the channel took
    /// it (never for a one-way message, which nothing can answer); otherwise it ends with the send's
    /// own failure or as its ending, and never as "no answer". A call whose answer the channel took
    /// always returns it, whatever ends the call. Afterwards no request is counted and no deadline
    /// is scheduled, and no log line carries the failure's text, a party's identifier, or the
    /// answer's id.
    /// </summary>
    /// <remarks>
    /// Before the channel decided a request where it decides how its call ends, a round could end as
    /// its ending although the channel had taken the answer, and such rounds were counted and
    /// printed here, not asserted. They are asserted now: none may occur.
    /// <see cref="An_answer_that_arrives_while_the_end_of_a_call_is_on_its_way_to_its_caller_is_not_taken"/>
    /// is that case on its own for the caller's cancellation and the deadline, and
    /// <see cref="MediatorClientChannelEndDecidedTests"/> has it for a send that fails on its own as
    /// the disposal begins.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryKindAndEnding))]
    public async Task An_answer_a_send_failing_on_its_own_and_an_ending_landing_together_leave_one_outcome_and_nothing_counted(Kind kind, Ending ending)
    {
        const int rounds = 40;
        int returned = 0, failedOnItsOwn = 0, endedFromOutside = 0;
        for (var round = 0; round < rounds; round++)
        {
            var time = new ManualTimeProvider();
            var logs = new ClientLogCapture();
            var sends = new HeldSends();
            using var caller = new CancellationTokenSource();
            var channel = Create(_w, new FakeSend { Respond = sends.RunAsync }, time: time, logs: logs);
            var (call, request) = Start(channel, kind, caller.Token);
            await sends.EnteredAsync(request.Id);
            var status = _w.Status(request.Id);
            var answer = await _w.SnapshotAsync(status);
            var failure = new TransportException("rt4-text-" + Guid.NewGuid().ToString("N"));

            using var go = new ManualResetEventSlim();
            var spins = new[] { 0, round / 6 * 150, round / 6 * 300 };
            var order = Orders[round % Orders.Length];
            var taking = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(spins[order[0]]);
                return channel.AsCorrelator().OnInbound(answer);
            });
            var failing = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(spins[order[1]]);
                sends.Fail(request.Id, failure);
            });
            var ended = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(spins[order[2]]);
                switch (ending)
                {
                    case Ending.CallerCancels:
                        caller.Cancel();
                        break;
                    case Ending.RequestTimesOut:
                        time.Advance(channel.Options.RequestTimeout);
                        break;
                    case Ending.ContainerDisposed:
                        channel.Dispose();
                        break;
                }
            });
            go.Set();
            var taken = await taking.WaitAsync(Guard);
            await failing.WaitAsync(Guard);
            await ended.WaitAsync(Guard);

            var thrown = await Record.ExceptionAsync(() => call.WaitAsync(Guard));
            call.IsCompleted.Should().BeTrue("the call ended (round {0})", round);
            if (kind == Kind.OneWay)
                taken.Should().BeFalse("nothing is registered for a one-way message (round {0})", round);
            if (thrown is not null)
                taken.Should().BeFalse("a call whose answer the channel took returns it, whatever ends the call (round {0}; it ended with {1})", round, thrown.GetType().Name);
            if (thrown is null)
            {
                returned++;
                taken.Should().BeTrue("a call returns an answer only when the channel took it (round {0})", round);
                (await (Task<InboundMessageSnapshot?>)call).Should().BeSameAs(answer, "round {0}", round);
            }
            else if (ReferenceEquals(thrown, failure))
            {
                failedOnItsOwn++;
            }
            else
            {
                endedFromOutside++;
                thrown.Should().NotBeNull("no answer was taken, so the call does not return one, nor \"no answer\": its send failed (round {0})", round);
                switch (ending)
                {
                    case Ending.CallerCancels:
                        thrown.Should().BeAssignableTo<OperationCanceledException>("round {0}", round);
                        break;
                    case Ending.RequestTimesOut:
                        thrown.Should().BeOfType<TimeoutException>("round {0}", round);
                        break;
                    case Ending.ContainerDisposed:
                        thrown.Should().BeOfType<ObjectDisposedException>("round {0}", round);
                        break;
                    default:
                        thrown.Should().BeSameAs(failure, "nothing ended the call, so its send's failure is its outcome (round {0})", round);
                        break;
                }
            }
            channel.PendingCount.Should().Be(0, "no request is left counted (round {0})", round);
            await channel.DisposeAsync();
            SpinWait.SpinUntil(() => time.ScheduledTimers == 0, Guard).Should().BeTrue("no deadline is left scheduled (round {0})", round);
            LogsShouldCarryNoTextAndNoWireValue(logs, failure, status, round);
        }
        output.WriteLine($"{kind}, {ending}: {rounds} rounds; returned the answer: {returned}; ended with the send's own failure: {failedOnItsOwn}; ended as {ending}: {endedFromOutside}");
        (returned + failedOnItsOwn + endedFromOutside).Should().Be(rounds);
    }

    /// <summary>Every kind of call with every ending.</summary>
    public static TheoryData<Kind, Ending> EveryKindAndEnding()
    {
        var rows = new TheoryData<Kind, Ending>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (var ending in Enum.GetValues<Ending>())
                rows.Add(kind, ending);
        }
        return rows;
    }

    /// <summary>
    /// Two requests are being sent at once, on either kind of route. The answer to the second
    /// arrives; then the send of the first fails on its own. The first ends with its own failure,
    /// not with the answer that had arrived for the other; the second returns its answer when its
    /// own send then fails; an answer naming the first, arriving afterwards, is not taken; and
    /// nothing is left counted.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Of_two_requests_being_sent_at_once_each_ends_with_its_own_outcome_when_one_is_answered_and_the_others_send_fails(bool overHttp)
    {
        var time = new ManualTimeProvider();
        var logs = new ClientLogCapture();
        var sends = new HeldSends();
        var channel = Create(_w, new FakeSend { Respond = sends.RunAsync }, time: time, logs: logs);
        var kind = overHttp ? Kind.RequestOverHttp : Kind.Request;
        var (first, firstRequest) = Start(channel, kind, CancellationToken.None);
        var (second, secondRequest) = Start(channel, kind, CancellationToken.None);
        await sends.EnteredAsync(firstRequest.Id);
        await sends.EnteredAsync(secondRequest.Id);
        channel.PendingCount.Should().Be(2);
        var statusForSecond = _w.Status(secondRequest.Id);
        var answerToSecond = await _w.SnapshotAsync(statusForSecond);
        var failureOfFirst = new TransportException("rt4-text-" + Guid.NewGuid().ToString("N"));
        var failureOfSecond = new TransportException("rt4-text-" + Guid.NewGuid().ToString("N"));

        channel.AsCorrelator().OnInbound(answerToSecond).Should().BeTrue("it names a waiting request");
        await Task.Run(() => sends.Fail(firstRequest.Id, failureOfFirst)).WaitAsync(Guard);

        (await Record.ExceptionAsync(() => first.WaitAsync(Guard))).Should().BeSameAs(failureOfFirst, "no answer had arrived for the first request");
        second.IsCompleted.Should().BeFalse("the second request's send still runs");
        channel.PendingCount.Should().Be(1);
        var statusForFirst = _w.Status(firstRequest.Id);
        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(statusForFirst)).Should().BeFalse("the first request had ended with its send's failure");

        await Task.Run(() => sends.Fail(secondRequest.Id, failureOfSecond)).WaitAsync(Guard);

        (await ((Task<InboundMessageSnapshot?>)second).WaitAsync(Guard)).Should().BeSameAs(answerToSecond, "its answer had won before its send failed");
        channel.PendingCount.Should().Be(0);
        time.ScheduledTimers.Should().Be(0, "both deadlines were released");
        LogsShouldCarryNoTextAndNoWireValue(logs, failureOfFirst, statusForSecond, round: 0);
        LogsShouldCarryNoTextAndNoWireValue(logs, failureOfSecond, statusForFirst, round: 0);
        await channel.DisposeAsync();
    }

    /// <summary>
    /// A problem report naming the request arrives while its send runs, and wins; the send then
    /// fails on its own. The request's outcome is the answer that won: the call ends with the
    /// problem report (a <see cref="MediationProtocolException"/> with its code), not with the
    /// send's failure, whose type alone is named in one Debug line.
    /// </summary>
    [Theory]
    [InlineData(Kind.Request)]
    [InlineData(Kind.RequestOverHttp)]
    [InlineData(Kind.RequestAllowingNoAnswer)]
    public async Task A_problem_report_that_won_while_the_send_ran_is_the_requests_outcome_when_the_send_then_fails_on_its_own(Kind kind)
    {
        var time = new ManualTimeProvider();
        var logs = new ClientLogCapture();
        var sends = new HeldSends();
        var channel = Create(_w, new FakeSend { Respond = sends.RunAsync }, time: time, logs: logs);
        var (call, request) = Start(channel, kind, CancellationToken.None);
        await sends.EnteredAsync(request.Id);
        var report = _w.Problem(request.Id);
        var failure = new TransportException("rt4-text-" + Guid.NewGuid().ToString("N"));

        channel.AsCorrelator().OnInbound(await _w.SnapshotAsync(report)).Should().BeTrue();
        await Task.Run(() => sends.Fail(request.Id, failure)).WaitAsync(Guard);

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(Guard));
        thrown.Should().BeOfType<MediationProtocolException>().Which.ProblemCode.Should().Be("e.p.req.not-enrolled");
        channel.PendingCount.Should().Be(0);
        time.ScheduledTimers.Should().Be(0);
        logs.Of<MediatorClientChannel>().Should().ContainSingle(l => Equals(l.State.GetValueOrDefault("ExceptionType"), nameof(TransportException)) && l.Formatted.Contains("after the answer had arrived", StringComparison.Ordinal))
            .Which.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Debug);
        LogsShouldCarryNoTextAndNoWireValue(logs, failure, report, round: 0);
        await channel.DisposeAsync();
    }

    /// <summary>
    /// Over HTTP two answers name one request: one arrives through the dispatcher on one thread
    /// while, on another, the send completes with the other in its response; forty rounds, in an
    /// order that varies. The request is completed once: the call returns the dispatcher's answer
    /// exactly when the channel took it, and the response's otherwise.
    /// </summary>
    [Fact]
    public async Task Over_HTTP_of_an_answer_from_the_dispatcher_and_one_in_the_response_the_call_returns_the_one_that_was_taken()
    {
        int fromTheDispatcher = 0, fromTheResponse = 0;
        for (var round = 0; round < 40; round++)
        {
            var time = new ManualTimeProvider();
            var sends = new HeldSends();
            var channel = Create(_w, new FakeSend { Respond = sends.RunAsync }, time: time);
            var (call, request) = Start(channel, Kind.RequestOverHttp, CancellationToken.None);
            await sends.EnteredAsync(request.Id);
            var pushed = await _w.SnapshotAsync(_w.Status(request.Id, count: 1));
            var inResponse = await _w.ReceiveAsync(_w.Status(request.Id, count: 2));

            using var go = new ManualResetEventSlim();
            var spins = round / 2 * 200;
            var taking = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(round % 2 == 0 ? spins : 0);
                return channel.AsCorrelator().OnInbound(pushed);
            });
            var responding = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(round % 2 == 0 ? 0 : spins);
                sends.Complete(request.Id, FakeSend.Accepted(Https, inResponse, status: 200));
            });
            go.Set();
            var taken = await taking.WaitAsync(Guard);
            await responding.WaitAsync(Guard);

            var result = await ((Task<InboundMessageSnapshot?>)call).WaitAsync(Guard);
            if (taken)
            {
                fromTheDispatcher++;
                result.Should().BeSameAs(pushed, "the channel took the dispatcher's answer (round {0})", round);
            }
            else
            {
                fromTheResponse++;
                result.Should().BeSameAs(ClientWorld.SnapshotOf(inResponse), "the dispatcher's answer was not taken, so the response's completed the request (round {0})", round);
            }
            channel.PendingCount.Should().Be(0, "round {0}", round);
            await channel.DisposeAsync();
            SpinWait.SpinUntil(() => time.ScheduledTimers == 0, Guard).Should().BeTrue("round {0}", round);
        }
        output.WriteLine($"returned the dispatcher's answer: {fromTheDispatcher}; the response's: {fromTheResponse}");
        (fromTheDispatcher + fromTheResponse).Should().Be(40);
    }

    /// <summary>
    /// A call is ended by its caller's cancellation or by its deadline while its send still runs,
    /// and the answer naming it arrives while that ending is on its way to the caller: after the
    /// channel has decided how the call ends, before it has taken the request out of its table (the
    /// test delivers the answer from inside the logger, as the line that reports the request's end
    /// is written). As for a send that failed on its own
    /// (<see cref="MediatorClientChannelTokenStateRt3Tests"/>), the answer is not taken: the request
    /// was decided where the call's end was. (Written as a reproduction by the independent pass: on
    /// the code before the fix the channel took the answer and the call still ended as cancelled or
    /// timed out.)
    /// </summary>
    [Theory]
    [InlineData(Kind.Request, Ending.CallerCancels)]
    [InlineData(Kind.Request, Ending.RequestTimesOut)]
    [InlineData(Kind.RequestOverHttp, Ending.CallerCancels)]
    [InlineData(Kind.RequestOverHttp, Ending.RequestTimesOut)]
    [InlineData(Kind.RequestAllowingNoAnswer, Ending.CallerCancels)]
    [InlineData(Kind.RequestAllowingNoAnswer, Ending.RequestTimesOut)]
    public Task An_answer_that_arrives_while_the_end_of_a_call_is_on_its_way_to_its_caller_is_not_taken(Kind kind, Ending ending)
        => AnswerWhileTheEndIsOnItsWayAsync(kind, ending);

    /// <summary>
    /// Held: the same when the container's disposal ends the call while the send still runs. The
    /// disposal decides every waiting request before it ends their sends, so the answer that arrives
    /// while the disposal is on its way to the caller is not taken.
    /// </summary>
    [Theory]
    [InlineData(Kind.Request)]
    [InlineData(Kind.RequestOverHttp)]
    [InlineData(Kind.RequestAllowingNoAnswer)]
    public Task An_answer_that_arrives_while_the_disposal_is_on_its_way_to_the_caller_is_not_taken(Kind kind)
        => AnswerWhileTheEndIsOnItsWayAsync(kind, Ending.ContainerDisposed);

    private async Task AnswerWhileTheEndIsOnItsWayAsync(Kind kind, Ending ending)
    {
        var time = new ManualTimeProvider();
        var sends = new HeldSends();
        using var caller = new CancellationTokenSource();
        MediatorClientChannel channel = null!;
        InboundMessageSnapshot answer = null!;
        var countedThen = -1;
        bool? taken = null;
        var logs = new OnLine("ended without a usable answer", () =>
        {
            countedThen = channel.PendingCount;
            taken = channel.AsCorrelator().OnInbound(answer);
        });
        channel = Create(_w, new FakeSend { Respond = sends.RunAsync }, time: time, logs: logs);
        var (call, request) = Start(channel, kind, caller.Token);
        await sends.EnteredAsync(request.Id);
        answer = await _w.SnapshotAsync(_w.Status(request.Id));

        // From a thread-pool thread, so the call's continuation runs inside the cancellation.
        await Task.Run(() =>
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
        }).WaitAsync(Guard);

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(Guard));
        sends.Fail(request.Id, new TransportException("The connection was closed."));
        output.WriteLine($"{kind}, {ending}: the call ended with {thrown?.GetType().Name ?? "a result"}; requests counted when the answer arrived: {countedThen}; the channel took the answer: {taken}");
        switch (ending)
        {
            case Ending.CallerCancels:
                thrown.Should().BeAssignableTo<OperationCanceledException>();
                break;
            case Ending.RequestTimesOut:
                thrown.Should().BeOfType<TimeoutException>();
                break;
            default:
                thrown.Should().BeOfType<ObjectDisposedException>();
                break;
        }
        countedThen.Should().Be(1, "the set-up: the request was still registered when the answer arrived");
        taken.Should().BeFalse("the call had been ended, so the request was decided: an answer the channel takes is the call's outcome, and this call ended as {0}", ending);
        channel.PendingCount.Should().Be(0);
        await channel.DisposeAsync();
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static readonly int[][] Orders =
    {
        new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
    };

    // Start a call; it returns once it waits on its send.
    private (Task Call, Message Request) Start(MediatorClientChannel channel, Kind kind, CancellationToken ct)
    {
        Message request;
        Task call;
        switch (kind)
        {
            case Kind.Request:
            case Kind.RequestOverHttp:
                request = _w.Request();
                call = channel.ExchangeAsync(kind == Kind.RequestOverHttp ? HttpRoute : WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, ct);
                break;
            case Kind.RequestAllowingNoAnswer:
                request = _w.Request(MessagePickup.MessagesReceivedType);
                call = channel.ExchangeAllowingNoAnswerAsync(WebSocketRoute, request, DeliveryReader.MessagesReceivedSpec, _w.Mediator.Did, _w.Local.Did, channel.Options.RequestTimeout / 2, ct);
                break;
            default:
                request = _w.Request(MessagePickup.MessagesReceivedType);
                call = channel.SendOneWayAsync(WebSocketRoute, request, "messages-received", _w.Mediator.Did, _w.Local.Did, ct);
                break;
        }
        call.IsCompleted.Should().BeFalse("the call waits on its send");
        call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (call, request);
    }

    private void LogsShouldCarryNoTextAndNoWireValue(ClientLogCapture logs, Exception failure, Message answer, int round)
    {
        foreach (var entry in logs.Entries)
        {
            entry.Exception.Should().BeNull("no exception is handed to the logger (round {0})", round);
            entry.Everything.Should().NotContain(failure.Message, "the text of the send's failure is not logged (round {0})", round);
            entry.Everything.Should().NotContain(answer.Id, "the answer's own id is a value from the wire (round {0})", round);
            foreach (var identifier in _w.Identifiers)
                entry.Everything.Should().NotContain(identifier, "no party's DID or key id is logged (round {0})", round);
        }
    }

    /// <summary>
    /// A logger factory that runs an action once, inside the logging call, when a line containing
    /// the given text is written: a way to act at that very point of the channel.
    /// </summary>
    private sealed class OnLine(string text, Action then) : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private int _called;

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
        {
        }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Hook(this);

        public void Dispose()
        {
        }

        private sealed class Hook(OnLine owner) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (formatter(state, exception).Contains(owner.Text, StringComparison.Ordinal) && Interlocked.Exchange(ref owner._called, 1) == 0)
                    owner.Then();
            }
        }

        private string Text { get; } = text;

        private Action Then { get; } = then;
    }

    /// <summary>
    /// The send step: each send is held until the test ends it, with a result or with a failure, on
    /// the thread that ends it. It ignores the token it is handed.
    /// </summary>
    private sealed class HeldSends
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SendResult>> _ends = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new(StringComparer.Ordinal);

        public Task<SendResult> RunAsync(Message message, SendOptions options, CancellationToken ct)
        {
            var end = EndOf(message.Id);
            Entered(message.Id).TrySetResult();
            return end.Task;
        }

        public Task EnteredAsync(string id) => Entered(id).Task.WaitAsync(Guard);

        public bool Fail(string id, Exception failure) => EndOf(id).TrySetException(failure);

        public bool Complete(string id, SendResult result) => EndOf(id).TrySetResult(result);

        // Its continuations run inline, on the thread that ends the send.
        private TaskCompletionSource<SendResult> EndOf(string id) => _ends.GetOrAdd(id, static _ => new TaskCompletionSource<SendResult>());

        private TaskCompletionSource Entered(string id) => _entered.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
