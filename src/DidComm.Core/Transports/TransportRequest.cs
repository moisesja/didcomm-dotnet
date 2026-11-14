using DidComm.Transports.ReturnRoute;

namespace DidComm.Transports;

/// <summary>
/// Bytes-on-the-wire input handed to <see cref="IDidCommTransport.SendAsync"/>. The facade builds
/// the instance after packing; the return-route members (PRD §8a) are set only by
/// <see cref="Facade.DidCommClient.SendAsync"/>, and a transport that does not implement the
/// extension may ignore them.
/// </summary>
/// <param name="Endpoint">The target URI. The transport router (FR-TRN-01) picks an implementation based on <c>Endpoint.Scheme</c>.</param>
/// <param name="Payload">The packed envelope bytes — already JOSE-serialized and UTF-8 encoded.</param>
/// <param name="MediaType">The IANA media type for the payload (FR-TRN-02) — e.g. <c>application/didcomm-encrypted+json</c>.</param>
public sealed record TransportRequest(Uri Endpoint, ReadOnlyMemory<byte> Payload, string MediaType)
{
    /// <summary>
    /// The <c>return_route</c> header the packed message carries, as sent (FR-RR-07); <c>null</c>
    /// when the message carries none. A malformed header is reported as
    /// <see cref="ReturnRouteMode.None"/>, the way the receiver reads it (FR-RR-01).
    /// </summary>
    public ReturnRouteMode? ReturnRoute { get; init; }

    /// <summary>
    /// The sending DID's subject (a bare DID), set by the facade only for an authcrypt send
    /// delivered directly (no forward layer) to recipients naming one DID, and only when both this
    /// and <see cref="PeerDid"/> are at most 2048 characters long (FR-API-09); <c>null</c> otherwise.
    /// A transport that keeps return-route connections uses it, together with
    /// <see cref="PeerDid"/>, for connection affinity (FR-RR-07).
    /// </summary>
    public string? LocalDid { get; init; }

    /// <summary>
    /// The addressed recipient DID's subject (a bare DID), set together with
    /// <see cref="LocalDid"/>; <c>null</c> otherwise.
    /// </summary>
    public string? PeerDid { get; init; }

    /// <summary>
    /// The most bytes of an in-band response the transport may read (FR-RR-07). <c>null</c> (the
    /// default) means "do not read an in-band response". The facade sets it to
    /// <see cref="Facade.DidCommOptions.MaxReceiveBytes"/> only for an authcrypt send asking for
    /// <c>return_route</c> <c>all</c> or <c>thread</c> whose <see cref="LocalDid"/> is set (both DIDs
    /// within 2048 characters, see <see cref="LocalDid"/>). A transport that can return a response
    /// reads at most this many bytes and never buffers more.
    /// </summary>
    public int? MaxResponseBytes { get; init; }
}
