using DidComm.Transports;

namespace DidComm.AspNetCore;

/// <summary>
/// Per-endpoint configuration for the <c>MapDidCommEndpoint</c> / <c>MapDidCommWebSocket</c>
/// extensions on <see cref="DidCommEndpointRouteBuilderExtensions"/>. Defaults match the
/// DIDComm v2.1 spec.
/// </summary>
public sealed class DidCommReceiveOptions
{
    /// <summary>
    /// Media types accepted by the endpoint. The HTTP receive path returns 415 when the request
    /// <c>Content-Type</c> does not match any of these. Defaults cover the three DIDComm v2.1
    /// envelope flavors.
    /// </summary>
    public IReadOnlyList<string> AcceptedMediaTypes { get; set; } = new[]
    {
        DidCommMediaTypes.Encrypted,
        DidCommMediaTypes.Signed,
        DidCommMediaTypes.Plain,
    };

    /// <summary>
    /// The WebSocket return-route switch (PRD §8a, FR-RR-05). When <c>true</c>, the registry-aware
    /// <c>MapDidCommWebSocket</c> opens a return-route session at the first authcrypted (authcrypt layer)
    /// message carrying <c>return_route: all</c> (FR-RR-03) and delivers handler replies and
    /// <c>IReturnRouteSession.TrySendAsync</c> pushes on that socket, authcrypted from the DID that
    /// decrypted that message to exactly its authenticated sender; a header-less message on a
    /// socket without a session gets no reply (FR-RR-01). Defaults to <c>false</c> (FR-TRN-10
    /// one-way): no session is opened and nothing is written back, while FR-RR-08 replay admission
    /// still applies. HTTP return-route (<c>MapDidCommEndpoint</c>) has no switch (FR-RR-04). Not
    /// available under <see cref="UseStomp"/>.
    /// </summary>
    public bool AllowSameSocketReplies { get; set; }

    /// <summary>
    /// Opt in to minimal STOMP 1.2 framing on the <c>MapDidCommWebSocket</c> endpoints
    /// (FR-TRN-12). OFF by default — each WebSocket message is then one bare packed envelope
    /// (FR-TRN-09), unchanged. When enabled, the endpoint answers a client
    /// <c>CONNECT</c>(accept-version incl. 1.2) with <c>CONNECTED</c>(version:1.2,
    /// heart-beat:0,0), accepts one packed DIDComm message per <c>SEND</c> frame body
    /// (validating <c>content-type</c> against <see cref="AcceptedMediaTypes"/> — the WebSocket
    /// analog of the HTTP 415 gate) and closes normally on <c>DISCONNECT</c>. Malformed or
    /// out-of-order STOMP frames close the connection with 1002 (protocol error); the
    /// <c>MaxReceiveBytes</c> cap still closes with 1009 before any decoding (FR-API-06). No
    /// broker semantics — subscriptions, acks, receipts, and heart-beats are not supported.
    /// Pair with <c>WebSocketTransportOptions.UseStomp</c> on the sending side. In STOMP mode no
    /// return-route session is ever created and <see cref="AllowSameSocketReplies"/> is ignored
    /// (one Information line per connection on the first eligible message): a compliant
    /// server-to-client push needs MESSAGE frames and subscriptions — broker semantics outside
    /// this minimal subset — so replies travel out of band per FR-TRN-10.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddOptions&lt;DidCommReceiveOptions&gt;().Configure(o => o.UseStomp = true);
    /// app.MapDidCommWebSocket("/ws/didcomm");
    /// </code>
    /// </example>
    public bool UseStomp { get; set; }

