using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// What one kind of request expects back (design §3.2, §4.2): the matcher completes a pending
/// request only with an answer whose type matches one of <see cref="Expected"/>
/// (<see cref="MessageTypeUri.Matches"/>), or with a problem report (Report Problem 2.x, or Message
/// Pickup 4.0's own), which the exchange turns into <see cref="MediationProtocolException"/>. One
/// static instance per request type and protocol version, defined by the facade that sends it
/// (engineer B: Discover Features and Coordinate Mediation 3.0 / 2.0; engineer C: Message Pickup 3.0
/// and 4.0, <see cref="PickupWire"/>). Owner of the type: engineer A.
/// </summary>
/// <param name="Name">The request's message name for log lines and exception texts (a constant such as <c>mediate-request</c>; never a wire value).</param>
/// <param name="Expected">The answer types that complete the request.</param>
/// <param name="ExpectsDelivery">Whether a Message Pickup <c>delivery</c> is an expected answer; it must agree with <paramref name="Expected"/> (<see cref="MediatorAnswerTypes.Validate"/>). A <c>delivery</c> completes the request only when its <c>thid</c> names it, on every path: one without <c>thid</c> completes no request, in the HTTP response to it as anywhere else (over a WebSocket it is a live push).</param>
internal sealed record MediatorExchangeSpec(string Name, IReadOnlyList<MessageTypeUri> Expected, bool ExpectsDelivery);
