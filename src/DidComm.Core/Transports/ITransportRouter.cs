namespace DidComm.Transports;

/// <summary>
/// Picks the right <see cref="IDidCommTransport"/> for a target URI (FR-TRN-01) and hands off
/// the send. Registered as a singleton; injected into <see cref="Facade.DidCommClient"/>.
/// </summary>
public interface ITransportRouter
{
    /// <summary>
    /// Find a transport whose <see cref="IDidCommTransport.CanHandle"/> accepts
    /// <paramref name="request"/>.Endpoint and send via it. Throws
    /// <see cref="Exceptions.TransportException"/> when no registered transport accepts the
    /// endpoint scheme.
    /// </summary>
    /// <param name="request">The packed envelope + destination URI + media type.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct);
}
