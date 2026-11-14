using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Client;

/// <summary>
/// The recipient side of Message Pickup 3.0 (FR-PICK-11): see what a mediator holds for you, fetch
/// and acknowledge it, or receive it as it arrives over a WebSocket. Messages the mediator
/// re-wrapped for you are opened (FR-ROUTE-06, receiver side).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Setting up.</strong> With dependency injection, call <c>AddMediatorClient()</c> on the
/// <c>AddDidComm</c> builder and resolve this class. Over HTTP the mediator's answer comes back in
/// the response. Over a WebSocket it arrives on the connection and is handed to this client by the
/// inbound dispatcher, so the container also needs
/// <c>UseWebSocketTransport(o =&gt; o.ReceiveInbound = true)</c> and <c>UseInboundDispatch()</c>;
/// without them a WebSocket route is refused before anything is sent. Constructed without
/// dependency injection (<c>new PickupClient(didComm)</c>), the client works over HTTP only, and
/// live delivery, which needs a WebSocket, is not available.
/// </para>
/// <para>
/// <strong>Delivery is at-least-once.</strong> A message can reach you more than once: when an
/// acknowledgement is lost, when a connection drops, when the mediator's lease on a delivered
/// message runs out. The clients remember recent messages
/// (<see cref="MediatorClientOptions.DedupeCapacity"/>) and do not return one twice while they
/// remember it, but an application that must never act twice on a message should also keep track
/// of the message ids it has handled.
/// </para>
/// <para>
/// <strong>Messages that do not unpack</strong> are returned as items with
/// <see cref="PickupItem.Error"/> set and are not acknowledged: the mediator keeps them and delivers
/// them again after its lease (60 seconds at this library's mediator). Discard one you have given
/// up on with <see cref="AckAsync"/>.
/// </para>
/// <para>
/// <strong>Which endpoint.</strong> The first <c>DIDCommMessaging</c> service entry of the
/// mediator's DID decides the address and so the transport; a mediator reached through routing
/// keys or another mediator is refused. Pass <c>serviceEndpointOverride</c> to use another of its
/// addresses, for example its <c>wss</c> one for live delivery when its DID document lists
/// <c>https</c> first.
/// </para>
/// <para>
/// <strong>Retrying is safe.</strong> A <see cref="TimeoutException"/> means the answer did not
/// arrive, not that nothing happened. Repeat the call: a message delivered but never acknowledged
/// comes back.
/// </para>
/// <para>
/// <strong>Clocks.</strong> Every request carries the time from <c>DidCommOptions.Clock</c>
/// (FR-RR-09). Answers and pushes over a WebSocket are checked against this agent's clock: a
/// mediator whose clock runs more than a minute ahead (the default
/// <c>ReplayProtectionOptions.ClockSkew</c>) has them dropped, and calls time out. Keep the two
/// clocks within a minute of each other.
/// </para>
/// <para>
/// <strong>Never from a handler on the same connection.</strong> Do not call this client from a
/// protocol handler or observer that is running for a message received on the same WebSocket
/// connection to the mediator: that connection reads its next message only after the handler
/// returns, so the answer would wait behind the handler until the call times out.
/// </para>
/// <para>Thread-safe; one instance per container (or per <see cref="DidCommClient"/>) is enough.</para>
/// </remarks>
/// <example>
/// <code>
/// PickupBatch batch;
/// do
/// {
///     batch = await pickup.FetchAsync(mediatorDid, myDid, limit: 10);
///     foreach (var item in batch.Items)
///     {
///         if (item.Unpacked is { } message)
///             Handle(message);
///     }
/// }
/// while (!batch.Drained);
/// </code>
/// </example>
public sealed class PickupClient
{
    /// <summary>
    /// Create a client over <paramref name="client"/> without dependency injection. It works over
    /// HTTP only: a mediator reached over a WebSocket is refused with
    /// <see cref="InvalidOperationException"/>, because nothing hands this client the answers that
    /// arrive on the connection; for the same reason live delivery is not available. Use
    /// <c>AddMediatorClient()</c> for WebSocket routes.
    /// </summary>
    /// <param name="client">The facade to send through and unpack with; it needs a transport and a service endpoint resolver (as <c>AddDidComm</c> builds it), and the keys of the DIDs your messages are encrypted for.</param>
    /// <param name="options">The settings; the defaults when <c>null</c>. Copied and checked here.</param>
    /// <param name="logger">Optional logger. Lines carry request ids, message names and counts, never a DID, key id, message id or message content.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting of <paramref name="options"/> is outside its range (<see cref="MediatorClientOptions.Validate"/>).</exception>
    public PickupClient(DidCommClient client, MediatorClientOptions? options = null, ILogger<PickupClient>? logger = null)
        : this(new MediatorClientChannel(client, options ?? new MediatorClientOptions(), receivesInbound: false, TimeProvider.System, loggerFactory: null), logger)
    {
    }

