using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.Stomp;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7: <see cref="WebSocketDidCommTransport.DisposeAsync"/> closes each open pooled and
/// active session socket with <see cref="WebSocket.CloseAsync"/>: it sends its close frame and waits
/// for the peer's, bounded by the close budget (one <see cref="WebSocketTransportOptions.SendTimeout"/>;
/// zero when that is infinite or not positive), every socket at once. A peer that answers at once
/// lets dispose return at once; a peer that never answers costs one budget in total, however many
/// sockets it holds, and is then cut off. <see cref="WebSocketDidCommTransport.DisposeAsync"/> takes no
/// <see cref="CancellationToken"/>: the budget is its only bound, and these tests add none.
/// </summary>
/// <remarks>
/// The close and receive behaviour runs against a real Kestrel peer on loopback that writes raw frames
/// (<see cref="RawFrameWebSocketPeer"/>) and real <see cref="ClientWebSocket"/>s in the transport:
/// how a pending receive and a close share a socket is a property of the real socket. The budget is a
/// real-time <see cref="CancellationTokenSource"/> in the transport, so these tests use small
/// <c>SendTimeout</c>s with generous upper bounds, and show that dispose is still waiting by events (a
/// ping it answers, a connection it has not ended), never by an exact elapsed time.
/// </remarks>
public sealed class WebSocketDisposeCloseWaitTests
{
    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    // The close budget of the tests that wait it out: small, so a test that waits it out stays quick.
    internal static readonly TimeSpan ShortBudget = TimeSpan.FromSeconds(1);
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";

    /// <summary>Which socket dispose closes.</summary>
    public enum Kind
    {
        /// <summary>The endpoint's pooled socket (a header-less send). Nothing reads it but the close.</summary>
        Pooled,

        /// <summary>A return-route session socket (a <c>return_route: all</c> send), read by its receive loop.</summary>
        Session,
    }

    public static TheoryData<Kind> Kinds => new() { Kind.Pooled, Kind.Session };

    // ---- a peer that never answers the close ------------------------------------------------------

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Dispose_waits_for_a_silent_peers_close_and_gives_up_at_the_close_budget(Kind kind)
    {
        await using var t = await Live.StartAsync(o => o.SendTimeout = ShortBudget);
        var peer = await t.OpenAsync(kind);

        var clock = Stopwatch.StartNew();
        var dispose = t.Transport.DisposeAsync().AsTask();
        var close = await NextCloseAsync(peer);
        close.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        close.CloseReason.Should().Be("transport disposed");
        // Dispose is still reading the socket after its close frame: it answers a ping from inside the
        // receive that waits for the peer's close (a close that only sent would have dropped the connection).
        (await peer.PingAsync(Guard)).Should().BeEmpty("nothing but the pong follows the client's close frame");

        await dispose.WaitAsync(Guard);
        clock.Stop();

        (await peer.NextFrameAsync(Guard)).Should().BeNull("at the budget the client cut the connection off");
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(ShortBudget / 2, "only the close budget ends the wait (a loose lower bound, not an exact time)");
        clock.Elapsed.Should().BeLessThan(ShortBudget + TimeSpan.FromSeconds(8), "one close budget, not much more");
        t.Sockets.Single().State.Should().BeOneOf(WebSocketState.Aborted, WebSocketState.Closed);
        t.Events.Count(e => e == WebSocketLifecycleEventKind.Disconnected).Should().Be(1, "one socket, one Disconnected");
        t.Transport.LiveSessionSocketCount.Should().Be(0);
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
    }

    // ---- a peer that answers the close ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Dispose_returns_as_soon_as_the_peer_answers_the_close_well_inside_the_budget(Kind kind)
    {
        // The default SendTimeout: a 30 s close budget, against which "as soon as" is measured.
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(kind);

        var dispose = t.Transport.DisposeAsync().AsTask();
        (await NextCloseAsync(peer)).CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        dispose.IsCompleted.Should().BeFalse("dispose waits for the peer's close");
        await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        peer.End();

        await dispose.WaitAsync(TimeSpan.FromSeconds(10)); // a third of the 30 s budget: it did not wait that out
        var socket = t.Sockets.Single();
        socket.State.Should().Be(WebSocketState.Closed, "the close handshake completed; nothing was aborted");
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the peer's answer");
        socket.CloseStatusDescription.Should().Be("bye");
        t.Events.Count(e => e == WebSocketLifecycleEventKind.Disconnected).Should().Be(1, "the loop's end and dispose report the socket once between them");
        t.Transport.LiveSessionSocketCount.Should().Be(0);
        t.ExceptionLines().Should().BeEmpty("neither the close nor the receive loop failed: no InvalidOperationException, no abort");
        if (kind == Kind.Session)
            t.Consumer.Calls.Should().Be(0, "the peer pushed nothing");
    }

