using System.Globalization;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Client;

/// <summary>
/// The recipient side of Message Pickup 3.0 and 4.0 (FR-PICK-11, FR-PICK-12): see what a mediator
/// holds for you, fetch and acknowledge it, or receive it as it arrives over a WebSocket. Messages
/// the mediator re-wrapped for you are opened (FR-ROUTE-06, receiver side).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which version.</strong> <see cref="MediatorClientOptions.MessagePickupVersion"/> decides.
/// By default each call first asks the mediator with Discover Features which versions it speaks, on
/// the same route, and speaks 4.0 when the mediator discloses it, 3.0 otherwise; the answer is
/// remembered per mediator for an hour, so later calls send no query. A mediator that discloses
/// neither fails the call with <see cref="MediationNotSupportedException"/>, and one that does not
/// answer Discover Features at all fails it with <see cref="TimeoutException"/>: pin
/// <see cref="MessagePickupVersion.V4"/> or <see cref="MessagePickupVersion.V3"/> for such a
/// mediator. A live stream settles the version once, when it starts. The public types are the same
/// in both versions; where 4.0 differs, the member says so (a 4.0 status gives no
/// <see cref="PickupStatus.LongestWaitedSeconds"/>, and a drained 4.0 fetch carries no status).
/// </para>
/// <para>
/// <strong>Setting up.</strong> With dependency injection, call <c>AddMediatorClient()</c> on the
/// <c>AddDidComm</c> builder and resolve this class. Over HTTP the mediator's answer comes back in
/// the response. Over a WebSocket it arrives on the connection and is handed to this client by the
/// inbound dispatcher, so the container also needs
/// <c>UseWebSocketTransport(o =&gt; o.ReceiveInbound = true)</c> and <c>UseInboundDispatch()</c>;
/// without them a WebSocket route is refused before anything is sent. (An
/// <c>IInboundFrameConsumer</c> of your own counts as one that receives: if it does not hand what it
/// receives to the <c>ProtocolDispatcher</c>, WebSocket requests time out instead of being refused,
/// and a live stream receives no answers or pushes.) Constructed without dependency injection
/// (<c>new PickupClient(didComm)</c>), the client works over HTTP only, and live delivery, which
/// needs a WebSocket, is not available.
/// </para>
/// <para>
/// <strong>Delivery is at-least-once.</strong> A message can reach you more than once: when an
/// acknowledgement is lost, when a connection drops, when the mediator's lease on a delivered
/// message runs out. The clients remember recent messages
/// (<see cref="MediatorClientOptions.DedupeCapacity"/>) and do not return one twice while they
/// remember it, but an application that must never act twice on a message should also keep track
/// of the message ids it has handled. They remember a message per mailbox, the <c>from</c> of the
/// fetch or stream that returned it: a copy that reaches another of your DIDs' mailboxes (a third
/// party can relay one there) is returned from that mailbox too, and never makes the first
/// mailbox's own copy count as a repeat.
/// </para>
/// <para>
/// <strong>Messages that do not unpack</strong> are returned as items with
/// <see cref="PickupItem.Error"/> set and are not acknowledged: the mediator keeps them and delivers
/// them again after its lease (60 seconds at this library's mediator). So is a delivered message
/// that is not a DIDComm message in encrypted form (a JWE, as delivered): Message Pickup allows no
/// other kind of attachment, so it is never unpacked. The clients never discard
/// one on their own: discard one you have given up on with <see cref="AckAsync"/>. That includes a
/// message that did not open in time: all the messages of one delivery share one
/// <see cref="MediatorClientOptions.UnpackTimeout"/>. The error is the unpack's own exception, and
/// its text can quote DIDs and key ids: log its type, not its message, wherever identifiers must not
/// go.
/// </para>
/// <para>
/// <strong>No replay check.</strong> Messages these clients return have not been through the
/// replay check that messages arriving on the transport get (FR-RR-08). If you hand them on to your
/// own protocol handlers, track the message ids you have handled.
/// </para>
/// <para>
/// <strong>Several of your DIDs.</strong> A <c>recipientDid</c> filter accepts only a message that
/// one of that DID's own keys opened. A message a sender encrypted to two of your DIDs opens with
/// the key its envelope lists first among those this agent holds, so it is accepted without a
/// filter or under that first DID's filter; under the second DID's filter it is a failed item,
/// "for another of your DIDs", and stays queued: discard it with <see cref="AckAsync"/>, or it ages
/// out at the mediator.
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
/// <strong>What confirms an acknowledgement.</strong> Only a status does. Message Pickup 3.0 says a
/// mediator SHOULD answer <c>messages-received</c> with its status, not that it must, and some send
/// none (the Identus mediator). Under 4.0 no status ever answers <c>messages-received</c>: it is sent
/// without <c>return_route</c> and nothing is waited for. In either case <see cref="AckAsync"/> asks
/// with a <c>status-request</c> when no status came, and returns that status: the count it reports
/// is the confirmation. The acknowledgement a fetch sends is not confirmed to you: if the mediator
/// did not apply it, the messages come back and are recognised as repeats.
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
    /// <exception cref="TimeoutException">No answer within <see cref="MediatorClientOptions.RequestTimeout"/> (or none in the HTTP response), to the request or to the Discover Features query that negotiates the version; the message names the request id. Retry. (Running out of time while finding the mediator's endpoint names none: nothing was sent.)</exception>
    /// <exception cref="MediationProtocolException">The mediator answered with a problem report, or with a malformed status, one about another DID than <paramref name="recipientDid"/>, or one that does not repeat <paramref name="recipientDid"/>; or, negotiating, it disclosed neither Message Pickup version (<see cref="MediationNotSupportedException"/>).</exception>
    /// <exception cref="TransportException">The transport failed (over HTTP with status 200: the mediator accepted the request but its answer was too large, stalled or unreadable; retry).</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public async Task<PickupStatus> GetStatusAsync(string mediatorDid, string from, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        RequireOptionalBareDid(recipientDid, nameof(recipientDid));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var version = await ResolveVersionAsync(route, mediatorDid, from, ct).ConfigureAwait(false);
        var (request, spec) = PickupWire.StatusRequest(version, from, mediatorDid, recipientDid);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        return PickupWire.ReadStatus(version, DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: true, recipientDid);
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
    /// back later and are recognised as repeats. A mediator that sends no status back after it
    /// (Message Pickup 3.0 allows that; the Identus mediator over HTTP answers with an empty 202)
    /// has not failed it: over HTTP nothing is logged. On a WebSocket the client cannot tell that
    /// silence from a slow answer, so there it is still logged as failed after
    /// <see cref="MediatorClientOptions.RequestTimeout"/>. Under Message Pickup 4.0 the
    /// acknowledgement is sent one way (nothing answers it), so only a failure to send it within
    /// <see cref="MediatorClientOptions.RequestTimeout"/> is logged. A message that failed stays leased to you for the
    /// mediator's lease, so a loop that fetches until drained gets past failures to what is behind
    /// them (for messages that are slow to open, only so many: see the known limits below); one that
    /// polls more slowly than the lease, with more failures than <paramref name="limit"/>, keeps
    /// seeing only the failures until it discards them with <see cref="AckAsync"/>.
    /// </para>
    /// <para>
    /// <strong>Time to open the messages.</strong> All the messages of one fetch share one budget,
    /// <see cref="MediatorClientOptions.UnpackTimeout"/> (20 seconds by default), opened four at a
    /// time, each with an equal share of it (the budget divided by the rounds of four the fetch
    /// needs). One that is not open when its share is up is returned as a failed item with a
    /// <see cref="TimeoutException"/> that names the request (never a message) and is not
    /// acknowledged, and the next one takes its place; the others are returned and acknowledged as
    /// usual. So a fetch ends within the budget even when some senders' DIDs resolve slowly or
    /// never, slow messages cannot keep the ones behind them in the same fetch from being opened,
    /// and while the mediator still sets the slow ones aside the next fetch reaches what is queued
    /// behind them. A smaller <paramref name="limit"/> gives each message a larger share.
    /// </para>
    /// <para>
    /// <strong>Resolvers that block.</strong> Each message is opened on a thread-pool work item of
    /// its own, never on your thread, so the call returns its task even when your DID or secrets
    /// resolver blocks its thread. Blocking cannot be bounded, though: the outcome is still decided
    /// at the budget, but the blocked thread and its work stay the resolver's until it returns, and
    /// meanwhile it holds one of the abandoned-unpack slots (below). Use resolvers that are
    /// asynchronous and honour their cancellation token.
    /// </para>
    /// <para>
    /// <strong>A message that keeps failing.</strong> Discard it with <see cref="AckAsync"/> once you
    /// have given up on it: the client never does. A mediator that does not set fetched messages
    /// aside delivers it first on every fetch, and then neither the budget nor a loop that fetches
    /// until drained gets you past it; only discarding it does. Until it is discarded, or ages out
    /// at the mediator (after 30 days by default), it stays queued and counts against your mailbox's
    /// quota there (at this library's mediator, <c>MessagePickupOptions.MaxQueueMessages</c>, 100 by
    /// default). Messages that never open — junk anyone who can address you can send — can fill that
    /// quota, and then the mediator refuses honest senders' messages, until you discard them.
    /// </para>
    /// <para>
    /// <strong>A DID resolver that ignores cancellation.</strong> A message whose budget ran out keeps
    /// one of the container's 16 slots for abandoned unpacks until its sender's DID lookup really
    /// ends. With a resolver that ignores its cancellation token, one slow sender can hold all 16,
    /// and until those lookups end every delivered message fails at once ("unpack capacity
    /// exhausted"), for every DID and tenant of the container: the slots are shared by the whole
    /// container (a known limit, deferred to
    /// <see href="https://github.com/moisesja/didcomm-dotnet/issues/111">#111</see>). Use a resolver
    /// that honours cancellation.
    /// </para>
    /// <para>
    /// <strong>Known limits with slow senders</strong> (to be hardened in
    /// <see href="https://github.com/moisesja/didcomm-dotnet/issues/111">#111</see>):
    /// </para>
    /// <list type="bullet">
    /// <item><description>The client does not remember slow messages from one fetch to the next. A
    /// fetch of slow messages takes its whole budget, so within one of the mediator's leases (60
    /// seconds at this library's mediator) a loop makes about three fetches and passes over about
    /// three times <paramref name="limit"/> slow messages at most. When more than that are queued in
    /// front of yours, the oldest come back first as their lease ends, and a loop that fetches until
    /// drained (or a live stream's drain) keeps meeting them and does not reach what is behind them.
    /// Discard what keeps failing with <see cref="AckAsync"/>.</description></item>
    /// <item><description>A message's share is fixed from the size of its delivery before any of it
    /// is opened. Messages that fail at once leave an honest message only its own share, not the time
    /// they did not use, so it can fail as out of time while most of the budget went
    /// unused.</description></item>
    /// <item><description>The bound of 16 abandoned unpacks is checked when an unpack starts, not
    /// reserved, so deliveries opened at the same moment (several fetches or live streams) can
    /// together leave more than 16 running.</description></item>
    /// <item><description>While a sender's DID lookup blocks its thread, the client keeps that whole
    /// delivery in memory — the other messages in it and the ones already opened — until the lookup
    /// returns (bounded by the abandoned-unpack slots). Each lookup runs on a thread-pool thread of
    /// its own, so a resolver that blocks can hold up to four pool threads per delivery being opened
    /// besides the 16 slots, and on a small machine that can slow the whole process, its timers
    /// included.</description></item>
    /// <item><description>When a delivery ends (its budget, your cancellation or the container's
    /// disposal) just as a message is being started, that message's DID and secrets lookups can
    /// still start afterwards, with an already-cancelled token, possibly after the container was
    /// disposed.</description></item>
    /// </list>
    /// <para>
    /// <strong>Repeats.</strong> A message the clients already returned from this mailbox (this
    /// <paramref name="from"/>) is acknowledged again and left out while they remember it
    /// (<see cref="MediatorClientOptions.DedupeCapacity"/>). With
    /// <c>DedupeCapacity</c> set to 0 nothing is remembered from one call to the next, but a message
    /// that comes twice in the same delivery is still returned only once.
    /// </para>
    /// <para>
    /// <strong>The <paramref name="recipientDid"/> filter.</strong> Under a filter a message counts as
    /// that DID's only when one of that DID's own keys opened it. The other recipients an envelope
    /// lists are not proof: whoever writes an envelope can list anyone, so a message is never
    /// accepted for a DID because its envelope names that DID.
    /// </para>
    /// <para>
    /// <strong>One message to several of your DIDs.</strong> A sender can encrypt one message to
    /// several DIDs whose keys this agent holds, and the mediator then queues a copy for each. Every
    /// copy opens with the first of those keys the envelope lists (its
    /// <see cref="UnpackResult.RecipientKid"/> names that DID). Fetched with the same
    /// <paramref name="from"/>, the copies are the same message: it is returned once and the other
    /// copies are acknowledged as repeats. Such a message is accepted without a filter, or under the
    /// filter of the DID whose key opened it. Under the filter of another of those DIDs its copy is
    /// a failed item, "for another of your DIDs", and stays queued: fetch without a filter to take
    /// it, discard it with <see cref="AckAsync"/>, or let it age out at the mediator.
    /// </para>
    /// <para>
    /// <strong>Who sent it.</strong> A delivered message is encrypted (one that is not, as delivered,
    /// is a failed item), but it can be anoncrypted (or, inside a forward the mediator re-wrapped for
    /// you, not protected at all), so its <c>from</c> may be anyone's claim: check
    /// <see cref="UnpackResult.Authenticated"/> before you trust it.
    /// </para>
    /// <para>
    /// <strong>Message Pickup 4.0.</strong> The request carries <c>message_count_limit</c>, and the
    /// mediator answers a queue with nothing to deliver with an empty <c>delivery</c>, never a status,
    /// so a drained 4.0 fetch has <see cref="PickupBatch.Status"/> <c>null</c> (call
    /// <see cref="GetStatusAsync"/> for one). 4.0 is strict where 3.0 is not: a delivery with more
    /// messages than <paramref name="limit"/>, or without an <c>attachments</c> array, is refused as a
    /// whole (<see cref="MediationProtocolException"/>), before any of it is opened, and nothing in it
    /// is acknowledged.
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="limit">The most messages to take, 1 to 100. Under 3.0, anything the mediator sends beyond it is not opened and not acknowledged; under 4.0, such a delivery is refused whole.</param>
    /// <param name="recipientDid">Only messages for this DID: a bare DID, or <c>null</c> for all of yours. A message is this DID's only when one of this DID's own keys opened it; any other is returned as a failed item and not acknowledged (see the remarks for a message encrypted to two of your DIDs).</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The batch: the items in delivery order, whether the queue was drained, and the mediator's <c>status</c> when it answered with one (3.0 only: a drained 4.0 fetch has none).</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="limit"/> is outside 1 to 100, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="GetStatusAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No answer in time, to the request or to the Discover Features query that negotiates the version; retry (nothing was acknowledged).</exception>
    /// <exception cref="MediationProtocolException">A problem report; an answer that is malformed or about another DID than <paramref name="recipientDid"/> (or, as a status, does not repeat it); a delivery in which no message has a usable id, so nothing in it could be acknowledged; under 4.0, a delivery with more messages than <paramref name="limit"/> or without an <c>attachments</c> array; or, negotiating, a mediator that disclosed neither Message Pickup version (<see cref="MediationNotSupportedException"/>).</exception>
    /// <exception cref="TransportException">The transport failed. Over HTTP with <see cref="TransportException.HttpStatusCode"/> 200, the mediator answered but its delivery was larger than this agent's <c>DidCommOptions.MaxReceiveBytes</c>, or could not be read: retry with a smaller <paramref name="limit"/>, or raise <c>MaxReceiveBytes</c>.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed, also while the fetch was opening its delivery: it then ends at once, nothing is acknowledged, and the resolvers opening the delivery see their cancellation token cancelled.</exception>
    public async Task<PickupBatch> FetchAsync(string mediatorDid, string from, int limit, string? recipientDid = null, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        if (limit is < 1 or > MaxFetchLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), "The limit must be from 1 to 100.");
        RequireOptionalBareDid(recipientDid, nameof(recipientDid));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var version = await ResolveVersionAsync(route, mediatorDid, from, ct).ConfigureAwait(false);
        var (request, spec) = PickupWire.DeliveryRequest(version, from, mediatorDid, limit, recipientDid);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);

        // 3.0: a status when nothing is deliverable, or a delivery whose first `limit` attachments
        // are read (L-055: what the mediator sent beyond the request is not decoded, not unpacked and
        // not acknowledged, so it stays queued). 4.0: a delivery only, an empty one when nothing is
        // deliverable, refused whole when it has more than `limit` attachments or none at all.
        var read = PickupWire.ReadDeliveryAnswer(version, DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, limit, checkFilter: true, recipientDid);
        if (read.Status is { } status)
            return new PickupBatch(Array.Empty<PickupItem>(), drained: true, status);
        var contents = read.Contents!;
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
            : await Exchange.UnpackDeliveredBatchAsync(payloads, request.Id, ct).ConfigureAwait(false);
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
                    $"A message delivered for request '{request.Id}' is for another of your DIDs: a key of another DID than the recipient_did filter's opened it. " +
                    "It was not acknowledged and stays queued: discard it with AckAsync, or it ages out at the mediator.", request.Id)));
                continue;
            }
            if (Exchange.Duplicates.Contains(from, outcome.DedupeKey) || !seen.Add(outcome.DedupeKey))
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
            Exchange.Duplicates.Add(from, key);
        if (acknowledge.Count > 0)
            await AcknowledgeQuietlyAsync(version, route, mediatorDid, from, acknowledge).ConfigureAwait(false);

        Log.Fetched(request.Id, commit.Count, failures.Count + mismatches, repeats);
        return new PickupBatch(items, drained: false, status: null);
    }

    /// <summary>
    /// Tell the mediator it may delete these messages (<c>messages-received</c>): usually items that
    /// failed and that you have given up on, or a live item you want gone before you stop a stream.
    /// Sent 100 ids per message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 100 ids per message is what this library's mediator accepts by default. A mediator can be
    /// configured to accept fewer (this library's down to its delivery batch size, 10 by default);
    /// such a mediator refuses a longer message as malformed, so call this method with at most as
    /// many ids as it accepts.
    /// </para>
    /// <para>
    /// <strong>Only the returned status confirms the acknowledgement.</strong> The mediator has
    /// applied it when the status it returns no longer counts the messages it removed. Message
    /// Pickup 3.0 says a mediator SHOULD answer <c>messages-received</c> with its status, not that it
    /// must, and some send none (the Identus mediator). A mediator that sends no status after the
    /// last message is asked for one with a <c>status-request</c>, and the count that status reports
    /// is the confirmation; after an earlier message that gets no status, the next message is sent
    /// without asking. A mediator that does answer with its status gets one message per 100 ids and
    /// nothing more. Under Message Pickup 4.0 no status ever answers <c>messages-received</c>: each
    /// message is sent one way, without <c>return_route</c>, and the call always ends with the
    /// <c>status-request</c>.
    /// </para>
    /// <para>
    /// <strong>Under 4.0 on a WebSocket</strong> the call first sends a <c>status-request</c> of its
    /// own, before any <c>messages-received</c>; its answer is checked as a status and not returned.
    /// A message sent one way goes on the connection that carries this agent's answers when one is
    /// open, and on another connection when none is; that first <c>status-request</c> opens it, or
    /// reuses the open one. The acknowledgements and the <c>status-request</c> after them then travel
    /// on that one connection, which the mediator handles in order, so the status returned is taken
    /// after the acknowledgements were applied. If that connection drops after the first status and
    /// before an acknowledgement is sent, the acknowledgement goes on another connection and the status
    /// may not reflect it yet; call again (acknowledging twice is harmless). Over HTTP nothing is sent
    /// first: the mediator handles each request before it answers it.
    /// </para>
    /// <para>
    /// <strong>Time.</strong> Each <c>messages-received</c> and the <c>status-request</c> that may
    /// follow it share one <see cref="MediatorClientOptions.RequestTimeout"/>, counted from when the
    /// <c>messages-received</c> is sent; the <c>status-request</c> never gets a
    /// <c>RequestTimeout</c> of its own. Over HTTP the response shows at once that no status came,
    /// and the <c>status-request</c> gets the rest of that time. On a WebSocket, silence cannot be
    /// told from a slow answer, so the client waits half of <c>RequestTimeout</c> for the status, then
    /// moves on: to the next message, or to the <c>status-request</c>, which gets the other half.
    /// Under 4.0 nothing is waited for after a message: it is only sent, within its
    /// <c>RequestTimeout</c>, and the <c>status-request</c> gets what is left of the last one's. The
    /// <c>status-request</c> a 4.0 call sends first on a WebSocket has a <c>RequestTimeout</c> of its
    /// own, which does not count against the messages after it.
    /// </para>
    /// <para>
    /// <strong>Which status answers which message.</strong> DIDComm threading: a reply carries the
    /// <c>thid</c> of the message it answers, and a message without that <c>thid</c> is not part of
    /// its thread. So a status answers a message only when its <c>thid</c> names that message, over
    /// a WebSocket and in the HTTP response to it alike; one without <c>thid</c> answers none and is
    /// dropped, which also keeps a late status for a message given up on from being taken for
    /// another. A mediator that leaves <c>thid</c> out of its status is therefore treated as one that
    /// sends no status: the call asks with a <c>status-request</c>, whose answer needs a <c>thid</c>
    /// too (without one the call fails with <see cref="TimeoutException"/>).
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="ids">1 to 10 000 ids, each exactly as <see cref="PickupItem.Id"/> gave it.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mediator's <c>status</c> after the last message: its answer to that message, or, when it sent none (always under 4.0), its answer to the <c>status-request</c> that asked for it.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="ids"/> is empty, longer than 10 000 or has an empty, over-long or non-URI-safe id (the message names its index), or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="GetStatusAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">No status in time (see the remarks): a message got no answer within <see cref="MediatorClientOptions.RequestTimeout"/> (under 4.0: was not sent within it), the <c>status-request</c> after the last one got none in the time left (over HTTP, none in its response), or the Discover Features query that negotiates the version got none. The mediator may still have applied the acknowledgement; retry (acknowledging twice is harmless). Under 4.0 on a WebSocket, also the <c>status-request</c> sent first got no answer within its own <c>RequestTimeout</c>; the message names that request, and nothing was acknowledged.</exception>
    /// <exception cref="MediationProtocolException">A problem report or a malformed status; for a <c>status-request</c>, also a status about one DID only (it asked about all of yours); or, negotiating, a mediator that disclosed neither Message Pickup version (<see cref="MediationNotSupportedException"/>). When it is the answer to the <c>status-request</c> a 4.0 call sends first on a WebSocket, nothing was acknowledged.</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public async Task<PickupStatus> AckAsync(string mediatorDid, string from, IReadOnlyList<string> ids, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        var copy = RequireIds(ids, nameof(ids));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var version = await ResolveVersionAsync(route, mediatorDid, from, ct).ConfigureAwait(false);
        if (!PickupWire.AnswersAcknowledgement(version) && !route.IsHttp)
            await OpenReturnRouteAsync(version, route, mediatorDid, from, ct).ConfigureAwait(false);
        var spec = DeliveryReader.MessagesReceivedSpec;
        PickupStatus? status = null;
        var lastId = string.Empty;
        var lastSentAt = 0L;
        for (var start = 0; start < copy.Length; start += AckChunk)
        {
            var chunk = copy.AsSpan(start, Math.Min(AckChunk, copy.Length - start)).ToArray();
            var request = PickupWire.MessagesReceived(version, from, mediatorDid, chunk);
            lastId = request.Id;
            lastSentAt = Exchange.Time.GetTimestamp();
            if (!PickupWire.AnswersAcknowledgement(version))
            {
                // 4.0: nothing answers messages-received (L62, L294); it is only sent, and the
                // status-request after the last one confirms them all. On a WebSocket it rides the
                // session OpenReturnRouteAsync opened, as that status-request does.
                await Exchange.SendOneWayAsync(route, request, PickupWire.MessagesReceivedName, mediatorDid, from, ct).ConfigureAwait(false);
                continue;
            }
            var answer = await AcknowledgeChunkAsync(route, request, mediatorDid, from, ct).ConfigureAwait(false);
            // A message left without a status is confirmed by the status after the last one.
            status = answer is null
                ? null
                : DeliveryReader.ReadStatus(DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: false, filter: null);
        }
        return status ?? await AskForStatusAsync(version, route, mediatorDid, from, lastId, lastSentAt, ct).ConfigureAwait(false);
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
    /// <exception cref="TimeoutException">No answer in time, to the request or to the Discover Features query that negotiates the version; retry.</exception>
    /// <exception cref="MediationProtocolException">A problem report — Report Problem 2.0 under 3.0, Message Pickup 4.0's own <c>problem-report</c> under 4.0 — (<see cref="MediationProtocolException.ProblemCode"/> <c>e.m.live-mode-not-supported</c> when the mediator cannot deliver live on this connection), a malformed status, or, negotiating, a mediator that disclosed neither Message Pickup version (<see cref="MediationNotSupportedException"/>).</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public async Task<PickupStatus> SetLiveDeliveryAsync(string mediatorDid, string from, bool liveDelivery, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        // Refused before anything is sent when the route is not a WebSocket (design §4.3).
        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: true, ct).ConfigureAwait(false);
        var version = await ResolveVersionAsync(route, mediatorDid, from, ct).ConfigureAwait(false);
        var (request, spec) = PickupWire.LiveDeliveryChange(version, from, mediatorDid, liveDelivery);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        return PickupWire.ReadStatus(version, DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: false, filter: null);
    }

    /// <summary>
    /// Receive your messages as they arrive, over a WebSocket. The stream turns live delivery on,
    /// takes what is already queued, then yields each message the mediator pushes. One
    /// <see cref="MediatorClientOptions.LivePollInterval"/> after each such round ends it turns live
    /// delivery on again and takes what is queued: that picks up messages whose delivery was delayed
    /// and recovers from a dropped connection. Items that failed to unpack are yielded too, with <see cref="PickupItem.Error"/>
    /// set; they are never acknowledged and are yielded again when the mediator delivers them again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Acknowledgement.</strong> An item is acknowledged when you ask for the next one: the
    /// next <c>MoveNextAsync</c>, which <c>await foreach</c> makes when your loop body has finished
    /// with the item. Nothing is acknowledged before it was handed to you. Acknowledgements are sent
    /// in batches (when ten are waiting, when nothing else is ready, or after ten seconds) of at most
    /// ten ids per message. <strong>Known limit</strong> (owner decision 2026-11-21: documented, not
    /// changed): a mediator must accept at least ten ids per <c>messages-received</c> for the stream
    /// to confirm on time. One that accepts fewer (this library's mediator with
    /// <c>MessagePickupOptions.MaxAckIds</c> set below 10; the default is ten times its delivery
    /// batch) refuses such a confirmation, and the messages it named are delivered again at the
    /// stream's next collection, one <see cref="MediatorClientOptions.LivePollInterval"/> later,
    /// and recognised as repeats. Nothing is lost. The stream remembers the item
    /// (<see cref="MediatorClientOptions.DedupeCapacity"/>) so a repeat is acknowledged and not
    /// yielded again. With <c>DedupeCapacity</c> set to 0 it remembers nothing, so a message that
    /// comes twice in one delivery is yielded twice (<see cref="FetchAsync"/> returns it once).
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
    /// token says: a mediator that withholds its answers can hold up the enumerator's
    /// <c>DisposeAsync</c> (the end of an <c>await foreach</c>) for up to one
    /// <see cref="MediatorClientOptions.RequestTimeout"/>. Enumerate with <c>await foreach</c> (or
    /// dispose the enumerator): that is what closes the stream and frees its place.
    /// </para>
    /// <para>
    /// <strong>Opening.</strong> Each delivery the stream takes — the answer to a drain round, or a
    /// push — is opened before any of its items is yielded, all of its messages within one
    /// <see cref="MediatorClientOptions.UnpackTimeout"/>, four at a time and each with an equal
    /// share of it, during your own <c>MoveNextAsync</c> (each message on a thread-pool work item of
    /// its own, as in <see cref="FetchAsync"/>). The time you spend on an item never counts against
    /// it. Messages it runs out on are yielded as failed items (<see cref="TimeoutException"/>) and
    /// not acknowledged; the rest are yielded and acknowledged as usual.
    /// </para>
    /// <para>
    /// <strong>Drain rounds and pushes take turns.</strong> Once a cycle is draining, its next drain
    /// round runs before the stream takes another push, and a waiting push gets its turn after that
    /// round. So a steady flow of pushes that are slow to open (from a sender whose DID resolves
    /// slowly or never) cannot keep the drain from running, and a drain that keeps meeting slow
    /// messages cannot keep the pushes from theirs. A push that waits stays leased to your
    /// connection at the mediator: nothing is lost.
    /// </para>
    /// <para>
    /// <strong>A cycle runs to its end before the next one.</strong> Once a cycle has turned live
    /// delivery on, it sends its delivery requests until the mediator has nothing more for it, until
    /// 50 rounds, or until one of its steps fails, and only then is the next cycle scheduled: one
    /// <see cref="MediatorClientOptions.LivePollInterval"/> after that end, or after the back-off when
    /// a step failed. So however slowly the mediator answers — even more slowly than the interval —
    /// every cycle fetches what is queued, and a cycle coming due never pre-empts a drain or the
    /// pushes waiting their turn. Two consequences: turning live delivery on again after a dropped
    /// connection waits for the drain in progress to end (at most 50 rounds, each bounded by
    /// <see cref="MediatorClientOptions.RequestTimeout"/> and the decrypt budget), and the stream
    /// does its work — cycles, the 10-second acknowledgement, re-arming live mode — only inside your
    /// <c>MoveNextAsync</c>, so while your loop body holds an item none of it happens (a slow loop
    /// body delays the poll and can let the mediator's lease on taken messages run out; they then
    /// come again and are recognised as repeats).
    /// </para>
    /// <para>
    /// <strong>A message that keeps failing</strong> is yielded again each time the mediator delivers
    /// it again. Discard it with <c>AckAsync(mediatorDid, from, new[] { item.Id })</c> once you have
    /// given up on it: the stream never does. A mediator that does not set delivered messages aside
    /// delivers it on every drain, and the budget does not get the stream past it. Until it is
    /// discarded or ages out it counts against your mailbox's quota at the mediator (see
    /// <see cref="FetchAsync"/>). The known limits with slow senders described there apply to the
    /// stream's drain as well (to be hardened in
    /// <see href="https://github.com/moisesja/didcomm-dotnet/issues/111">#111</see>). The error's text
    /// can quote DIDs and key ids (see <see cref="PickupItem.Error"/>), and the messages are not
    /// replay-checked (see the class remarks).
    /// </para>
    /// <para>
    /// <strong>Polling is part of the design.</strong> A message whose sender delayed it is never
    /// pushed when it falls due, and the mediator has no scheduler that would push it later, now or
    /// in a future version (FR-PICK-08). The drain when the stream starts and the one each
    /// <see cref="MediatorClientOptions.LivePollInterval"/> after the previous drain ended are how
    /// such a message reaches you, and
    /// how the stream recovers from a dropped connection: they are the lasting design, not a
    /// stopgap. Poll at least as often as the longest delay you expect your senders to ask for (this
    /// library's mediator caps a delay at 1 hour by default, <c>MediatorOptions.MaxForwardDelay</c>),
    /// or such a message waits up to one interval longer.
    /// </para>
    /// <para>
    /// <strong>Starting and failing.</strong> The arguments are checked when you call this method.
    /// The route is resolved at the first <c>MoveNextAsync</c>, and refused when it is not a
    /// WebSocket, before anything is sent. One stream per mediator and DID may be open at a time,
    /// and at most 16 per container. Then the Message Pickup version is settled, once for the
    /// stream's whole life (<see cref="MediatorClientOptions.MessagePickupVersion"/>: the pinned
    /// one, the one remembered for this mediator, or a Discover Features query); a query that fails
    /// ends the stream with its exception at once, not retried, and frees its place. A push of the
    /// other version is ignored and not acknowledged. Under 4.0 acknowledgements are sent one way:
    /// nothing answers them, so none is waited for. A step that got no answer (<see cref="TimeoutException"/>) or
    /// failed in the transport (<see cref="TransportException"/>) is retried after a pause of 1, 2,
    /// 4, 8, 16, then 30 seconds; so is a request refused because too many of the container's
    /// requests are already waiting (an <see cref="InvalidOperationException"/>; every other one is
    /// thrown at once). While a pause lasts nothing is sent, acknowledgements included. Once the
    /// stream's steps have kept failing for longer than
    /// <see cref="MediatorClientOptions.RequestTimeout"/> plus 30 seconds (one minute by default),
    /// the next failure ends the stream with its exception, so a condition that clears within one
    /// <c>RequestTimeout</c> (the busy refusal, a mediator restarting) never does. The run of failures
    /// starts again only when a drain completes: failures separated by successful steps in cycles
    /// that never complete a drain still count as one run (a known limit, deferred to
    /// <see href="https://github.com/moisesja/didcomm-dotnet/issues/111">#111</see>). Any other failure ends it at once with its exception: a problem report from the
    /// mediator, a malformed answer, a key of <paramref name="from"/> this agent does not hold, the
    /// container's disposal.
    /// </para>
    /// <para>
    /// <strong>A mediator that does not set delivered messages aside</strong> (Message Pickup 3.0 does
    /// not require it; this library's mediator does) answers every delivery request with the head of
    /// your queue until it is acknowledged. A message there that never opens is then delivered on
    /// every round of a drain: each cycle sends 50 delivery requests and yields that message 50
    /// times, and everything behind it waits until you discard it with <see cref="AckAsync"/> (a known
    /// limit, deferred to <see href="https://github.com/moisesja/didcomm-dotnet/issues/111">#111</see>).
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>); a <c>ws</c> or <c>wss</c> URI.</param>
    /// <param name="ct">Cancellation token (combined with one passed through <c>WithCancellation</c>); cancelling ends the enumeration.</param>
    /// <returns>The items, as they arrive.</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">At the first <c>MoveNextAsync</c>: the route is not a WebSocket, this container cannot receive on one, a stream for this mediator and DID is already open, or 16 are; nothing was sent (or, negotiating, too many requests were waiting for the Discover Features query to be sent). While enumerating: a refusal of the send path (no key curve in common with the mediator, a mediator reached through routing, no transport router), at once; or the stream's steps kept being refused because too many of the container's requests were waiting for longer than <see cref="MediatorClientOptions.RequestTimeout"/> plus 30 seconds.</exception>
    /// <exception cref="MediationProtocolException">At the first <c>MoveNextAsync</c>, negotiating: a mediator that disclosed neither Message Pickup version (<see cref="MediationNotSupportedException"/>). While enumerating: a problem report (for example <c>e.m.live-mode-not-supported</c>), a malformed answer, a drain answered with a delivery in which no message has a usable id, or under 4.0 a drain answered with a delivery of more than its limit or without an <c>attachments</c> array.</exception>
    /// <exception cref="TimeoutException">At the first <c>MoveNextAsync</c>, negotiating: the Discover Features query got no answer within <see cref="MediatorClientOptions.RequestTimeout"/>. While enumerating: the stream's steps kept getting no answer for longer than <see cref="MediatorClientOptions.RequestTimeout"/> plus 30 seconds (this is the last one).</exception>
    /// <exception cref="TransportException">While enumerating: the stream's steps kept failing to send for longer than <see cref="MediatorClientOptions.RequestTimeout"/> plus 30 seconds (this is the last one).</exception>
    /// <exception cref="SecretNotFoundException">While enumerating: this agent holds no key for <paramref name="from"/> (at once, like any failure that no retry can fix).</exception>
    /// <exception cref="ObjectDisposedException">While enumerating: the container was disposed, also while the stream was opening a delivery (it then ends at once, and the resolvers opening it see their cancellation token cancelled).</exception>
    public IAsyncEnumerable<PickupItem> ReceiveLiveAsync(string mediatorDid, string from, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        // Checked here, when the method is called; the stream itself starts at the first MoveNextAsync.
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));
        return LiveDeliveryStream.ReceiveAsync(Exchange, Log, mediatorDid, from, serviceEndpointOverride, ResolveVersionAsync, ct);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // The version to speak on `route` (#100 DC-6), resolved after the route in every call: the pinned
    // one; else the one negotiated with this mediator within the hour; else one Discover Features
    // query on the same route, with its own RequestTimeout. A query that is not answered fails the
    // call (no guessing, no fallback); a disclose naming neither version is
    // MediationNotSupportedException. Only a negotiated version is remembered.
    private async Task<MessagePickupVersion> ResolveVersionAsync(MediatorRoute route, string mediatorDid, string from, CancellationToken ct)
    {
        var pinned = Exchange.Options.MessagePickupVersion;
        if (pinned != MessagePickupVersion.Negotiate)
            return pinned;
        if (Exchange.PickupVersions.TryGet(mediatorDid, out var remembered))
            return remembered;

        var query = FeatureNegotiation.CreatePickupQuery(from, mediatorDid);
        var answer = await Exchange.ExchangeAsync(route, query, FeatureNegotiation.QuerySpec, mediatorDid, from, ct).ConfigureAwait(false);
        var version = FeatureNegotiation.ChoosePickup(DeliveryReader.Open(answer, query.Id, FeatureNegotiation.QuerySpec), query.Id);
        Exchange.PickupVersions.Set(mediatorDid, version);
        Log.VersionNegotiated(query.Id, version);
        return version;
    }

    // One messages-received of AckAsync: the mediator's answer, or null when it sent no status
    // (Message Pickup 3.0: it SHOULD, not MUST). Over HTTP the response says so at once. On any other
    // route (a WebSocket) silence cannot be told from a slow answer: the status is waited for half of
    // RequestTimeout, which leaves the other half for the status-request that may follow, and then
    // the wait is given up.
    private async Task<InboundMessageSnapshot?> AcknowledgeChunkAsync(MediatorRoute route, Message request, string mediatorDid, string from, CancellationToken ct)
    {
        var spec = DeliveryReader.MessagesReceivedSpec;
        if (route.IsHttp)
            return await Exchange.ExchangeAllowingNoAnswerAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);

        using var wait = new GuardedDeadline(Exchange.Time, Exchange.Options.RequestTimeout / 2, ct);
        try
        {
            return await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (wait.TimedOut)
        {
            return null;
        }
    }

    // Under 4.0 on a route that is not HTTP, before the first messages-received: one status-request
    // as an ordinary exchange (return_route all, its own RequestTimeout), its answer read as a status
    // and not returned. A header-less message rides the return-route session socket only when one is
    // open (FR-RR-05 affinity, FR-RR-07), and the pooled socket otherwise, while the status-request
    // after the last acknowledgement would open the session socket: two connections, which the
    // mediator handles independently, so that status could be answered before an acknowledgement was
    // applied. This exchange opens the session socket, or reuses the open one, so the acknowledgements
    // and the status-request after them travel on one connection, which the mediator handles in order.
    // Over HTTP each POST is handled before its response, so nothing is needed there.
    private async Task OpenReturnRouteAsync(MessagePickupVersion version, MediatorRoute route, string mediatorDid, string from, CancellationToken ct)
    {
        var (request, spec) = PickupWire.StatusRequest(version, from, mediatorDid, recipientDid: null);
        var answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, ct).ConfigureAwait(false);
        _ = PickupWire.ReadStatus(version, DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: true, filter: null);
    }

    // The status-request after the last messages-received when the mediator sent no status for it
    // (under 4.0, always), in what is left of that message's RequestTimeout (counted from `sentAt`):
    // never a RequestTimeout of its own.
    private async Task<PickupStatus> AskForStatusAsync(MessagePickupVersion version, MediatorRoute route, string mediatorDid, string from, string acknowledgementId, long sentAt, CancellationToken ct)
    {
        var timeout = Exchange.Options.RequestTimeout;
        var left = timeout - Exchange.Time.GetElapsedTime(sentAt);
        if (left <= TimeSpan.Zero)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"Mediator request '{acknowledgementId}' (messages-received) got no status back, and no time was left within {timeout} (MediatorClientOptions.RequestTimeout) to ask for one; the mediator may still have applied it, so retry it (acknowledging twice is harmless)."));
        }

        var (request, spec) = PickupWire.StatusRequest(version, from, mediatorDid, recipientDid: null);
        using var budget = new GuardedDeadline(Exchange.Time, left, ct);
        InboundMessageSnapshot answer;
        try
        {
            answer = await Exchange.ExchangeAsync(route, request, spec, mediatorDid, from, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (budget.TimedOut)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"Mediator request '{acknowledgementId}' (messages-received) got no status back, and the status-request '{request.Id}' that asked for one was not answered within {timeout} of it (MediatorClientOptions.RequestTimeout); the mediator may still have applied it, so retry it (acknowledging twice is harmless)."));
        }
        return PickupWire.ReadStatus(version, DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: true, filter: null);
    }

    // Acknowledge on a deadline of our own (RequestTimeout), not the caller's token: the batch is
    // committed, so a cancellation now would only make the messages come back. Any failure is
    // logged by type name; the messages come back and are then recognised as repeats. The status
    // that may answer is not read, and Message Pickup 3.0 lets a mediator send none (it SHOULD, not
    // MUST): over HTTP an empty response is that, not a failure. On a WebSocket the wait is as before.
    // Under 4.0 nothing answers it (L62, L294): it is sent one way, and only the send is waited for.
    private async Task AcknowledgeQuietlyAsync(MessagePickupVersion version, MediatorRoute route, string mediatorDid, string from, IReadOnlyList<string> ids)
    {
        var request = PickupWire.MessagesReceived(version, from, mediatorDid, ids);
        try
        {
            using var deadline = OwnDeadline.Start(Exchange.Time, Exchange.Options.RequestTimeout);
            if (PickupWire.AnswersAcknowledgement(version))
                await Exchange.ExchangeAllowingNoAnswerAsync(route, request, DeliveryReader.MessagesReceivedSpec, mediatorDid, from, deadline.Token).ConfigureAwait(false);
            else
                await Exchange.SendOneWayAsync(route, request, PickupWire.MessagesReceivedName, mediatorDid, from, deadline.Token).ConfigureAwait(false);
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

    // D-20 (owner decision 1-A on the L-063 stop): a message that unpacked is the filter's DID's only
    // when one of that DID's own keys opened it: the DID subject of the key that decrypted the
    // innermost envelope (UnpackResult.RecipientKid) is the filter's DID. The envelope's other
    // recipient labels are not evidence (L-040): a co-recipient that re-encrypts a message, or a
    // relay that appends a label to an envelope without `apv`, can write any of them (ADV1-2,
    // ADV1-3). The consequence for a message a sender encrypted to two of our DIDs is documented on
    // FetchAsync: it passes the filter of the DID whose key opened it (the first held key the
    // envelope lists), not the other one's. One comparison, no early exit; the key id is checked
    // against the FR-API-09 bound before it is parsed (L-055).
    private static bool IsFor(UnpackResult unpacked, string recipientDid)
    {
        var kid = unpacked.RecipientKid;
        return kid is not null
               && IdentifierLimits.IsWithinKeyIdLimits(kid)
               && string.Equals(DidSubject.DidSubjectOf(kid), recipientDid, StringComparison.Ordinal);
    }

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
