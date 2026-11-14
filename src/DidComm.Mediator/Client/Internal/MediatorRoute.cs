namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The route of one client operation, resolved once (design §4.3): the mediator endpoint every
/// request of the operation is sent to (as the send's endpoint override), and how its answers come
/// back. Owner: engineer A (<see cref="MediatorClientChannel.ResolveRouteAsync"/> builds it; a fake
/// exchange in B's and C's tests builds it directly).
/// </summary>
/// <param name="Endpoint">The absolute endpoint URI: the caller's override, or the first service entry of the mediator's DID.</param>
/// <param name="IsHttp">The scheme is <c>http</c> or <c>https</c>: the answer is the HTTP response body, and only that.</param>
/// <param name="IsWebSocket">The scheme is <c>ws</c> or <c>wss</c>: the answer arrives on the session socket and reaches the channel through the dispatcher (correlator). Any scheme that is neither is treated like a WebSocket (wait for the correlator) with both flags <c>false</c>.</param>
internal sealed record MediatorRoute(Uri Endpoint, bool IsHttp, bool IsWebSocket);
