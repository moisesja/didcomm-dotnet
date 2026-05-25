namespace DidComm.Transports;

/// <summary>
/// Outcome returned by <see cref="IDidCommTransport.SendAsync"/>. Phase 5 transports are
/// delivery-only (FR-TRN-03) — this is the success indicator the facade surfaces back to the
/// caller, not a protocol-level reply.
/// </summary>
/// <param name="Accepted"><c>true</c> when the remote indicated successful receipt (any 2xx for HTTP per FR-TRN-05; a completed send for WebSocket).</param>
/// <param name="HttpStatusCode">The HTTP status code observed, when the transport is HTTP-flavored. <c>null</c> for non-HTTP transports.</param>
public sealed record TransportResult(bool Accepted, int? HttpStatusCode);
