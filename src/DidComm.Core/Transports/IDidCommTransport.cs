namespace DidComm.Transports;

/// <summary>
/// One bytes-on-the-wire transport binding (HTTP, WebSocket, or a custom extension —
/// FR-TRN-01). The transport router picks an implementation by matching the recipient's
/// service-endpoint URI scheme against <see cref="Scheme"/> / <see cref="CanHandle"/>, then
/// hands off the packed envelope via <see cref="SendAsync"/>. Transports are delivery-only
/// (FR-TRN-03) — they do not return protocol replies.
/// </summary>
public interface IDidCommTransport
{
    /// <summary>
    /// The canonical URI scheme this transport binds to (lowercase — e.g. <c>"https"</c>,
    /// <c>"wss"</c>). Used by the router as a fast-path lookup; <see cref="CanHandle"/> is the
    /// authoritative check.
    /// </summary>
    string Scheme { get; }

    /// <summary>
    /// Returns <c>true</c> when this transport can deliver to <paramref name="endpoint"/>.
    /// Implementations typically compare <c>endpoint.Scheme</c> against <see cref="Scheme"/>
    /// case-insensitively, but a transport MAY apply additional filters (e.g. a custom
    /// transport tied to a specific authority).
    /// </summary>
    /// <param name="endpoint">The target URI.</param>
    bool CanHandle(Uri endpoint);

    /// <summary>
    /// Send <paramref name="request"/> to <paramref name="request"/>.Endpoint. Throws
    /// <see cref="Exceptions.TransportException"/> on transport-level failure (non-2xx HTTP,
    /// refused redirect, exhausted retries, dropped socket).
    /// </summary>
    /// <param name="request">The packed envelope plus its destination URI and media type.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct);
}
