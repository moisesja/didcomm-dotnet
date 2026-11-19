using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// What one kind of request expects back (design §3.2, §4.2): the matcher completes a pending
/// request only with an answer whose type matches one of <see cref="Expected"/>
/// (<see cref="MessageTypeUri.Matches"/>), or with a Report Problem 2.x message, which the exchange
/// turns into <see cref="MediationProtocolException"/>. One static instance per request type and
/// protocol version, defined by the facade that sends it (engineer B: Discover Features and
/// Coordinate Mediation 3.0 / 2.0; engineer C: Message Pickup 3.0). Owner of the type: engineer A.
/// </summary>
/// <param name="Name">The request's message name for log lines and exception texts (a constant such as <c>mediate-request</c>; never a wire value).</param>
/// <param name="Expected">The answer types that complete the request.</param>
/// <param name="ExpectsDelivery">Whether a Message Pickup <c>delivery</c> is an expected answer. Over HTTP a thid-less <c>delivery</c> completes the request it answers only when this is set; over a WebSocket a thid-less <c>delivery</c> is a live push and never completes a request.</param>
internal sealed record MediatorExchangeSpec(string Name, IReadOnlyList<MessageTypeUri> Expected, bool ExpectsDelivery)
{
    /// <summary>
    /// Whether only an answer that names the request (<c>thid</c>, or <c>pthid</c> for a problem
    /// report) completes it when it arrives on its own, not in an HTTP response. Set by
    /// <see cref="PickupClient.AckAsync"/> on the requests it sends after it gave up waiting for an
    /// earlier one on a WebSocket: a status without a thread could be that request's late answer.
    /// Such a request still counts as a candidate for a thread-less answer, so it never makes one
    /// unambiguous for another request; it is just never completed by one. An answer bound to an
    /// HTTP response is the response's own and is not affected.
    /// </summary>
    public bool RequiresThread { get; init; }
}
