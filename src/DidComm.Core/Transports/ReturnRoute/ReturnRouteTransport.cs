namespace DidComm.Transports.ReturnRoute;

/// <summary>The transport an <see cref="IReturnRouteSession"/> delivers on.</summary>
public enum ReturnRouteTransport
{
    /// <summary>An HTTP request: at most one message is returned, as the 200 response body (FR-RR-04).</summary>
    Http = 0,

    /// <summary>A WebSocket connection: messages are pushed on the socket for as long as it stays open (FR-RR-05).</summary>
    WebSocket = 1,
}
