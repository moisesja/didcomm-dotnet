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
    /// <strong>What it proves, and what it does not.</strong> A non-null value proves one thing: the
    /// message was authcrypted by the DID the send addressed to the DID that sent it. It is not
    /// FR-RR-08 replay-admitted, and it does not prove that it answers this request: a reply the same
    /// peer authcrypted earlier, for another request, passes the same rule. Over HTTPS the response
    /// arrives on the TLS connection the client opened to the endpoint it chose, so only that
    /// endpoint (or a party terminating TLS for it) could have returned it. Correlate it with the
    /// request on its <c>thid</c> (DD-11) and handle it idempotently.
    /// </para>
    /// <para>
    /// The decrypting DID is the one of the first recipient key of the envelope this client holds
    /// (in envelope order). So a message encrypted to several recipients, whose first held key
    /// belongs to another local DID than the sending one, is not attributed to the sending DID and is
    /// discarded. A receiver's return-route replies (FR-RR-03) are always encrypted to exactly one
    /// peer.
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
