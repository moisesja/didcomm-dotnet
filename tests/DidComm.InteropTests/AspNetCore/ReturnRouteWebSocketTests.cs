using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using DidComm.AspNetCore;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.Stomp;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #91 F11 — the WebSocket binding of the return-route extension (FR-RR-05) end to end against a
/// TestServer with <c>AllowSameSocketReplies = true</c>: an authcrypt message carrying
/// <c>return_route: all</c> opens a session on its socket, every push a handler makes through
/// <c>context.Session</c> arrives on that socket in order, and closing the socket unregisters it.
/// "Nothing was written" is proven without sleeps: either a later sentinel's frame is the first one
/// the client receives, or the client closes and the first frame back is the server's close (the
/// receive loop handles messages in order and the session writer is FIFO). Every scenario ends with
/// the F14 log scan (no DID, key id or thread value in any captured line).
/// </summary>
public sealed class ReturnRouteWebSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Two_pushes_through_the_session_arrive_on_the_same_socket_in_order_and_close_unregisters()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var closed = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        var opened = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionOpened += (_, e) => opened.Enqueue(e);
        host.Registry.SessionClosed += (_, e) => closed.Enqueue(e);

        host.Recorder.Behavior = (message, context) => (
            new[]
            {
                Push(host, "first", message.Id),
                Push(host, "second", message.Id),
            },
            null);

        var inbound = host.Stamped(host.RecordingMessage(host.Alice, "push-two").WithReturnRoute(ReturnRouteMode.All)).Build();
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, inbound));

        var first = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        var second = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        first.Type.Should().Be(WebSocketMessageType.Binary, "each push is one binary WebSocket message (FR-TRN-09)");
        second.Type.Should().Be(WebSocketMessageType.Binary);

        var firstUnpacked = await host.Alice.Client.UnpackAsync(first.Text);
        var secondUnpacked = await host.Alice.Client.UnpackAsync(second.Text);
        firstUnpacked.Message.Type.Should().EndWith("/first", "pushes are written in the order they were made");
        secondUnpacked.Message.Type.Should().EndWith("/second");
        foreach (var pushed in new[] { firstUnpacked, secondUnpacked })
        {
            pushed.Encrypted.Should().BeTrue();
            pushed.Authenticated.Should().BeTrue();
            pushed.SenderKid.Should().StartWith(host.ServerDid + "#");
            pushed.Message.From.Should().Be(host.ServerDid);
            pushed.Message.To.Should().Equal(host.Alice.Did);
            pushed.Message.Thid.Should().Be(inbound.Id);
        }

        // The handler records after its pushes returned, which can be after the frames arrived here.
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue();
        var seen = host.Recorder.Seen.Single();
        seen.PushResults.Should().Equal(true, true);
        seen.Session.Should().NotBeNull();
        seen.Session!.PeerDid.Should().Be(host.Alice.Did, "the peer is the authcrypt sender's DID subject");
        seen.Session.LocalDid.Should().Be(host.ServerDid, "the local DID is the one whose key decrypted the inbound");
        seen.Session.Transport.Should().Be(ReturnRouteTransport.WebSocket);
        seen.Session.IsLive.Should().BeTrue();
        seen.ReturnRoute.Mode.Should().Be(ReturnRouteMode.All);
        host.Registry.Count.Should().Be(1);
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(seen.Session);
        // Registry events are raised on the thread pool after the call, so they are awaited (bounded).
        (await ReturnRouteTestHost.WaitUntilAsync(() => !opened.IsEmpty, Timeout)).Should().BeTrue("registering the session raises SessionOpened");
        opened.Should().ContainSingle().Which.Session.Should().BeSameAs(seen.Session);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);

        (await ReturnRouteTestHost.WaitUntilAsync(() => !closed.IsEmpty, Timeout)).Should().BeTrue("closing the socket raises SessionClosed (FR-RR-05)");
        var close = closed.Single();
        close.Session.Should().BeSameAs(seen.Session);
        close.Reason.Should().Be(SessionCloseReason.Unregistered);
        host.Registry.Count.Should().Be(0, "the session is unregistered when its socket closes");
        seen.Session.Closed.IsCancellationRequested.Should().BeTrue();
        seen.Session.Mode.Should().Be(ReturnRouteMode.None);
        host.LeakingLogLines().Should().BeEmpty("no log line may carry a DID or key id (JC-6)");
    }

    [Fact]
    public async Task A_second_socket_for_the_same_DID_at_the_per_peer_bound_evicts_the_first_with_a_normal_close()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s => s.MaxSessionsPerPeer = 1;
        });
        var closed = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionClosed += (_, e) => closed.Enqueue(e);

        using var first = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(first, await host.AuthcryptAsync(host.Alice,
            host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build()));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 1, Timeout)).Should().BeTrue();
        var firstSession = host.Registry.Find(host.Alice.Did).Single();

        using var second = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(second, await host.AuthcryptAsync(host.Alice,
            host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build()));

        // FR-RR-03: evicting a WebSocket session closes its socket so the peer observes it.
        var observed = await ReturnRouteTestHost.ReceiveAsync(first, Timeout);
        observed.Type.Should().Be(WebSocketMessageType.Close);
        first.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);

        (await ReturnRouteTestHost.WaitUntilAsync(() => !closed.IsEmpty, Timeout)).Should().BeTrue();
        closed.Should().ContainSingle().Which.Should().Match<ReturnRouteSessionEventArgs>(e => e.Reason == SessionCloseReason.Evicted && ReferenceEquals(e.Session, firstSession));
        var remaining = host.Registry.Find(host.Alice.Did).Should().ContainSingle().Subject;
        remaining.Should().NotBeSameAs(firstSession);
        firstSession.Closed.IsCancellationRequested.Should().BeTrue();

        // The evicted connection's own end unregisters nothing twice.
        await Task.Delay(200);
        closed.Should().HaveCount(1, "the registry already forgot the evicted session; its connection's end raises no second event");
        host.Registry.Count.Should().Be(1);

        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_SessionClosed_subscriber_that_waits_for_the_evicted_session_to_close_does_not_hold_up_the_eviction()
    {
        // PR #103 review (P2): subscribers ran inside Register, before the endpoint closed the evicted
        // session, so a SessionClosed(Evicted) subscriber that waited for that session's Closed
        // deadlocked until it gave up — no close frame, no Closed cancellation, no backstop. The
        // registry below holds the evicting Register until the subscriber has started (bounded), so
        // the subscriber is provably waiting before the endpoint closes the session.
        var cap = TimeSpan.FromSeconds(15);
        EvictionHoldingRegistry? registry = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureServices = s => s.AddSingleton<IReturnRouteSessionRegistry>(_ => registry = new EvictionHoldingRegistry(Timeout));
        });
        var subscriber = new TaskCompletionSource<(bool ClosedAtEntry, bool SawClosed, TimeSpan Waited, IReturnRouteSession Session)>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Registry.SessionClosed += (_, e) =>
        {
            if (e.Reason != SessionCloseReason.Evicted)
                return;
            var closedAtEntry = e.Session.Closed.IsCancellationRequested;
            registry!.SubscriberStarted.Set();
            var waited = Stopwatch.StartNew();
            var sawClosed = e.Session.Closed.WaitHandle.WaitOne(cap); // blocks this thread, as a careless subscriber would
            subscriber.TrySetResult((closedAtEntry, sawClosed, waited.Elapsed, e.Session));
        };

        using var first = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(first, await host.AuthcryptAsync(host.Alice,
            host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build()));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 1, Timeout)).Should().BeTrue();
        var firstSession = (WebSocketReturnRouteSession)host.Registry.Find(host.Alice.Did).Single();
        var endedBefore = host.EndedWebSocketConnections;

        using var second = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(second, await host.AuthcryptAsync(host.Alice,
            host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build()));

        // FR-RR-03: the evicted peer observes the eviction as a normal close.
        var observed = await ReturnRouteTestHost.ReceiveAsync(first, Timeout);
        observed.Type.Should().Be(WebSocketMessageType.Close);
        first.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        first.CloseStatusDescription.Should().Be(WebSocketReturnRouteSession.EvictedDescription);

        var (closedAtEntry, sawClosed, waited, session) = await subscriber.Task.WaitAsync(Timeout);
        session.Should().BeSameAs(firstSession);
        registry!.SubscriberStartedWhileHeld.Should().Be(1, "the subscriber was running while Register was still held (the overlap happened)");
        closedAtEntry.Should().BeFalse("the subscriber started before the endpoint closed the evicted session");
        sawClosed.Should().BeTrue("the evicted session's Closed was cancelled while its SessionClosed subscriber waited for it");
        waited.Should().BeLessThan(TimeSpan.FromSeconds(5), "the subscriber was released well before its {0} cap", cap);
        firstSession.CloseReason.Should().Be(SessionCloseReason.Evicted);

        // The client never answers the close frame: the backstop still ends the connection within the grace.
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections > endedBefore,
            WebSocketReturnRouteSession.DefaultCloseGrace + TimeSpan.FromSeconds(5))).Should().BeTrue("the eviction backstop ends the unanswered connection");
        (await ReturnRouteTestHost.WaitUntilAsync(() => firstSession.WriterStopped, Timeout)).Should().BeTrue();
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().NotBeSameAs(firstSession);

        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_SessionClosed_subscriber_that_blocks_does_not_hold_up_the_client_close_handshake()
    {
        // PR #103 review (P2): the receive loop's finally unregisters the session before it stops the
        // writer and answers the client's close; with subscribers raised inline, one that blocked held
        // the close handshake (and the request) for as long as it blocked.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<ReturnRouteSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = 0;
        host.Registry.SessionClosed += (_, e) =>
        {
            if (e.Reason != SessionCloseReason.Unregistered)
                return;
            entered.TrySetResult(e);
            release.Wait(TimeSpan.FromSeconds(15)); // capped, so a failing run cannot hold a pool thread forever
            Interlocked.Exchange(ref returned, 1);
        };

        try
        {
            using var socket = await host.ConnectAsync();
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice,
                host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build()));
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 1, Timeout)).Should().BeTrue();
            var session = (WebSocketReturnRouteSession)host.Registry.Find(host.Alice.Did).Single();
            var endedBefore = host.EndedWebSocketConnections;

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client done", default).WaitAsync(Timeout);
            socket.State.Should().Be(WebSocketState.Closed, "the server answered the client's close");
            socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections > endedBefore, Timeout)).Should().BeTrue("the connection ended");

            (await entered.Task.WaitAsync(Timeout)).Session.Should().BeSameAs(session);
            Volatile.Read(ref returned).Should().Be(0, "the subscriber had not returned when the handshake and the connection end completed");
            host.Registry.Count.Should().Be(0);
            session.Closed.IsCancellationRequested.Should().BeTrue();
            session.WriterStopped.Should().BeTrue();
        }
        finally
        {
            release.Set();
        }

        (await ReturnRouteTestHost.WaitUntilAsync(() => Volatile.Read(ref returned) == 1, Timeout)).Should().BeTrue("the released subscriber returns");
        host.LeakingLogLines().Should().BeEmpty();
    }

    /// <summary>
    /// A registry (per-peer bound 1) whose <see cref="Register"/>, when it evicted a session, waits
    /// (bounded) for <see cref="SubscriberStarted"/> before returning — so a test can prove a
    /// SessionClosed subscriber was running before the endpoint closed the evicted session.
    /// </summary>
    private sealed class EvictionHoldingRegistry : IReturnRouteSessionRegistry
    {
        private readonly InMemoryReturnRouteSessionRegistry _inner = new(new ReturnRouteSessionOptions { MaxSessionsPerPeer = 1 });
        private readonly TimeSpan _timeout;
        private int _subscriberStartedWhileHeld;

        public EvictionHoldingRegistry(TimeSpan timeout) => _timeout = timeout;

        public ManualResetEventSlim SubscriberStarted { get; } = new(false);

        /// <summary>Evicting registrations during which the subscriber started.</summary>
        public int SubscriberStartedWhileHeld => Volatile.Read(ref _subscriberStartedWhileHeld);

        public int Count => _inner.Count;

        public event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened
        {
            add => _inner.SessionOpened += value;
            remove => _inner.SessionOpened -= value;
        }

        public event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed
        {
            add => _inner.SessionClosed += value;
            remove => _inner.SessionClosed -= value;
        }

        public SessionRegistration Register(IReturnRouteSession session)
        {
            var result = _inner.Register(session);
            if (result.Evicted is not null && SubscriberStarted.Wait(_timeout))
                Interlocked.Increment(ref _subscriberStartedWhileHeld);
            return result;
        }

        public bool Unregister(IReturnRouteSession session) => _inner.Unregister(session);

        public IReadOnlyList<IReturnRouteSession> Find(string did) => _inner.Find(did);
    }

    [Fact]
    public async Task An_eviction_that_lands_while_the_evicted_connection_is_dispatching_still_waits_for_the_peers_close()
    {
        // QA #91 regression (JC-21): the writer sends the eviction close frame, and the receive loop is
        // meant to finish on the peer's answering close (bounded by the backstop). The loop used to
        // run only while the socket was Open, so an eviction that landed while it was dispatching
        // made it exit at the next iteration (state CloseSent) and dispose the socket mid-handshake:
        // the peer's answer failed and anything it sent before seeing the close was never read.
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s => s.MaxSessionsPerPeer = 1;
        });
        using var first = await OpenSessionAsync(host, host.Alice);

        // Park the first connection's loop inside a dispatch.
        var parked = host.Stamped(host.RecordingMessage(host.Alice, "parked")).Build();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        host.Recorder.OnEnter = message =>
        {
            if (message.Id != parked.Id)
                return;
            entered.Set();
            release.Wait(Timeout);
        };
        await ReturnRouteTestHost.SendAsync(first, await host.AuthcryptAsync(host.Alice, parked));
        entered.Wait(Timeout).Should().BeTrue();

        // A second socket for Alice evicts the first while its loop is busy.
        using var second = await OpenSessionAsync(host, host.Alice);
        var close = await ReturnRouteTestHost.ReceiveAsync(first, Timeout);
        close.Type.Should().Be(WebSocketMessageType.Close, "the evicted peer observes a normal close (FR-RR-03)");
        first.CloseStatusDescription.Should().Be(WebSocketReturnRouteSession.EvictedDescription);

        release.Set();
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Seen.Any(r => r.Message.Id == parked.Id), Timeout)).Should().BeTrue();

        // The peer had one more message in flight before it saw the close; then it answers the close.
        var inFlight = host.Stamped(host.RecordingMessage(host.Alice, "in-flight")).Build();
        await ReturnRouteTestHost.SendAsync(first, await host.AuthcryptAsync(host.Alice, inFlight));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Seen.Any(r => r.Message.Id == inFlight.Id), Timeout))
            .Should().BeTrue("the loop keeps reading until the peer's close frame arrives");
        host.Recorder.Seen.Single(r => r.Message.Id == inFlight.Id).Session.Should().BeNull("the evicted session is gone");
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == 1, Timeout)).Should().BeTrue("the handshake completed and the connection ended");

        host.Registry.Find(host.Alice.Did).Should().ContainSingle();
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversize_message_from_a_peer_that_never_answers_the_1009_close_ends_the_connection_within_the_grace()
    {
        // QA #91 (item 4): the receive loop's final CloseAsync waits for the peer's close frame; it
        // used to wait without bound (CancellationToken.None), so a peer that sends one oversize
        // message and then never reads nor closes held the request — and its buffers — forever.
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.MaxReceiveBytes = 1024;
        });
        using var socket = await host.ConnectAsync();
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.ActiveWebSocketConnections == 1, Timeout)).Should().BeTrue();

        var started = DateTime.UtcNow;
        await socket.SendAsync(new byte[4096], WebSocketMessageType.Binary, endOfMessage: true, default);

        // The peer neither reads the 1009 close frame nor answers it.
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == 1, WebSocketReturnRouteSessionGrace + Timeout))
            .Should().BeTrue("the final close handshake is bounded by the close grace and then aborted");
        (DateTime.UtcNow - started).Should().BeLessThan(WebSocketReturnRouteSessionGrace + TimeSpan.FromSeconds(3));
        host.Registry.Count.Should().Be(0);
        host.LeakingLogLines().Should().BeEmpty();
    }

    public static TheoryData<EnvelopeShape, string> EligibilityMatrix() => ReturnRouteHttpTests.EligibilityMatrix();

    [Theory]
    [MemberData(nameof(EligibilityMatrix))]
    public async Task Eligibility_matrix_only_an_authcrypted_all_message_opens_a_session_on_the_socket(EnvelopeShape shape, string header)
    {
        // FR-RR-03 / FR-RR-05 on a WebSocket with the switch on: only an authcrypt layer + `all`
        // opens a session. The handler returns a reply to the sender for every inbound; a sentinel
        // (authcrypt + all from Alice) follows on the same socket, so the first frame is the case's
        // reply when it opened a session and the sentinel's reply otherwise.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var thread = "thr-" + Guid.NewGuid().ToString("N");
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));

        var inbound = ReturnRouteHttpTests.WithHeader(host.Stamped(host.RecordingMessage(host.Alice, "matrix")), header, thread);
        var (packed, _) = await host.PackAsync(host.Alice, inbound, shape);
        var sentinel = host.Stamped(host.RecordingMessage(host.Alice, "sentinel").WithReturnRoute(ReturnRouteMode.All)).Build();
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, sentinel));

        var authcrypted = shape is EnvelopeShape.Authcrypt or EnvelopeShape.AnoncryptOfAuthcrypt or EnvelopeShape.AuthcryptOfSigned;
        var expectSession = authcrypted && header == "all";
        var first = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        first.Type.Should().Be(WebSocketMessageType.Binary);
        (await host.Alice.Client.UnpackAsync(first.Text)).Message.Thid
            .Should().Be(expectSession ? inbound.Id : sentinel.Id, "{0} + {1} {2} a session", shape, header, expectSession ? "opens" : "opens no");

        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 2, Timeout)).Should().BeTrue();
        var seen = host.Recorder.Seen.Single(r => r.Message.Id == inbound.Id);
        (seen.Session is not null).Should().Be(expectSession);
        seen.ReturnRoute.Mode.Should().Be(header switch { "all" => ReturnRouteMode.All, "thread" => ReturnRouteMode.Thread, _ => ReturnRouteMode.None });
        host.Registry.Find(host.Alice.Did).Should().ContainSingle("the sentinel or the case opened exactly one session");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines(thread).Should().BeEmpty();
    }

    [Fact]
    public async Task A_later_header_less_message_keeps_pushes_none_stops_them_all_resumes_and_thread_downgrades()
    {
        // FR-RR-05: the mode changes only for a message carrying return_route; header-less leaves it.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var thread = "thr-" + Guid.NewGuid().ToString("N");
        host.Recorder.Behavior = (message, _) => (new[] { PushTo(host, host.Alice.Did, "push", message.Id) }, null);
        using var socket = await host.ConnectAsync();

        async Task<RecordedInbound> SendAsync(MessageBuilder builder)
        {
            var message = host.Stamped(builder).Build();
            await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, message));
            (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Seen.Any(r => r.Message.Id == message.Id), Timeout)).Should().BeTrue();
            return host.Recorder.Seen.Single(r => r.Message.Id == message.Id);
        }

        async Task ExpectPushForAsync(RecordedInbound inbound)
        {
            var frame = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
            frame.Type.Should().Be(WebSocketMessageType.Binary);
            (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Thid.Should().Be(inbound.Message.Id, "no earlier push leaked onto the socket");
        }

        var opened = await SendAsync(host.RecordingMessage(host.Alice, "open").WithReturnRoute(ReturnRouteMode.All));
        opened.PushResults.Should().Equal(true);
        await ExpectPushForAsync(opened);
        var session = opened.Session!;

        var headerless = await SendAsync(host.RecordingMessage(host.Alice, "headerless"));
        headerless.ReturnRoute.Mode.Should().Be(ReturnRouteMode.None, "the context shows this message's own (absent) header");
        headerless.Session.Should().BeSameAs(session);
        headerless.PushResults.Should().Equal(new[] { true }, "a header-less message leaves the session in all");
        await ExpectPushForAsync(headerless);

        var none = await SendAsync(host.RecordingMessage(host.Alice, "none").WithReturnRoute(ReturnRouteMode.None));
        none.PushResults.Should().Equal(new[] { false }, "an explicit none downgrades the session");
        session.Mode.Should().Be(ReturnRouteMode.None);
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(session, "a downgraded session stays registered");

        var resumed = await SendAsync(host.RecordingMessage(host.Alice, "resume").WithReturnRoute(ReturnRouteMode.All));
        resumed.PushResults.Should().Equal(true);
        await ExpectPushForAsync(resumed); // the first frame after the downgrade is this one: nothing was written for `none`

        var threaded = await SendAsync(host.RecordingMessage(host.Alice, "thread").WithReturnRoute(ReturnRouteMode.Thread, thread));
        threaded.ReturnRoute.Mode.Should().Be(ReturnRouteMode.Thread);
        threaded.ReturnRoute.Thread.Should().Be(thread);
        threaded.PushResults.Should().Equal(new[] { false }, "thread on a session downgrades it (JC-20)");
        session.Mode.Should().Be(ReturnRouteMode.None);
        session.Thread.Should().Be(thread, "the thread is surfaced on the session");

        var again = await SendAsync(host.RecordingMessage(host.Alice, "again").WithReturnRoute(ReturnRouteMode.All));
        await ExpectPushForAsync(again);
        session.Thread.Should().BeNull();

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines(thread).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_sockets_for_one_DID_are_two_live_sessions_each_receiving_its_own_pushes()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        using var first = await OpenSessionAsync(host, host.Alice);
        using var second = await OpenSessionAsync(host, host.Alice);

        var sessions = host.Registry.Find(host.Alice.Did);
        sessions.Should().HaveCount(2, "two sockets for one DID are two sessions (FR-RR-05)");
        sessions.Should().AllSatisfy(s =>
        {
            s.IsLive.Should().BeTrue();
            s.Transport.Should().Be(ReturnRouteTransport.WebSocket);
            s.PeerDid.Should().Be(host.Alice.Did);
        });

        // Find is oldest first: sessions[0] belongs to the first socket.
        var toFirst = PushTo(host, host.Alice.Did, "to-first", "t1");
        var toSecond = PushTo(host, host.Alice.Did, "to-second", "t2");
        (await sessions[0].TrySendAsync(toFirst, default)).Should().BeTrue();
        (await sessions[1].TrySendAsync(toSecond, default)).Should().BeTrue();
        (await host.Alice.Client.UnpackAsync((await ReturnRouteTestHost.ReceiveAsync(first, Timeout)).Text)).Message.Id.Should().Be(toFirst.Id);
        (await host.Alice.Client.UnpackAsync((await ReturnRouteTestHost.ReceiveAsync(second, Timeout)).Text)).Message.Id.Should().Be(toSecond.Id);

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task At_the_total_bound_another_DID_is_dispatched_without_a_session_and_only_the_socket_pair_may_retry()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s =>
            {
                s.MaxSessions = 1;
                s.MaxSessionsPerPeer = 1;
            };
        });
        var aliceSocket = await OpenSessionAsync(host, host.Alice);
        using var shared = await host.ConnectAsync();

        var carolFirst = await SendAndRecordAsync(host, shared, host.Carol, ReturnRouteMode.All);
        carolFirst.Session.Should().BeNull("the registry is full: dispatched, no session (FR-RR-03)");
        host.Registry.Count.Should().Be(1);
        host.Logs.Entries.Should().Contain(e => e.Contains(carolFirst.Message.Id) && e.Contains(nameof(SessionRegistrationOutcome.RefusedTotalBound)));

        // The socket's pair is now fixed to Carol: Mallory on the same socket never opens a session.
        (await SendAndRecordAsync(host, shared, host.Mallory, ReturnRouteMode.All)).Session.Should().BeNull();

        await aliceSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        aliceSocket.Dispose();
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();

        var carolRetry = await SendAndRecordAsync(host, shared, host.Carol, ReturnRouteMode.All);
        carolRetry.Session.Should().NotBeNull("the socket's own pair may retry once there is room");
        carolRetry.Session!.PeerDid.Should().Be(host.Carol.Did);
        (await SendAndRecordAsync(host, shared, host.Mallory, ReturnRouteMode.All)).Session.Should().BeNull("another DID never re-binds the socket");
        host.Registry.Find(host.Mallory.Did).Should().BeEmpty();

        await shared.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Another_DID_on_a_session_socket_is_dispatched_without_a_session_and_its_reply_is_not_written()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        using var socket = await OpenSessionAsync(host, host.Alice);
        var aliceSession = host.Registry.Find(host.Alice.Did).Single();
        host.Recorder.Behavior = (message, _) => (
            new[] { PushTo(host, message.From!, "push", message.Id) },
            ReturnRouteHttpTests.ReplyTo(host, message, message.From!));

        var carol = await SendAndRecordAsync(host, socket, host.Carol, ReturnRouteMode.All);
        carol.Session.Should().BeNull("the session peer is fixed at creation (FR-RR-05)");
        carol.PushResults.Should().Equal(false);
        aliceSession.Mode.Should().Be(ReturnRouteMode.All, "Carol's header never touches Alice's session");
        aliceSession.PeerDid.Should().Be(host.Alice.Did);
        host.Registry.Find(host.Carol.Did).Should().BeEmpty();

        var alice = await SendAndRecordAsync(host, socket, host.Alice, mode: null);
        alice.Session.Should().BeSameAs(aliceSession);
        var frame = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Thid.Should().Be(alice.Message.Id, "Carol's reply was never written");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_switch_off_no_session_opens_nothing_is_written_and_replay_still_drops_a_duplicate()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = false);
        host.Recorder.Behavior = (message, _) => (
            new[] { PushTo(host, host.Alice.Did, "push", message.Id) },
            ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));

        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReturnRouteTestHost.SendAsync(socket, packed);
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);

        host.Recorder.Invocations.Should().Be(1, "FR-RR-08 applies with the switch off too");
        var seen = host.Recorder.Seen.Single();
        seen.Session.Should().BeNull();
        seen.PushResults.Should().Equal(false);
        host.Registry.Count.Should().Be(0);
        host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Under_STOMP_no_session_opens_nothing_is_pushed_and_the_notice_is_logged_once_per_connection()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.UseStomp = true;
        });
        host.Recorder.Behavior = (message, _) => (
            new[] { PushTo(host, host.Alice.Did, "push", message.Id) },
            ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        const string Notice = "is not available under STOMP";

        for (var connection = 1; connection <= 2; connection++)
        {
            using var socket = await host.ConnectAsync();
            await SendStompAsync(socket, new StompFrame("CONNECT", new[]
            {
                KeyValuePair.Create("accept-version", "1.2"),
                KeyValuePair.Create("host", "localhost"),
            }, ReadOnlyMemory<byte>.Empty));
            StompFrameCodec.Decode(await ReceiveBytesAsync(socket)).Command.Should().Be("CONNECTED");

            for (var i = 0; i < 2; i++)
            {
                var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
                await SendStompAsync(socket, new StompFrame("SEND", new[]
                {
                    KeyValuePair.Create("destination", "/ws"),
                    KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
                }, Encoding.UTF8.GetBytes(await host.AuthcryptAsync(host.Alice, inbound))));
            }

            await SendStompAsync(socket, new StompFrame("DISCONNECT", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty));
            var closing = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
            closing.Type.Should().Be(WebSocketMessageType.Close, "nothing was pushed before the DISCONNECT close");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);

            host.Recorder.Count.Should().Be(2 * connection);
            host.Logs.Entries.Count(e => e.Contains(Notice)).Should().Be(connection, "exactly one notice per connection");
        }

        host.Recorder.Seen.Should().AllSatisfy(r =>
        {
            r.Session.Should().BeNull("STOMP never gets a session (FR-RR-05)");
            r.PushResults.Should().Equal(false);
        });
        host.Registry.Count.Should().Be(0);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_header_less_ping_on_a_fresh_socket_gets_no_frame()
    {
        // JC-3: absent header = none (FR-RR-01), and a session starts only at an eligible message.
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var ping = TrustPing.CreatePing(host.Alice.Did, host.ServerDid);
        using var socket = await host.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, ping));
        await ReplayProtectionEndpointTests.AssertNothingWrittenThenCloseAsync(socket);

        host.Logs.Entries.Should().Contain(e => e.Contains("Protocol dispatch produced a reply") && e.Contains("returned-on-session=False"));
        host.Registry.Count.Should().Be(0);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- concurrency --------------------------------------------------------------------------

    [Fact]
    public async Task Many_concurrent_sessions_for_distinct_DIDs_each_receive_only_their_own_pushes()
    {
        const int Peers = 8;
        const int PushesEach = 3;
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var peers = await host.CreatePeersAsync(Peers);
        host.Recorder.Behavior = (message, _) => (new[] { PushTo(host, message.From!, "welcome", message.Id) }, null);

        // Open all sessions at once.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = peers.Select(async peer =>
        {
            var open = host.Stamped(host.RecordingMessage(peer, "open").WithReturnRoute(ReturnRouteMode.All)).Build();
            var packed = await host.AuthcryptAsync(peer, open);
            var socket = await host.ConnectAsync();
            await start.Task;
            await ReturnRouteTestHost.SendAsync(socket, packed);
            return socket;
        }).ToList();
        start.SetResult();
        var sockets = await Task.WhenAll(opening);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == Peers, Timeout)).Should().BeTrue();
        host.Registry.Count.Should().Be(Peers);

        // Then push to every peer at once, from host code that finds each session by DID.
        var inside = 0;
        var maxInside = 0;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushes = peers.Select((peer, index) => Task.Run(async () =>
        {
            await go.Task;
            var session = host.Registry.Find(peer.Did).Single();
            for (var i = 0; i < PushesEach; i++)
            {
                var now = Interlocked.Increment(ref inside);
                int seen;
                while ((seen = Volatile.Read(ref maxInside)) < now && Interlocked.CompareExchange(ref maxInside, now, seen) != seen)
                {
                }
                try
                {
                    var message = new MessageBuilder()
                        .WithType(RecordingHandler.ProtocolUri + "/fan-out")
                        .WithFrom(host.ServerDid)
                        .WithTo(peer.Did)
                        .WithBody(new System.Text.Json.Nodes.JsonObject { ["peer"] = index, ["n"] = i })
                        .Build();
                    (await session.TrySendAsync(message, default)).Should().BeTrue();
                }
                finally
                {
                    Interlocked.Decrement(ref inside);
                }
            }
        })).ToList();
        go.SetResult();
        await Task.WhenAll(pushes).WaitAsync(Timeout);
        maxInside.Should().BeGreaterThan(1, "the pushes overlapped (L-044)");

        for (var p = 0; p < Peers; p++)
        {
            var welcome = await peers[p].Client.UnpackAsync((await ReturnRouteTestHost.ReceiveAsync(sockets[p], Timeout)).Text);
            welcome.Message.Type.Should().EndWith("/welcome");
            welcome.Message.To.Should().Equal(peers[p].Did);
            for (var i = 0; i < PushesEach; i++)
            {
                var frame = await ReturnRouteTestHost.ReceiveAsync(sockets[p], Timeout);
                var unpacked = await peers[p].Client.UnpackAsync(frame.Text);
                unpacked.Message.To.Should().Equal(new[] { peers[p].Did }, "every frame on a socket is addressed to that socket's peer");
                unpacked.Message.Body!["peer"]!.GetValue<int>().Should().Be(p, "no cross-delivery");
                unpacked.Message.Body!["n"]!.GetValue<int>().Should().Be(i, "per-session order is preserved");
                unpacked.RecipientKid.Should().StartWith(peers[p].Did + "#");
            }
        }

        foreach (var socket in sockets)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
            socket.Dispose();
        }
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0 && host.EndedWebSocketConnections == Peers, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Two_sockets_for_one_DID_registering_concurrently_at_a_per_peer_bound_of_one_leave_exactly_one_session()
    {
        // Both registrations are parked in the registry until both arrived, then race. Invariant for
        // every interleaving: one session survives, the other socket observes a normal close, one
        // SessionClosed(Evicted) for the loser, and after both sockets close the registry is empty,
        // both connections ended and neither writer task is left running.
        RendezvousSessionRegistry? registry = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s => s.MaxSessionsPerPeer = 1;
            o.ConfigureServices = services => services.AddSingleton<IReturnRouteSessionRegistry>(_ => registry = new RendezvousSessionRegistry(
                new InMemoryReturnRouteSessionRegistry(new ReturnRouteSessionOptions { MaxSessionsPerPeer = 1 }),
                new Rendezvous(2, Timeout)));
        });
        var closed = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionClosed += (_, e) => closed.Enqueue(e);

        using var a = await host.ConnectAsync();
        using var b = await host.ConnectAsync();
        var openA = host.Stamped(host.RecordingMessage(host.Alice, "open-a").WithReturnRoute(ReturnRouteMode.All)).Build();
        var openB = host.Stamped(host.RecordingMessage(host.Alice, "open-b").WithReturnRoute(ReturnRouteMode.All)).Build();
        var packedA = await host.AuthcryptAsync(host.Alice, openA);
        var packedB = await host.AuthcryptAsync(host.Alice, openB);
        await Task.WhenAll(ReturnRouteTestHost.SendAsync(a, packedA), ReturnRouteTestHost.SendAsync(b, packedB));

        (await ReturnRouteTestHost.WaitUntilAsync(() => registry!.Registrations.Count == 2, Timeout)).Should().BeTrue();
        registry!.Rendezvous.MaxConcurrent.Should().Be(2, "both registrations were inside the registry together (L-044)");
        registry.Registrations.Should().OnlyContain(r => r.Result.Outcome == SessionRegistrationOutcome.Registered);
        registry.Registrations.Count(r => r.Result.Evicted is not null).Should().Be(1, "exactly one registration evicted");
        var survivor = host.Registry.Find(host.Alice.Did).Should().ContainSingle().Subject;
        var loser = registry.Registrations.Select(r => r.Session).Single(s => !ReferenceEquals(s, survivor));

        // The loser's socket observes the eviction as a normal close; the survivor's does not.
        var receiveA = ReturnRouteTestHost.ReceiveAsync(a, Timeout);
        var receiveB = ReturnRouteTestHost.ReceiveAsync(b, Timeout);
        var firstDone = await Task.WhenAny(receiveA, receiveB);
        var (evictedSocket, survivorSocket, survivorReceive) = firstDone == receiveA ? (a, b, receiveB) : (b, a, receiveA);
        (await firstDone).Type.Should().Be(WebSocketMessageType.Close);
        evictedSocket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        evictedSocket.CloseStatusDescription.Should().Be(WebSocketReturnRouteSession.EvictedDescription);
        await evictedSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);

        var probe = PushTo(host, host.Alice.Did, "probe", "p");
        (await survivor.TrySendAsync(probe, default)).Should().BeTrue();
        var frame = await survivorReceive;
        frame.Type.Should().Be(WebSocketMessageType.Binary, "the surviving socket is still serving");
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Id.Should().Be(probe.Id);

        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == 1, Timeout)).Should().BeTrue("the evicted connection ended");
        // Registry events are raised on the thread pool after the call, so they are awaited (bounded).
        (await ReturnRouteTestHost.WaitUntilAsync(() => !closed.IsEmpty, Timeout)).Should().BeTrue("the eviction raises SessionClosed");
        closed.Should().ContainSingle().Which.Should().Match<ReturnRouteSessionEventArgs>(e => e.Reason == SessionCloseReason.Evicted && ReferenceEquals(e.Session, loser));

        await survivorSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == 2, Timeout)).Should().BeTrue();
        host.Registry.Count.Should().Be(0);
        (await ReturnRouteTestHost.WaitUntilAsync(() => closed.Count >= 2, Timeout)).Should().BeTrue("the survivor's close raises SessionClosed");
        closed.Should().HaveCount(2);
        foreach (var session in new[] { survivor, loser }.Cast<WebSocketReturnRouteSession>())
        {
            session.WriterStopped.Should().BeTrue("no writer task outlives its connection");
            session.Closed.IsCancellationRequested.Should().BeTrue();
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_handler_pushing_while_the_peer_closes_the_socket_never_faults()
    {
        // A background push loop keeps pushing through context.Session while the client closes the
        // socket. No exception may escape TrySendAsync (it answers false once the session closed),
        // and no task the library started may end with an unobserved exception.
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                if (inner.ToString().Contains("DidComm.AspNetCore", StringComparison.Ordinal))
                    unobserved.Enqueue(inner);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await PushWhileClosingAsync();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            unobserved.Should().BeEmpty("no library task ends with an unobserved exception");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task PushWhileClosingAsync()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var pusherResults = new ConcurrentQueue<(int Seq, bool Result, bool ClosedBefore)>();
        Task? pusher = null;
        host.Recorder.Behavior = (message, context) =>
        {
            var session = context.Session!;
            pusher = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow + Timeout;
                for (var seq = 0; DateTime.UtcNow < deadline; seq++)
                {
                    var closedBefore = session.Closed.IsCancellationRequested;
                    var ok = await session.TrySendAsync(PushTo(host, host.Alice.Did, "bg-" + seq, message.Id), CancellationToken.None);
                    pusherResults.Enqueue((seq, ok, closedBefore));
                    if (closedBefore)
                        return;
                    if (!ok)
                        await Task.Yield();
                }
            });
            return (Array.Empty<Message>(), null);
        };

        var socket = await host.ConnectAsync();
        var open = host.Stamped(host.RecordingMessage(host.Alice, "open").WithReturnRoute(ReturnRouteMode.All)).Build();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, open));
        for (var i = 0; i < 3; i++)
            (await ReturnRouteTestHost.ReceiveAsync(socket, Timeout)).Type.Should().Be(WebSocketMessageType.Binary, "the pusher is live");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing under load", default).WaitAsync(Timeout);
        socket.Dispose();

        (await ReturnRouteTestHost.WaitUntilAsync(() => pusher is { IsCompleted: true }, Timeout)).Should().BeTrue();
        pusher!.Status.Should().Be(TaskStatus.RanToCompletion, "TrySendAsync never throws on a closing session");
        pusherResults.Should().Contain(r => r.Result, "pushes succeeded before the close");
        pusherResults.Should().Contain(r => r.ClosedBefore && !r.Result, "the pusher was still pushing when the session closed (overlap) and got false");
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == 1, Timeout)).Should().BeTrue();
        host.Registry.Count.Should().Be(0);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Pushes_racing_a_none_are_refused_from_the_moment_the_none_is_processed()
    {
        // Judgment call 3 end to end: a host-side pusher keeps pushing through Find(alice) while Alice
        // sends `none`. The receive loop applies the downgrade before it dispatches the message, so
        // every push started after the handler saw the `none` answers false; the session stays
        // registered; the next `all` resumes. (The writer-level guarantee — no send begins once the
        // downgrade was applied — is pinned against a fake socket in ReturnRouteSessionTests.)
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        using var socket = await OpenSessionAsync(host, host.Alice);
        var session = host.Registry.Find(host.Alice.Did).Single();
        var none = host.Stamped(host.RecordingMessage(host.Alice, "none").WithReturnRoute(ReturnRouteMode.None)).Build();
        var processed = 0;
        host.Recorder.OnEnter = message =>
        {
            if (message.Id == none.Id)
                Volatile.Write(ref processed, 1);
        };

        var frames = new ConcurrentQueue<string>();
        var reader = Task.Run(async () =>
        {
            while (true)
            {
                var (type, text) = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
                if (type == WebSocketMessageType.Close)
                    return;
                frames.Enqueue(text);
            }
        });

        var results = new ConcurrentQueue<(bool After, bool Result)>();
        using var stop = new CancellationTokenSource();
        var pusher = Task.Run(async () =>
        {
            for (var seq = 0; !stop.IsCancellationRequested; seq++)
            {
                var after = Volatile.Read(ref processed) == 1;
                results.Enqueue((after, await session.TrySendAsync(PushTo(host, host.Alice.Did, "race-" + seq, "race"), CancellationToken.None)));
                await Task.Yield();
            }
        });

        (await ReturnRouteTestHost.WaitUntilAsync(() => frames.Count >= 2, Timeout)).Should().BeTrue("the pusher is live before the none");
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, none));
        (await ReturnRouteTestHost.WaitUntilAsync(() => results.Count(r => r.After) >= 5, Timeout)).Should().BeTrue("the pusher kept pushing after the none (overlap)");
        stop.Cancel();
        await pusher.WaitAsync(Timeout);

        results.Where(r => r.After).Should().AllSatisfy(r => r.Result.Should().BeFalse("a push started after the none was processed is refused"));
        results.Should().Contain(r => !r.After && r.Result);
        session.Mode.Should().Be(ReturnRouteMode.None);
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(session);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        await reader.WaitAsync(Timeout);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Connect and open a session for <paramref name="peer"/> (authcrypt, `all`, stamped); waits until it was dispatched.</summary>
    private static async Task<WebSocket> OpenSessionAsync(ReturnRouteTestHost host, TestPeer peer)
    {
        var socket = await host.ConnectAsync();
        await SendAndRecordAsync(host, socket, peer, ReturnRouteMode.All, "open");
        return socket;
    }

    /// <summary>Send a stamped recording message from <paramref name="peer"/> on <paramref name="socket"/> (optionally with a header) and return what the handler saw.</summary>
    private static async Task<RecordedInbound> SendAndRecordAsync(ReturnRouteTestHost host, WebSocket socket, TestPeer peer, ReturnRouteMode? mode, string name = "probe")
    {
        var builder = host.Stamped(host.RecordingMessage(peer, name));
        if (mode is { } m)
            builder.WithReturnRoute(m);
        var message = builder.Build();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(peer, message));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Seen.Any(r => r.Message.Id == message.Id), Timeout)).Should().BeTrue();
        return host.Recorder.Seen.Single(r => r.Message.Id == message.Id);
    }

    private static Task SendStompAsync(WebSocket socket, StompFrame frame)
        => socket.SendAsync(StompFrameCodec.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, default);

    private static async Task<byte[]> ReceiveBytesAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return ms.ToArray();
    }

    private static Message PushTo(ReturnRouteTestHost host, string to, string name, string thid)
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/" + name)
            .WithFrom(host.ServerDid)
            .WithTo(to)
            .WithThid(thid)
            .Build();

    private static readonly TimeSpan WebSocketReturnRouteSessionGrace = DidComm.AspNetCore.WebSocketReturnRouteSession.DefaultCloseGrace;

    private static Message Push(ReturnRouteTestHost host, string name, string thid)
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/" + name)
            .WithFrom(host.ServerDid)
            .WithTo(host.Alice.Did)
            .WithThid(thid)
            .Build();
}
