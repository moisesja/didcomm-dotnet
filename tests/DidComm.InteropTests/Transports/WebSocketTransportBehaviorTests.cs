using System.Net.WebSockets;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Transports;
using DidComm.Transports.Stomp;
using DidComm.Transports.WebSocket;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// Phase 5 review remediation — unit-level coverage for <see cref="WebSocketDidCommTransport"/>'s
/// failure handling that doesn't need a live TestServer: exhausted-budget failures are wrapped in
/// <see cref="TransportException"/> (FR-API-07), a failed connect disposes its nascent socket
/// (no leak), the lifecycle event surface raises Reconnected / Disconnected, and the default
/// connect path pins the TCP connection to a guard-vetted IP (SSRF defense).
/// </summary>
public sealed class WebSocketTransportBehaviorTests
{
    [Fact]
    public async Task SendAsync_rejects_private_ip_literal_endpoint_on_default_path()
    {
        // Default connect path (no custom Connect/factory): the SSRF guard must refuse a private host.
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
        });
        await using var transport = new WebSocketDidCommTransport(options);

        var request = new TransportRequest(
            new Uri("ws://127.0.0.1:9/socket"), new byte[] { 1 }, "application/didcomm-encrypted+json");

        var ex = (await ((Func<Task>)(() => transport.SendAsync(request, default)))
            .Should().ThrowAsync<TransportException>()).Which;
        FlattenMessages(ex).Should().Contain("private or reserved");
    }

    [Fact]
    public async Task SendAsync_pins_connection_so_a_name_resolving_to_loopback_is_blocked_even_with_dns_check_disabled()
    {
        // ResolveDnsNames = false makes the pre-send Validate() a no-op for DNS names, so reaching the
        // refusal here proves it is the connect-time IP pinning (OutboundEndpointGuard.ConnectAsync,
        // which always resolves) — not the pre-check — that blocks a name resolving to a private IP.
        // This is the DNS-rebinding / ResolveDnsNames=false gap the fix closes for the WS transport.
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
            // Explicit transport policy (#27 made the policy nullable = inherit-core-by-default).
            OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false },
        });
        await using var transport = new WebSocketDidCommTransport(options);

        // 'localhost' is a DNS name (not an IP literal) that resolves only to loopback. Port 9 is
        // never actually dialed: the guard filters every resolved address before connecting.
        var request = new TransportRequest(
            new Uri("ws://localhost:9/socket"), new byte[] { 1 }, "application/didcomm-encrypted+json");

        var ex = (await ((Func<Task>)(() => transport.SendAsync(request, default)))
            .Should().ThrowAsync<TransportException>()).Which;
        FlattenMessages(ex).Should().Contain("private or reserved");
    }

    [Fact]
    public async Task SendAsync_wraps_connect_failure_in_TransportException()
    {
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
            WebSocketFactory = () => new TrackingWebSocket(),
            Connect = (_, _, _) => Task.FromException(new WebSocketException("connect refused")),
        });
        await using var transport = new WebSocketDidCommTransport(options);

        var act = async () => await transport.SendAsync(NewRequest(), default);

        var ex = (await act.Should().ThrowAsync<TransportException>()).Which;
        ex.Scheme.Should().Be("ws");
        ex.InnerException.Should().BeOfType<WebSocketException>();
    }

    [Fact]
    public async Task SendAsync_disposes_socket_when_connect_fails()
    {
        var created = new List<TrackingWebSocket>();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            WebSocketFactory = () =>
            {
                var socket = new TrackingWebSocket();
                created.Add(socket);
                return socket;
            },
            Connect = (_, _, _) => Task.FromException(new WebSocketException("connect refused")),
        });
        await using var transport = new WebSocketDidCommTransport(options);

        await ((Func<Task>)(() => transport.SendAsync(NewRequest(), default)))
            .Should().ThrowAsync<TransportException>();

        created.Should().ContainSingle();
        created[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_raises_Reconnected_after_a_failed_then_successful_attempt()
    {
        var kinds = new List<WebSocketLifecycleEventKind>();
        var attempt = 0;
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 1,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(1),
            WebSocketFactory = () => new TrackingWebSocket(),
            Connect = (_, _, _) =>
            {
                attempt++;
                return attempt == 1
                    ? Task.FromException(new WebSocketException("transient"))
                    : Task.CompletedTask;
            },
        });
        await using var transport = new WebSocketDidCommTransport(options);
        transport.Lifecycle += (_, e) => kinds.Add(e.Kind);

        await transport.SendAsync(NewRequest(), default);

        kinds.Should().Contain(WebSocketLifecycleEventKind.SendFailed);
        kinds.Should().Contain(WebSocketLifecycleEventKind.Connected);
        kinds.Should().Contain(WebSocketLifecycleEventKind.Reconnected);
    }

    [Fact]
    public async Task DisposeAsync_raises_Disconnected_for_each_pooled_socket()
    {
        var kinds = new List<WebSocketLifecycleEventKind>();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            WebSocketFactory = () => new TrackingWebSocket(),
            Connect = (_, _, _) => Task.CompletedTask,
        });
        var transport = new WebSocketDidCommTransport(options);
        transport.Lifecycle += (_, e) => kinds.Add(e.Kind);

        await transport.SendAsync(NewRequest(), default);
        kinds.Should().Contain(WebSocketLifecycleEventKind.Connected);

        await transport.DisposeAsync();

        kinds.Should().Contain(WebSocketLifecycleEventKind.Disconnected);
    }

    [Fact]
    public async Task SendAsync_inherits_core_OutboundEndpointPolicy_when_transport_policy_unset()
    {
        // #27: the WS transport's OutboundEndpointPolicy is null by default = inherit the single source
        // of truth (DidCommOptions). A core policy that does NOT block private networks must be honored —
        // localhost then passes the SSRF guard and the connect fails for a DIFFERENT reason. With the
        // default (blocking) policy, localhost would be rejected as "private or reserved".
        var wsOptions = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
            // OutboundEndpointPolicy left null → inherit the core policy below.
        });
        var core = Options.Create(new DidCommOptions
        {
            OutboundEndpointPolicy = new OutboundEndpointPolicy { BlockPrivateNetworks = false },
        });
        await using var transport = new WebSocketDidCommTransport(wsOptions, logger: null, coreOptions: core);

        var request = new TransportRequest(
            new Uri("ws://localhost:9/socket"), new byte[] { 1 }, "application/didcomm-encrypted+json");

        var ex = (await ((Func<Task>)(() => transport.SendAsync(request, default)))
            .Should().ThrowAsync<TransportException>()).Which;
        FlattenMessages(ex).Should().NotContain("private or reserved",
            "the permissive core policy was inherited, so localhost is not SSRF-blocked");
    }

    [Fact]
    public async Task Concurrent_sends_to_one_endpoint_are_serialized_and_all_succeed()
    {
        // A WebSocket allows only ONE outstanding SendAsync. Before the per-endpoint send gate,
        // two concurrent SendAsync calls raced on the pooled socket: the loser threw
        // InvalidOperationException, its recovery path disposed the HEALTHY socket out from under
        // the winner (whose in-flight send then died with an unretried ObjectDisposedException),
        // and with no retry budget both calls failed on a perfectly good connection. The strict
        // fake below enforces the real single-outstanding-send rule, so any regression to
        // overlapping sends fails this test deterministically.
        var socket = new StrictSingleSenderWebSocket();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0, // no retry — a collision would surface, not be papered over
            WebSocketFactory = () => socket,
            Connect = (_, _, _) => Task.CompletedTask,
        });
        await using var transport = new WebSocketDidCommTransport(options);

        var sends = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => transport.SendAsync(NewRequest(), default)))
            .ToArray();

        var results = await Task.WhenAll(sends);

        results.Should().OnlyContain(r => r.Accepted);
        socket.CompletedSends.Should().Be(8, "every envelope must reach the wire exactly once");
        socket.Disposed.Should().BeFalse("no healthy socket may be torn down by a concurrent send");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0")]
    [InlineData("1.1")]
    public async Task Stomp_handshake_rejects_CONNECTED_that_is_not_version_12(string? version)
    {
        // FR-TRN-12: the transport offers accept-version:1.2 and nothing else, so a CONNECTED
        // that omits the version header or states any other version is a session we cannot
        // legally speak — the handshake must fail instead of continuing on mismatched framing.
        var headers = new List<KeyValuePair<string, string>> { KeyValuePair.Create("heart-beat", "0,0") };
        if (version is not null)
            headers.Insert(0, KeyValuePair.Create("version", version));
        var peer = new ScriptedStompPeerWebSocket(new StompFrame("CONNECTED", headers, ReadOnlyMemory<byte>.Empty));
        await using var transport = new WebSocketDidCommTransport(StompOptions(() => peer));

        var ex = (await ((Func<Task>)(() => transport.SendAsync(NewRequest(), default)))
            .Should().ThrowAsync<TransportException>()).Which;
        FlattenMessages(ex).Should().Contain("1.2").And.Contain("FR-TRN-12");
    }

    [Fact]
    public async Task Stomp_handshake_accepts_CONNECTED_version_12_and_delivers_the_send()
    {
        var peer = new ScriptedStompPeerWebSocket(ConnectedFrame());
        await using var transport = new WebSocketDidCommTransport(StompOptions(() => peer));

        var result = await transport.SendAsync(NewRequest(), default);

        result.Accepted.Should().BeTrue();
        peer.SentCommands.Should().ContainInOrder("CONNECT", "SEND");
    }

    [Fact]
    public async Task Stomp_handshake_failure_consumes_the_reconnect_budget_and_recovers()
    {
        // A STOMP handshake failure is a connection-establishment failure like any other, so it
        // must consume the reconnect budget (FR-TRN-11) instead of failing the send on attempt 1:
        // the peer closes cleanly mid-handshake on attempt 1, then answers properly on attempt 2.
        var attempt = 0;
        var kinds = new List<WebSocketLifecycleEventKind>();
        var options = StompOptions(
            () => ++attempt == 1
                ? new ScriptedStompPeerWebSocket(connectedAnswer: null) // closes mid-handshake
                : new ScriptedStompPeerWebSocket(ConnectedFrame()),
            maxReconnectAttempts: 1);
        await using var transport = new WebSocketDidCommTransport(options);
        transport.Lifecycle += (_, e) => { lock (kinds) kinds.Add(e.Kind); };

        var result = await transport.SendAsync(NewRequest(), default);

        result.Accepted.Should().BeTrue();
        kinds.Should().Contain(WebSocketLifecycleEventKind.Reconnected);
    }

    [Fact]
    public async Task Exhausted_budget_wraps_a_stomp_handshake_failure_in_one_consistent_shape()
    {
        // Decision pin: SendAsync gives callers ONE failure shape — the budget-exhausted
        // TransportException with the last underlying failure as InnerException — whether the
        // attempts died in the socket layer or in the STOMP handshake. (Pre-pipeline refusals,
        // the scheme check and the SSRF guard, keep their own specific messages.)
        var options = StompOptions(
            () => new ScriptedStompPeerWebSocket(connectedAnswer: null),
            maxReconnectAttempts: 1);
        await using var transport = new WebSocketDidCommTransport(options);

        var ex = (await ((Func<Task>)(() => transport.SendAsync(NewRequest(), default)))
            .Should().ThrowAsync<TransportException>()).Which;
        ex.Message.Should().Contain("reconnect budget");
        ex.InnerException.Should().BeOfType<TransportException>()
            .Which.Message.Should().Contain("closed the connection before answering");
    }

    [Fact]
    public async Task DisposeAsync_waits_for_the_inflight_send_and_never_overlaps_the_socket()
    {
        // Dispose vs concurrent send: DisposeAsync must acquire the per-endpoint send gate
        // before it writes DISCONNECT / closes the socket. Otherwise its DISCONNECT overlaps
        // the in-flight application SendAsync (the same overlapping-SendAsync race as
        // send/send), and disposing the gates mid-wait kills the sender's finally with
        // ObjectDisposedException.
        var socket = new BlockingSendStompWebSocket();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            SendTimeout = TimeSpan.FromSeconds(5),
            UseStomp = true,
            WebSocketFactory = () => socket,
            Connect = (_, _, _) => Task.CompletedTask,
        });
        var transport = new WebSocketDidCommTransport(options);

        var sendTask = Task.Run(() => transport.SendAsync(NewRequest(), default));
        (await Task.WhenAny(socket.AppSendEntered, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().Be(socket.AppSendEntered, "the application send must reach the socket and block there");

        var disposeTask = transport.DisposeAsync().AsTask();
        await Task.Delay(100);
        disposeTask.IsCompleted.Should().BeFalse("dispose must wait for the in-flight send to drain, not race it");

        socket.ReleaseAppSend();
        var result = await sendTask; // must complete cleanly — no ObjectDisposedException
        await disposeTask;

        result.Accepted.Should().BeTrue();
        socket.MaxConcurrentSends.Should().Be(1,
            "a WebSocket allows one outstanding SendAsync; dispose's DISCONNECT must not overlap the in-flight send");

        await ((Func<Task>)(() => transport.SendAsync(NewRequest(), default)))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task SendAsync_after_dispose_is_rejected_and_does_not_repopulate_the_pool()
    {
        var factoryCalls = 0;
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            WebSocketFactory = () => { factoryCalls++; return new TrackingWebSocket(); },
            Connect = (_, _, _) => Task.CompletedTask,
        });
        var transport = new WebSocketDidCommTransport(options);
        await transport.SendAsync(NewRequest(), default);
        factoryCalls.Should().Be(1);

        await transport.DisposeAsync();

        await ((Func<Task>)(() => transport.SendAsync(NewRequest(), default)))
            .Should().ThrowAsync<ObjectDisposedException>();
        factoryCalls.Should().Be(1, "a disposed transport must not open new connections (pool-resurrection leak)");
    }

    [Fact]
    public async Task DisposeAsync_is_bounded_when_the_peer_never_answers_the_close()
    {
        // A hostile / wedged peer must not be able to hang DisposeAsync forever. Both shutdown
        // writes — the STOMP DISCONNECT frame and the WebSocket close — go to a peer that never
        // completes them, so an unbounded dispose (CancellationToken.None + CloseAsync, which
        // additionally waits for the peer's close frame) blocks the caller indefinitely.
        var peer = new UnresponsiveClosePeerWebSocket();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            SendTimeout = TimeSpan.FromMilliseconds(200),
            UseStomp = true,
            WebSocketFactory = () => peer,
            Connect = (_, _, _) => Task.CompletedTask,
        });
        var transport = new WebSocketDidCommTransport(options);
        await transport.SendAsync(NewRequest(), default); // pools an Open socket to close on dispose

        var dispose = transport.DisposeAsync().AsTask();

        (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)))).Should().Be(dispose,
            "dispose must be bounded by its own timeout budget, not by the peer's willingness to answer");
        await dispose;
        peer.Disposed.Should().BeTrue("the bounded close falls through to the hard Dispose");
    }

    [Fact]
    public async Task Concurrent_DisposeAsync_is_idempotent_and_completes_teardown()
    {
        // Concurrent disposers must not trip over each other: the loser used to hit an
        // already-disposed gate, and the ObjectDisposedException escaped DisposeAsync — skipping
        // the remaining teardown (gates, and the pinned SocketsHttpHandler with its connection
        // pool). Many rounds: the race is timing-dependent, and one escape is one leak.
        for (var round = 0; round < 100; round++)
        {
            var sockets = new System.Collections.Concurrent.ConcurrentBag<TrackingWebSocket>();
            var options = Options.Create(new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ConnectTimeout = TimeSpan.FromSeconds(1),
                SendTimeout = TimeSpan.FromSeconds(1),
                WebSocketFactory = () => { var s = new TrackingWebSocket(); sockets.Add(s); return s; },
                Connect = (_, _, _) => Task.CompletedTask,
            });
            var transport = new WebSocketDidCommTransport(options);
            foreach (var path in new[] { "/a", "/b", "/c" })
            {
                await transport.SendAsync(
                    new TransportRequest(new Uri($"ws://agents.r.us{path}"), new byte[] { 1 }, "application/didcomm-encrypted+json"),
                    default);
            }

            var disposers = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(async () => await transport.DisposeAsync()))
                .ToArray();

            await ((Func<Task>)(() => Task.WhenAll(disposers)))
                .Should().NotThrowAsync("DisposeAsync must be idempotent under concurrency");
            sockets.Should().HaveCount(3).And.OnlyContain(s => s.Disposed,
                "every pooled socket is closed exactly once, whichever caller wins");
        }
    }

    [Fact]
    public async Task DisposeAsync_drains_a_send_that_is_still_connecting_and_leaves_no_open_socket()
    {
        // A send inside its CONNECT phase holds its send gate but has NO pool entry yet, so a
        // dispose that drains only the pool walks straight past it: dispose returns immediately,
        // the connect then completes and writes an Open socket into the pool of an already-disposed
        // transport (never closed, and the send reports Accepted from a disposed transport).
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sockets = new System.Collections.Concurrent.ConcurrentBag<TrackingWebSocket>();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SendTimeout = TimeSpan.FromSeconds(5),
            WebSocketFactory = () => { var s = new TrackingWebSocket(); sockets.Add(s); return s; },
            Connect = async (_, _, _) =>
            {
                connectEntered.TrySetResult();
                await releaseConnect.Task.ConfigureAwait(false);
            },
        });
        var transport = new WebSocketDidCommTransport(options);

        var send = Task.Run(() => transport.SendAsync(NewRequest(), default));
        (await Task.WhenAny(connectEntered.Task, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().Be(connectEntered.Task, "the send must reach the connect and block there");

        var dispose = transport.DisposeAsync().AsTask();
        await Task.Delay(200);
        dispose.IsCompleted.Should().BeFalse(
            "the drain must cover a send that holds its gate while connecting, not just pooled sockets");

        releaseConnect.TrySetResult();
        (await send).Accepted.Should().BeTrue("a send that started before dispose is drained, not aborted");
        await dispose;

        sockets.Should().ContainSingle().Which.Disposed.Should().BeTrue(
            "a socket connected while dispose was running must be closed, not left Open in the pool");
        await ((Func<Task>)(() => transport.SendAsync(NewRequest(), default)))
            .Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_drain_budget_covers_the_whole_connect_plus_send_hold()
    {
        // The send gate is held across connect + handshake + send, so a healthy hold reaches
        // ConnectTimeout + SendTimeout. A drain budget of only SendTimeout expires mid-connect,
        // dispose walks away, and the send is then killed on a transport that was still able to
        // wait for it. Hold here is ~1.5 s connect + ~0.5 s send = 2 s, with SendTimeout 0.8 s.
        var socket = new SlowSendWebSocket(TimeSpan.FromMilliseconds(500));
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            SendTimeout = TimeSpan.FromMilliseconds(800),
            WebSocketFactory = () => socket,
            Connect = (_, _, ct) => Task.Delay(TimeSpan.FromMilliseconds(1500), ct),
        });
        var transport = new WebSocketDidCommTransport(options);

        var send = Task.Run(() => transport.SendAsync(NewRequest(), default));
        await Task.Delay(200); // dispose lands while the send is still connecting
        var dispose = transport.DisposeAsync().AsTask();

        (await send).Accepted.Should().BeTrue(
            "the drain budget must cover the real worst-case hold (ConnectTimeout + SendTimeout), not SendTimeout alone");
        await dispose;
        socket.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_is_bounded_even_when_SendTimeout_is_infinite()
    {
        // SendTimeout is caller-supplied and reaches SemaphoreSlim.WaitAsync unvalidated:
        // Timeout.InfiniteTimeSpan makes the drain — and therefore dispose — wait forever on a send
        // that never returns. The drain budget must always be finite.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new GatedSendWebSocket(release.Task);
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            SendTimeout = Timeout.InfiniteTimeSpan,
            WebSocketFactory = () => socket,
            Connect = (_, _, _) => Task.CompletedTask,
        });
        var transport = new WebSocketDidCommTransport(options);
        var send = Task.Run(() => transport.SendAsync(NewRequest(), default));
        (await Task.WhenAny(socket.SendEntered, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().Be(socket.SendEntered, "the send must be in flight, holding the gate");

        var dispose = transport.DisposeAsync().AsTask();

        (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)))).Should().Be(dispose,
            "an unbounded SendTimeout must not become an unbounded dispose");
        await dispose;
        release.TrySetResult();
        await ((Func<Task>)(() => send)).Should().ThrowAsync<Exception>(
            "the abandoned send dies on the hard-closed socket");
    }

    [Fact]
    public async Task DisposeAsync_survives_a_negative_SendTimeout()
    {
        // A negative (non-infinite) timeout makes SemaphoreSlim.WaitAsync / CancellationTokenSource
        // throw ArgumentOutOfRangeException from the middle of the cleanup path, aborting the
        // teardown of every remaining socket. The budget must be clamped instead.
        var socket = new TrackingWebSocket();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            SendTimeout = TimeSpan.FromMilliseconds(-5),
            WebSocketFactory = () => socket,
            Connect = (_, _, _) => Task.CompletedTask,
        });
        var transport = new WebSocketDidCommTransport(options);
        // The send itself fails on the bad timeout; what matters is that dispose still tears down.
        await ((Func<Task>)(() => transport.SendAsync(NewRequest(), default))).Should().ThrowAsync<Exception>();

        await ((Func<Task>)(() => transport.DisposeAsync().AsTask()))
            .Should().NotThrowAsync("a misconfigured timeout must not abort dispose");
        socket.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Stomp_version_mismatch_is_never_retried_while_a_transient_failure_still_reconnects()
    {
        // A CONNECTED naming a version we don't speak can never succeed on retry, and every retry
        // re-connects while HOLDING the endpoint's send gate (head-of-line blocking for every other
        // sender). Permanently fatal ⇒ exactly one connect. A peer that closed mid-handshake is
        // transient and must still consume the reconnect budget.
        var fatalConnects = 0;
        var fatalOptions = StompOptions(
            () => new ScriptedStompPeerWebSocket(new StompFrame(
                "CONNECTED", new[] { KeyValuePair.Create("version", "1.1") }, ReadOnlyMemory<byte>.Empty)),
            maxReconnectAttempts: 3);
        fatalOptions.Value.Connect = (_, _, _) => { Interlocked.Increment(ref fatalConnects); return Task.CompletedTask; };
        await using (var fatal = new WebSocketDidCommTransport(fatalOptions))
        {
            var ex = (await ((Func<Task>)(() => fatal.SendAsync(NewRequest(), default)))
                .Should().ThrowAsync<TransportException>()).Which;
            FlattenMessages(ex).Should().Contain("1.2").And.Contain("FR-TRN-12");
        }
        fatalConnects.Should().Be(1, "a permanently incompatible handshake must not burn the reconnect budget");

        var transientConnects = 0;
        var attempt = 0;
        var transientOptions = StompOptions(
            () => ++attempt == 1
                ? new ScriptedStompPeerWebSocket(connectedAnswer: null) // closes mid-handshake
                : new ScriptedStompPeerWebSocket(ConnectedFrame()),
            maxReconnectAttempts: 3);
        transientOptions.Value.Connect = (_, _, _) => { Interlocked.Increment(ref transientConnects); return Task.CompletedTask; };
        await using var transient = new WebSocketDidCommTransport(transientOptions);

        (await transient.SendAsync(NewRequest(), default)).Accepted.Should().BeTrue();
        transientConnects.Should().Be(2, "a peer that closed mid-handshake may well answer the next attempt");
    }

    [Fact]
    public async Task Lifecycle_handler_can_send_to_the_same_endpoint_without_deadlocking()
    {
        // SemaphoreSlim is not reentrant, so raising lifecycle events while holding the per-endpoint
        // send gate deadlocks any handler that sends to that same endpoint. (DisposeAsync already
        // releases the gate before it raises.)
        var attempt = 0;
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 1,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(1),
            // Small budgets so a regression's dispose-time drain fails fast instead of hanging CI.
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            SendTimeout = TimeSpan.FromMilliseconds(500),
            WebSocketFactory = () => new TrackingWebSocket(),
            Connect = (_, _, _) => Interlocked.Increment(ref attempt) == 1
                ? Task.FromException(new WebSocketException("transient"))
                : Task.CompletedTask,
        });
        await using var transport = new WebSocketDidCommTransport(options);
        var reentered = 0;
        TransportResult? inner = null;
        transport.Lifecycle += (_, e) =>
        {
            if (e.Kind == WebSocketLifecycleEventKind.Reconnected && Interlocked.Exchange(ref reentered, 1) == 0)
                inner = transport.SendAsync(NewRequest(), default).GetAwaiter().GetResult();
        };

        var outer = Task.Run(() => transport.SendAsync(NewRequest(), default));

        (await Task.WhenAny(outer, Task.Delay(TimeSpan.FromSeconds(5)))).Should().Be(outer,
            "a handler must not deadlock on the send gate its own notification is holding");
        (await outer).Accepted.Should().BeTrue();
        inner.Should().NotBeNull();
        inner!.Accepted.Should().BeTrue();
    }

    private static TransportRequest NewRequest() =>
        new(new Uri("ws://agents.r.us/socket"), new byte[] { 1, 2, 3 }, "application/didcomm-encrypted+json");

    // Walk the InnerException chain into one string: the guard's SSRF TransportException can be
    // wrapped by ClientWebSocket (WebSocketException) and then by the transport's budget-exhausted
    // TransportException, so assert against the whole chain rather than a single layer's message.
    private static string FlattenMessages(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            sb.Append(e.Message).Append(" | ");
        return sb.ToString();
    }

    /// <summary>Options for the fake-peer STOMP tests: STOMP on, custom connect (SSRF guard off), tight backoff.</summary>
    private static IOptions<WebSocketTransportOptions> StompOptions(
        Func<System.Net.WebSockets.WebSocket> factory, int maxReconnectAttempts = 0) =>
        Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(1),
            UseStomp = true,
            WebSocketFactory = factory,
            Connect = (_, _, _) => Task.CompletedTask,
        });

    /// <summary>A well-formed CONNECTED answer, as StompServerSession emits it.</summary>
    private static StompFrame ConnectedFrame() => new(
        "CONNECTED",
        new[] { KeyValuePair.Create("version", "1.2"), KeyValuePair.Create("heart-beat", "0,0") },
        ReadOnlyMemory<byte>.Empty);

    /// <summary>
    /// In-memory STOMP peer for client-handshake tests: answers the transport's CONNECT with one
    /// scripted CONNECTED frame — or, when constructed with <c>null</c>, a clean close
    /// mid-handshake — and records the command of every frame the transport sends.
    /// </summary>
    private sealed class ScriptedStompPeerWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly byte[]? _answer;
        private readonly List<string> _sentCommands = new();
        private int _answerOffset;

        public ScriptedStompPeerWebSocket(StompFrame? connectedAnswer) =>
            _answer = connectedAnswer is null ? null : StompFrameCodec.Encode(connectedAnswer);

        public IReadOnlyList<string> SentCommands
        {
            get { lock (_sentCommands) return _sentCommands.ToArray(); }
        }

        public bool Disposed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => Disposed = true;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_answer is null)
            {
                // Scripted handshake failure: the peer closes cleanly before answering CONNECT.
                return Task.FromResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "scripted close"));
            }
            var count = Math.Min(buffer.Count, _answer.Length - _answerOffset);
            Array.Copy(_answer, _answerOffset, buffer.Array!, buffer.Offset, count);
            _answerOffset += count;
            return Task.FromResult(new WebSocketReceiveResult(
                count, WebSocketMessageType.Binary, _answerOffset >= _answer.Length));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_sentCommands)
                _sentCommands.Add(StompFrameCodec.Decode(buffer).Command);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A STOMP-speaking fake whose APPLICATION send (the SEND frame) blocks until released via
    /// <see cref="ReleaseAppSend"/>, while CONNECT / DISCONNECT pass through immediately. The
    /// handshake is answered with a well-formed CONNECTED. Tracks the high-water mark of
    /// concurrent <see cref="SendAsync"/> calls so any overlap (e.g. dispose's DISCONNECT racing
    /// an in-flight send) is detected deterministically.
    /// </summary>
    private sealed class BlockingSendStompWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _connected = StompFrameCodec.Encode(ConnectedFrame());
        private int _receiveOffset;
        private int _sendsInFlight;
        private int _maxConcurrentSends;

        /// <summary>Completes when the application SEND frame has entered <see cref="SendAsync"/> and blocked.</summary>
        public Task AppSendEntered => _entered.Task;

        /// <summary>The largest number of simultaneously outstanding <see cref="SendAsync"/> calls observed.</summary>
        public int MaxConcurrentSends => Volatile.Read(ref _maxConcurrentSends);

        public bool Disposed { get; private set; }

        /// <summary>Unblock the application SEND frame.</summary>
        public void ReleaseAppSend() => _release.TrySetResult();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => Disposed = true;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var count = Math.Min(buffer.Count, _connected.Length - _receiveOffset);
            Array.Copy(_connected, _receiveOffset, buffer.Array!, buffer.Offset, count);
            _receiveOffset += count;
            return Task.FromResult(new WebSocketReceiveResult(
                count, WebSocketMessageType.Binary, _receiveOffset >= _connected.Length));
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var inFlight = Interlocked.Increment(ref _sendsInFlight);
            int observedMax;
            while ((observedMax = Volatile.Read(ref _maxConcurrentSends)) < inFlight)
                Interlocked.CompareExchange(ref _maxConcurrentSends, inFlight, observedMax);
            try
            {
                if (string.Equals(StompFrameCodec.Decode(buffer).Command, "SEND", StringComparison.Ordinal))
                {
                    _entered.TrySetResult();
                    await _release.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _sendsInFlight);
            }
        }
    }

    /// <summary>
    /// A STOMP peer that answers CONNECTED and accepts application SEND frames, then goes silent at
    /// shutdown: the DISCONNECT frame and the close handshake complete only if the caller's
    /// cancellation token fires. Models a peer that stopped reading (TCP backpressure) or never
    /// returns its close frame — the case that hangs an unbounded dispose forever.
    /// </summary>
    private sealed class UnresponsiveClosePeerWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly byte[] _connected = StompFrameCodec.Encode(ConnectedFrame());
        private int _receiveOffset;

        public bool Disposed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
        public override void Dispose() => Disposed = true;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var count = Math.Min(buffer.Count, _connected.Length - _receiveOffset);
            Array.Copy(_connected, _receiveOffset, buffer.Array!, buffer.Offset, count);
            _receiveOffset += count;
            return Task.FromResult(new WebSocketReceiveResult(
                count, WebSocketMessageType.Binary, _receiveOffset >= _connected.Length));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            string.Equals(StompFrameCodec.Decode(buffer).Command, "DISCONNECT", StringComparison.Ordinal)
                ? Task.Delay(Timeout.Infinite, cancellationToken)
                : Task.CompletedTask;
    }

    /// <summary>
    /// A socket whose send takes a fixed wall-clock time and, like a real one, fails if the socket
    /// is disposed under it.
    /// </summary>
    private sealed class SlowSendWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly TimeSpan _sendDuration;

        /// <summary>Initialize with how long each send takes.</summary>
        /// <param name="sendDuration">Wall-clock duration of every <see cref="SendAsync"/>.</param>
        public SlowSendWebSocket(TimeSpan sendDuration) => _sendDuration = sendDuration;

        public bool Disposed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => Disposed = true;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            await Task.Delay(_sendDuration, cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Disposed, this);
        }
    }

    /// <summary>A socket whose send blocks until an externally supplied task completes.</summary>
    private sealed class GatedSendWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Task _release;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Initialize with the task that unblocks the send.</summary>
        /// <param name="release">Completes to let the in-flight send finish.</param>
        public GatedSendWebSocket(Task release) => _release = release;

        /// <summary>Completes once a send has entered and blocked.</summary>
        public Task SendEntered => _entered.Task;

        public bool Disposed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => Disposed = true;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Disposed, this);
        }
    }

    /// <summary>A minimal in-memory <see cref="WebSocket"/> that records disposal and accepts sends.</summary>
    private sealed class TrackingWebSocket : System.Net.WebSockets.WebSocket
    {
        public bool Disposed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => Disposed = true;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A fake that enforces the real <see cref="WebSocket"/> contract the transport must respect:
    /// at most one outstanding <see cref="SendAsync"/> (a second concurrent call throws
    /// <see cref="InvalidOperationException"/>, as ManagedWebSocket does), and any send after
    /// <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/>. Each send yields to the
    /// thread pool so a racing caller has a real window to collide in.
    /// </summary>
    private sealed class StrictSingleSenderWebSocket : System.Net.WebSockets.WebSocket
    {
        private int _sendsInFlight;
        private int _completedSends;

        public bool Disposed { get; private set; }
        public int CompletedSends => Volatile.Read(ref _completedSends);

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => Disposed = true;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (Interlocked.Increment(ref _sendsInFlight) != 1)
            {
                Interlocked.Decrement(ref _sendsInFlight);
                throw new InvalidOperationException("There is already one outstanding 'SendAsync' call for this WebSocket instance.");
            }
            try
            {
                await Task.Yield(); // give a racing second sender a real collision window
                ObjectDisposedException.ThrowIf(Disposed, this);
                Interlocked.Increment(ref _completedSends);
            }
            finally
            {
                Interlocked.Decrement(ref _sendsInFlight);
            }
        }
    }
}
