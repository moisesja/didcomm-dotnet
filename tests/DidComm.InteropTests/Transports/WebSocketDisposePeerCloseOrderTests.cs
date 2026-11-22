using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7: once the peer's close has been read on a return-route session socket, exactly one
/// party answers it (RFC 6455 §5.5.1), and nobody disposes the socket before that answer is written
/// or its budget runs out; <c>Disconnected</c> is raised once. The party is the one that ends the
/// socket: its receive loop before dispose, a send that finds it closed (the eviction path), or
/// dispose (its close phase for a socket its drain took, its sweep for one the loop took). Each test
/// pins one order of the peer's close against dispose with the scripted socket's holds, never with
/// timing, and checks the socket's own record: one close output, written before the socket was
/// disposed whenever the peer's close came first.
/// </summary>
public sealed class WebSocketDisposePeerCloseOrderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(7);
    private static readonly Uri Endpoint = new("ws://agents.r.us/ws");
    private static readonly Uri PoolEndpoint = new("ws://agents.r.us/pool");
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string LoopAnswer = "return-route session closed";
    private const string DisposeAnswer = "transport disposed";

    // ---- before dispose -----------------------------------------------------------------------

    [Fact]
    public async Task Order_a_the_loop_ends_before_dispose_and_answers()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();

        s.PushClose();
        await WaitAsync(() => t.Transport.LiveSessionSocketCount == 0);
        await t.Transport.DisposeAsync().AsTask().WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, LoopAnswer);
    }

    [Fact]
    public async Task Order_a1_the_loops_answer_is_being_written_when_dispose_begins()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldCloseOutput();

        s.PushClose();
        await s.CloseOutputEntered.WaitAsync(Timeout);
        var dispose = t.Transport.DisposeAsync().AsTask();
        s.ReleaseCloseOutput();
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, LoopAnswer);
    }

    [Fact]
    public async Task Order_a2_the_loop_waits_for_the_send_gate_to_answer_when_dispose_begins()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldSends();
        var inFlight = Observed(t.Transport.SendAsync(Request("in flight"), default));
        await s.SendEntered.WaitAsync(Timeout);
        // The loop's next step after it read the peer's close reads the state only when it saw no
        // dispose yet: it is then committed to waiting for the gate the send holds.
        var committed = s.StateReadAfterCloseReturned();

        s.PushClose();
        await committed.WaitAsync(Timeout);
        var dispose = t.Transport.DisposeAsync().AsTask();
        s.ReleaseSends();
        await inFlight.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, DisposeAnswer);
    }

    [Fact]
    public async Task Order_a3_the_peers_close_is_read_before_dispose_but_the_drain_takes_the_socket_first()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        var (pooled, holding) = await t.HoldPooledSendAsync(); // keeps every drain, and so the close phase, waiting
        s.HoldCloseRead();

        s.PushClose();
        await s.CloseRead.WaitAsync(Timeout);
        var dispose = t.Transport.DisposeAsync().AsTask();
        await WaitAsync(() => t.Transport.ActiveSessionSocketCount == 0); // the drain took the socket
        s.ReleaseCloseRead();
        await t.Transport.SessionLoopDone(s).WaitAsync(Timeout);
        s.Disposed.Should().BeFalse("the loop leaves a socket dispose has begun on to dispose");
        s.CloseOutputAttempts.Should().Be(0, "nothing answered yet: the close phase has not begun");
        pooled.ReleaseSends();
        await holding.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, DisposeAnswer);
    }

    // ---- dispose has begun ---------------------------------------------------------------------

    [Fact]
    public async Task Order_b_the_loop_takes_the_socket_after_dispose_began_and_before_the_drain()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldSends();
        var inFlight = Observed(t.Transport.SendAsync(Request("in flight"), default));
        await s.SendEntered.WaitAsync(Timeout);

        var dispose = t.Transport.DisposeAsync().AsTask(); // the socket's drain waits for the send
        s.PushClose();
        await WaitAsync(() => t.Transport.ActiveSessionSocketCount == 0); // the loop took the socket
        await t.Transport.SessionLoopDone(s).WaitAsync(Timeout);
        s.Disposed.Should().BeFalse("the loop leaves a socket dispose has begun on to dispose");
        s.CloseOutputAttempts.Should().Be(0);
        s.ReleaseSends();
        await inFlight.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, DisposeAnswer);
    }

    [Fact]
    public async Task Order_c_the_drain_took_the_socket_and_the_peers_close_is_read_before_our_close()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        var (pooled, holding) = await t.HoldPooledSendAsync();

        var dispose = t.Transport.DisposeAsync().AsTask();
        await WaitAsync(() => t.Transport.ActiveSessionSocketCount == 0); // the drain took the socket
        s.PushClose();
        await t.Transport.SessionLoopDone(s).WaitAsync(Timeout);
        s.Disposed.Should().BeFalse("the loop leaves a socket dispose has begun on to dispose");
        s.CloseOutputAttempts.Should().Be(0);
        pooled.ReleaseSends();
        await holding.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, DisposeAnswer);
    }

    [Fact]
    public async Task Order_d_the_peers_close_is_read_after_our_close()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.AnswerCloseOutput = false;

        var dispose = t.Transport.DisposeAsync().AsTask();
        await WaitAsync(() => s.CloseOutputStatus is not null);
        s.PushClose();
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, DisposeAnswer);
        s.Operations.Should().Equal($"close-output:1000:{DisposeAnswer}", "close-received", "dispose");
    }

    [Fact]
    public async Task Order_d_prime_the_peers_close_is_read_while_our_close_is_being_written()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldCloseOutput();

        var dispose = t.Transport.DisposeAsync().AsTask();
        await s.CloseOutputEntered.WaitAsync(Timeout);
        s.PushClose();
        await t.Transport.SessionLoopDone(s).WaitAsync(Timeout);
        s.ReleaseCloseOutput();
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, DisposeAnswer);
    }

    // ---- a send finds the socket closed (eviction) ----------------------------------------------

    [Fact]
    public async Task Order_e_a_send_that_finds_the_peers_close_already_read_answers_it_before_it_disposes_the_socket()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldCloseRead();

        s.PushClose(WebSocketCloseStatus.PolicyViolation, "evicted");
        await s.CloseRead.WaitAsync(Timeout); // the socket is in CloseReceived; the loop has not seen it
        (await t.Transport.SendAsync(Request("after the eviction"), default).WaitAsync(Timeout)).Accepted.Should().BeTrue("it went on a pooled socket");
        s.ReleaseCloseRead();
        await WaitAsync(() => t.Transport.LiveSessionSocketCount == 0);

        ShouldBeAnsweredOnce(t, s, LoopAnswer);
        t.Sockets.Count.Should().Be(2, "the send itself went on a pooled socket");
    }

    [Fact]
    public async Task Order_e_with_dispose_beginning_while_the_send_writes_its_answer()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldCloseRead();
        s.HoldCloseOutput();

        s.PushClose(WebSocketCloseStatus.PolicyViolation, "evicted");
        await s.CloseRead.WaitAsync(Timeout);
        var send = Observed(t.Transport.SendAsync(Request("after the eviction"), default));
        await s.CloseOutputEntered.WaitAsync(Timeout); // the send is answering, under the socket's gate
        var dispose = t.Transport.DisposeAsync().AsTask();
        s.ReleaseCloseOutput();
        await ((Func<Task>)(() => send.WaitAsync(Timeout))).Should().ThrowAsync<ObjectDisposedException>("its pooled retry met the disposed transport");
        s.ReleaseCloseRead();
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, LoopAnswer);
    }

    // ---- retire, and a 1008 close suppressed by dispose ------------------------------------------

    [Fact]
    public async Task Order_f_the_peers_close_read_before_the_retire_is_answered_by_the_retire()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldSends();
        var none = Observed(t.Transport.SendAsync(Request("none", ReturnRouteMode.None), default));
        await s.SendEntered.WaitAsync(Timeout);

        s.PushClose();
        s.ReleaseSends();
        await none.WaitAsync(Timeout);
        await WaitAsync(() => t.Transport.LiveSessionSocketCount == 0);

        ShouldBeAnsweredOnce(t, s, "return-route session ended");
    }

    [Fact]
    public async Task Order_g_prime_a_1008_close_dispose_suppressed_then_the_peers_close_is_answered_by_dispose()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldSends();
        var inFlight = Observed(t.Transport.SendAsync(Request("in flight"), default));
        await s.SendEntered.WaitAsync(Timeout);
        var mark = t.Clock.Mark();
        s.PushPart(new byte[100], endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, Limit, Timeout);
        t.Clock.Advance(Limit); // the message's time runs out: the loop's 1008 close waits for the gate
        await WaitAsync(() => t.Transport.ActiveSessionSocketCount == 0);
        s.HoldCloseRead();

        s.PushClose();
        await s.CloseRead.WaitAsync(Timeout);
        var dispose = t.Transport.DisposeAsync().AsTask();
        s.ReleaseSends(); // the 1008 is suppressed: dispose began
        await inFlight.WaitAsync(Timeout);
        await s.CloseReadCancelled.WaitAsync(Timeout); // the sweep stopped the loop
        s.ReleaseCloseRead();
        await dispose.WaitAsync(Timeout);

        ShouldBeAnsweredOnce(t, s, DisposeAnswer);
        s.Operations.Should().NotContain(o => o.StartsWith("close-output:1008", StringComparison.Ordinal));
    }

    // ---- the sweep writes under the gate ---------------------------------------------------------

    [Fact]
    public async Task The_sweep_never_writes_its_answer_while_a_send_that_outlived_the_drain_holds_the_gate()
    {
        // A send that ignores its token outlives the drain (DrainBudget) on the session socket, so
        // the sweep, which owns the socket the loop took, must take the gate to answer: it waits for
        // its budget, then disposes the socket unanswered (the rule for an abandoned send), and never
        // writes while that send is still writing.
        await using var t = Harness.Create(o =>
        {
            o.ConnectTimeout = TimeSpan.FromMilliseconds(100);
            o.SendTimeout = TimeSpan.FromMilliseconds(300);
        });
        var s = await t.OpenSessionAsync();
        s.HoldSendsIgnoringToken();
        var stuck = Observed(t.Transport.SendAsync(Request("stuck"), default));
        await s.SendEntered.WaitAsync(Timeout);

        var dispose = t.Transport.DisposeAsync().AsTask();
        s.PushClose();
        await WaitAsync(() => t.Transport.ActiveSessionSocketCount == 0);
        await dispose.WaitAsync(Timeout);

        s.CloseOutputsDuringSend.Should().Be(0, "no close output while the stuck send was writing");
        s.CloseOutputAttempts.Should().Be(0, "the gate stayed held for the whole budget: unanswered, as for any abandoned send");
        s.Disposed.Should().BeTrue();
        t.DisconnectedFor(Endpoint).Should().Be(1);
        s.ReleaseSends();
        await ((Func<Task>)(() => stuck.WaitAsync(Timeout))).Should().ThrowAsync<Exception>();
    }

    // ---- nothing reaches the consumer once dispose began -----------------------------------------

    [Fact]
    public async Task A_push_on_a_retiring_socket_after_dispose_began_never_reaches_the_consumer()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.AnswerCloseOutput = false; // the retiring socket stays open, reading
        await t.Transport.SendAsync(Request("none", ReturnRouteMode.None), default).WaitAsync(Timeout);
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.Transport.LiveSessionSocketCount.Should().Be(1, "precondition: retiring, still read");
        var (pooled, holding) = await t.HoldPooledSendAsync(); // dispose stays in its drain

        var dropped = t.Transport.InboundMessagesDropped;
        var dispose = t.Transport.DisposeAsync().AsTask();
        s.PushMessage(Utf8("late"));
        await WaitAsync(() => t.Transport.InboundMessagesDropped == dropped + 1);
        t.Consumer.Calls.Should().Be(0, "a retiring socket drops what it reads once dispose began");
        pooled.ReleaseSends();
        await holding.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);
        t.Consumer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_push_on_an_active_socket_whose_drain_still_waits_for_its_gate_never_reaches_the_consumer()
    {
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        s.HoldSends();
        var inFlight = Observed(t.Transport.SendAsync(Request("in flight"), default));
        await s.SendEntered.WaitAsync(Timeout);

        var dropped = t.Transport.InboundMessagesDropped;
        var dispose = t.Transport.DisposeAsync().AsTask(); // the socket's drain waits for the send
        s.PushMessage(Utf8("late"));
        await WaitAsync(() => t.Transport.InboundMessagesDropped == dropped + 1);
        t.Consumer.Calls.Should().Be(0, "an active socket drops what it reads once dispose began, before its drain");
        s.ReleaseSends();
        await inFlight.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);
        t.Consumer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_push_on_a_session_socket_a_send_opened_during_the_drain_never_reaches_the_consumer()
    {
        await using var t = Harness.Create();
        t.Sockets.OnCreate = s => s.AnswerCloseOutput = false; // the close phase waits for the peer
        t.HoldConnects();
        var opening = Observed(t.Transport.SendAsync(Request("open", ReturnRouteMode.All), default));
        await t.ConnectEntered.WaitAsync(Timeout);

        var dispose = t.Transport.DisposeAsync().AsTask(); // the socket is not published yet
        t.ReleaseConnects();
        (await opening.WaitAsync(Timeout)).Accepted.Should().BeTrue("a send past the entry check is drained");
        var s = t.Sockets.Sockets.Single();
        await WaitAsync(() => s.CloseOutputStatus is not null); // every drain is done: the close phase began
        var dropped = t.Transport.InboundMessagesDropped;
        s.PushMessage(Utf8("late"));
        await WaitAsync(() => t.Transport.InboundMessagesDropped == dropped + 1);
        t.Consumer.Calls.Should().Be(0, "the drain switched the socket it took to dropping");
        s.PushClose();
        await dispose.WaitAsync(Timeout);
        t.Consumer.Calls.Should().Be(0);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    // One close output, the expected one; the socket disposed, after the answer when the peer's close
    // came first; one Disconnected for the session's endpoint.
    private static void ShouldBeAnsweredOnce(Harness t, ScriptedSessionWebSocket s, string answer)
    {
        var operations = s.Operations;
        var trail = string.Join(", ", operations);
        operations.Should().Contain("close-received", "precondition: the peer's close was read ({0})", trail);
        s.CloseOutputAttempts.Should().Be(1, "the peer's close is answered exactly once ({0})", trail);
        operations.Single(o => o.StartsWith("close-output:", StringComparison.Ordinal)).Should().Be($"close-output:1000:{answer}", "the expected party answered ({0})", trail);
        s.Disposed.Should().BeTrue();
        var received = IndexOf(operations, "close-received");
        var output = IndexOf(operations, "close-output:");
        if (received < output)
            output.Should().BeLessThan(IndexOf(operations, "dispose"), "the socket is disposed only after its answer ({0})", trail);
        t.DisconnectedFor(Endpoint).Should().Be(1, "Disconnected once for the session socket");
    }

    private static int IndexOf(IReadOnlyList<string> operations, string prefix)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i].StartsWith(prefix, StringComparison.Ordinal))
                return i;
        }
        return int.MaxValue;
    }

    private static TransportRequest Request(string text, ReturnRouteMode? mode = null)
        => new(Endpoint, Utf8(text), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = Alice, PeerDid = Bob };

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // A task the test may stop before awaiting (a failed assertion): its failure is observed, so it
    // never surfaces as an unobserved task exception in another test.
    private static Task<T> Observed<T>(Task<T> task)
    {
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private static async Task WaitAsync(Func<bool> condition)
        => (await ReturnRouteTestHost.WaitUntilAsync(condition, Timeout)).Should().BeTrue();

    /// <summary>A transport over scripted sockets, on a hand-moved clock, recording its lifecycle events.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> _events = new();
        private TaskCompletionSource? _connectGate;
        private readonly TaskCompletionSource _connectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Harness(Action<WebSocketTransportOptions>? configure)
        {
            var options = new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                SendTimeout = TimeSpan.FromSeconds(10),
                MessageReceiveTimeout = Limit,
                WebSocketFactory = Sockets.Create,
                Connect = async (_, _, _) =>
                {
                    if (Volatile.Read(ref _connectGate) is { } gate)
                    {
                        _connectEntered.TrySetResult();
                        await gate.Task.ConfigureAwait(false);
                    }
                },
            };
            configure?.Invoke(options);
            var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            Transport = new WebSocketDidCommTransport(
                Options.Create(options),
                factory.CreateLogger<WebSocketDidCommTransport>(),
                Options.Create(new DidCommOptions()),
                Consumer,
                Clock);
            Transport.Lifecycle += (_, e) => _events.Enqueue((e.Kind, e.Endpoint));
        }

        public ScriptedSocketFactory Sockets { get; } = new();

        public RecordingFrameConsumer Consumer { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public QaManualTimeProvider Clock { get; } = new();

        public WebSocketDidCommTransport Transport { get; }

        public Task ConnectEntered => _connectEntered.Task;

        public static Harness Create(Action<WebSocketTransportOptions>? configure = null) => new(configure);

        public int DisconnectedFor(Uri endpoint)
            => _events.Count(e => e.Kind == WebSocketLifecycleEventKind.Disconnected && e.Endpoint == endpoint);

        /// <summary>Open the session socket with an <c>all</c> send; its reader waits for a push.</summary>
        public async Task<ScriptedSessionWebSocket> OpenSessionAsync()
        {
            await Transport.SendAsync(Request("open", ReturnRouteMode.All), default).WaitAsync(Timeout);
            Transport.ActiveSessionSocketCount.Should().Be(1);
            return Sockets.Sockets.Single();
        }

        /// <summary>A pooled send in flight, held: its drain, and with it every close, waits until the socket's sends are released.</summary>
        public async Task<(ScriptedSessionWebSocket Socket, Task Sending)> HoldPooledSendAsync()
        {
            await Transport.SendAsync(new TransportRequest(PoolEndpoint, Utf8("pooled"), MediaTypes.Encrypted), default).WaitAsync(Timeout);
            var pooled = Sockets.Sockets[^1];
            pooled.HoldSends();
            var sending = Observed(Transport.SendAsync(new TransportRequest(PoolEndpoint, Utf8("held"), MediaTypes.Encrypted), default));
            await pooled.SendEntered.WaitAsync(Timeout);
            return (pooled, sending);
        }

        public void HoldConnects() => Volatile.Write(ref _connectGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseConnects() => Interlocked.Exchange(ref _connectGate, null)?.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            ReleaseConnects();
            Sockets.AnswerClosesFromNowOn();
            foreach (var socket in Sockets.Sockets)
            {
                socket.ReleaseSends();
                socket.ReleaseCloseRead();
                socket.ReleaseCloseOutput();
            }
            await Transport.DisposeAsync();
        }
    }
}
