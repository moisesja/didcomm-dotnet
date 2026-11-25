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
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The request path of the real channel at the moment it decides how a call ends (#111): the
/// request is decided there, through its terminal compare-and-swap, so an answer that had already
/// won is the call's outcome, and an answer that arrives afterwards is not taken.
/// <see cref="MediatorClientChannelAnswerAndSendRt4Tests"/> delivers the answer as the call's end
/// is reported; here it is delivered a step earlier (as the channel begins to end the call), and
/// for a send that fails on its own as the disposal begins.
/// </summary>
/// <remarks>
/// The sends never end on their own token (the test ends them). An answer is delivered from inside
/// the logger, as a chosen line is written: that is a fixed point of the channel's code, on the
/// thread that runs it. Whatever ends a call is done from a thread-pool thread. The deadline is on
/// the manual clock; real time is only a hang guard.
/// </remarks>
public sealed class MediatorClientChannelEndDecidedTests : IAsyncLifetime
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(60);
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    /// <summary>The kind of request.</summary>
    public enum Kind
    {
        /// <summary><c>ExchangeAsync</c> on a WebSocket route.</summary>
        Request,

        /// <summary><c>ExchangeAsync</c> on an HTTP route.</summary>
        RequestOverHttp,

        /// <summary><c>ExchangeAllowingNoAnswerAsync</c> with an answer wait, on a WebSocket route.</summary>
        RequestAllowingNoAnswer,
    }

    /// <summary>
    /// A call is ended by its caller's cancellation or by its deadline while its send still runs,
    /// and the answer naming it arrives as the channel begins to end the call: after the channel
    /// has observed that the call is being ended, before it has decided the request (the test
    /// delivers the answer as the line that names what the send step ended with is written, which
    /// is the first thing the channel does there). The request was still waiting, so the answer
    /// wins: the channel takes it and the call returns it.
    /// </summary>
    [Theory]
    [InlineData(Kind.Request, false)]
    [InlineData(Kind.Request, true)]
    [InlineData(Kind.RequestOverHttp, false)]
    [InlineData(Kind.RequestOverHttp, true)]
    [InlineData(Kind.RequestAllowingNoAnswer, false)]
    [InlineData(Kind.RequestAllowingNoAnswer, true)]
    public async Task An_answer_that_arrives_as_the_channel_begins_to_end_a_call_wins_and_is_returned(Kind kind, bool theDeadlinePasses)
    {
        var time = new ManualTimeProvider();
        var sends = new HeldSends();
        using var caller = new CancellationTokenSource();
        MediatorClientChannel channel = null!;
        InboundMessageSnapshot answer = null!;
        bool? taken = null;
        var logs = new OnLine(line => line.Contains("while the call was being ended", StringComparison.Ordinal), () => taken = channel.AsCorrelator().OnInbound(answer));
        channel = Create(_w, new FakeSend { Respond = sends.RunAsync }, time: time, logs: logs);
        var (call, request) = Start(channel, kind, caller.Token);
        await sends.EnteredAsync(request.Id);
        answer = await _w.SnapshotAsync(_w.Status(request.Id));

        await Task.Run(() =>
        {
            if (theDeadlinePasses)
                time.Advance(channel.Options.RequestTimeout);
            else
                caller.Cancel();
        }).WaitAsync(Guard);

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(Guard));
        taken.Should().BeTrue("the request was still waiting when the channel began to end the call");
        thrown.Should().BeNull("the answer won before the request was decided, so it is the call's outcome (the call ended with {0})", thrown?.GetType().Name ?? "a result");
        (await (Task<InboundMessageSnapshot?>)call).Should().BeSameAs(answer);
        channel.PendingCount.Should().Be(0, "the request's place was given back, once");
        time.ScheduledTimers.Should().Be(0, "the call's deadline was released");
        await channel.DisposeAsync();
    }

    /// <summary>
    /// Many requests wait. The container is disposed on one thread while, on another, the send of
    /// one of them fails on its own as soon as the disposal has begun; the answer naming that
    /// request arrives while its end is on its way to the caller (from inside the logger, as its
    /// end is reported). Twenty rounds. Whichever of the disposal's pass over the waiting requests
    /// and the failing send reaches the request first, the request is decided when its call's end
    /// is: the call ends as the disposal does and the answer is not taken.
    /// </summary>
    /// <remarks>
    /// Which of the two reaches the request first is not forced (no code of a host runs between the
    /// disposal's first step and its pass over the waiting requests): the many waiting requests
    /// make that pass long, so that the failing send is often first. The assertion is the same in
    /// either order.
    /// </remarks>
    [Theory]
    [InlineData(Kind.Request)]
    [InlineData(Kind.RequestOverHttp)]
    [InlineData(Kind.RequestAllowingNoAnswer)]
    public async Task An_answer_that_arrives_after_a_send_failed_on_its_own_as_the_disposal_began_is_not_taken(Kind kind)
    {
        const int others = 512;
        for (var round = 0; round < 20; round++)
        {
            var time = new ManualTimeProvider();
            var sends = new HeldSends();
            MediatorClientChannel channel = null!;
            InboundMessageSnapshot answer = null!;
            var watched = "(not set yet)";
            bool? taken = null;
            var logs = new OnLine(
                line => line.Contains("ended without a usable answer", StringComparison.Ordinal) && line.Contains(watched, StringComparison.Ordinal),
                () => taken = channel.AsCorrelator().OnInbound(answer));
            channel = Create(_w, new FakeSend { Respond = sends.RunAsync }, o => o.MaxPendingRequests = others + 1, time: time, logs: logs);
            for (var i = 0; i < others / 2; i++)
                Start(channel, kind, CancellationToken.None);
            var (call, request) = Start(channel, kind, CancellationToken.None);
            for (var i = others / 2; i < others; i++)
                Start(channel, kind, CancellationToken.None);
            await sends.EnteredAsync(request.Id);
            answer = await _w.SnapshotAsync(_w.Status(request.Id));
            watched = request.Id;
            channel.PendingCount.Should().Be(others + 1);
            var failure = new TransportException("The connection was closed.");

            using var go = new ManualResetEventSlim();
            var failing = Task.Run(() =>
            {
                go.Wait();
                SpinWait.SpinUntil(() => channel.IsDisposed, Guard).Should().BeTrue("the disposal began");
                sends.Fail(request.Id, failure);
            });
            var disposing = Task.Run(() =>
            {
                go.Wait();
                channel.Dispose();
            });
            go.Set();
            await failing.WaitAsync(Guard);
            await disposing.WaitAsync(Guard);

            var thrown = await Record.ExceptionAsync(() => call.WaitAsync(Guard));
            thrown.Should().BeOfType<ObjectDisposedException>("the send failed once the disposal had begun, so the call ends as the disposal does (round {0})", round);
            taken.Should().BeFalse("the request was decided when its call's end was (round {0})", round);
            SpinWait.SpinUntil(() => channel.PendingCount == 0, Guard).Should().BeTrue("no request is left counted (round {0})", round);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private Message NewRequest(Kind kind)
        => kind == Kind.RequestAllowingNoAnswer ? _w.Request(MessagePickup.MessagesReceivedType) : _w.Request();

    private Task Call(MediatorClientChannel channel, Kind kind, Message request, CancellationToken ct)
    {
        switch (kind)
        {
            case Kind.Request:
                return channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, ct);
            case Kind.RequestOverHttp:
                return channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, _w.Mediator.Did, _w.Local.Did, ct);
            default:
                return channel.ExchangeAllowingNoAnswerAsync(WebSocketRoute, request, DeliveryReader.MessagesReceivedSpec, _w.Mediator.Did, _w.Local.Did, channel.Options.RequestTimeout / 2, ct);
        }
    }

    // Start a call; it returns once it waits on its send.
    private (Task Call, Message Request) Start(MediatorClientChannel channel, Kind kind, CancellationToken ct)
    {
        var request = NewRequest(kind);
        var call = Call(channel, kind, request, ct);
        call.IsCompleted.Should().BeFalse("the call waits on its send");
        // Observed here: a call that fails must not leave an unobserved fault for the parallel
        // unobserved-exception tests to count.
        call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (call, request);
    }

    /// <summary>
    /// A logger factory that runs an action once, inside the logging call, when a line that
    /// matches is written: a way to act at that very point of the channel.
    /// </summary>
    private sealed class OnLine(Func<string, bool> matches, Action then) : ILoggerFactory
    {
        private int _called;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Hook(this);

        public void Dispose()
        {
        }

        private sealed class Hook(OnLine owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (owner.Matches(formatter(state, exception)) && Interlocked.Exchange(ref owner._called, 1) == 0)
                    owner.Then();
            }
        }

        private Func<string, bool> Matches { get; } = matches;

        private Action Then { get; } = then;
    }

    /// <summary>
    /// The send step: each send is held until the test ends it with a failure, on the thread that
    /// ends it. It ignores the token it is handed.
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

        // Its continuations run inline, on the thread that ends the send.
        private TaskCompletionSource<SendResult> EndOf(string id) => _ends.GetOrAdd(id, static _ => new TaskCompletionSource<SendResult>());

        private TaskCompletionSource Entered(string id) => _entered.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
