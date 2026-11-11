namespace DidComm.Transports;

/// <summary>
/// Outcome returned by <see cref="IDidCommTransport.SendAsync"/>: the success indicator the facade
/// surfaces back to the caller. Transports are delivery-only (FR-TRN-03) unless the return-route
/// extension is in effect (PRD §8a): an HTTP transport may then hand back the response body in
/// <see cref="ResponsePayload"/>, raw and unverified.
/// </summary>
/// <param name="Accepted"><c>true</c> when the remote indicated successful receipt (any 2xx for HTTP per FR-TRN-05; a completed send for WebSocket).</param>
/// <param name="HttpStatusCode">The HTTP status code observed, when the transport is HTTP-flavored. <c>null</c> for non-HTTP transports.</param>
public sealed record TransportResult(bool Accepted, int? HttpStatusCode)
{
    /// <summary>
    /// The in-band response body the transport read (FR-RR-07): raw, unverified bytes, at most
    /// <see cref="TransportRequest.MaxResponseBytes"/> long. <c>null</c> when the request did not
    /// ask for one, the response carried none (or an empty one), or it was discarded (see
    /// <see cref="ResponseDiscarded"/>).
    /// </summary>
    public ReadOnlyMemory<byte>? ResponsePayload { get; init; }

    /// <summary>
    /// The DIDComm media type of <see cref="ResponsePayload"/>, as the canonical constant from
    /// <see cref="Messages.MediaTypes"/> that the response's <c>Content-Type</c> matched;
    /// <c>null</c> when there is no payload.
    /// </summary>
    public string? ResponseMediaType { get; init; }

    /// <summary>
    /// <c>true</c> when the peer returned a DIDComm body that was not read: it was over
    /// <see cref="TransportRequest.MaxResponseBytes"/>, or it was not received within the
    /// transport's timeout, or reading it failed. <see cref="Accepted"/> is unaffected: the
    /// request itself was delivered. <see cref="Facade.DidCommClient.SendAsync"/>, having asked
    /// for the response, turns this into a <see cref="Exceptions.TransportException"/>.
    /// </summary>
    public bool ResponseDiscarded { get; init; }
}