    [Fact]
    public async Task On_the_pooled_socket_data_the_peer_sends_before_its_close_is_read_and_dropped()
    {
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(Kind.Pooled);

        var dispose = t.Transport.DisposeAsync().AsTask();
        await NextCloseAsync(peer);
        await peer.SendTextAsync(Utf8("late, in parts "), endOfMessage: false);
        await peer.SendContinuationAsync(Utf8("then the end"), endOfMessage: true);
        await peer.SendTextAsync(Utf8("another"), endOfMessage: true);
        await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        peer.End();

        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        var socket = t.Sockets.Single();
        socket.State.Should().Be(WebSocketState.Closed);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the close came after the data, which the wait read past");
        t.Consumer.Calls.Should().Be(0, "a pooled socket is never surfaced");
        t.ExceptionLines().Should().BeEmpty();
    }

    [Fact]
    public async Task When_a_session_sockets_peer_closes_just_before_dispose_the_close_is_answered_once_and_reported_once()
    {
        // The peer's close races dispose: the receive loop reads it and answers it unless dispose has
        // begun, and dispose answers it (CloseAsync in CloseReceived only sends) when it finds the
        // socket there first. Whichever does, the answer goes out once, nothing waits for a second
        // close, and Disconnected is raised once.
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(Kind.Session);
        var disconnected = 0;
        t.Transport.Lifecycle += (_, e) =>
        {
            if (e.Kind == WebSocketLifecycleEventKind.Disconnected)
                Interlocked.Increment(ref disconnected);
        };

        await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "peer first");
        await t.Transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var frame = await peer.NextFrameAsync(Guard);
        frame.Should().NotBeNull();
        frame!.IsClose.Should().BeTrue("the client answered the peer's close exactly once");
        (await peer.NextFrameAsync(Guard)).Should().BeNull("nothing after the one answer");
        Volatile.Read(ref disconnected).Should().Be(1);
        t.Sockets.Single().State.Should().Be(WebSocketState.Closed);
    }

    // ---- many sockets: one budget in total --------------------------------------------------------

    [Fact]
    public async Task Dispose_waits_for_every_silent_peers_close_at_once_one_budget_in_total()
    {
        // Three pooled sockets and two session sockets, all to peers that never answer the close. The
        // closes run concurrently: every connection receives its close frame, and still answers a
        // ping, before any connection has been cut off (closed one after another, the second close
        // frame would only go out once the first socket's budget had run out and it was cut off).
        await using var t = await Live.StartAsync(o => o.SendTimeout = TimeSpan.FromSeconds(2));
        var peers = new List<RawFrameConnection>();
        foreach (var n in new[] { 1, 2, 3 })
            peers.Add(await t.OpenAsync(Kind.Pooled, query: $"?n={n}"));
        foreach (var bob in new[] { "did:example:bob1", "did:example:bob2" })
            peers.Add(await t.OpenAsync(Kind.Session, peerDid: bob));
        t.Transport.ActiveSessionSocketCount.Should().Be(2);

        var clock = Stopwatch.StartNew();
        var dispose = t.Transport.DisposeAsync().AsTask();
        foreach (var peer in peers)
            await NextCloseAsync(peer);
        foreach (var peer in peers)
            (await peer.PingAsync(Guard)).Should().BeEmpty("every socket is still open and waiting for its peer's close");

        await dispose.WaitAsync(Guard);
        clock.Stop();
        foreach (var peer in peers)
            (await peer.NextFrameAsync(Guard)).Should().BeNull("each was cut off at the budget");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2) * 3, "one budget for all five, far below one budget each");
        t.Events.Count(e => e == WebSocketLifecycleEventKind.Disconnected).Should().Be(5);
    }

    [Fact]
    public async Task Dispose_waits_for_every_peers_close_concurrently_never_one_after_another()
    {
        // The close-wait variant of DisposeAsync_drains_multiple_wedged_endpoints_concurrently_not_serially,
        // without real-time measurement: three pooled sockets whose close is never answered. All three
        // waits are under way at once, so the budget is paid once, not three times.
        var sockets = new ConcurrentQueue<SilentCloseWebSocket>();
        var inFlight = new CloseWaits();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            SendTimeout = ShortBudget,
            WebSocketFactory = () =>
            {
                var s = new SilentCloseWebSocket(inFlight);
                sockets.Enqueue(s);
                return s;
            },
            Connect = (_, _, _) => Task.CompletedTask,
        });
        var transport = new WebSocketDidCommTransport(options);
        foreach (var path in new[] { "/a", "/b", "/c" })
            await transport.SendAsync(new TransportRequest(new Uri($"ws://agents.r.us{path}"), Utf8("x"), MediaTypes.Encrypted), default);

        var dispose = transport.DisposeAsync().AsTask();
        (await ReturnRouteTestHost.WaitUntilAsync(() => inFlight.Max == 3, Guard)).Should().BeTrue(
            "all three close waits run at the same time");

        await dispose.WaitAsync(Guard);
        sockets.Should().HaveCount(3).And.OnlyContain(s => s.Disposed && s.WaitCancelled,
            "each wait ended at the budget, and the socket was disposed");
        inFlight.Current.Should().Be(0);
    }

    // ---- no budget: no wait -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task With_an_infinite_SendTimeout_dispose_does_not_wait_for_a_silent_peer(Kind kind)
    {
        // An infinite SendTimeout gives dispose a zero close budget: it must stay bounded, so it waits
        // for nothing. A silent peer therefore costs no time at all, where a 30 s default would.
        await using var t = await Live.StartAsync(o => o.SendTimeout = Timeout.InfiniteTimeSpan);
        var peer = await t.OpenAsync(kind);

        await t.Transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // The connection ends without the peer ever answering anything: nothing waited for it.
        while (await peer.NextFrameAsync(Guard) is { } frame)
            frame.IsClose.Should().BeTrue("at most a close frame, then the end");
        t.Events.Count(e => e == WebSocketLifecycleEventKind.Disconnected).Should().Be(1);
        // A zero budget does not wait for a session socket's receive loop either: it was stopped with
        // its socket disposed, and ends on its own.
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Guard)).Should().BeTrue();
    }

    // ---- STOMP: DISCONNECT, then the close and its wait --------------------------------------------

    [Fact]
    public async Task Under_STOMP_dispose_sends_DISCONNECT_then_closes_and_waits_for_the_peers_close()
    {
        var socket = new RecordingStompWebSocket();
        var transport = new WebSocketDidCommTransport(Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            UseStomp = true,
            WebSocketFactory = () => socket,
            Connect = (_, _, _) => Task.CompletedTask,
        }));
        await transport.SendAsync(new TransportRequest(new Uri("ws://agents.r.us/stomp"), Utf8("x"), MediaTypes.Encrypted), default);

        var dispose = transport.DisposeAsync().AsTask();
        await socket.CloseEntered.WaitAsync(Guard);
        dispose.IsCompleted.Should().BeFalse("dispose waits for the peer's close");
        socket.Calls.Should().Equal("SEND CONNECT", "SEND SEND", "SEND DISCONNECT", "CloseAsync NormalClosure");
        socket.AnswerClose();

        await dispose.WaitAsync(Guard);
        socket.Disposed.Should().BeTrue();
        socket.Calls.Should().NotContain(c => c.StartsWith("CloseOutputAsync", StringComparison.Ordinal));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static TransportRequest Request(Uri endpoint, string text, ReturnRouteMode? mode, string peerDid)
        => new(endpoint, Utf8(text), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = Alice, PeerDid = peerDid };

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>The client's next frame, which must be a close frame.</summary>
    internal static async Task<ClientFrame> NextCloseAsync(RawFrameConnection connection)
    {
        var frame = await connection.NextFrameAsync(Guard);
        frame.Should().NotBeNull("the client sends a close frame before ending the connection");
        frame!.IsClose.Should().BeTrue();
        return frame;
    }

    private static ILogger<WebSocketDidCommTransport> LoggerFor(CapturingLoggerProvider logs)
        => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)).CreateLogger<WebSocketDidCommTransport>();

    /// <summary>A transport over real <see cref="ClientWebSocket"/>s to a <see cref="RawFrameWebSocketPeer"/>.</summary>
    internal sealed class Live : IAsyncDisposable
    {
        private readonly ConcurrentQueue<WebSocketLifecycleEventKind> _events = new();
        private readonly ConcurrentQueue<ClientWebSocket> _sockets = new();

        private Live(RawFrameWebSocketPeer peer, Action<WebSocketTransportOptions>? configure)
        {
            Peer = peer;
            var options = new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                WebSocketFactory = () =>
                {
                    var socket = new ClientWebSocket();
                    _sockets.Enqueue(socket);
                    return socket;
                },
                // A custom Connect skips the private-address guard (the peer is on loopback).
                Connect = (socket, uri, ct) => ((ClientWebSocket)socket).ConnectAsync(uri, ct),
            };
            configure?.Invoke(options);
            Transport = new WebSocketDidCommTransport(Options.Create(options), LoggerFor(Logs), coreOptions: null, Consumer, TimeProvider.System);
            Transport.Lifecycle += (_, e) => _events.Enqueue(e.Kind);
        }

        public RawFrameWebSocketPeer Peer { get; }

        public RecordingFrameConsumer Consumer { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public WebSocketDidCommTransport Transport { get; }

        public IReadOnlyList<WebSocketLifecycleEventKind> Events => _events.ToArray();

        /// <summary>Every socket the transport created, in order.</summary>
        public IReadOnlyList<ClientWebSocket> Sockets => _sockets.ToArray();

        public static async Task<Live> StartAsync(Action<WebSocketTransportOptions>? configure = null)
            => new(await RawFrameWebSocketPeer.StartAsync(), configure);

        /// <summary>
        /// Open a socket of <paramref name="kind"/> with one send, read that message at the peer, and
        /// return once the client is idle on it (for a session socket, its reader waits for a push).
        /// </summary>
        public async Task<RawFrameConnection> OpenAsync(Kind kind, string query = "", string peerDid = Bob)
        {
            var endpoint = new Uri(Peer.Endpoint + query);
            var sessions = Transport.ActiveSessionSocketCount;
            await Transport.SendAsync(Request(endpoint, "open", kind == Kind.Session ? ReturnRouteMode.All : null, peerDid), default).WaitAsync(Guard);
            var connection = await Peer.NextConnectionAsync(Guard);
            Text(await connection.NextMessageAsync(Guard)).Should().Be("open");
            Transport.ActiveSessionSocketCount.Should().Be(sessions + (kind == Kind.Session ? 1 : 0));
            if (kind == Kind.Session)
                (await connection.PingAsync(Guard)).Should().BeEmpty("the session socket's reader is waiting");
            return connection;
        }

        /// <summary>Lines that report a failed close or a receive loop that stopped on an exception.</summary>
        public IReadOnlyList<string> ExceptionLines()
            => Logs.Entries.Where(e => e.Contains("ignoring close exception", StringComparison.Ordinal)
                                       || e.Contains("stopped reading", StringComparison.Ordinal)
                                       || e.Contains("Exception", StringComparison.Ordinal)).ToList();

        public async ValueTask DisposeAsync()
        {
            // The peer first: it ends every connection, so a test that stopped before its own dispose
            // does not leave the transport waiting out its close budget on a peer that never answers.
            await Peer.DisposeAsync();
            await Transport.DisposeAsync();
            foreach (var socket in _sockets)
                socket.Dispose();
        }
    }

    /// <summary>How many close waits are under way, and the most there ever were at once.</summary>
    private sealed class CloseWaits
    {
        private int _current;
        private int _max;

        public int Current => Volatile.Read(ref _current);

        public int Max => Volatile.Read(ref _max);

        public void Enter()
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while ((seen = Volatile.Read(ref _max)) < now)
                Interlocked.CompareExchange(ref _max, now, seen);
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    /// <summary>
    /// A connected socket whose peer never answers the close: <see cref="CloseAsync"/> waits until its
    /// token is cancelled. <see cref="CloseOutputAsync"/> only sends, so it completes at once.
    /// </summary>
    private sealed class SilentCloseWebSocket(CloseWaits waits) : WebSocket
    {
        private volatile bool _disposed;
        private volatile bool _waitCancelled;

        public bool Disposed => _disposed;

        public bool WaitCancelled => _waitCancelled;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            waits.Enter();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _waitCancelled = true;
                throw;
            }
            finally
            {
                waits.Exit();
            }
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => _disposed = true;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A STOMP peer that answers CONNECT with CONNECTED, records every send (by STOMP command) and
    /// close call in order, and answers the close only when the test says so.
    /// </summary>
    private sealed class RecordingStompWebSocket : WebSocket
    {
        private readonly byte[] _connected = StompFrameCodec.Encode(new StompFrame(
            "CONNECTED",
            new[] { KeyValuePair.Create("version", "1.2"), KeyValuePair.Create("heart-beat", "0,0") },
            ReadOnlyMemory<byte>.Empty));
        private readonly ConcurrentQueue<string> _calls = new();
        private readonly TaskCompletionSource _closeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _receiveOffset;
        private volatile bool _disposed;

        public IReadOnlyList<string> Calls => _calls.ToArray();

        public Task CloseEntered => _closeEntered.Task;

        public bool Disposed => _disposed;

        public void AnswerClose() => _answer.TrySetResult();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _calls.Enqueue($"CloseAsync {closeStatus}");
            _closeEntered.TrySetResult();
            await _answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _calls.Enqueue($"CloseOutputAsync {closeStatus}");
            _closeEntered.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose() => _disposed = true;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var count = Math.Min(buffer.Count, _connected.Length - _receiveOffset);
            Array.Copy(_connected, _receiveOffset, buffer.Array!, buffer.Offset, count);
            _receiveOffset += count;
            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Binary, _receiveOffset >= _connected.Length));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _calls.Enqueue("SEND " + StompFrameCodec.Decode(buffer).Command);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// #100 OD-7: on real sockets, dispose's close of a return-route session socket shares the socket with
