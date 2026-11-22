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
/// #100 OD-7, round 4 red team of 18d4002 (a close the peer started on a session socket is answered
/// once, before the socket is disposed). Each test names the order it attacks and fails on 18d4002
/// when the transport breaks a guarantee it documents; the last one is a real-socket check of what
/// held.
/// </summary>
public sealed class WebSocketDisposePeerCloseRedTeamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly Uri Endpoint = new("ws://agents.r.us/ws");
    private static readonly Uri PoolEndpoint = new("ws://agents.r.us/pool");
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";

    // ---- RT4-1: a session socket opened during the drain surfaces pushes before the drain takes it --

    [Fact]
    public async Task RT4_1_a_push_on_a_session_socket_a_send_opened_during_the_drain_never_reaches_the_consumer_even_before_the_drain_took_it()
    {
        // DisposeAsync: "From the moment this call begins, nothing new reaches the inbound consumer".
        // A send that held its gate when dispose began publishes its new session socket and starts
        // its loop; the drain marks the socket as dropping only once it gets the gate, after that
        // send. A push read in between goes to the consumer, with an uncancelled token.
        await using var t = Harness.Create();
        t.Sockets.OnCreate = s => s.HoldSends(); // the opening send stays on the new socket
        t.HoldConnects();
        var opening = Observed(t.Transport.SendAsync(Request("open", ReturnRouteMode.All), default));
        await t.ConnectEntered.WaitAsync(Timeout);

        var dispose = t.Transport.DisposeAsync().AsTask(); // the socket is not published yet
        t.ReleaseConnects();
        var s = t.Sockets.Sockets.Single();
        await s.SendEntered.WaitAsync(Timeout); // published, its loop reading; the drain still waits
        var dropped = t.Transport.InboundMessagesDropped;
        s.PushMessage(Utf8("pushed after dispose began"));
        await WaitAsync(() => t.Consumer.Calls + (t.Transport.InboundMessagesDropped - dropped) >= 1);

        t.Consumer.Calls.Should().Be(0, "dispose had begun before the peer pushed this message");
        s.ReleaseSends();
        await opening.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);
    }

    // ---- RT4-2: dispose stops before its sweep, so the loop's hand-off is never picked up ----------

    [Fact]
    public async Task RT4_2_a_pooled_socket_whose_dispose_throws_does_not_strand_the_session_socket_the_loop_left_to_the_sweep()
    {
        // Order b: dispose began, the loop read the peer's close and, as 18d4002 requires, left the
        // socket to the sweep. A host socket (WebSocketFactory) that throws on Dispose makes the close
        // phase's finally throw (CloseEndpointAsync says it never throws), so DisposeAsync ends before
        // the seal and the sweep: the session socket is never answered, never disposed, stays live and
        // is never reported. Before 18d4002 the loop disposed it itself.
        await using var t = Harness.Create();
        var s = await t.OpenSessionAsync();
        t.ThrowOnDisposeOfNextSocket = true;
        await t.Transport.SendAsync(new TransportRequest(PoolEndpoint, Utf8("pooled"), MediaTypes.Encrypted), default).WaitAsync(Timeout);
        t.ThrowOnDisposeOfNextSocket = false;
        s.HoldSends();
        var inFlight = Observed(t.Transport.SendAsync(Request("in flight"), default));
        await s.SendEntered.WaitAsync(Timeout);

        var dispose = t.Transport.DisposeAsync().AsTask(); // the session's drain waits for the send
        s.PushClose();
        await WaitAsync(() => t.Transport.ActiveSessionSocketCount == 0); // the loop took the socket
        await t.Transport.SessionLoopDone(s).WaitAsync(Timeout);
        s.ReleaseSends();
        await inFlight.WaitAsync(Timeout);
        var thrown = await Record.ExceptionAsync(() => dispose.WaitAsync(Timeout));

        s.CloseOutputAttempts.Should().Be(1, "the peer's close is answered ({0})", string.Join(", ", s.Operations));
        s.Disposed.Should().BeTrue("no session socket survives dispose");
        t.Transport.LiveSessionSocketCount.Should().Be(0);
        t.DisconnectedFor(Endpoint).Should().Be(1, "Disconnected once for the session socket");
        thrown.Should().BeNull("a throwing host socket must not abort the teardown");
    }

    // ---- RT4-3: the eviction answer lengthens a healthy send past the drain budget ------------------

    [Fact]
    public async Task RT4_3_a_return_route_send_that_finds_its_socket_closed_by_the_peer_reopens_without_writing_on_it()
    {
        // DrainBudget = ConnectTimeout + SendTimeout is "the worst case for a HEALTHY hold". A send
        // that finds the socket closed by the peer (an eviction) must therefore not write the answer
        // under its gate first (up to CloseGrace more): it only stops routing to the old socket and
        // reopens; the old socket's own loop answers. With the old socket's close output held, the
        // send completes before any close output on it begins.
        await using var t = Harness.Create();
        var s1 = await t.OpenSessionAsync();
        s1.HoldCloseRead();
        s1.HoldCloseOutput();
        s1.PushClose(WebSocketCloseStatus.PolicyViolation, "evicted");
        await s1.CloseRead.WaitAsync(Timeout);

        var reopen = Observed(t.Transport.SendAsync(Request("reopen", ReturnRouteMode.All), default));
        var first = await Task.WhenAny(reopen, s1.CloseOutputEntered).WaitAsync(Timeout);

        first.Should().BeSameAs(reopen, "the send wrote nothing on the evicted socket: its hold is connect + send");
        (await reopen).Accepted.Should().BeTrue();
        s1.CloseOutputAttempts.Should().Be(0);
        t.Sockets.Count.Should().Be(2, "the send opened a fresh session socket");
        t.Transport.ActiveSessionSocketCount.Should().Be(1);
        // The old socket's loop answers the peer's close, once, then ends it.
        s1.ReleaseCloseRead();
        await s1.CloseOutputEntered.WaitAsync(Timeout);
        s1.ReleaseCloseOutput();
        await WaitAsync(() => t.Transport.LiveSessionSocketCount == 1);
        s1.CloseOutputAttempts.Should().Be(1);
        s1.CloseOutputDescription.Should().Be("return-route session closed");
        s1.Disposed.Should().BeTrue();
        t.DisconnectedFor(Endpoint).Should().Be(1, "the evicted socket's end, once");
    }

    // ---- what held: a peer that closes every session socket at once, on real sockets -------------------

    [Fact]
    public async Task Held_a_peer_that_closes_every_session_socket_just_before_dispose_gets_one_answer_on_each()
    {
        for (var round = 0; round < 3; round++)
        {
            await using var t = await WebSocketDisposeCloseWaitTests.Live.StartAsync();
            var peers = new List<RawFrameConnection>();
            foreach (var bob in new[] { "did:example:bob1", "did:example:bob2", "did:example:bob3", "did:example:bob4" })
                peers.Add(await t.OpenAsync(WebSocketDisposeCloseWaitTests.Kind.Session, peerDid: bob));

            await Task.WhenAll(peers.Select(p => p.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "peer first")));
            await t.Transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            foreach (var peer in peers)
            {
                var frame = await peer.NextFrameAsync(WebSocketDisposeCloseWaitTests.Guard);
                frame.Should().NotBeNull($"round {round}: every peer's close is answered");
                frame!.IsClose.Should().BeTrue();
                (await peer.NextFrameAsync(WebSocketDisposeCloseWaitTests.Guard)).Should().BeNull($"round {round}: one answer only");
            }
            t.Events.Count(e => e == WebSocketLifecycleEventKind.Disconnected).Should().Be(4, $"round {round}");
            t.Transport.LiveSessionSocketCount.Should().Be(0);
            t.Sockets.Should().OnlyContain(s => s.State == WebSocketState.Closed, $"round {round}: every handshake completed");
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static TransportRequest Request(string text, ReturnRouteMode? mode = null)
        => new(Endpoint, Utf8(text), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = Alice, PeerDid = Bob };

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static Task<T> Observed<T>(Task<T> task)
    {
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private static async Task WaitAsync(Func<bool> condition)
        => (await ReturnRouteTestHost.WaitUntilAsync(condition, Timeout)).Should().BeTrue();

    /// <summary>A transport over scripted sockets recording its lifecycle events; can wrap the next socket so its Dispose throws.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> _events = new();
        private TaskCompletionSource? _connectGate;
        private readonly TaskCompletionSource _connectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _throwOnDispose;

        private Harness(Action<WebSocketTransportOptions>? configure)
        {
            var options = new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                SendTimeout = TimeSpan.FromSeconds(10),
                WebSocketFactory = () =>
                {
                    var socket = (ScriptedSessionWebSocket)Sockets.Create();
                    return _throwOnDispose ? new ThrowOnDisposeWebSocket(socket) : socket;
                },
                Connect = async (_, _, ct) =>
                {
                    if (Volatile.Read(ref _connectGate) is { } gate)
                    {
                        _connectEntered.TrySetResult();
                        await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                    }
                },
            };
            configure?.Invoke(options);
            Transport = new WebSocketDidCommTransport(
                Options.Create(options),
                LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs)).CreateLogger<WebSocketDidCommTransport>(),
                Options.Create(new DidCommOptions()),
                Consumer,
                new QaManualTimeProvider());
            Transport.Lifecycle += (_, e) => _events.Enqueue((e.Kind, e.Endpoint));
        }

        public ScriptedSocketFactory Sockets { get; } = new();

        public RecordingFrameConsumer Consumer { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public WebSocketDidCommTransport Transport { get; }

        public Task ConnectEntered => _connectEntered.Task;

        public bool ThrowOnDisposeOfNextSocket
        {
            get => _throwOnDispose;
            set => _throwOnDispose = value;
        }

        public static Harness Create(Action<WebSocketTransportOptions>? configure = null) => new(configure);

        public int DisconnectedFor(Uri endpoint)
            => _events.Count(e => e.Kind == WebSocketLifecycleEventKind.Disconnected && e.Endpoint == endpoint);

        public async Task<ScriptedSessionWebSocket> OpenSessionAsync()
        {
            await Transport.SendAsync(Request("open", ReturnRouteMode.All), default).WaitAsync(Timeout);
            Transport.ActiveSessionSocketCount.Should().Be(1);
            return Sockets.Sockets.Single();
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
            try
            {
                await Transport.DisposeAsync();
            }
            catch (InvalidOperationException)
            {
                // RT4-2's host socket.
            }
            foreach (var socket in Sockets.Sockets)
                socket.Dispose();
        }
    }

    /// <summary>A host socket (custom <c>WebSocketFactory</c>) that throws from <see cref="Dispose"/> after it closed.</summary>
    private sealed class ThrowOnDisposeWebSocket(ScriptedSessionWebSocket inner) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;

        public override string? CloseStatusDescription => inner.CloseStatusDescription;

        public override WebSocketState State => inner.State;

        public override string? SubProtocol => inner.SubProtocol;

        public override void Abort() => inner.Abort();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => inner.ReceiveAsync(buffer, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public override void Dispose()
        {
            inner.Dispose();
            throw new InvalidOperationException("host socket throws on dispose");
        }
    }
}
