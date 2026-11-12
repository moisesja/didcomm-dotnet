using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.Messages;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #92 G8 — the WebSocket transport's return-route session sockets S(E, L, P) against a scripted
/// peer (<see cref="ScriptedSessionWebSocket"/>): keying, the send gate shared by every send on S,
/// re-routing of a send queued behind a retiring S, a fresh S for an <c>all</c> queued behind one,
/// parallel opens for two local DIDs, the send-failure path, the 1009 cap, a consumer that throws,
/// a bounded dispose with live and retiring sockets, and the constructor / request validation.
/// Every wait is a rendezvous or a bounded condition; nothing sleeps for a fixed time (L-044).
/// </summary>
public sealed class WebSocketSessionSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri Endpoint = new("ws://agents.r.us/ws");
    private const string Alice = "did:example:alice";
    private const string Carol = "did:example:carol";
    private const string Bob = "did:example:bob";

    [Fact]
    public async Task Parallel_sends_on_one_session_socket_never_overlap()
    {
        await using var t = Harness.Create();
        t.Sockets.OnCreate = s => s.HoldSends();

        var first = t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default);
        var socket = await WaitForSocketAsync(t, 0);
        await socket.SendEntered.WaitAsync(Timeout);

        // Started while the first send holds S's gate: each routes to S (it is published) and parks there.
        var queued = Enumerable.Range(0, 8).Select(i => t.Transport.SendAsync(Request("plain-" + i), default)).ToArray();
        queued.Should().OnlyContain(q => !q.IsCompleted, "every send to (E, L, P) waits for S's gate while the first send holds it");
        socket.SendsInFlight.Should().Be(1);

        socket.ReleaseSends();
        await first.WaitAsync(Timeout);
        await Task.WhenAll(queued).WaitAsync(Timeout);

        t.Sockets.Count.Should().Be(1, "all nine sends used the one session socket");
        socket.Sent.Should().HaveCount(9);
        socket.MaxSendsInFlight.Should().Be(1, "a WebSocket allows one outstanding send; the session gate serializes them");
        Text(socket.Sent[0]).Should().Be("all");
    }

    [Fact]
    public async Task A_send_queued_behind_a_retiring_session_re_routes_to_the_pool()
    {
        await using var t = Harness.Create();
        bool? sessionClosedWhenPoolOpened = null;
        t.Sockets.OnCreate = s =>
        {
            if (s.Index == 1)
                sessionClosedWhenPoolOpened = t.Sockets.Sockets[0].CloseOutputStatus is not null;
        };
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
        var session = t.Sockets.Sockets[0];

        session.HoldSends();
        var none = t.Transport.SendAsync(Request("none", ReturnRouteMode.None), default);
        await session.SendEntered.WaitAsync(Timeout);
        // S is still active, so this header-less send routes to it and parks on its gate.
        var queued = t.Transport.SendAsync(Request("plain"), default);
        queued.IsCompleted.Should().BeFalse("it waits on S's gate, behind the none");

        session.ReleaseSends();
        (await none.WaitAsync(Timeout)).Accepted.Should().BeTrue();
        (await queued.WaitAsync(Timeout)).Accepted.Should().BeTrue();

        t.Sockets.Count.Should().Be(2, "the queued send found S retired and used a pooled socket");
        sessionClosedWhenPoolOpened.Should().BeTrue("the pooled socket was opened after S retired, so the send really was queued behind S");
        session.Sent.Select(Text).Should().Equal("all", "none");
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        session.CloseOutputDescription.Should().Be("return-route session ended");
        t.Sockets.Sockets[1].Sent.Select(Text).Should().Equal("plain");
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue("the peer answered the close, so the retired socket's loop ended");
    }

    [Fact]
    public async Task An_All_queued_behind_a_retiring_session_opens_a_fresh_socket()
    {
        await using var t = Harness.Create();
        t.Sockets.OnCreate = s => s.AnswerCloseOutput = false; // the old socket keeps draining after its close output
        await t.Transport.SendAsync(Request("all-1", ReturnRouteMode.All), default).WaitAsync(Timeout);
        var old = t.Sockets.Sockets[0];

        old.HoldSends();
        var none = t.Transport.SendAsync(Request("none", ReturnRouteMode.None), default);
        await old.SendEntered.WaitAsync(Timeout);
        var queued = t.Transport.SendAsync(Request("all-2", ReturnRouteMode.All), default);
        queued.IsCompleted.Should().BeFalse("it waits on the session gate, behind the none");

        old.ReleaseSends();
        await none.WaitAsync(Timeout);
        await queued.WaitAsync(Timeout);

        t.Sockets.Count.Should().Be(2);
        var fresh = t.Sockets.Sockets[1];
        old.Sent.Select(Text).Should().Equal("all-1", "none");
        fresh.Sent.Select(Text).Should().Equal("all-2");
        old.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        t.Transport.ActiveSessionSocketCount.Should().Be(1, "the fresh socket is the session now");
        t.Transport.LiveSessionSocketCount.Should().Be(2, "the retired socket is still reading");

        // A message already on its way on the retired socket is still surfaced, with its own attribution.
        old.PushMessage(Utf8("late"));
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Consumer.Calls == 1, Timeout)).Should().BeTrue();
        t.Consumer.Frames.Single().Should().Match<InboundFrame>(f => Text(f.Payload) == "late" && f.LocalDid == Alice && f.PeerDid == Bob && f.Endpoint == Endpoint);

        old.PushClose();
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 1, Timeout)).Should().BeTrue();
        old.Disposed.Should().BeTrue();
        t.Disconnected.Should().ContainSingle("the retired socket reported its end once").Which.Should().Be(Endpoint);
        fresh.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task Two_local_DIDs_racing_All_open_two_sockets()
    {
        var rendezvous = new Rendezvous(2, Timeout);
        await using var t = Harness.Create();
        t.Sockets.Around = make => rendezvous.Run(make);

        var sends = new[] { Alice, Carol }
            .Select(local => Task.Run(() => t.Transport.SendAsync(Request("all", ReturnRouteMode.All, local), default)))
            .ToArray();
        await Task.WhenAll(sends).WaitAsync(Timeout);

        rendezvous.MaxConcurrent.Should().Be(2, "the two opens overlapped: one gate per (E, L, P), not per endpoint");
        t.Sockets.Count.Should().Be(2);
        t.Transport.ActiveSessionSocketCount.Should().Be(2);
    }

    [Fact]
    public async Task A_session_send_failure_removes_it_and_the_retry_re_routes()
    {
        await using var t = Harness.Create(o =>
        {
            o.MaxReconnectAttempts = 1;
            o.ReconnectBaseDelay = TimeSpan.FromMilliseconds(1);
            o.ReconnectMaxDelay = TimeSpan.FromMilliseconds(1);
        });
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default).WaitAsync(Timeout);
        var session = t.Sockets.Sockets[0];
        t.Events.Clear();

        session.FailNextSend();
        (await t.Transport.SendAsync(Request("plain"), default).WaitAsync(Timeout)).Accepted.Should().BeTrue("the retry succeeded");

        session.Disposed.Should().BeTrue("a failed send drops S");
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.Sockets.Count.Should().Be(2, "a header-less retry re-routes to a pooled socket");
        t.Sockets.Sockets[1].Sent.Select(Text).Should().Equal("plain");
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
        // Who reports S's end (the failed send or its loop) is a race; that it is reported once is not.
        t.Events.Where(e => e == WebSocketLifecycleEventKind.Disconnected).Should().ContainSingle();
        t.Events.Where(e => e == WebSocketLifecycleEventKind.SendFailed).Should().ContainSingle();
        t.Events.Where(e => e == WebSocketLifecycleEventKind.Reconnected).Should().ContainSingle();

        // An all after the failure opens a fresh session socket.
        await t.Transport.SendAsync(Request("all-again", ReturnRouteMode.All), default).WaitAsync(Timeout);
        t.Sockets.Count.Should().Be(3);
        t.Sockets.Sockets[2].Sent.Select(Text).Should().Equal("all-again");
    }

    [Fact]
    public async Task Session_sockets_are_keyed_by_endpoint_local_and_peer()
    {
        await using var t = Harness.Create();
        var other = new Uri("ws://agents.r.us/other");

        await t.Transport.SendAsync(Request("a", ReturnRouteMode.All), default);
        await t.Transport.SendAsync(Request("b", ReturnRouteMode.All, local: Carol), default);
        await t.Transport.SendAsync(Request("c", ReturnRouteMode.All, peer: Carol), default);
        await t.Transport.SendAsync(Request("d", ReturnRouteMode.All, endpoint: other), default);
        t.Sockets.Count.Should().Be(4, "each (endpoint, local DID, peer DID) has its own session socket");

        await t.Transport.SendAsync(Request("e"), default);
        await t.Transport.SendAsync(Request("f", ReturnRouteMode.Thread), default);
        t.Sockets.Count.Should().Be(4, "later sends to (E, L, P) reuse its socket, with or without a header");
        t.Sockets.Sockets[0].Sent.Select(Text).Should().Equal("a", "e", "f");

        // Without both identities a send never touches a session socket.
        await t.Transport.SendAsync(new TransportRequest(Endpoint, Utf8("g"), MediaTypes.Encrypted) { ReturnRoute = ReturnRouteMode.All, LocalDid = Alice }, default);
        await t.Transport.SendAsync(new TransportRequest(Endpoint, Utf8("h"), MediaTypes.Encrypted), default);
        t.Sockets.Count.Should().Be(5);
        t.Sockets.Sockets[4].Sent.Select(Text).Should().Equal("g", "h");
        t.Transport.ActiveSessionSocketCount.Should().Be(4);
    }

    [Fact]
    public async Task An_oversized_push_closes_with_1009_and_buffers_at_most_cap_plus_one_read()
    {
        const int cap = 64 * 1024;
        await using var t = Harness.Create(maxReceiveBytes: cap);
        t.Sockets.OnCreate = s => s.AnswerCloseOutput = false;
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default);
        var session = t.Sockets.Sockets[0];

        session.PushFragments(new byte[cap * 3], fragmentSize: 8 * 1024);

        (await ReturnRouteTestHost.WaitUntilAsync(() => session.CloseOutputStatus is not null, Timeout)).Should().BeTrue();
        session.CloseOutputStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        session.BytesReadAtCloseOutput.Should().BeGreaterThan(cap).And.BeLessThanOrEqualTo(cap + session.LargestRead,
            "the loop stops at the read that crossed the cap: nothing past the cap plus one read was taken in");
        t.Transport.ActiveSessionSocketCount.Should().Be(0, "an oversized push retires S at once");

        // The rest of that message and every later one is read and dropped.
        session.PushMessage(Utf8("after"));
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 2, Timeout)).Should().BeTrue();
        t.Consumer.Calls.Should().Be(0);

        await t.Transport.SendAsync(Request("plain"), default);
        t.Sockets.Count.Should().Be(2, "a header-less send after the 1009 uses the pooled socket");

        session.PushClose();
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
        t.Disconnected.Should().ContainSingle().Which.Should().Be(Endpoint);
        var lines = t.Logs.Records.Where(r => r.Text.Contains("exceeded", StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        lines[0].Text.Should().Contain(cap.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.NotContain("ws://");
    }

    [Fact]
    public async Task A_consumer_that_throws_does_not_end_the_loop()
    {
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        await using var t = Harness.Create();
        t.Consumer.Behavior = (frame, n, ct) => n switch
        {
            1 => throw new InvalidOperationException("consumer fault carrying did:example:leak"),
            2 => throw new OperationCanceledException("a cancellation that is not the loop's", foreign.Token),
            _ => ValueTask.CompletedTask,
        };
        await t.Transport.SendAsync(Request("all", ReturnRouteMode.All), default);
        var session = t.Sockets.Sockets[0];

        session.PushMessage(Utf8("one"));
        session.PushMessage(Utf8("two"));
        session.PushMessage(Utf8("three"));

        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Consumer.Calls == 3, Timeout)).Should().BeTrue("the loop kept reading after both faults");
        t.Consumer.Frames.Select(f => Text(f.Payload)).Should().Equal("one", "two", "three");
        t.Transport.ActiveSessionSocketCount.Should().Be(1, "S is still in service");
        t.Disconnected.Should().BeEmpty();

        var faults = t.Logs.Records.Where(r => r.Text.Contains("inbound consumer threw", StringComparison.Ordinal)).ToList();
        faults.Should().HaveCount(2);
        faults.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle("rate-limited: the first at Warning, the next at Debug")
            .Which.Text.Should().Contain(nameof(InvalidOperationException));
        faults[1].Text.Should().Contain(nameof(OperationCanceledException));
        t.Logs.Entries.Should().NotContain(l => l.Contains("did:example:leak", StringComparison.Ordinal), "the exception's message is never logged");

        await t.Transport.SendAsync(Request("plain"), default);
        session.Sent.Select(Text).Should().Equal("all", "plain");
    }

    [Fact]
    public async Task DisposeAsync_with_live_and_retiring_session_sockets_is_bounded_and_leaves_no_open_socket()
    {
        var connectTimeout = TimeSpan.FromMilliseconds(500);
        var sendTimeout = TimeSpan.FromSeconds(3);
        var e1 = new Uri("ws://agents.r.us/one");
        var e2 = new Uri("ws://agents.r.us/two");
        var e3 = new Uri("ws://agents.r.us/three");
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = Harness.Create(o =>
        {
            o.ConnectTimeout = connectTimeout;
            o.SendTimeout = sendTimeout;
        });
        try
        {
            t.Sockets.OnCreate = s => s.AnswerCloseOutput = false; // the peers never answer a close
            // The consumer ignores cancellation: it returns only when the test lets it.
            t.Consumer.Behavior = async (_, _, _) =>
            {
                parked.TrySetResult();
                await release.Task.ConfigureAwait(false);
            };

            await t.Transport.SendAsync(Request("s1", ReturnRouteMode.All, endpoint: e1), default);
            await t.Transport.SendAsync(Request("s2", ReturnRouteMode.All, endpoint: e2), default);
            await t.Transport.SendAsync(Request("s3", ReturnRouteMode.All, endpoint: e3), default);
            var (s1, s2, s3) = (t.Sockets.Sockets[0], t.Sockets.Sockets[1], t.Sockets.Sockets[2]);
            s1.CloseOutputHangs = true; // and these two never complete a close write either
            s3.CloseOutputHangs = true;
            s3.PushMessage(Utf8("parks the consumer"));
            await parked.Task.WaitAsync(Timeout);
            await t.Transport.SendAsync(Request("retire", ReturnRouteMode.None, endpoint: e2), default);

            t.Transport.ActiveSessionSocketCount.Should().Be(2, "s1 and s3 are active");
            t.Transport.LiveSessionSocketCount.Should().Be(3, "s2 is retiring: closed for sends, still reading (its backstop is one SendTimeout away)");
            t.Disconnected.Should().BeEmpty();

            var watch = Stopwatch.StartNew();
            await t.Transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            watch.Stop();

            var drainBudget = connectTimeout + sendTimeout;
            watch.Elapsed.Should().BeLessThan(drainBudget + sendTimeout + sendTimeout + TimeSpan.FromSeconds(3),
                "dispose is bounded by DrainBudget + 2 x CloseBudget even with hung closes and a consumer that ignores cancellation");
            new[] { s1, s2, s3 }.Should().OnlyContain(s => s.Disposed, "no socket survives dispose: active, retiring, or held by a consumer");
            t.Disconnected.Should().BeEquivalentTo(new[] { e1, e2, e3 }, "Disconnected names each socket's endpoint, once");

            // The abandoned loop ends once its consumer returns, and reports nothing more.
            release.TrySetResult();
            (await ReturnRouteTestHost.WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
            t.Disconnected.Should().HaveCount(3);
            await ((Func<Task>)(() => t.Transport.SendAsync(Request("late", ReturnRouteMode.All), default))).Should().ThrowAsync<ObjectDisposedException>();
            t.Sockets.Count.Should().Be(3, "a disposed transport opens nothing");
        }
        finally
        {
            release.TrySetResult();
            await t.DisposeAsync();
        }
    }

    [Fact]
    public void ReceiveInbound_without_a_consumer_fails_fast()
    {
        var options = Options.Create(new WebSocketTransportOptions { AllowedSchemes = new[] { "ws" }, ReceiveInbound = true });

        var threeParameter = () => new WebSocketDidCommTransport(options);
        var fourParameter = () => new WebSocketDidCommTransport(options, logger: null, coreOptions: null, inboundConsumer: null);

        threeParameter.Should().Throw<InvalidOperationException>().WithMessage("*UseInboundDispatch*");
        fourParameter.Should().Throw<InvalidOperationException>().WithMessage("*IInboundFrameConsumer*");
    }

    public static TheoryData<string> InvalidIdentityCases() => new(new[] { "empty", "not a DID", "bare prefix", "line feed", "space", "control", "2049 characters" });

    [Theory]
    [MemberData(nameof(InvalidIdentityCases))]
    public async Task Invalid_LocalDid_or_PeerDid_is_refused_without_echo(string @case)
    {
        var value = @case switch
        {
            "empty" => "",
            "not a DID" => "mailto:alice@example.com",
            "bare prefix" => "did:",
            "line feed" => "did:example:alice\ndid:example:mallory",
            "space" => "did:example:al ice",
            "control" => "did:example:al\u0001ice",
            "2049 characters" => "did:example:" + new string('a', 2049 - "did:example:".Length),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        await using var t = Harness.Create();

        foreach (var request in new[]
                 {
                     Request("x", ReturnRouteMode.All) with { LocalDid = value },
                     Request("x", ReturnRouteMode.All) with { PeerDid = value },
                 })
        {
            var thrown = (await ((Func<Task>)(() => t.Transport.SendAsync(request, default))).Should().ThrowAsync<ArgumentException>(@case)).Which;
            if (value.Length > "did:".Length)
                thrown.Message.Should().NotContain(value, "the value is not echoed");
            thrown.Message.Should().NotContain("mallory").And.NotContain("al ice");
        }
        t.Sockets.Count.Should().Be(0, "a refused request touches no socket");
    }

    // ---- helpers ----

    private static TransportRequest Request(string text, ReturnRouteMode? mode = null, string local = Alice, string peer = Bob, Uri? endpoint = null)
        => new(endpoint ?? Endpoint, Utf8(text), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = local, PeerDid = peer };

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    private static async Task<ScriptedSessionWebSocket> WaitForSocketAsync(Harness t, int index)
    {
        (await ReturnRouteTestHost.WaitUntilAsync(() => t.Sockets.Count > index, Timeout)).Should().BeTrue();
        return t.Sockets.Sockets[index];
    }

    /// <summary>A transport over scripted sockets, with a recording consumer, its lifecycle events and its logs.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> _events = new();

        private Harness(Action<WebSocketTransportOptions>? configure, int maxReceiveBytes)
        {
            Sockets = new ScriptedSocketFactory();
            Consumer = new RecordingFrameConsumer();
            Logs = new CapturingLoggerProvider();
            var options = new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                SendTimeout = TimeSpan.FromSeconds(10),
                WebSocketFactory = Sockets.Create,
                Connect = (_, _, _) => Task.CompletedTask,
            };
            configure?.Invoke(options);
            var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            Transport = new WebSocketDidCommTransport(
                Options.Create(options),
                factory.CreateLogger<WebSocketDidCommTransport>(),
                Options.Create(new DidCommOptions { MaxReceiveBytes = maxReceiveBytes }),
                Consumer);
            Transport.Lifecycle += (_, e) => _events.Enqueue((e.Kind, e.Endpoint));
        }

        public WebSocketDidCommTransport Transport { get; }

        public ScriptedSocketFactory Sockets { get; }

        public RecordingFrameConsumer Consumer { get; }

        public CapturingLoggerProvider Logs { get; }

        public EventLog Events => new(_events);

        /// <summary>The endpoints of every Disconnected event so far.</summary>
        public IReadOnlyList<Uri> Disconnected => _events.Where(e => e.Kind == WebSocketLifecycleEventKind.Disconnected).Select(e => e.Endpoint).ToList();

        public static Harness Create(Action<WebSocketTransportOptions>? configure = null, int maxReceiveBytes = 256 * 1024)
            => new(configure, maxReceiveBytes);

        public ValueTask DisposeAsync() => Transport.DisposeAsync();

        /// <summary>The lifecycle kinds recorded so far (clearable).</summary>
        public sealed class EventLog : IEnumerable<WebSocketLifecycleEventKind>
        {
            private readonly ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> _events;

            public EventLog(ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> events) => _events = events;

            public void Clear() => _events.Clear();

            public IEnumerator<WebSocketLifecycleEventKind> GetEnumerator() => _events.Select(e => e.Kind).ToList().GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
