using DidComm.Transports;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Transports.WebSocket;

/// <summary>
/// Configuration knobs for <see cref="WebSocketDidCommTransport"/> (PRD §9.3 / FR-TRN-09..11).
/// </summary>
public sealed class WebSocketTransportOptions
{
    /// <summary>How long the initial connect handshake is allowed to take. Defaults to 10 s.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-send timeout. Defaults to 30 s.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max reconnect attempts after a drop. Defaults to 5 (DD-05).</summary>
    public int MaxReconnectAttempts { get; set; } = 5;

    /// <summary>Base delay for the exponential reconnect backoff. Defaults to 1 s (DD-05).</summary>
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap on the reconnect backoff. Defaults to 30 s (DD-05).</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Allowed URI schemes (lowercase). Defaults to <c>{"wss"}</c>; tests can opt in to <c>"ws"</c>.</summary>
    public IReadOnlyList<string> AllowedSchemes { get; set; } = new[] { "wss" };

    /// <summary>
    /// Factory for the <see cref="System.Net.WebSockets.WebSocket"/> instance used per
    /// connection. Defaults to constructing a new <see cref="System.Net.WebSockets.ClientWebSocket"/>;
    /// tests substitute a TestServer-bound socket via this seam.
    /// </summary>
    public Func<System.Net.WebSockets.WebSocket>? WebSocketFactory { get; set; }

    /// <summary>
    /// Optional connect callback applied to the produced socket. The default invokes
    /// <c>ClientWebSocket.ConnectAsync(Uri, CancellationToken)</c> on the produced socket;
    /// tests substitute a TestServer-handshake variant.
    /// </summary>
    public Func<System.Net.WebSockets.WebSocket, Uri, CancellationToken, Task>? Connect { get; set; }

    /// <summary>
    /// Opt in to minimal STOMP 1.2 framing over the WebSocket (FR-TRN-12). OFF by default —
    /// the transport then sends each packed envelope as a bare binary WebSocket message
    /// (FR-TRN-09), unchanged. When enabled, each new connection performs a
    /// <c>CONNECT</c>(accept-version:1.2, heart-beat:0,0) → <c>CONNECTED</c> handshake, every
    /// envelope rides in one <c>SEND</c> frame with <c>destination</c>,
    /// <c>content-type: application/didcomm-encrypted+json</c> and <c>content-length</c>
    /// (one packed message per SEND body), and disposal sends <c>DISCONNECT</c>. No broker
    /// semantics — no subscriptions, acks, receipts, or heart-beats. The receive side must
    /// enable the matching <c>DidCommReceiveOptions.UseStomp</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.UseWebSocketTransport(o => { o.UseStomp = true; o.StompDestination = "/didcomm"; });
    /// </code>
    /// </example>
    public bool UseStomp { get; set; }

    /// <summary>
    /// The STOMP <c>destination</c> header value for <c>SEND</c> frames when
    /// <see cref="UseStomp"/> is enabled. <c>null</c> (the default) uses the target endpoint
    /// URI's absolute path.
    /// </summary>
    public string? StompDestination { get; set; }

    /// <summary>
    /// SSRF-defense policy applied before the default connect path opens a socket. Skipped when a
    /// custom <see cref="Connect"/> delegate is supplied (the host then owns connection vetting — used
    /// by tests against an in-process TestServer).
    /// </summary>
    /// <remarks>
    /// <c>null</c> (the default) means <b>inherit</b> the single source of truth,
    /// <c>DidCommOptions.OutboundEndpointPolicy</c> (which itself defaults to blocking private /
    /// loopback / link-local / metadata destinations), so the policy is configured in one place and
    /// applies to the pre-send check and the transport connect-time pin alike (#27). Set a non-null
    /// value only to give this transport a policy distinct from the core one.
    /// </remarks>
    public OutboundEndpointPolicy? OutboundEndpointPolicy { get; set; }

    /// <summary>
    /// Hand the messages a peer pushes on a return-route session socket to the registered
    /// <see cref="IInboundFrameConsumer"/> (PRD §8a, FR-RR-07). Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A send whose message carries <c>return_route</c> <c>all</c> or <c>thread</c> and is authcrypted
    /// directly to one DID opens a session socket of its own (see
    /// <see cref="WebSocketDidCommTransport"/>); the receiver may push messages back on it. With this
    /// flag on, each whole pushed message, at most <c>DidCommOptions.MaxReceiveBytes</c> long, is
    /// handed to the consumer one at a time. <c>DidCommBuilder.UseInboundDispatch()</c> registers the
    /// built-in consumer, which unpacks the message, checks it is authcrypt from the session's peer to
    /// its local DID, applies FR-RR-08 replay admission and dispatches it to the protocol handlers
    /// with no session; a handler's reply is never sent automatically.
    /// </para>
    /// <para>
    /// Turning it on without a consumer fails at construction with
    /// <see cref="InvalidOperationException"/>. Off, session sockets are still read, so the transport
    /// notices a close or an eviction and completes the close handshake, but every pushed message is
    /// discarded (logged once). Pooled sockets, used by sends without a return route, are never read.
    /// Under <see cref="UseStomp"/> no session socket is ever opened, so nothing is received.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddDidComm(b => b
    ///     .UseWebSocketTransport(o => o.ReceiveInbound = true)
    ///     .UseInboundDispatch()
    ///     .AddBuiltInProtocols());
    /// </code>
    /// </example>
    public bool ReceiveInbound { get; set; }
}
