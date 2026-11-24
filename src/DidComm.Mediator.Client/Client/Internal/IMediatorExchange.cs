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
    NegotiatedVersionCache<CoordinateMediationVersion> Versions { get; }

    /// <summary>The negotiated Message Pickup versions (#100 DC-6), held here so they outlive a call; the same bounds as <see cref="Versions"/>.</summary>
    NegotiatedVersionCache<MessagePickupVersion> PickupVersions { get; }

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
    /// request (its <c>thid</c>, or a problem report's <c>pthid</c>, names it, on every path, the HTTP
    /// response to it included; §4.2), whose type is one of
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
    /// <see cref="ExchangeAsync"/> for a request the mediator may leave without an answer: Message
    /// Pickup 3.0 says a mediator SHOULD answer <c>messages-received</c> with a <c>status</c>, not
    /// that it must (the Identus mediator sends none). Over HTTP, a response that carried no answer
    /// to the request returns <c>null</c> at once, where <see cref="ExchangeAsync"/> throws
    /// <see cref="TimeoutException"/>. On any other route (a WebSocket) silence cannot be told from a
    /// slow answer: with <paramref name="answerWait"/>, the answer is waited for that long once the
    /// request is sent, within the request's own deadline, and none by then returns <c>null</c>;
    /// without it, the wait is as for <see cref="ExchangeAsync"/> and never returns <c>null</c>. A
    /// request whose send did not complete never returns <c>null</c>: that is a
    /// <see cref="TimeoutException"/> or a transport failure, as for <see cref="ExchangeAsync"/>.
    /// Everything else is as for <see cref="ExchangeAsync"/>: the deadline, the matcher, a problem
    /// report, a transport failure. The default implementation, for test exchanges that send at once
    /// and do not model an empty HTTP response, starts <paramref name="answerWait"/> at the call.
    /// </summary>
    /// <param name="route">The operation's route, from <see cref="ResolveRouteAsync"/>.</param>
    /// <param name="request">The request (as for <see cref="ExchangeAsync"/>).</param>
    /// <param name="spec">What the request expects back.</param>
    /// <param name="mediatorDid">The mediator's DID.</param>
    /// <param name="localDid">Our DID.</param>
    /// <param name="answerWait">On a route that is not HTTP, how long to wait for the answer once the request is sent (greater than zero); <c>null</c> to wait as <see cref="ExchangeAsync"/> does. Ignored over HTTP.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The answer's snapshot, or <c>null</c> when the HTTP response carried none or, with <paramref name="answerWait"/>, none came within it after the send.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="answerWait"/> is zero or negative; nothing was sent.</exception>
    async Task<InboundMessageSnapshot?> ExchangeAllowingNoAnswerAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, TimeSpan? answerWait, CancellationToken ct)
    {
        if (answerWait is { } positive)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(positive, TimeSpan.Zero, nameof(answerWait));
        if (route.IsHttp || answerWait is not { } wait)
            return await ExchangeAsync(route, request, spec, mediatorDid, localDid, ct).ConfigureAwait(false);

        using var window = new GuardedDeadline(Time, wait, ct);
        try
        {
            return await ExchangeAsync(route, request, spec, mediatorDid, localDid, window.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (window.TimedOut)
        {
            return null;
        }
    }

    /// <summary>
    /// Send <paramref name="request"/> to the mediator on <paramref name="route"/> and expect no
    /// answer (#100 DC-8): Message Pickup 4.0 <c>messages-received</c>, which carries no
    /// <c>return_route</c> (4.0 L35) and which the mediator does not answer. Sent without a
    /// <c>return_route</c> header (<c>none</c> is the extension's default when it is omitted), with
    /// <paramref name="route"/>'s endpoint as the override and authcrypted from
    /// <paramref name="localDid"/>; never stamped by hand (the send stamps <c>created_time</c> on its
    /// copy, FR-RR-09). Nothing is registered for an answer, and whatever an HTTP response carries is
    /// ignored. It completes when the send does, bounded by
    /// <see cref="MediatorClientOptions.RequestTimeout"/> and the owner's shutdown, and it counts
    /// against <see cref="MediatorClientOptions.MaxPendingRequests"/> while the send runs.
    /// </summary>
    /// <param name="route">The operation's route, from <see cref="ResolveRouteAsync"/>.</param>
    /// <param name="request">The message, built with <c>UuidV4MessageIdGenerator.Instance</c>, <c>from</c> = <paramref name="localDid"/>, <c>to</c> = [<paramref name="mediatorDid"/>].</param>
    /// <param name="name">The message's name for log lines and exception texts (a constant such as <c>messages-received</c>; never a wire value).</param>
    /// <param name="mediatorDid">The mediator's DID: the recipient.</param>
    /// <param name="localDid">Our DID: the authcrypt sender.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <exception cref="InvalidOperationException"><see cref="MediatorClientOptions.MaxPendingRequests"/> requests are already waiting (refused before sending).</exception>
    /// <exception cref="TimeoutException">The send did not complete within <see cref="MediatorClientOptions.RequestTimeout"/>; the text names the request id and <paramref name="name"/>, never a DID. The mediator may still have received it.</exception>
    /// <exception cref="Exceptions.TransportException">Propagated from the send as it is.</exception>
    /// <exception cref="ObjectDisposedException">The channel was disposed.</exception>
    Task SendOneWayAsync(MediatorRoute route, Message request, string name, string mediatorDid, string localDid, CancellationToken ct);

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

    /// <summary>
    /// Open the messages of one delivery — a fetch's answer, a live drain round's answer, or a live
    /// push — under one budget (<see cref="MediatorClientOptions.UnpackTimeout"/>, owner decision 2-A),
    /// at most <see cref="DeliveredMessageUnpacker.FetchConcurrency"/> at a time (design D-19), each
    /// with a share of the time left and every host unpack call reserved before it starts
    /// (<see cref="DeliveredMessageUnpacker"/>, #111). A message remembered as slow to open in
    /// <paramref name="scope"/> fails at once without being opened (<see cref="SlowMemory"/>). A
    /// message the budget, its share or a bound refuses is a <see cref="TimeoutException"/> failure
    /// naming <paramref name="requestId"/>. Outcomes are in the order of <paramref name="messages"/>.
    /// </summary>
    /// <param name="scope">The mediator and the mailbox the delivery came from, as the host's call named them (#111).</param>
    /// <param name="messages">The delivered messages (attachment id and bytes), in delivery order.</param>
    /// <param name="requestId">Our id of the request the delivery answers; <c>null</c> for a live push.</param>
    /// <param name="ct">The caller's cancellation; it and the channel's disposal alone make the call throw — a bad message is a failure outcome.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The channel was disposed, before or while the delivery was being opened (ADV3-7: the disposal cancels the host code opening it).</exception>
    Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, string? requestId, CancellationToken ct);
}
