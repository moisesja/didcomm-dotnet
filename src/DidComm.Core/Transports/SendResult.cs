using DidComm.Facade;

namespace DidComm.Transports;

/// <summary>
/// Outcome of <see cref="Facade.DidCommClient.SendAsync"/> — bundles the pack stage's
/// <see cref="PackEncryptedResult"/> together with the transport-level result and the actual
/// URI bytes were delivered to.
/// </summary>
/// <param name="Packed">The pack-stage output (envelope + service endpoint + fallbacks).</param>
/// <param name="Transport">The transport-level outcome (accepted + HTTP status when applicable).</param>
/// <param name="EndpointUsed">The URI actually used for delivery — either the resolved service endpoint or the caller-supplied override.</param>
public sealed record SendResult(
    PackEncryptedResult Packed,
    TransportResult Transport,
    Uri EndpointUsed);
