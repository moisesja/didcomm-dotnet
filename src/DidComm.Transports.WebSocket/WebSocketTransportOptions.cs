using DidComm.Transports;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Transports.WebSocket;

/// <summary>
/// Configuration knobs for <see cref="WebSocketDidCommTransport"/> (PRD §9.3 / FR-TRN-09..11).
/// </summary>
public sealed class WebSocketTransportOptions
{
    private TimeSpan _messageReceiveTimeout = ReceiveDeadline.DefaultTimeout;

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
    /// Optional connect callback applied to the produced socket. The default connects the produced
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/> through the transport's guarded handler
    /// (directly, or through <see cref="TrustedProxy"/>); tests substitute a TestServer-handshake
    /// variant. A delegate set here owns how and where it connects: the outbound-endpoint checks and
    /// <see cref="TrustedProxy"/> do not apply to it, except that with <see cref="TrustedProxy"/> set
    /// the destination is still checked before each send.
    /// </summary>
    public Func<System.Net.WebSockets.WebSocket, Uri, CancellationToken, Task>? Connect { get; set; }

    /// <summary>
    /// Opt in to minimal STOMP 1.2 framing over the WebSocket (FR-TRN-12). OFF by default —
    /// the transport then sends each packed envelope as a bare text WebSocket message
    /// (FR-TRN-09), unchanged. STOMP frames are binary WebSocket messages. When enabled, each new connection performs a
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
    /// <para>
    /// The connect-time pin applies to direct connections. With <see cref="TrustedProxy"/> set, the
    /// connection goes to the proxy instead; see there for what is still checked.
    /// </para>
    /// </remarks>
    public OutboundEndpointPolicy? OutboundEndpointPolicy { get; set; }

    /// <summary>
    /// A proxy you trust to carry this transport's connections. <c>null</c> (the default) means
    /// every connection is <b>direct</b>: the transport never uses a proxy from the environment
    /// (<c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c>, <c>ALL_PROXY</c>,
    /// <see cref="System.Net.Http.HttpClient.DefaultProxy"/> or the operating system's proxy
    /// settings). Set it when this host can reach other agents only through a proxy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the host takes on.</b> Once a connection goes through a proxy, the library can no
    /// longer see where it ends up: the proxy opens the connection to the destination and does its
    /// own name lookup. Your proxy must therefore refuse private, loopback, link-local and
    /// cloud-metadata destinations (egress filtering). A DID document's service endpoint is written
    /// by whoever controls that DID, so without that filtering a peer could have your proxy reach
    /// your internal network.
    /// </para>
    /// <para>
    /// <b>What the library still checks</b> before each send (first connections and reconnects
    /// alike): the scheme (<see cref="AllowedSchemes"/>), an IP-literal destination (refused when
    /// the <see cref="OutboundEndpointPolicy"/> blocks it), and, while
    /// <see cref="Transports.OutboundEndpointPolicy.ResolveDnsNames"/> is on, a local lookup of the
    /// destination's name. That lookup is best effort, because the proxy looks the name up again; a
    /// name that does not resolve locally is refused. A refusal is a
    /// <see cref="Exceptions.TransportException"/>, thrown before the proxy is contacted and never
    /// retried. Through a proxy the opening handshake follows no redirects. Where
    /// only the proxy can resolve names, set <c>ResolveDnsNames = false</c> (on
    /// <c>DidCommOptions.OutboundEndpointPolicy</c>, which <c>DidCommClient</c> checks too); then
    /// only IP literals are checked here.
    /// </para>
    /// <para>
    /// <b>The proxy itself</b> is your explicit choice, so its own address is never refused, even
    /// on a private or loopback network; don't add it to
    /// <see cref="Transports.OutboundEndpointPolicy.AllowedHosts"/> (that would also allow it as a
    /// destination). Supported schemes: <c>http</c>, <c>https</c>, <c>socks4</c>, <c>socks4a</c>,
    /// <c>socks5</c>. Put credentials in <see cref="System.Net.WebProxy.Credentials"/> (or set
    /// <see cref="System.Net.WebProxy.UseDefaultCredentials"/>); an address carrying a user name or
    /// password is refused. A destination on <see cref="System.Net.WebProxy.BypassList"/> (or a
    /// local one with <see cref="System.Net.WebProxy.BypassProxyOnLocal"/>) is connected to directly,
    /// with the full connect-time check, as when no proxy is set, even when it names the proxy's own
    /// host and port: only a connection that uses the proxy as a proxy is exempt.
    /// </para>
    /// <para>
    /// The value is checked and copied once, when the transport is created, so later changes to the
    /// same <see cref="System.Net.WebProxy"/> instance do not reach it. An invalid value throws
    /// <see cref="InvalidOperationException"/>. When a custom <see cref="Connect"/> delegate is set,
    /// that delegate decides how and where to connect (the proxy is not applied), and the
    /// destination checks above still run before each send.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.UseWebSocketTransport(o => o.TrustedProxy = new WebProxy("http://egress.internal:3128"));
    /// </code>
    /// </example>
    public System.Net.WebProxy? TrustedProxy { get; set; }

    /// <summary>
    /// Hand the messages a peer pushes on a return-route session socket to the registered
    /// <see cref="IInboundFrameConsumer"/> (PRD §8a, FR-RR-07). Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A send whose message carries <c>return_route</c> <c>all</c> or <c>thread</c> and is authcrypted
    /// directly to one DID (the sending and receiving DIDs each at most 2048 characters) opens a
    /// session socket of its own (see
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
    /// On or off, a session socket on which a pushed message does not arrive whole within
    /// <see cref="MessageReceiveTimeout"/> of its first part is closed with status 1008.
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

    /// <summary>
    /// How long a peer has to deliver each whole message it pushes on a return-route session socket,
    /// counted from the moment the message's first part is read to its last frame. Defaults to 60
    /// seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wait for the next message has no limit: a session socket (or a live-delivery socket) may
    /// stay idle as long as it likes. The client accepts no connections, so there is no limit here on
    /// how long a new connection may stay silent before its first message.
    /// </para>
    /// <para>
    /// When the time runs out, the transport closes that session socket with status 1008 (policy
    /// violation), drops what it had read of the message, and discards the rest of the socket's
    /// messages, as after a message over <c>DidCommOptions.MaxReceiveBytes</c>: the socket takes no
    /// more sends, so a later send to the same endpoint and DIDs opens a fresh session socket when it
    /// asks for a return route, and uses the pooled socket otherwise. Only the peer's delivery is
    /// timed: the inbound consumer's handling of a message is not.
    /// </para>
    /// <para>
    /// <see cref="Timeout.InfiniteTimeSpan"/> turns the limit off. Any other value must be greater
    /// than zero and at most <see cref="int.MaxValue"/> milliseconds. The value is read once, when the
    /// transport is created. Pooled sockets, used by sends without a return route, are never read, so
    /// it does not apply to them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is zero, negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or above
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    /// <example>
    /// <code>
    /// builder.UseWebSocketTransport(o => o.MessageReceiveTimeout = TimeSpan.FromSeconds(30));
    /// </code>
    /// </example>
    public TimeSpan MessageReceiveTimeout
    {
        get => _messageReceiveTimeout;
        set => _messageReceiveTimeout = ReceiveDeadline.Validate(value, nameof(MessageReceiveTimeout));
    }
}
