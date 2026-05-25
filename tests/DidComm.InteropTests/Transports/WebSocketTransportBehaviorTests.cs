using System.Net.WebSockets;
using DidComm.Exceptions;
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
/// (no leak), and the lifecycle event surface raises Reconnected / Disconnected.
/// </summary>
public sealed class WebSocketTransportBehaviorTests
{
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

    private static TransportRequest NewRequest() =>
        new(new Uri("ws://agents.r.us/socket"), new byte[] { 1, 2, 3 }, "application/didcomm-encrypted+json");

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
