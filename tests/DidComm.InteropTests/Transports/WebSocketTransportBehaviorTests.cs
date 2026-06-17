using System.Net.WebSockets;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Transports;
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
}