    /// <summary>
    /// Minimum wall-clock time the HTTP receive endpoint takes before returning the uniform
    /// <c>400</c> rejection. The handler times itself from entry and, before answering 400, waits
    /// out the remainder of this floor so the response time no longer reveals how far envelope
    /// processing got. This closes the recipient-kid timing side-channel (#35): without it, an
    /// envelope addressed to a recipient key the server holds runs the full ECDH/AEAD path (~360 µs)
    /// while one addressed to a key it does NOT hold fast-fails (~180 µs) — a single timed request
    /// then tells an attacker which recipient keys the agent holds, the same enumeration the uniform
    /// 400 body (#20) set out to remove. Padding every rejection up to a fixed floor removes that
    /// gap (FR-API-07).
    ///
    /// Applies to the 400 path only. A successfully received message answers 202 and is never
    /// padded (it stays full-throughput); the 415 / 413 pre-decrypt rejections carry a different
    /// status code and reveal nothing about which keys are held, so they are not padded either. The
    /// cost is therefore paid only on rejected — typically hostile or malformed — traffic. The floor
    /// is measured from just after the request body is read, so a peer cannot exhaust it by padding
    /// the body out to the size limit.
    ///
    /// Keep this small (single-digit milliseconds): 5 ms already dominates the local-crypto spread
    /// with margin, and the wait is backed by a timer (it holds no thread). Defaults to 5 ms; set
    /// <see cref="TimeSpan.Zero"/> to disable the floor entirely. On Windows, <see cref="Task.Delay(TimeSpan)"/>
    /// rounds up to the system timer resolution (~15 ms by default), so values below that are silently
    /// floored to it — this does not weaken the security property (15 ms still dominates the µs-scale
    /// crypto gap) but means rejected requests cost ~15 ms there rather than the configured 5 ms.
    ///
    /// <para><b>What this does and does not close.</b> The floor closes the cheap, universal probe —
    /// an unauthenticated peer sending garbage ciphertext to guessed recipient kids and timing the
    /// 400 to learn which keys the agent holds. It does NOT, on its own, close the held-only path
    /// where a decryptable envelope triggers network DID resolution that runs longer than the floor
    /// (an attacker-controlled slow sender DID makes the held response visibly exceed the floor while
    /// an unheld one stays at it). That residual is network-bound and unbounded; close it with
    /// authentication / a rate-limiter in front of the endpoint, not by inflating this value — see the
    /// tracking issue #44.</para>
    /// </summary>
    public TimeSpan ReceiveRejectionFloor { get; set; } = TimeSpan.FromMilliseconds(5);

    private TimeSpan _messageReceiveTimeout = ReceiveDeadline.DefaultTimeout;

    /// <summary>
    /// How long a peer has to deliver each whole WebSocket message on the <c>MapDidCommWebSocket</c>
    /// endpoints and on the mediator's WebSocket endpoint (<c>MapDidCommMediator</c>): the time runs
    /// from the moment the message's first part is read until its last frame arrives. On a connection
    /// the endpoint accepted, the first message's time starts when the connection is accepted, so a
    /// connection that stays silent is closed too. After the first message, the wait for the next one
    /// has no limit, so return-route sessions and live delivery stay open while they are idle. When
    /// the time runs out the endpoint closes the connection with status 1008 (policy violation) and
    /// drops what it had read of the message; a peer that does not answer that close within a few
    /// seconds is disconnected. Defaults to 60 seconds; <see cref="Timeout.InfiniteTimeSpan"/> turns
    /// the limit off. The HTTP endpoints (<c>MapDidCommEndpoint</c>) are not affected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The time is measured on the container's <see cref="TimeProvider"/> (<see cref="TimeProvider.System"/>
    /// when none is registered). The limit is the same for every message, large or small: a sender
    /// that needs longer than this to send one message (60 seconds is about 17 KB/s for a message of
    /// <c>DidCommOptions.MaxReceiveBytes</c> at its 1 MiB default), or that opens a connection and
    /// waits longer than this before its first message, is cut off, so raise it for peers on slow
    /// links.
    /// </para>
    /// <para>
    /// The limit bounds how long one connection can hold a message it never finishes. It does not
    /// bound how many connections are open: a connection that finished a message is then untimed by
    /// design. Bound the count with Kestrel's connection limits
    /// (<c>KestrelServerLimits.MaxConcurrentUpgradedConnections</c>, <c>MaxConcurrentConnections</c>)
    /// and a concurrency limiter in front of the endpoint.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddOptions&lt;DidCommReceiveOptions&gt;().Configure(o => o.MessageReceiveTimeout = TimeSpan.FromSeconds(30));
    /// app.MapDidCommWebSocket("/ws/didcomm");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">On set: the value is zero, negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or more than <see cref="int.MaxValue"/> milliseconds.</exception>
    public TimeSpan MessageReceiveTimeout
    {
        get => _messageReceiveTimeout;
        set => _messageReceiveTimeout = ReceiveDeadline.Validate(value, nameof(MessageReceiveTimeout));
    }

    /// <summary>
    /// A copy of these options for the mediator's WebSocket endpoint: same-socket replies on (pickup
    /// over a WebSocket answers on the socket) and STOMP off (a STOMP session can never push). The
    /// other settings are copied unchanged. A property added to this class must be copied here.
    /// </summary>
    internal DidCommReceiveOptions ForMediatorEndpoint() => new()
    {
        AcceptedMediaTypes = AcceptedMediaTypes,
        ReceiveRejectionFloor = ReceiveRejectionFloor,
        MessageReceiveTimeout = MessageReceiveTimeout,
        AllowSameSocketReplies = true,
        UseStomp = false,
    };
}
