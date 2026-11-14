using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The seam between the two public facades and the shared correlation channel (design D-2, §3.2).
/// <see cref="MediatorClientChannel"/> implements it (engineer A); <see cref="MediationClient"/>
/// (engineer B) and <see cref="PickupClient"/> (engineer C) are built over it and unit-test against a
/// fake. One exchange is one request and its one answer; the facades parse answers, the channel
/// never does.
/// </summary>
internal interface IMediatorExchange
{
    /// <summary>The settings in force: a validated copy, never the caller's instance (L-060).</summary>
    MediatorClientOptions Options { get; }

    /// <summary>The clock of every deadline and interval (timers through <see cref="TimeProvider.CreateTimer"/>, ages through <see cref="TimeProvider.GetTimestamp"/>).</summary>
    TimeProvider Time { get; }

    /// <summary>The duplicate filter fetches and live streams share (engineer A's).</summary>
    DuplicateFilter Duplicates { get; }

    /// <summary>The negotiated Coordinate Mediation versions (engineer B's), held here so they outlive a call.</summary>
    NegotiatedVersionCache Versions { get; }

    /// <summary>
    /// Resolve the route of one operation (design §4.3): <paramref name="serviceEndpointOverride"/>
    /// when given (trusted, not guarded — as <c>SendOptions.ServiceEndpointOverride</c>), otherwise
    /// <c>DidCommClient.ResolveDirectEndpointAsync</c>, bounded by
    /// <see cref="MediatorClientOptions.RequestTimeout"/>. Refuses before anything is sent.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID (a bare DID; the facades validate it first).</param>
    /// <param name="serviceEndpointOverride">The caller's endpoint, or <c>null</c>.</param>
    /// <param name="requireWebSocket">Live delivery: refuse a route that is not a WebSocket.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <exception cref="InvalidOperationException">A WebSocket route while this channel cannot receive (non-DI, or no <c>UseInboundDispatch()</c>), naming the fix; <paramref name="requireWebSocket"/> and the route is not a WebSocket; the mediator's first service entry is routed; no service endpoint resolver.</exception>
    /// <exception cref="TimeoutException">Resolving the mediator's DID did not finish within <see cref="MediatorClientOptions.RequestTimeout"/>.</exception>
    Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct);

    /// <summary>
    /// Send <paramref name="request"/> to the mediator on <paramref name="route"/> and return its
    /// answer (design §4.1): registered before sending, sent with <c>return_route: all</c> and
    /// <paramref name="route"/>'s endpoint as the override, never stamped by hand (FR-RR-09), awaited
    /// under <see cref="MediatorClientOptions.RequestTimeout"/>. The answer is a verified snapshot
    /// authcrypted by <paramref name="mediatorDid"/> to <paramref name="localDid"/>, threaded to the
    /// request (or, without a thread, the only candidate; §4.2), whose type is one of
    /// <paramref name="spec"/>'s. The continuation never runs on the receive path.
    /// </summary>
    /// <param name="route">The operation's route, from <see cref="ResolveRouteAsync"/>.</param>
    /// <param name="request">The request, built with <c>UuidV4MessageIdGenerator.Instance</c> (a fresh UUID id), <c>from</c> = <paramref name="localDid"/>, <c>to</c> = [<paramref name="mediatorDid"/>].</param>
    /// <param name="spec">What the request expects back.</param>
    /// <param name="mediatorDid">The mediator's DID: the recipient, and the only sender whose answer counts.</param>
    /// <param name="localDid">Our DID: the authcrypt sender, and the only DID the answer may be decrypted for.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The answer's snapshot; the facade parses it (<see cref="InboundMessageSnapshot.DeserializeMessage"/>).</returns>
    /// <exception cref="InvalidOperationException"><see cref="MediatorClientOptions.MaxPendingRequests"/> requests are already waiting (refused before sending), or the request id is already pending.</exception>
    /// <exception cref="TimeoutException">No answer within <see cref="MediatorClientOptions.RequestTimeout"/>, or (HTTP) the response carried no answer to this request; the text names the request id and <see cref="MediatorExchangeSpec.Name"/>, never a DID.</exception>
    /// <exception cref="MediationProtocolException">The mediator answered with a problem report (<see cref="MediationProtocolException.ProblemCode"/> sanitized).</exception>
    /// <exception cref="Exceptions.TransportException">Propagated from the send as it is.</exception>
    /// <exception cref="ObjectDisposedException">The channel was disposed.</exception>
    Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct);

    /// <summary>
    /// Open the live-push mailbox of (<paramref name="mediatorDid"/>, <paramref name="localDid"/>)
    /// (design D-18). Thid-less <c>delivery</c> messages from that mediator to that DID arriving over
    /// a WebSocket are buffered in it from now until it is disposed. The live stream disposes it when
    /// its enumeration ends.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID (keyed by DID subject).</param>
    /// <param name="localDid">Our DID (keyed by DID subject).</param>
    /// <exception cref="InvalidOperationException">A stream for this pair is already open, or <see cref="MediatorClientChannel.MaxLiveStreams"/> are.</exception>
    /// <exception cref="ObjectDisposedException">The channel was disposed.</exception>
    LiveRegistration RegisterLiveStream(string mediatorDid, string localDid);

    /// <summary>Unpack one delivered message under the unpack bounds (design D-19; the live stream's sequential path).</summary>
    /// <param name="payload">The delivered bytes.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw — a bad message is a failure outcome.</param>
    Task<DeliveredOutcome> UnpackDeliveredAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>Unpack the messages of one delivery, at most <see cref="DeliveredMessageUnpacker.FetchConcurrency"/> at a time (design D-19; the fetch path). Outcomes are in the order of <paramref name="payloads"/>.</summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw — a bad message is a failure outcome.</param>
    Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct);
}