    /// <summary>The DI constructor (and the test seam): a client over a shared exchange.</summary>
    /// <param name="exchange">The container's channel, or a fake.</param>
    /// <param name="logger">Optional logger.</param>
    internal PickupClient(IMediatorExchange exchange, ILogger<PickupClient>? logger)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        Exchange = exchange;
        Logger = logger;
    }

    /// <summary>The exchange this client sends through.</summary>
    internal IMediatorExchange Exchange { get; }

    /// <summary>The client's logger, or <c>null</c>.</summary>
    internal ILogger<PickupClient>? Logger { get; }

    /// <summary>What the mediator holds for you (<c>status-request</c>).</summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID (no path, query or fragment).</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="recipientDid">Only count messages for this DID: a bare DID, or <c>null</c> for all of yours.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document: an absolute URI without user information. Trusted as given (not checked against the outbound endpoint policy), as with <c>SendOptions.ServiceEndpointOverride</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mediator's <c>status</c>.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information (<see cref="ArgumentNullException"/> for a <c>null</c> DID).</exception>
    /// <exception cref="InvalidOperationException">A WebSocket route this client cannot receive on, a mediator reached through routing, no service endpoint resolver, or too many requests already waiting; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer within <see cref="MediatorClientOptions.RequestTimeout"/> (or none in the HTTP response); the message names the request id. Retry.</exception>
    /// <exception cref="MediationProtocolException">The mediator answered with a problem report, or with a malformed status or one about another DID than <paramref name="recipientDid"/>.</exception>
    /// <exception cref="TransportException">The transport failed (over HTTP with status 200: the mediator accepted the request but its answer was too large, stalled or unreadable; retry).</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<PickupStatus> GetStatusAsync(string mediatorDid, string from, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
        => throw new NotImplementedException("#96 engineer C: PickupClient.GetStatusAsync (design §3.3).");

    /// <summary>
    /// Ask for up to <paramref name="limit"/> messages (<c>delivery-request</c>), unpack each, and
    /// acknowledge the ones that unpacked (<c>messages-received</c>) before returning. Call it until
    /// <see cref="PickupBatch.Drained"/> is <c>true</c> to take everything queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The acknowledgement is sent before the call returns, so the mediator deletes what it
    /// returned: once you have the batch, you hold the only copy. Handle it before your process can
    /// stop, or use <see cref="ReceiveLiveAsync"/>, which acknowledges an item only when you ask for
    /// the next one.
    /// </para>
    /// <para>
    /// Cancelling before the acknowledgement is sent leaves everything with the mediator. If the
    /// acknowledgement itself fails, the batch is still returned (and logged); those messages come
    /// back later and are recognised as repeats. A message that failed stays leased to you for the
    /// mediator's lease, so a loop that fetches until drained gets past failures to what is behind
    /// them; one that polls more slowly than the lease, with more failures than
    /// <paramref name="limit"/>, keeps seeing only the failures until it discards them with
    /// <see cref="AckAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="limit">The most messages to take, 1 to 100. Anything the mediator sends beyond it is not opened and not acknowledged.</param>
    /// <param name="recipientDid">Only messages for this DID: a bare DID, or <c>null</c> for all of yours. A message that unpacks for another DID is returned as a failed item.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The batch: the items in delivery order, whether the queue was drained, and the mediator's <c>status</c> when it answered with one.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="limit"/> is outside 1 to 100, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="GetStatusAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer in time; retry (nothing was acknowledged).</exception>
    /// <exception cref="MediationProtocolException">A problem report, or an answer that is malformed or about another DID than <paramref name="recipientDid"/>.</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<PickupBatch> FetchAsync(string mediatorDid, string from, int limit, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
        => throw new NotImplementedException("#96 engineer C: PickupClient.FetchAsync (design §4.7).");

    /// <summary>
    /// Tell the mediator it may delete these messages (<c>messages-received</c>): usually items that
    /// failed and that you have given up on, or a live item you want gone before you stop a stream.
    /// Sent 100 ids per message.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="ids">1 to 10 000 ids, each exactly as <see cref="PickupItem.Id"/> gave it.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mediator's <c>status</c> after the last message.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="ids"/> is empty, longer than 10 000 or has an empty, over-long or non-URI-safe id (the message names its index), or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="GetStatusAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer in time; retry (acknowledging twice is harmless).</exception>
    /// <exception cref="MediationProtocolException">A problem report or a malformed status.</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<PickupStatus> AckAsync(string mediatorDid, string from, IReadOnlyList<string> ids, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
        => throw new NotImplementedException("#96 engineer C: PickupClient.AckAsync (design §2, chunks of 100).");

    /// <summary>
    /// Turn live delivery on or off for your WebSocket connection to the mediator
    /// (<c>live-delivery-change</c>): with it on, the mediator pushes messages as they arrive.
    /// <see cref="ReceiveLiveAsync"/> turns it on and off by itself; use this method only when you
    /// handle the pushes some other way. Live delivery needs a connection that stays open, so a
    /// route that is not a WebSocket is refused before anything is sent.
    /// </summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="liveDelivery"><c>true</c> to turn live delivery on, <c>false</c> to turn it off.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mediator's <c>status</c>; <see cref="PickupStatus.LiveDelivery"/> says what is now in effect.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">The route is not a WebSocket, or as for <see cref="GetStatusAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer in time; retry.</exception>
    /// <exception cref="MediationProtocolException">A problem report (<see cref="MediationProtocolException.ProblemCode"/> <c>e.m.live-mode-not-supported</c> when the mediator cannot deliver live on this connection) or a malformed status.</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public Task<PickupStatus> SetLiveDeliveryAsync(string mediatorDid, string from, bool liveDelivery, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
        => throw new NotImplementedException("#96 engineer C: PickupClient.SetLiveDeliveryAsync (design §4.3: refused over HTTP before sending).");

    /// <summary>
    /// Receive your messages as they arrive, over a WebSocket. The stream turns live delivery on,
    /// takes what is already queued, then yields each message the mediator pushes; every
    /// <see cref="MediatorClientOptions.LivePollInterval"/> it turns live delivery on again and takes
    /// what is queued, which also picks up messages whose delivery was delayed and recovers from a
    /// dropped connection. Items that failed to unpack are yielded too, with
    /// <see cref="PickupItem.Error"/> set, and are never acknowledged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Acknowledgement.</strong> An item is acknowledged when you ask for the next one (the
    /// next <c>MoveNextAsync</c>, which <c>await foreach</c> makes when your loop body finishes). The
    /// last item before the enumeration ends — by <c>break</c>, <c>return</c>, an exception out of
    /// your loop body, cancellation or disposal — is <strong>not</strong> acknowledged and comes back
    /// later. To discard it before you stop, call
    /// <c>AckAsync(mediatorDid, from, new[] { item.Id })</c>. When the enumeration ends the stream
    /// acknowledges what you have taken and turns live delivery off.
    /// </para>
    /// <para>
    /// The arguments are checked when you call this method. The route is resolved, and refused when
    /// it is not a WebSocket, at the first <c>MoveNextAsync</c>, before anything is sent. One stream
    /// per mediator and DID may be open at a time, and at most 16 per container. A mediator that
    /// keeps failing (five cycles in a row) ends the stream with the last exception.
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>); a <c>ws</c> or <c>wss</c> URI.</param>
    /// <param name="ct">Cancellation token (also reachable through <c>WithCancellation</c>); cancelling ends the enumeration.</param>
    /// <returns>The items, as they arrive.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">At the first <c>MoveNextAsync</c>: the route is not a WebSocket, this container cannot receive on one, a stream for this mediator and DID is already open, or 16 are.</exception>
    /// <exception cref="MediationProtocolException">While enumerating: a problem report (for example <c>e.m.live-mode-not-supported</c>) or a malformed answer.</exception>
    /// <exception cref="TimeoutException">While enumerating: five cycles in a row got no answer.</exception>
    /// <exception cref="TransportException">While enumerating: five cycles in a row failed to send.</exception>
    public IAsyncEnumerable<PickupItem> ReceiveLiveAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
        => throw new NotImplementedException("#96 engineer C: PickupClient.ReceiveLiveAsync (design §4.8, R-2 acknowledgement).");
}
