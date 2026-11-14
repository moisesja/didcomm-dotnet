using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
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
        : this(new MediatorClientChannel(client, options ?? new MediatorClientOptions(), receivesInbound: false, TimeProvider.System, SingleLoggerFactory.For(logger)), logger)
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
        Log = new PickupLog(logger);
    }

    /// <summary>The most messages one <see cref="FetchAsync"/> asks for (the mediator's default batch bound).</summary>
    internal const int MaxFetchLimit = 100;

    /// <summary>The most ids one <see cref="AckAsync"/> takes.</summary>
    internal const int MaxAckIds = 10_000;

    /// <summary>The most ids one <c>messages-received</c> carries (the mediator's default <c>MaxAckIds</c>).</summary>
    internal const int AckChunk = 100;

    /// <summary>The exchange this client sends through.</summary>
    internal IMediatorExchange Exchange { get; }

    /// <summary>The client's logger, or <c>null</c>.</summary>
    internal ILogger<PickupClient>? Logger { get; }

    /// <summary>The client's log lines (rate limits shared by its fetches and live streams).</summary>
    internal PickupLog Log { get; }

    /// <summary>What the mediator holds for you (<c>status-request</c>).</summary>
    /// <param name="mediatorDid">The mediator's DID: a bare DID (no path, query or fragment).</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="recipientDid">Only count messages for this DID: a bare DID, or <c>null</c> for all of yours.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document: an absolute URI without user information. Trusted as given (not checked against the outbound endpoint policy), as with <c>SendOptions.ServiceEndpointOverride</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mediator's <c>status</c>.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information (<see cref="ArgumentNullException"/> for a <c>null</c> DID).</exception>
    /// <exception cref="InvalidOperationException">A WebSocket route this client cannot receive on, a mediator reached through routing, no service endpoint resolver, or too many requests already waiting; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer within <see cref="MediatorClientOptions.RequestTimeout"/> (or none in the HTTP response); the message names the request id. Retry. (Running out of time while finding the mediator's endpoint names none: nothing was sent.)</exception>
    /// <exception cref="MediationProtocolException">The mediator answered with a problem report, or with a malformed status or one about another DID than <paramref name="recipientDid"/>.</exception>
    /// <exception cref="TransportException">The transport failed (over HTTP with status 200: the mediator accepted the request but its answer was too large, stalled or unreadable; retry).</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public async Task<PickupStatus> GetStatusAsync(string mediatorDid, string from, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        RequireOptionalBareDid(recipientDid, nameof(recipientDid));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var spec = DeliveryReader.StatusRequestSpec;
        var request = MessagePickup.CreateStatusRequest(from, mediatorDid, recipientDid, UuidV4MessageIdGenerator.Instance);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        return DeliveryReader.ReadStatus(DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: true, recipientDid);
    }

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
    /// <para>
    /// <strong>Repeats.</strong> A message the clients already returned is acknowledged again and
    /// left out while they remember it (<see cref="MediatorClientOptions.DedupeCapacity"/>). With
    /// <c>DedupeCapacity</c> set to 0 nothing is remembered from one call to the next, but a message
    /// that comes twice in the same delivery is still returned only once.
    /// </para>
    /// <para>
    /// <strong>One message to several of your DIDs.</strong> A sender can encrypt one message to
    /// several DIDs whose keys this agent holds, and the mediator then queues a copy for each. Every
    /// copy opens with the first of those keys the message names, so the copies are the same message:
    /// it is returned once (its <see cref="UnpackResult.RecipientKid"/> names that first DID) and the
    /// other copies are acknowledged as repeats. Under a <paramref name="recipientDid"/> filter such a
    /// message is that DID's when its sender encrypted it to that DID too, whichever key opened it.
    /// </para>
    /// <para>
    /// <strong>Who sent it.</strong> The message inside an item can be plaintext or anoncrypted, so
    /// its <c>from</c> may be anyone's claim: check <see cref="UnpackResult.Authenticated"/> before you
    /// trust it.
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="limit">The most messages to take, 1 to 100. Anything the mediator sends beyond it is not opened and not acknowledged.</param>
    /// <param name="recipientDid">Only messages for this DID: a bare DID, or <c>null</c> for all of yours. A message is this DID's when this DID's key opened it or its sender encrypted it to this DID too; any other is returned as a failed item and not acknowledged.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The batch: the items in delivery order, whether the queue was drained, and the mediator's <c>status</c> when it answered with one.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="limit"/> is outside 1 to 100, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="GetStatusAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer in time; retry (nothing was acknowledged).</exception>
    /// <exception cref="MediationProtocolException">A problem report; an answer that is malformed or about another DID than <paramref name="recipientDid"/>; or a delivery in which no message has a usable id, so nothing in it could be acknowledged.</exception>
    /// <exception cref="TransportException">The transport failed. Over HTTP with <see cref="TransportException.HttpStatusCode"/> 200, the mediator answered but its delivery was larger than this agent's <c>DidCommOptions.MaxReceiveBytes</c>, or could not be read: retry with a smaller <paramref name="limit"/>, or raise <c>MaxReceiveBytes</c>.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public async Task<PickupBatch> FetchAsync(string mediatorDid, string from, int limit, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        if (limit is < 1 or > MaxFetchLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), "The limit must be from 1 to 100.");
        RequireOptionalBareDid(recipientDid, nameof(recipientDid));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var spec = DeliveryReader.DeliveryRequestSpec;
        var request = MessagePickup.CreateDeliveryRequest(from, mediatorDid, limit, recipientDid, UuidV4MessageIdGenerator.Instance);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        var message = DeliveryReader.Open(answer, request.Id, spec);

        // Nothing deliverable: the mediator answered with its status.
        if (DeliveryReader.IsStatus(message))
            return new PickupBatch(Array.Empty<PickupItem>(), drained: true, DeliveryReader.ReadStatus(message, request.Id, spec, checkFilter: true, recipientDid));
        if (!DeliveryReader.IsDelivery(message))
            throw DeliveryReader.Unexpected(request.Id, spec);
        DeliveryReader.RequireDeliveryFilterEcho(message, request.Id, spec, recipientDid);

        // Only the first `limit` attachments are read (L-055): what the mediator sent beyond the
        // request is not decoded, not unpacked and not acknowledged, so it stays queued.
        var contents = DeliveryReader.ReadEntries(message.Attachments, limit);
        if (contents.AttachmentCount == 0)
            return new PickupBatch(Array.Empty<PickupItem>(), drained: true, status: null);
        // A delivery of messages none of which can be acknowledged is not an empty batch: returned
        // as one (not drained, no items), it would keep a fetch-until-drained loop spinning.
        if (contents.Entries.Count == 0)
            throw DeliveryReader.NoUsableIds(request.Id, spec);
        LogReading(contents, request.Id);

        var payloads = new List<ReadOnlyMemory<byte>>(contents.Entries.Count);
        foreach (var entry in contents.Entries)
        {
            if (entry.Payload is { } payload)
                payloads.Add(payload);
        }
        var outcomes = payloads.Count == 0
            ? Array.Empty<DeliveredOutcome>()
            : await Exchange.UnpackDeliveredBatchAsync(payloads, ct).ConfigureAwait(false);
        if (outcomes.Count != payloads.Count)
            throw new InvalidOperationException("The unpacker returned another number of outcomes than it was given messages.");

        // Classify in delivery order. Failures (did not decode, did not unpack, for another DID than
        // the filter) are returned and never acknowledged (FR-PICK-11); a message already returned
        // once, or earlier in this batch, is acknowledged and left out.
        var items = new List<PickupItem>(contents.Entries.Count);
        var acknowledge = new List<string>(contents.Entries.Count);
        var commit = new List<UInt128>(contents.Entries.Count);
        var seen = new HashSet<UInt128>();
        var failures = new List<Exception>();
        var mismatches = 0;
        var repeats = 0;
        var next = 0;
        foreach (var entry in contents.Entries)
        {
            if (entry.Payload is null)
            {
                failures.Add(entry.Error!);
                items.Add(new PickupItem(entry.Id, unpacked: null, entry.Error));
                continue;
            }
            var outcome = outcomes[next++];
            if (outcome.Unpacked is not { } unpacked)
            {
                failures.Add(outcome.Error!);
                items.Add(new PickupItem(entry.Id, unpacked: null, outcome.Error));
                continue;
            }
            if (recipientDid is not null && !IsFor(unpacked, recipientDid))
            {
                // D-20: the filter is enforced, not trusted to the mediator (IsFor).
                mismatches++;
                items.Add(new PickupItem(entry.Id, unpacked: null, new MediationProtocolException(
                    $"A message delivered for request '{request.Id}' was decrypted for another DID than its recipient_did filter; it was not acknowledged.", request.Id)));
                continue;
            }
            if (Exchange.Duplicates.Contains(outcome.DedupeKey) || !seen.Add(outcome.DedupeKey))
            {
                repeats++;
                acknowledge.Add(entry.Id);
                continue;
            }
            items.Add(new PickupItem(entry.Id, unpacked, error: null));
            acknowledge.Add(entry.Id);
            commit.Add(outcome.DedupeKey);
        }
        Log.UnpackFailures(request.Id, failures);
        if (mismatches > 0)
            Log.FilterMismatches(request.Id, mismatches);

        // The last cancellation point: nothing is acknowledged or remembered yet, so everything
        // comes back. After it the batch is returned whatever happens to the acknowledgement.
        ct.ThrowIfCancellationRequested();
        foreach (var key in commit)
            Exchange.Duplicates.Add(key);
        if (acknowledge.Count > 0)
            await AcknowledgeQuietlyAsync(route, mediatorDid, from, acknowledge).ConfigureAwait(false);

        Log.Fetched(request.Id, commit.Count, failures.Count + mismatches, repeats);
        return new PickupBatch(items, drained: false, status: null);
    }

    /// <summary>
    /// Tell the mediator it may delete these messages (<c>messages-received</c>): usually items that
    /// failed and that you have given up on, or a live item you want gone before you stop a stream.
    /// Sent 100 ids per message.
    /// </summary>
    /// <remarks>
    /// 100 ids per message is what this library's mediator accepts by default. A mediator can be
    /// configured to accept fewer (this library's down to its delivery batch size, 10 by default);
    /// such a mediator refuses a longer message as malformed, so call this method with at most as
    /// many ids as it accepts.
    /// </remarks>
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
    public async Task<PickupStatus> AckAsync(string mediatorDid, string from, IReadOnlyList<string> ids, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        var copy = RequireIds(ids, nameof(ids));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var spec = DeliveryReader.MessagesReceivedSpec;
        PickupStatus? status = null;
        for (var start = 0; start < copy.Length; start += AckChunk)
        {
            var chunk = copy.AsSpan(start, Math.Min(AckChunk, copy.Length - start)).ToArray();
            var request = MessagePickup.CreateMessagesReceived(from, mediatorDid, chunk, UuidV4MessageIdGenerator.Instance);
            var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
            status = DeliveryReader.ReadStatus(DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: false, filter: null);
        }
        return status!;
    }

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
    public async Task<PickupStatus> SetLiveDeliveryAsync(string mediatorDid, string from, bool liveDelivery, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        // Refused before anything is sent when the route is not a WebSocket (design §4.3).
        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: true, ct).ConfigureAwait(false);
        var spec = DeliveryReader.LiveDeliveryChangeSpec;
        var request = MessagePickup.CreateLiveDeliveryChange(from, mediatorDid, liveDelivery, UuidV4MessageIdGenerator.Instance);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        return DeliveryReader.ReadStatus(DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: false, filter: null);
    }

    /// <summary>
    /// Receive your messages as they arrive, over a WebSocket. The stream turns live delivery on,
    /// takes what is already queued, then yields each message the mediator pushes. Every
    /// <see cref="MediatorClientOptions.LivePollInterval"/> it turns live delivery on again and takes
    /// what is queued: that picks up messages whose delivery was delayed and recovers from a dropped
    /// connection. Items that failed to unpack are yielded too, with <see cref="PickupItem.Error"/>
    /// set; they are never acknowledged and are yielded again when the mediator delivers them again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Acknowledgement.</strong> An item is acknowledged when you ask for the next one: the
    /// next <c>MoveNextAsync</c>, which <c>await foreach</c> makes when your loop body has finished
    /// with the item. Nothing is acknowledged before it was handed to you. Acknowledgements are sent
    /// in batches (when ten are waiting, when nothing else is ready, or after ten seconds) of at most
    /// ten ids per message, which a mediator that accepts no more ids per message than it delivers
    /// at once (ten by default at this library's mediator) still takes. The stream remembers the item
    /// (<see cref="MediatorClientOptions.DedupeCapacity"/>) so a repeat is acknowledged and not
    /// yielded again.
    /// </para>
    /// <para>
    /// <strong>Stopping.</strong> When the enumeration ends — by <c>break</c>, <c>return</c>, an
    /// exception out of your loop body, cancellation, or a failure of the stream — the last item it
    /// yielded is <strong>not</strong> acknowledged, because the stream cannot tell whether you handled
    /// it: the mediator delivers it again later. To discard the last item before stopping, call
    /// <c>AckAsync(mediatorDid, from, new[] { item.Id })</c>. A token cancelled while your loop body
    /// runs leaves that item unacknowledged too. Items the stream had received but not yet yielded
    /// are never acknowledged; they come back after the connection closes or the mediator's lease
    /// ends. On the way out the stream sends the acknowledgements still waiting and turns live
    /// delivery off, all within one <see cref="MediatorClientOptions.RequestTimeout"/> whatever your
    /// token says. Enumerate with <c>await foreach</c> (or dispose the enumerator): that is what
    /// closes the stream and frees its place.
    /// </para>
    /// <para>
    /// <strong>Polling is part of the design.</strong> A message whose sender delayed it is never
    /// pushed when it falls due, and the mediator has no scheduler that would push it later, now or
    /// in a future version (FR-PICK-08). The drain when the stream starts and the one every
    /// <see cref="MediatorClientOptions.LivePollInterval"/> are how such a message reaches you, and
    /// how the stream recovers from a dropped connection: they are the lasting design, not a
    /// stopgap. Poll at least as often as the longest delay you expect your senders to ask for (this
    /// library's mediator caps a delay at 1 hour by default, <c>MediatorOptions.MaxForwardDelay</c>),
    /// or such a message waits up to one interval longer.
    /// </para>
    /// <para>
    /// <strong>Starting and failing.</strong> The arguments are checked when you call this method.
    /// The route is resolved at the first <c>MoveNextAsync</c>, and refused when it is not a
    /// WebSocket, before anything is sent. One stream per mediator and DID may be open at a time,
    /// and at most 16 per container. A step that got no answer (<see cref="TimeoutException"/>) or
    /// failed in the transport (<see cref="TransportException"/>) is retried after a pause that
    /// starts at one second and doubles up to 30; so is a request refused because too many of the
    /// container's requests are already waiting (an <see cref="InvalidOperationException"/>; every
    /// other one is thrown at once). Five failures in a row end the stream with the last
    /// exception. Any other failure ends it at once with its exception: a problem report from the
    /// mediator, a malformed answer, a key of <paramref name="from"/> this agent does not hold, the
    /// container's disposal.
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>); a <c>ws</c> or <c>wss</c> URI.</param>
    /// <param name="ct">Cancellation token (combined with one passed through <c>WithCancellation</c>); cancelling ends the enumeration.</param>
    /// <returns>The items, as they arrive.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">At the first <c>MoveNextAsync</c>: the route is not a WebSocket, this container cannot receive on one, a stream for this mediator and DID is already open, or 16 are; nothing was sent. While enumerating: a refusal of the send path (no key curve in common with the mediator, a mediator reached through routing, no transport router), at once; or five refusals in a row because too many of the container's requests were waiting.</exception>
    /// <exception cref="MediationProtocolException">While enumerating: a problem report (for example <c>e.m.live-mode-not-supported</c>), a malformed answer, or a drain answered with a delivery in which no message has a usable id.</exception>
    /// <exception cref="TimeoutException">While enumerating: five steps in a row got no answer.</exception>
    /// <exception cref="TransportException">While enumerating: five steps in a row failed to send.</exception>
    /// <exception cref="SecretNotFoundException">While enumerating: this agent holds no key for <paramref name="from"/> (at once, like any failure that no retry can fix).</exception>
    /// <exception cref="ObjectDisposedException">While enumerating: the container was disposed.</exception>
    public IAsyncEnumerable<PickupItem> ReceiveLiveAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        // Checked here, when the method is called; the stream itself starts at the first MoveNextAsync.
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));
        return LiveDeliveryStream.ReceiveAsync(Exchange, Log, mediatorDid, from, serviceEndpointOverride, ct);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // Acknowledge on a deadline of our own (RequestTimeout), not the caller's token: the batch is
    // committed, so a cancellation now would only make the messages come back. Any failure is
    // logged by type name; the messages come back and are then recognised as repeats.
    private async Task AcknowledgeQuietlyAsync(MediatorRoute route, string mediatorDid, string from, IReadOnlyList<string> ids)
    {
        var request = MessagePickup.CreateMessagesReceived(from, mediatorDid, ids, UuidV4MessageIdGenerator.Instance);
        try
        {
            using var deadline = OwnDeadline.Start(Exchange.Time, Exchange.Options.RequestTimeout);
            await Exchange.ExchangeAsync(route, request, DeliveryReader.MessagesReceivedSpec, mediatorDid, from, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.AckFailed(request.Id, ids.Count, ex);
        }
    }

    private void LogReading(DeliveryContents contents, string requestId)
    {
        if (contents.Excess > 0)
            Log.BeyondLimit(requestId, contents.Excess);
        if (contents.Unusable > 0)
            Log.UnusableIds(requestId, contents.Unusable);
        if (contents.RepeatedIds > 0)
            Log.RepeatedIds(requestId, contents.RepeatedIds);
    }

    // D-20 (as refined for Q2-1): whether a message that unpacked is the filter's DID's. It is when
    // that DID's key opened the innermost message, or when the innermost envelope names that DID
    // among its recipients: a sender that encrypted one message to two of our DIDs addressed it to
    // both, and the unpack opens it with the first held key the envelope names, which may be the
    // other DID's. A message whose envelope does not name the filter's DID at all (a mediator
    // handing over another of our DIDs' messages, or another tenant's, under this filter) is not.
    // One pass over every kid, no early exit (`|`, not `||`); each kid is checked against the
    // FR-API-09 bound the unpack applies to recipient kids before it is parsed (L-055).
    private static bool IsFor(UnpackResult unpacked, string recipientDid)
    {
        var match = NamesDid(unpacked.RecipientKid, recipientDid);
        foreach (var kid in unpacked.AllRecipientKids)
            match |= NamesDid(kid, recipientDid);
        return match;
    }

    // Whether a key id's DID subject is the bare DID `did` (L-034: subjects, never raw strings).
    private static bool NamesDid(string? kid, string did)
        => kid is not null
           && IdentifierLimits.IsWithinKeyIdLimits(kid)
           && string.Equals(DidSubject.DidSubjectOf(kid), did, StringComparison.Ordinal);

    private static void RequireBareDid(string? value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (!BareDid.IsBareDid(value))
            throw new ArgumentException("The value must be a bare DID (no path, query or fragment) of at most 2048 characters.", parameter);
    }

    private static void RequireOptionalBareDid(string? value, string parameter)
    {
        if (value is not null && !BareDid.IsBareDid(value))
            throw new ArgumentException("The value must be a bare DID (no path, query or fragment) of at most 2048 characters, or null.", parameter);
    }

    private static void RequireEndpoint(Uri? endpoint, string parameter)
    {
        if (endpoint is null)
            return;
        if (!endpoint.IsAbsoluteUri)
            throw new ArgumentException("The endpoint override must be an absolute URI.", parameter);
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("The endpoint override must not carry user information.", parameter);
    }

    // The count bound first, before any entry is looked at (L-055); then a copy, so what is checked
    // is what is sent even if the caller's list changes meanwhile (L-039).
    private static string[] RequireIds(IReadOnlyList<string> ids, string parameter)
    {
        ArgumentNullException.ThrowIfNull(ids, parameter);
        var count = ids.Count;
        if (count is < 1 or > MaxAckIds)
            throw new ArgumentException("Between 1 and 10 000 ids are needed.", parameter);
        var copy = new string[count];
        for (var i = 0; i < count; i++)
        {
            var id = ids[i];
            if (string.IsNullOrEmpty(id) || id.Length > PickupBodies.MaxIdLength || !UnreservedUriChars.IsUnreserved(id))
                throw new ArgumentException($"Id {i} is empty, longer than 2048 characters or not made of unreserved URI characters.", parameter);
            copy[i] = id;
        }
        return copy;
    }
}
