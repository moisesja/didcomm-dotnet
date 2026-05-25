namespace DidComm.Transports;

/// <summary>
/// Bytes-on-the-wire input handed to <see cref="IDidCommTransport.SendAsync"/>. Phase 5 transports
/// take exactly this shape; the facade builds the instance after packing.
/// </summary>
/// <param name="Endpoint">The target URI. The transport router (FR-TRN-01) picks an implementation based on <c>Endpoint.Scheme</c>.</param>
/// <param name="Payload">The packed envelope bytes — already JOSE-serialized and UTF-8 encoded.</param>
/// <param name="MediaType">The IANA media type for the payload (FR-TRN-02) — e.g. <c>application/didcomm-encrypted+json</c>.</param>
public sealed record TransportRequest(Uri Endpoint, ReadOnlyMemory<byte> Payload, string MediaType);
