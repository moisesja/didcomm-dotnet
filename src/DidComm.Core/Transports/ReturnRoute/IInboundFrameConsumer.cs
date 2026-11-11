namespace DidComm.Transports.ReturnRoute;

/// <summary>
/// Receives the messages a peer pushes back over a return-route connection the client opened
/// (PRD §8a, FR-RR-07): a WebSocket transport reads each such session socket and hands every whole
/// message it receives to this consumer. <c>DidCommBuilder.UseInboundDispatch()</c> registers the
/// built-in consumer, which unpacks the frame, checks it came from the session's peer to its local
/// DID, runs FR-RR-08 replay admission and dispatches it to the protocol handlers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Call contract.</strong> A transport calls <see cref="ConsumeAsync"/> from the receive
/// loop of one session socket, one frame at a time: the next frame on that socket is read only
/// after the call returns. Frames of different sockets may be consumed concurrently. A consumer, or
/// a handler it runs, must therefore not wait for another inbound message on the same session: that
/// session stops reading and deadlocks until the wait's own timeout. The cancellation token is
/// cancelled when the transport is disposed or a send on the socket fails, and when a retired socket
/// (after <c>return_route: none</c> or an oversized message) has not finished closing
/// within the transport's close grace; a frame already being consumed when the socket retires may
/// otherwise finish. An exception thrown by the consumer is logged by its type name and the loop
/// continues with the next frame.
/// </para>
/// <para>
/// <strong>Cancellation callbacks.</strong> The token is the session's own, and the same token
/// reaches everything the consumer passes it to (the built-in consumer hands it to unpacking, to the
/// dispatcher and to every protocol handler). A callback registered on it with
/// <see cref="CancellationToken.Register(Action)"/> runs on whichever transport thread cancels it: a
/// timer thread when the close grace expires, the thread that disposes the transport, or the
/// thread of the send that failed. Such a callback must be short, must not block, and must not
/// throw. <c>WebSocketDidCommTransport</c> contains what one throws (it is swallowed, and the
/// remaining callbacks still run), so a faulty callback does not end the process through the
/// transport's cancellation, but nothing reports the failure either. Dispose every registration
/// once the work it guards is done: a registration left on the token runs whenever the session
/// ends, long after the frame was consumed.
/// </para>
/// <para>
/// <strong>Trust.</strong> <see cref="InboundFrame.Payload"/> is untrusted bytes from the network.
/// <see cref="InboundFrame.LocalDid"/> and <see cref="InboundFrame.PeerDid"/> are the transport's
/// attribution (the identities of the send that opened the socket), not a claim that the frame
/// came from that peer: only an authcrypt envelope from the peer to the local DID establishes that.
/// </para>
/// <para>
/// <strong>Dependency rule.</strong> The transport is a dependency of <c>DidCommClient</c>, and the
/// consumer a dependency of the transport, so an implementation must not take <c>DidCommClient</c>,
/// <see cref="ITransportRouter"/>, an <see cref="IDidCommTransport"/>, <c>ProtocolDispatcher</c>,
/// <c>ProtocolHandlerRegistry</c> or a protocol handler in its constructor; that would cycle the
/// DI graph. Resolve them through an <see cref="IServiceProvider"/> at call time. Under
/// <c>AddDidComm</c>, a cycle that runs through <c>DidCommClient</c>, <c>ProtocolDispatcher</c> or
/// <c>ProtocolHandlerRegistry</c> fails the first resolution with an
/// <see cref="InvalidOperationException"/> instead of deadlocking it; one that runs only through
/// the transports or the router (a consumer that takes the WebSocket transport, for example) is not
/// detected and deadlocks it.
/// </para>
/// </remarks>
public interface IInboundFrameConsumer
{
    /// <summary>
    /// Handle one whole message received on a return-route session socket. The transport awaits
    /// the returned task before it reads the socket's next frame.
    /// </summary>
    /// <param name="frame">The received bytes and the session's attribution.</param>
    /// <param name="ct">Cancelled when the session socket stops being read (see the remarks).</param>
    ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct);
}
