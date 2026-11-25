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
/// acknowledgement is lost (under Message Pickup 4.0 nothing confirms one, from
/// <see cref="AckAsync"/> or <see cref="FetchAsync"/>, so a connection that drops right behind it can
/// lose it), when a connection drops, when the mediator's lease on a delivered message runs out. The clients remember recent messages
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
/// one on their own: discard one you have given up on with <see cref="AckAsync"/>, and never one
/// whose <see cref="PickupItem.Inconclusive"/> is <c>true</c> (the next paragraph). A message that
/// failed on its own, or that was given the whole time and did not open (all the messages of one
/// delivery share one <see cref="MediatorClientOptions.UnpackTimeout"/>), is remembered: it is not
/// opened again while its pause lasts, and is returned at once as a failed item with a
/// <see cref="TimeoutException"/> (see <see cref="FetchAsync"/>, "Messages that did not open"). The
/// error is the unpack's own exception, and its text can quote DIDs and key ids: log its type, not
/// its message, wherever identifiers must not go.
/// </para>
/// <para>
/// <strong>Failed items that do not count against their message.</strong> Do not discard an item
/// whose <see cref="PickupItem.Inconclusive"/> is <c>true</c>: the client has not counted this
/// delivery against the message (its turn did not come before the time ran out, it was refused for
/// capacity, or it was started late and cut). It is delivered again and started again then, once
/// what was in its way is gone. A host that discards every failed item also deletes messages that
/// were not given their chance to open. And <c>false</c> alone is not a reason to discard: a
/// message a <c>recipientDid</c> filter turned away is a failed item whose
/// <see cref="PickupItem.Inconclusive"/> is <c>false</c>, and a fetch without the filter returns it.
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
/// <strong>What confirms an acknowledgement.</strong> Only the mediator's own answer to it, and it may
/// send none. Message Pickup 3.0 says a mediator SHOULD answer <c>messages-received</c> with its
/// status, not that it must, and some send none (the Identus mediator). Under 4.0 nothing answers
/// <c>messages-received</c>: it is sent without <c>return_route</c> and nothing is waited for.
/// <see cref="AckAsync"/> returns the status the mediator answered with, or <c>null</c> when it sent
/// none (always under 4.0); it never asks for one, because a status asked for separately is not
/// ordered after an acknowledgement that got no answer. The acknowledgement a fetch sends is not
/// confirmed to you either: if the mediator did not apply it, the messages come back and are
/// recognised as repeats. Under 4.0, closing the connection right after <see cref="AckAsync"/> or a
/// <see cref="FetchAsync"/> relies on the close: disposing waits, bounded, for the mediator to
/// finish what it received (see <see cref="AckAsync"/>'s "Before you close the connection"), but an
/// abrupt drop can still lose the acknowledgement, and the messages then come back.
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
///
///     // A fetch that brought only inconclusive items settled nothing: wait before fetching again
///     // (FetchAsync says for what). A minute covers the 60-second lease of this library's mediator.
///     if (batch.Items.Count &gt; 0 &amp;&amp; batch.Items.All(item =&gt; item.Inconclusive))
///         await Task.Delay(TimeSpan.FromMinutes(1));
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
    /// <remarks>
    /// <strong>Not ordered after an acknowledgement that got no answer.</strong> The status is taken
    /// when the mediator handles this request. An acknowledgement it did not answer (always under
    /// Message Pickup 4.0, where <see cref="AckAsync"/>, a fetch and a live stream send it one way;
    /// and under 3.0 when the mediator sends no status) may be applied after that, for example when
    /// it travelled on another connection, so the count may still include messages you just
    /// acknowledged. Nothing in the protocol says when such an acknowledgement was applied.
    /// </remarks>
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
    /// <see cref="PickupBatch.Drained"/> is <c>true</c> to take everything queued; when a fetch
    /// returns only items whose <see cref="PickupItem.Inconclusive"/> is <c>true</c>, wait before
    /// the next call (see "A message that keeps failing" in the remarks).
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
    /// <see cref="MediatorClientOptions.RequestTimeout"/> is logged, and nothing confirms it was
    /// applied: a single fetch followed at once by closing the connection relies on the close (see
    /// <see cref="AckAsync"/>'s "Before you close the connection"); after an abrupt drop the messages
    /// can come back. A message that failed stays leased to you for the
    /// mediator's lease, so a loop that fetches until drained gets past failures to what is behind
    /// them (past messages that never open as well: see "Messages that did not open" below); one
    /// that polls more slowly than the lease, with more failures than <paramref name="limit"/> that
    /// are not remembered, keeps seeing only the failures until it discards them with
    /// <see cref="AckAsync"/> (never one whose <see cref="PickupItem.Inconclusive"/> is <c>true</c>:
    /// this delivery was not counted against that one).
    /// </para>
    /// <para>
    /// <strong>Time to open the messages.</strong> All the messages of one fetch share one budget,
    /// <see cref="MediatorClientOptions.UnpackTimeout"/> (20 seconds by default), and up to sixteen
    /// of them are opened at the same time: your DID resolver can be asked for up to sixteen lookups
    /// at once per fetch. A message has no time limit of its own: it runs until it opens, fails, or
    /// the budget ends. A message starts only while at least a quarter of the budget is left (5
    /// seconds by default); one whose turn comes later fails as out of time without being opened. A
    /// message still being opened when the budget ends is returned as a failed item with a
    /// <see cref="TimeoutException"/> that names the request (never a message) and is not
    /// acknowledged. The others are returned and acknowledged as usual. So a fetch does not wait
    /// for senders whose DIDs resolve slowly or never: the budget ends it. A message that fails at
    /// once gives its place to the next one. The cost: messages that never open keep their places
    /// until the budget ends. Sixteen of them at the front of a fetch keep every place for the whole
    /// budget, and the messages behind them in that fetch run out of time before their turn: they
    /// are returned as failed items whose <see cref="PickupItem.Inconclusive"/> is <c>true</c>, and
    /// get their turn in the next fetch that brings them, where the sixteen are remembered and not
    /// opened again (below). Only a fetch of more than sixteen messages has messages behind
    /// sixteen: this library's mediator delivers at most ten per fetch at its defaults, so there
    /// every message of a fetch starts when the fetch does.
    /// </para>
    /// <para>
    /// <strong>Messages that did not open</strong> are remembered, per mediator and mailbox (the
    /// <paramref name="mediatorDid"/> and <paramref name="from"/> of the call), by the mediator's id
    /// for them. A message is remembered in two cases only. One: your code ran for it and it failed
    /// on its own while the fetch was still going on, however quickly. Two: the budget ran out while
    /// it was still being opened, and it had had the whole budget: its code had started at most an
    /// eighth of the budget (2.5 seconds by default) after the fetch began to open its messages. A
    /// host call waits a moment for a thread before its code runs, which is what that eighth allows
    /// for. A message whose code started later than that (others were in front of it, or it waited
    /// for a thread) is not remembered when the budget ends, and is opened again when it is delivered
    /// again. Nor is a message remembered when it was refused for capacity, when it did not start,
    /// when it is not a message this client opens, when it was held back, or when it was still being
    /// opened as you cancelled or the container was disposed. A message that opens is forgotten.
    /// Nothing in the message decides any of this. A failed item tells you which it was:
    /// <see cref="PickupItem.Inconclusive"/> is <c>true</c> exactly when the message did not open
    /// and nothing was counted against it in this fetch (it was not remembered, was not held back,
    /// and is a message this client opens). When the mediator delivers a remembered message
    /// again, it is not opened: it is returned at once as a failed item (a
    /// <see cref="TimeoutException"/>: "it did not open when it was delivered before"), runs none of
    /// your code, costs none of the budget and is not acknowledged. Its pause is 10 minutes (30 times
    /// <see cref="MediatorClientOptions.UnpackTimeout"/>) and doubles each time it fails again, up to
    /// 256 times <c>UnpackTimeout</c> (about 85 minutes); a message still inside its pause is not
    /// counted again, so deliveries of it opened at the same time count once. So a message that
    /// failed on its own is held back for its first pause, an honest message that failed only for a
    /// passing reason included (its sender's DID host briefly down, for example): it comes back after
    /// 10 minutes, not at the next fetch. The memory keeps 256 messages per mediator and mailbox and
    /// 256 such pairs, the least recently seen going first, and it lives in this process only.
    /// </para>
    /// <para>
    /// <strong>How long a loop takes to get past messages that never open.</strong> A loop that
    /// fetches until drained reaches a message behind them: each fetch that meets new ones learns up
    /// to sixteen of them within its budget, and the fetches that meet only remembered ones take no
    /// time. With <c>ℓ</c> the messages each fetch gets (<paramref name="limit"/>, or fewer when the
    /// mediator delivers fewer) and <c>N</c> such messages in front (up to 256), learning them all
    /// once takes <c>L = ceil(N / ℓ)</c> budgets when ℓ is at most 16, and
    /// <c>L = ceil(ℓ / 16) × (ceil(N / ℓ) budgets + one of the mediator's leases)</c> above 16,
    /// because one fetch starts at most 16 messages that never open. When L fits in the first pause
    /// (30 budgets), the loop reaches your message within L. Otherwise the messages learned first
    /// come back as their pauses end and are learned again, each time paused twice as long, and it
    /// reaches your message within <c>2 × ceil(log2(L / first pause)) × L</c>; that second bound was
    /// checked by measurement, not proven. Measured at this library's mediator with its defaults (a
    /// quota of 100 messages per mailbox, at most 10 per delivery, a 60-second lease): behind 99
    /// messages that never open, yours is returned after 200 seconds with <paramref name="limit"/>
    /// 10 (ten fetches of one budget each) and after 500 seconds with <paramref name="limit"/> 4.
    /// Measured at a simulated mediator that delivers as many as the limit asks for: behind 99,
    /// fetched 100 at a time, yours is returned after 380 seconds at a 60-second lease and after 140
    /// seconds at a 10-second lease. These times assume that the lookups of one fetch have ended
    /// when the next fetch starts; "What remains with slow senders" below says what a fetch that
    /// starts earlier costs.
    /// </para>
    /// <para>
    /// <strong>Resolvers that block.</strong> Each message is opened on a thread-pool work item of
    /// its own, never on your thread, so the call returns its task even when your DID or secrets
    /// resolver blocks its thread. Blocking cannot be bounded, though: the blocked thread and its
    /// work stay the resolver's until it returns, and the opening keeps its reservation meanwhile
    /// (below). A resolver that blocks its thread holds a thread-pool thread for every lookup, up to
    /// sixteen per fetch. While threads are held that way, other messages can get their thread late:
    /// one whose code starts later than an eighth of the budget and is still being opened when the
    /// budget ends is inconclusive (not remembered, and started again when it is delivered again),
    /// and the timer that ends the budget can itself fire late. Use resolvers that are asynchronous
    /// and honour their cancellation token.
    /// </para>
    /// <para>
    /// <strong>A DID resolver that ignores cancellation.</strong> The library hands every lookup one
    /// token, the fetch's own, which is cancelled when the budget ends, when you cancel, and when the
    /// container is disposed. A resolver should watch the token it is handed: one that watches
    /// another token instead (one of two that your code combined into the token you passed, for
    /// example) and reports that token's cancellation as a failure has that failure taken as the
    /// message's own, and the message is remembered. A resolver that honours the token it is handed
    /// ends at once. The lookup is host code and may outlive its fetch: one that does not honour the
    /// token keeps its reservation until it returns. Each opening is reserved before it starts, and
    /// at most 16 are held at a time per sender (the DID of the sender key the message's outer
    /// envelope names in <c>skid</c>), 16 per mailbox for messages whose outer envelope names no
    /// sender (anoncrypted ones, for example), and 256 per container. A message whose sender, mailbox
    /// or container is at its bound fails at once as "unpack capacity exhausted": it is not opened,
    /// not remembered and not acknowledged, and comes back later. So one sender whose lookups do not
    /// return holds 16 openings, and its seventeenth and later messages are refused at once while
    /// other senders' messages still open. Measured at this library's mediator with thirty messages
    /// from such a sender in front: sixteen were opened, fourteen were refused at once, and another
    /// sender's message behind them was returned after 40 seconds with <paramref name="limit"/> 10
    /// and after 80 seconds with <paramref name="limit"/> 4, the time sixteen messages that never
    /// open take. With the same thirty and lookups that honour their token, every one of the thirty
    /// was opened once, none was refused, and the other sender's message was returned after 60
    /// seconds with <paramref name="limit"/> 10 and after 160 seconds with <paramref name="limit"/>
    /// 4. The sender the outer envelope names is not authenticated at that point: it decides
    /// only which bound a message counts against, never what is opened or how. The bounds do not cap
    /// the lookups of one sender's DID: per mailbox, up to 16 messages naming it and up to 16 that
    /// name no sender can be resolving it at once, and spellings of the DID that differ count as
    /// different senders. One sender's stalled lookups therefore do not refuse another sender's
    /// message within a mailbox; across the container's mailboxes they do once 256 openings are held
    /// in all (below). A resolver that blocks its thread can hold up to 256 threads of the container.
    /// </para>
    /// <para>
    /// <strong>A message that keeps failing.</strong> Discard it with <see cref="AckAsync"/> once you
    /// have given up on it: the client never does. Do not discard an item whose
    /// <see cref="PickupItem.Inconclusive"/> is <c>true</c>: the client has not counted this delivery
    /// against that message; it is delivered again and started again then, once what was in its way
    /// is gone. When a fetch returns only inconclusive items, wait before you fetch again: for the
    /// mediator's lease where the mediator sets delivered messages aside (60 seconds at this
    /// library's), and otherwise until the openings that were in their way have ended. A fetch of
    /// nothing but items refused for capacity takes no time and is not drained, so a loop that
    /// fetches again at once would spin. Until a message is discarded, or ages out at the mediator
    /// (after 30 days by default), it stays queued and counts against your mailbox's quota there (at
    /// this library's mediator, <c>MessagePickupOptions.MaxQueueMessages</c>, 100 by default).
    /// Messages that never open — junk anyone who can address you can send — can fill that quota, and
    /// then the mediator refuses honest senders' messages, until you discard them.
    /// </para>
    /// <para>
    /// <strong>What remains with slow senders:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Many senders, each with a DID of its own, whose lookups ignore cancellation
    /// can still hold all 256 openings of the container: sixteen such senders in one mailbox, or one
    /// such sender in sixteen mailboxes. Until those lookups end, every delivered message fails at
    /// once ("unpack capacity exhausted"), for every DID and tenant of the container. Use a resolver
    /// that honours its cancellation token.</description></item>
    /// <item><description>Even with lookups that honour their token, one sender that keeps sixteen
    /// mailboxes of one container busy, each with sixteen messages that are being opened, holds all
    /// 256 while they run. A message of another mailbox is then refused for the container; it is not
    /// remembered, and it comes back.</description></item>
    /// <item><description>Messages whose outer envelope names no sender (anoncrypted, authcrypted
    /// naming the sender only in <c>apu</c>, or re-wrapped by a mediator) share one bound of 16 per
    /// mailbox.</description></item>
    /// <item><description>Sixteen messages that never open at the front of one fetch keep every
    /// place for one budget. The messages behind them in that fetch run out of time before their
    /// turn (inconclusive), and get it in the next fetch that brings them, where the sixteen are held
    /// back. Only a fetch of more than sixteen messages has messages behind sixteen; this library's
    /// mediator delivers at most ten.</description></item>
    /// <item><description>The memory lives in this process, and its longest pause is 256 budgets
    /// (about 85 minutes). So when sixteen or more messages that never open are in front of yours in
    /// one fetch, a loop that is run only every two hours, a mediator whose lease is two hours, or a
    /// new process for each loop finds nothing held back, opens the sixteen again, and does not
    /// return your message (measured: not in twelve runs, and not in two simulated days). Run again
    /// five minutes later in the same process, the loop returns it in its second run. This takes a
    /// mediator that delivers more than sixteen per fetch.</description></item>
    /// <item><description>More than 256 messages that never open queued in front of yours (which needs
    /// a mediator whose quota is above this library's 100): the memory cannot keep them all, and the
    /// loop reaches your message only once you discard the failed items you have given up on with
    /// <see cref="AckAsync"/> (never one whose <see cref="PickupItem.Inconclusive"/> is <c>true</c>).
    /// More than 256 mediator and mailbox pairs push the least recently used out of the memory, and
    /// a restart forgets everything: those messages are opened and learned again.</description></item>
    /// <item><description>A mediator that does not set delivered messages aside delivers the head of
    /// your queue on every fetch: a loop then reaches only what is within <paramref name="limit"/> of
    /// it. Discard the failed items you have given up on with <see cref="AckAsync"/> (never one whose
    /// <see cref="PickupItem.Inconclusive"/> is <c>true</c>); meanwhile, fetches that meet only
    /// remembered messages take no time and run none of your code. An inconclusive item comes again
    /// with the very next fetch there, and a fetch of nothing but items refused for capacity takes
    /// no time either: when a fetch brings only inconclusive items, wait before you fetch again,
    /// until the openings that were in their way have ended.</description></item>
    /// <item><description>A message refused under a <paramref name="recipientDid"/> filter ("for
    /// another of your DIDs") is not remembered: it is opened again each time it comes.</description></item>
    /// <item><description>Up to 256 openings per container can be held by host code that does not
    /// return. A resolver that blocks its thread holds a thread-pool thread for each of its lookups,
    /// up to sixteen per fetch. While threads are held that way, a message can get its thread late:
    /// one whose code starts later than an eighth of the budget and is still being opened when the
    /// budget ends is inconclusive (not remembered, and started again when it is delivered again),
    /// and the timer that ends the budget can fire late.</description></item>
    /// <item><description>A message that never opens and whose code starts later than an eighth of
    /// the budget in every delivery is inconclusive every time and is never remembered. Under test
    /// this was produced by moving the clock; no way for a remote party to cause it was found. On a
    /// real clock such late starts come from a resolver that blocks its thread while the thread pool
    /// is small (the item above). Nothing else keeps a message inconclusive from one delivery to the
    /// next, on one assumption: the mediator delivers oldest first. Whatever was in front of a late
    /// start has then opened, been remembered or been discarded by the next delivery, so the message
    /// starts first.</description></item>
    /// <item><description>A lookup that has been cancelled keeps its place until it ends; one that
    /// never got a thread keeps its place until the thread pool reaches its cancelled work item.
    /// When the next fetch starts before that, messages of the same sender (or, for messages that
    /// name no sender, of the same mailbox) beyond the places still free are refused at once
    /// ("unpack capacity exhausted"): the fetch takes no time for them, they are inconclusive, and
    /// they come again with their next delivery, at most one of the mediator's leases later. Other
    /// senders' messages are not affected. With a lookup that ends as soon as it is cancelled this
    /// is rare; with one that takes even a millisecond to end and a fetch that follows immediately
    /// it is the usual outcome. Every time given above for reaching a message assumes that the
    /// lookups of one fetch have ended when the next one starts.</description></item>
    /// <item><description>A resolver or transport that watches another token than the one it is
    /// handed, and reports that token's cancellation as a failure, has that failure taken as the
    /// message's (or the request's) own. The client is handed one token; a token that one was
    /// combined from is not visible to it.</description></item>
    /// <item><description>The pauses are measured on the host's clock. A reading below the one a
    /// message's pause is counted from counts as due, so after the clock steps back the message is
    /// opened once more. If it fails again, that is the same episode: nothing more is counted against
    /// it, and it is held back for the same pause again, counted from that failure, not for the next,
    /// doubled pause. So a step back can make a message due later than without it (in the measured
    /// case half a minute later: at minute 30.5 instead of minute 30), and each further step back
    /// costs one more opening. The moment a message's code starts is read from the same clock: a
    /// reading below the one taken when the fetch began to open its messages, or more than an eighth
    /// of the budget after it, means the message is not remembered when the budget ends (it is
    /// inconclusive).</description></item>
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

        var inputs = new List<DeliveredInput>(contents.Entries.Count);
        foreach (var entry in contents.Entries)
        {
            if (entry.Payload is { } payload)
                inputs.Add(new DeliveredInput(entry.Id, payload));
        }
        var outcomes = inputs.Count == 0
            ? Array.Empty<DeliveredOutcome>()
            : await Exchange.UnpackDeliveredBatchAsync(new DeliveryScope(mediatorDid, from), inputs, request.Id, ct).ConfigureAwait(false);
        if (outcomes.Count != inputs.Count)
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
                // Whether this delivery counted against it is passed on as the unpacker said it
                // (PickupItem.Inconclusive). The failures made here (an entry with no usable payload,
                // a filter mismatch) are never inconclusive: the first is not a message this client
                // can open, the second opened.
                items.Add(new PickupItem(entry.Id, unpacked: null, outcome.Error, outcome.Inconclusive));
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
    /// Not an item whose <see cref="PickupItem.Inconclusive"/> is <c>true</c>: the client has not
    /// counted this delivery against that message, so leave it queued.
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
    /// <strong>What it returns.</strong> The status the mediator sent as its answer to the last
    /// message, or <c>null</c> when it sent none. Nothing is ever put in its place: this call never
    /// asks for a status of its own. Message Pickup 3.0 says the mediator SHOULD answer
    /// <c>messages-received</c> with a status reflecting the changes to the queue, not that it must,
    /// and some send none (the Identus mediator). Message Pickup 4.0 dropped that answer: each message
    /// is sent one way, without <c>return_route</c>, so under 4.0 this call always returns
    /// <c>null</c>. A status that comes is the mediator's own answer to the message, so what it counts
    /// already reflects the acknowledgement. <c>null</c> says only that no status came, not whether
    /// the mediator applied the acknowledgement; acknowledging again is harmless. To see what is
    /// queued, call <see cref="GetStatusAsync"/>, but its answer is not ordered after an
    /// acknowledgement that got no status, so it may still count messages just acknowledged.
    /// </para>
    /// <para>
    /// <strong>Before you close the connection.</strong> When no status came, nothing tells you the
    /// mediator has applied the acknowledgement. Disposing this agent's container closes its
    /// WebSocket connections politely: each sends its close and waits, up to
    /// <c>WebSocketTransportOptions.SendTimeout</c>, for the mediator to answer it, and this
    /// library's mediator answers only after it has handled what it received before the close. It
    /// also finishes a message it received in full when the connection drops behind it, for a few
    /// seconds. An abrupt drop before the message is through, a mediator slower than that, or one
    /// that drops the work of a closed connection can still lose the acknowledgement; the messages
    /// then come back (delivery is at-least-once). The same holds for the acknowledgement a
    /// <see cref="FetchAsync"/> sends under Message Pickup 4.0.
    /// </para>
    /// <para>
    /// <strong>A failure is never <c>null</c>.</strong> A message that could not be sent ends the
    /// call with an exception: <see cref="TransportException"/> when the transport failed,
    /// <see cref="TimeoutException"/> when it was not sent (over HTTP: not answered by a response)
    /// within <see cref="MediatorClientOptions.RequestTimeout"/>. The messages before it were sent.
    /// </para>
    /// <para>
    /// <strong>Time.</strong> Each message gets its own <see cref="MediatorClientOptions.RequestTimeout"/>.
    /// Over HTTP the response shows at once whether a status came. On a WebSocket, silence cannot be
    /// told from a slow answer, so once a 3.0 message is sent the client waits half of
    /// <c>RequestTimeout</c> for its status (never past that message's <c>RequestTimeout</c>), then
    /// moves on: to the next message, or it returns <c>null</c>. Under 4.0 nothing is waited for
    /// after a message: it is only sent.
    /// </para>
    /// <para>
    /// <strong>Which status answers which message.</strong> DIDComm threading: a reply carries the
    /// <c>thid</c> of the message it answers, and a message without that <c>thid</c> is not part of
    /// its thread. So a status answers a message only when its <c>thid</c> names that message, over
    /// a WebSocket and in the HTTP response to it alike; one without <c>thid</c> answers none and is
    /// dropped, which also keeps a late status for a message given up on from being taken for
    /// another. A mediator that leaves <c>thid</c> out of its status is therefore treated as one that
    /// sends no status: the call returns <c>null</c>.
    /// </para>
    /// </remarks>
    /// <param name="mediatorDid">The mediator's DID: a bare DID.</param>
    /// <param name="from">Your DID, the one you enrolled as: a bare DID whose keys this agent holds.</param>
    /// <param name="ids">1 to 10 000 ids, each exactly as <see cref="PickupItem.Id"/> gave it.</param>
    /// <param name="serviceEndpointOverride">The mediator address to use instead of the first one in its DID document (see <see cref="GetStatusAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mediator's <c>status</c> answering the last message, or <c>null</c> when it sent none (always under 4.0).</returns>
    /// <exception cref="ArgumentException">A DID is not a bare DID, <paramref name="ids"/> is empty, longer than 10 000 or has an empty, over-long or non-URI-safe id (the message names its index), or <paramref name="serviceEndpointOverride"/> is relative or carries user information.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="GetStatusAsync"/>; nothing was sent.</exception>
    /// <exception cref="TimeoutException">A message was not sent within <see cref="MediatorClientOptions.RequestTimeout"/> (over HTTP: its response did not come within it), or the Discover Features query that negotiates the version got no answer. The mediator may still have received it; retry (acknowledging twice is harmless). Never for a status that did not come: that is <c>null</c>.</exception>
    /// <exception cref="MediationProtocolException">A problem report or a malformed status; or, negotiating, a mediator that disclosed neither Message Pickup version (<see cref="MediationNotSupportedException"/>).</exception>
    /// <exception cref="TransportException">The transport failed.</exception>
    /// <exception cref="ObjectDisposedException">The container was disposed.</exception>
    public async Task<PickupStatus?> AckAsync(string mediatorDid, string from, IReadOnlyList<string> ids, Uri? serviceEndpointOverride = null, CancellationToken ct = default)
    {
        RequireBareDid(mediatorDid, nameof(mediatorDid));
        RequireBareDid(from, nameof(from));
        var copy = RequireIds(ids, nameof(ids));
        RequireEndpoint(serviceEndpointOverride, nameof(serviceEndpointOverride));

        var route = await Exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: false, ct).ConfigureAwait(false);
        var version = await ResolveVersionAsync(route, mediatorDid, from, ct).ConfigureAwait(false);
        var spec = DeliveryReader.MessagesReceivedSpec;
        PickupStatus? status = null;
        for (var start = 0; start < copy.Length; start += AckChunk)
        {
            var chunk = copy.AsSpan(start, Math.Min(AckChunk, copy.Length - start)).ToArray();
            var request = PickupWire.MessagesReceived(version, from, mediatorDid, chunk);
            if (!PickupWire.AnswersAcknowledgement(version))
            {
                // 4.0: nothing answers messages-received (L62, L294); it is only sent, and there is
                // no status to return (OD-6).
                await Exchange.SendOneWayAsync(route, request, PickupWire.MessagesReceivedName, mediatorDid, from, ct).ConfigureAwait(false);
                continue;
            }
            var answer = await AcknowledgeChunkAsync(route, request, mediatorDid, from, ct).ConfigureAwait(false);
            // Only the mediator's own answer to the last message is returned (OD-6): a status that
            // answered an earlier one predates the messages after it.
            status = answer is null
                ? null
                : DeliveryReader.ReadStatus(DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: false, filter: null);
        }
        return status;
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
    /// <see cref="MediatorClientOptions.UnpackTimeout"/>, during your own <c>MoveNextAsync</c>, the
    /// way <see cref="FetchAsync"/> opens a fetch: up to sixteen at the same time, each until it
    /// opens, fails or that time is up, each on a thread-pool work item of its own and reserved
    /// before it starts, and a message the client remembers because it did not open before yielded
    /// at once as a failed item, without being opened.
    /// The time you spend on an item never counts against it. Messages it runs out on are yielded as
    /// failed items (<see cref="TimeoutException"/>) and not acknowledged; the rest are yielded and
    /// acknowledged as usual. A delivery that brings nothing new is not opened at all.
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
    /// a round brings nothing new (see "A mediator that does not set delivered messages aside"), or
    /// until 50 rounds have brought something new; only then is the next cycle scheduled, one
    /// <see cref="MediatorClientOptions.LivePollInterval"/> after that end. A step that fails does
    /// not end the drain: the stream pauses (see "Starting and failing"), and the cycle that follows
    /// the pause turns live delivery on again and goes on with the same drain, which keeps what it
    /// has already been handed and its count of rounds. When the step that failed was an
    /// acknowledgement and nothing else is ready, the acknowledgement is sent again first, and a
    /// cycle follows only once the mediator has accepted it. So however slowly the mediator answers
    /// — even more slowly than the interval — every cycle fetches what is queued, and a cycle coming
    /// due never pre-empts a drain or the pushes waiting their turn. Two consequences: turning live
    /// delivery on again after a dropped connection waits for the drain in progress to end (at most
    /// 50 rounds that bring something new, each bounded by
    /// <see cref="MediatorClientOptions.RequestTimeout"/> and the decrypt budget), and the stream
    /// does its work — cycles, the 10-second acknowledgement, re-arming live mode — only inside your
    /// <c>MoveNextAsync</c>, so while your loop body holds an item none of it happens (a slow loop
    /// body delays the poll and can let the mediator's lease on taken messages run out; they then
    /// come again and are recognised as repeats).
    /// </para>
    /// <para>
    /// <strong>Clocks.</strong> The stream schedules its work — the next cycle, the end of a back-off,
    /// the 10-second acknowledgement, the give-up point — with timers it sets itself, and never by
    /// comparing clock readings. So a host clock that steps back or forward (an NTP correction, a
    /// restored snapshot) changes none of that scheduling: no early cycle, no burst of retries, no
    /// postponement. The pauses of messages that did not open are measured on the clock, though:
    /// <see cref="FetchAsync"/>'s "What remains with slow senders" says what a step back costs
    /// there.
    /// </para>
    /// <para>
    /// <strong>A message that keeps failing</strong> is yielded again each time the mediator delivers
    /// it again; while the client remembers it, it is yielded at once, without being opened (see
    /// <see cref="FetchAsync"/>, "Messages that did not open"). Discard it with
    /// <c>AckAsync(mediatorDid, from, new[] { item.Id })</c> once you have given up on it: the stream
    /// never does. Do not discard an item whose <see cref="PickupItem.Inconclusive"/> is <c>true</c>:
    /// the stream has not counted this delivery against that message; it is delivered again and
    /// started again then, once what was in its way is gone. The stream needs no waiting on your side
    /// for such items: a drain round that brings back only what the drain was already handed ends
    /// the drain, so an inconclusive item is yielded at most once per drain, and the next cycle
    /// starts one <see cref="MediatorClientOptions.LivePollInterval"/> after the drain ended. Until a
    /// message is discarded or ages out it counts against your mailbox's quota at the
    /// mediator (see <see cref="FetchAsync"/>). The error's text can quote DIDs and key ids (see
    /// <see cref="PickupItem.Error"/>), and the messages are not replay-checked (see the class
    /// remarks).
    /// </para>
    /// <para>
    /// <strong>Messages that never open, in front of yours.</strong> Each drain round learns up to 10
    /// of them within its budget, and a cycle drains as many such rounds as fit in the
    /// mediator's lease before the first round's messages come back (at least one); rounds that meet
    /// only remembered messages take no time. Measured at a simulated mediator that sets delivered
    /// messages aside, with the default <see cref="MediatorClientOptions.LivePollInterval"/> and 99
    /// such messages in front of yours: at a 60-second lease the stream yields yours after 380
    /// seconds, in its fourth cycle. With a lease shorter than
    /// <see cref="MediatorClientOptions.UnpackTimeout"/> it learns 10 per cycle, and when learning
    /// them all takes longer than their first pause (10 minutes), the ones learned first are opened
    /// once more as their pauses end: at a 10-second lease they are opened a second time from 640
    /// seconds on, and the stream yields yours after 1380 seconds, in its eighteenth cycle. These
    /// times assume that the lookups of one delivery have ended when the next one is opened. The
    /// bounds on openings, and what remains with slow senders, described for
    /// <see cref="FetchAsync"/>, apply to the stream as well.
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
    /// thrown at once). While a pause lasts nothing is sent, acknowledgements included. The first
    /// such failure sets a give-up point, <see cref="MediatorClientOptions.RequestTimeout"/> plus 30
    /// seconds later (one minute in all by default); a failure after that point ends the stream with
    /// its exception, so a condition that clears within one <c>RequestTimeout</c> (the busy refusal,
    /// a mediator restarting) does not end it. Two things start the pauses again at 1 second and let
    /// the give-up point go: a drain that ends, and a drain round that brings something new (a
    /// message the stream neither holds nor was handed earlier in the same drain). Two more let the
    /// give-up point go and keep the pause where it is: a push that brings something new, and an
    /// acknowledgement the mediator accepted (under 4.0: sent). They show that the connection is
    /// alive, or that one request went through, not that a cycle does. After any of the four, the
    /// next failure sets a new give-up point. A request that fails once your token is cancelled is
    /// not retried: the enumeration ends as cancelled, whatever the request reported. Any other
    /// failure ends the stream at once with its exception: a problem report from the mediator, a
    /// malformed answer, a key of <paramref name="from"/> this agent does not hold, the container's
    /// disposal.
    /// </para>
    /// <para>
    /// <strong>A mediator that keeps pushing while it refuses requests.</strong> Since a push with
    /// something new, like an accepted acknowledgement, lets the give-up point go, a stream whose
    /// cycle requests all fail does not give up while such pushes or acknowledgements keep coming
    /// before each give-up point. Its cycle requests stay at one per pause: after 1, 2, 4, 8 and 16
    /// seconds, then every 30 seconds, however often the pushes come. Measured over ten minutes: 24
    /// cycle requests when each is refused at once, 12 when each gets no answer within the default
    /// <see cref="MediatorClientOptions.RequestTimeout"/>. Once the pushes stop, the first request
    /// that fails after the give-up point ends the stream. That affects a recipient whose mediator
    /// still pushes but refuses or drops its requests: its stream keeps retrying instead of ending.
    /// </para>
    /// <para>
    /// <strong>A mediator that does not set delivered messages aside</strong> (Message Pickup 3.0 does
    /// not require it; this library's mediator does) answers every delivery request with the head of
    /// your queue until it is acknowledged. A round that brings only messages the stream holds, or
    /// was handed earlier in the same drain (yielded as failures, or taken), ends the drain. So a
    /// message there that never opens is yielded once per drain, and one the mediator keeps after it
    /// was acknowledged is opened once per drain. A drain is one cycle unless one of its steps
    /// fails, and that costs two delivery requests per cycle; everything
    /// behind it waits until you discard it with <see cref="AckAsync"/> (an item whose
    /// <see cref="PickupItem.Inconclusive"/> is <c>true</c> is not one to discard).
    /// </para>
    /// <para>
    /// <strong>A drain that outlasts the mediator's lease</strong> meets the messages it has already
    /// handled again, first: their lease has ended, and a delivery request names only how
    /// many messages to send, never where to start (a Message Pickup 3.0 <c>delivery-request</c> has no
    /// offset). A round made only of them ends the drain, so messages queued behind them wait one more
    /// <see cref="MediatorClientOptions.LivePollInterval"/>. Nothing is lost: live delivery stays on,
    /// and what is not acknowledged stays queued. That affects a recipient with more messages that do
    /// not open than one drain can pass within a lease.
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

    // One messages-received of AckAsync under 3.0: the mediator's answer, or null when it sent none
    // (3.0 L193: it SHOULD, not MUST). Over HTTP the response says so at once. On any other route (a
    // WebSocket) silence cannot be told from a slow answer: once the message is sent, its status is
    // waited for half of RequestTimeout (as before OD-6), and then the wait is given up. A message
    // whose send did not complete is a TimeoutException or a TransportException, never null.
    private async Task<InboundMessageSnapshot?> AcknowledgeChunkAsync(MediatorRoute route, Message request, string mediatorDid, string from, CancellationToken ct)
        => await Exchange.ExchangeAllowingNoAnswerAsync(route, request, DeliveryReader.MessagesReceivedSpec, mediatorDid, from, StatusWait(Exchange.Options.RequestTimeout), ct).ConfigureAwait(false);

    // Half of RequestTimeout, at least one tick: a RequestTimeout of one tick is valid (OD6-ADV-1).
    private static TimeSpan StatusWait(TimeSpan requestTimeout) => TimeSpan.FromTicks(Math.Max(1, requestTimeout.Ticks / 2));

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
                await Exchange.ExchangeAllowingNoAnswerAsync(route, request, DeliveryReader.MessagesReceivedSpec, mediatorDid, from, answerWait: null, deadline.Token).ConfigureAwait(false);
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
