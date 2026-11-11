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
/// <example>
/// Inspected in <c>samples/02-Cookbook</c> (section P).
/// </example>
public sealed record SendResult(
    PackEncryptedResult Packed,
    TransportResult Transport,
    Uri EndpointUsed)
{
    /// <summary>
    /// The message the recipient returned over the same HTTP request (the return-route extension,
    /// PRD §8a, FR-RR-07), unpacked; <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non-null only when all of these hold: the message went out with <c>return_route</c>
    /// <c>all</c> or <c>thread</c> from an authcrypt sender (<see cref="SendOptions.From"/> set); the
    /// response was HTTP <c>200</c> with a DIDComm media type; and the returned message unpacked as
    /// authcrypt whose sender key belongs to the DID the send addressed and whose decrypting key
    /// belongs to the sending DID (DID subjects compared, PRD §4.3). A returned message that fails
    /// that identity rule is discarded and logged (by the request's message id and the reason
    /// only), and this property stays <c>null</c>. A returned message that could not be read
    /// (over <see cref="DidCommOptions.MaxReceiveBytes"/>, not received within the transport's
    /// timeout, or not unpackable) fails the send with a
    /// <see cref="Exceptions.TransportException"/> whose status is <c>200</c> instead.
    /// </para>
    /// <para>
    /// The returned message is neither dispatched to a protocol handler nor run through replay
    /// admission: the caller asked for it and handles it. Over WebSocket, returned messages reach
    /// the registered inbound consumer instead, so this property is always <c>null</c> there.
    /// <see cref="TransportResult.ResponsePayload"/> on <see cref="Transport"/> holds the raw bytes
    /// the transport read; only this property has passed the identity rule.
    /// </para>
    /// </remarks>
    public UnpackResult? Returned { get; init; }
}
