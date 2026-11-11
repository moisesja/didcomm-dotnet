namespace DidComm.Protocols.Routing;

/// <summary>
/// Where <see cref="ForwardHandler"/> hands a processed forward: the onward payload, its next hop
/// and its scheduling hints (<see cref="ForwardProcessingResult.ExpiresTime"/>,
/// <see cref="ForwardProcessingResult.NotBefore"/>). A sink pushes it to a live session, queues
/// it for pickup, or delivers it directly (FR-ROUTE-11; the mediator service of #95). The default
/// <see cref="NullForwardSink"/> drops everything.
/// </summary>
/// <remarks>
/// <para>
/// The sink runs on the handler's background pump, never on the request path, and only after the
/// transport has acknowledged the forward: the handler publishes to the pump once
/// <see cref="ProtocolContext.Acknowledged"/> completes, which the registry-aware HTTP endpoint
/// ties to the committed 202 (<c>Response.OnCompleted</c>); a transport with no wire
/// acknowledgement (WebSocket, a direct dispatcher caller) publishes immediately. It receives the
/// handler's shutdown token, not the request's. A sink exception is logged with the message id and
/// the exception type name(s) only — never the exception object or its message, which may name a
/// DID — and swallowed; it never reaches the transport.
/// </para>
/// <para>
/// <strong>DI rule.</strong> An implementation MUST NOT take <see cref="ProtocolHandlerRegistry"/>
/// or <see cref="ProtocolDispatcher"/> in its constructor: the registry's factory constructs every
/// <see cref="IProtocolHandler"/>, including <see cref="ForwardHandler"/>, which takes the sink —
/// a sink that needs either must inject <see cref="IServiceProvider"/> and resolve it at call
/// time, or the first resolution of the registry fails: the cycle re-enters the registry's factory,
/// which (registered by <c>AddDidComm</c>) throws <see cref="InvalidOperationException"/> instead of
/// letting the container hang. Implementations are singletons and MUST be thread-safe.
/// </para>
/// </remarks>
public interface IForwardSink
{
    /// <summary>Accept one processed forward for delivery.</summary>
    /// <param name="result">The next hop, the onward bytes and the scheduling hints.</param>
    /// <param name="context">The dispatch context of the inbound forward (unpack metadata, thread, facade, options).</param>
    /// <param name="ct">The handler's shutdown token.</param>
    Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct);
}