/// the socket's receive loop, which always has a receive outstanding. A <see cref="ClientWebSocket"/>
/// runs one receive at a time, so <see cref="WebSocket.CloseAsync"/> waits for the loop's receive
/// rather than failing with <see cref="InvalidOperationException"/>, and no task the transport or the
/// socket started ends with an exception nobody observed: neither when the peer answers, nor when the
/// close budget cuts a silent peer off (the socket is aborted under the loop's receive). Serial,
/// because <see cref="TaskScheduler.UnobservedTaskException"/> is process-wide.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class WebSocketDisposeCloseWaitUnobservedTests
{
    [Fact]
    public async Task Closing_a_session_socket_under_its_pending_receive_leaves_no_unobserved_exception_whether_the_peer_answers_or_not()
    {
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
            {
                var text = inner.ToString();
                if (text.Contains("System.Net.WebSockets", StringComparison.Ordinal) || text.Contains("DidComm.Transports.WebSocket", StringComparison.Ordinal))
                    unobserved.Enqueue(inner);
            }
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var lines = new List<string>();
            for (var round = 0; round < 5; round++)
            {
                lines.AddRange(await AnsweredAsync());
                lines.AddRange(await SilentAsync());
            }
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            unobserved.Should().BeEmpty("every task the close and the receive loop started is observed");
            lines.Should().NotContain(l => l.Contains(nameof(InvalidOperationException), StringComparison.Ordinal),
                "the close never collides with the loop's pending receive");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // The peer answers dispose's close: the loop's pending receive reads it, the close completes.
    private static async Task<IReadOnlyList<string>> AnsweredAsync()
    {
        await using var t = await WebSocketDisposeCloseWaitTests.Live.StartAsync();
        var peer = await t.OpenAsync(WebSocketDisposeCloseWaitTests.Kind.Session);
        var dispose = t.Transport.DisposeAsync().AsTask();
        await WebSocketDisposeCloseWaitTests.NextCloseAsync(peer);
        await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        peer.End();
        await dispose.WaitAsync(WebSocketDisposeCloseWaitTests.Guard);
        t.Sockets.Single().State.Should().Be(WebSocketState.Closed);
        t.ExceptionLines().Should().BeEmpty();
        return t.Logs.Entries.ToList();
    }

    // The peer never answers: at the budget the close's wait is cancelled and the socket aborted under
    // the loop's pending receive, which then fails; both are awaited by their owners.
    private static async Task<IReadOnlyList<string>> SilentAsync()
    {
        await using var t = await WebSocketDisposeCloseWaitTests.Live.StartAsync(o => o.SendTimeout = TimeSpan.FromMilliseconds(300));
        var peer = await t.OpenAsync(WebSocketDisposeCloseWaitTests.Kind.Session);
        await t.Transport.DisposeAsync().AsTask().WaitAsync(WebSocketDisposeCloseWaitTests.Guard);
        await WebSocketDisposeCloseWaitTests.NextCloseAsync(peer);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, WebSocketDisposeCloseWaitTests.Guard)).Should().BeTrue();
        t.Sockets.Single().State.Should().BeOneOf(WebSocketState.Aborted, WebSocketState.Closed);
        return t.Logs.Entries.ToList();
    }
}
