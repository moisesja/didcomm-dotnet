using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// The mediator role of Message Pickup 4.0 (<c>https://didcomm.org/message-pickup/4.0</c>,
/// FR-PICK-12). Answers <c>status-request</c> with <c>status</c>, <c>delivery-request</c> with a
/// <c>delivery</c> (an empty one when nothing can be delivered), and <c>live-delivery-change</c>
/// with <c>status</c> or a problem report; <c>messages-received</c> is applied and not answered.
/// Registered by <c>AddMessagePickup()</c> next to <see cref="MessagePickupHandler"/> unless
/// <see cref="MessagePickupOptions.EnableV4"/> is <c>false</c>; Discover Features then discloses
/// both versions with <c>roles: ["mediator"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One mailbox for both versions.</strong> The handler works over the same
/// <see cref="MessagePickupQueue"/> as the 3.0 handler, with the same rules for who is a peer
/// (only an authcrypt sender; anything else is dropped before its type or body is read, and one
/// rate-limited log line records the id), whose messages it sees (its own grant; a
/// <c>recipient_did</c> it has not registered is answered like an empty queue), how much a batch
/// carries (<see cref="MessagePickupOptions.MaxDeliveryBatch"/>,
/// <see cref="MessagePickupOptions.MaxDeliveryBytes"/>), leases, passed-over messages and live
/// delivery. A message is queued once, whichever version its recipient picks it up with: a lease
/// taken through one version is acknowledged through the other, and a recipient may switch
/// versions between requests. Read <see cref="MessagePickupHandler"/> for those rules; what
/// follows is what 4.0 does differently.
/// </para>
/// <para>
/// <strong>Status.</strong> A <c>status</c> always carries the request's thread as <c>thid</c>,
/// gives <c>newest_received_time</c> and <c>oldest_received_time</c> in UTC epoch milliseconds (only
/// when something is waiting), and has no <c>longest_waited_seconds</c>.
/// </para>
/// <para>
/// <strong>Deliveries.</strong> A <c>delivery-request</c> names its limit
/// <c>message_count_limit</c> (zero or more; a request that carries only 3.0's <c>limit</c> is
/// malformed), and a <c>delivery</c> never carries more attachments than it. When nothing can be
/// delivered (an empty queue, a limit of zero, a <c>recipient_did</c> the peer has not registered,
/// or only messages that are leased, not yet due or passed over) the answer is a <c>delivery</c>
/// with an empty <c>attachments</c> array, on the request's thread and repeating its
/// <c>recipient_did</c>, never a <c>status</c>. A <c>delivery-request</c> that arrives without a
/// return-route session for this peer is not answered at all, and nothing is read or leased for
/// it: 4.0 asks for <c>return_route: all</c> on every request, and an empty <c>delivery</c> sent
/// any other way would claim that nothing is waiting. The library's endpoints never send a reply
/// without a session either.
/// </para>
/// <para>
/// <strong>Acknowledgements.</strong> <c>messages-received</c> removes the acknowledged messages
/// of the peer and gets no reply, even when it carries <c>return_route: all</c>; 4.0 sends it
/// without one. Its <c>message_id_list</c> is bounded by
/// <see cref="MessagePickupOptions.MaxAckIds"/> from its length, before any entry is read.
/// </para>
/// <para>
/// <strong>Live delivery.</strong> <c>live-delivery-change</c> with <c>true</c> on a WebSocket
/// session turns on pushes for that session, framed as 4.0 <c>delivery</c> messages with one
/// attachment and no <c>thid</c> (they answer no request), held until a <c>messages-received</c>
/// names them and handed back to the queue when the session closes or the lease runs out: a push
/// left unacknowledged goes back to the mailbox and is delivered only by a <c>delivery-request</c>,
/// never pushed live again (L304, L308; the same rule as 3.0, since one mailbox serves both). The
/// framing is the session's, set by the version of its last <c>live-delivery-change</c> that turned
/// live delivery on; turning it off in either version turns it off. On a connection that cannot do
/// it (HTTP, or no session) the answer is the 4.0 problem report
/// (<c>https://didcomm.org/message-pickup/4.0/problem-report</c>, <c>pthid</c> = the request's
/// thread, code <c>e.m.live-mode-not-supported</c>).
/// </para>
/// <para>
/// <strong>Errors.</strong> A malformed body from an authenticated peer throws
/// <see cref="MalformedMessageException"/> out of dispatch; its text names the member and the rule
/// and never a value from the message. A store, registry or session fault never leaves the
/// handler: nothing is answered and leases the request took are released. Messages of the protocol
/// that a mediator does not act on (<c>status</c>, <c>delivery</c>, <c>problem-report</c>, or a
/// name it does not know) are ignored. Log lines carry the request's id (shortened when long) and
/// type names, never a DID or a thread id.
/// </para>
/// <para>
/// <strong>Replay.</strong> The handler does not check for replayed requests itself: front it with
/// an endpoint that runs replay admission (the library's <c>MapDidCommMediator</c> and the
/// registry-aware <c>MapDidCommEndpoint</c> do), or call
/// <see cref="DidComm.Transports.ReturnRoute.IInboundReplayGuard.TryAdmit"/> yourself before dispatch
/// (see <see cref="ProtocolDispatcher"/>).
/// </para>
/// </remarks>
public sealed class MessagePickup40Handler : IProtocolHandler, IProtocolHandlerRoles
{
    private readonly PickupCoordinator _pickup;
    private readonly ILogger? _logger;

    /// <summary>Create the handler. Called by the container; hosts register it with <c>AddMessagePickup()</c>, or by hand over their own <see cref="MessagePickupQueue"/>.</summary>
    /// <param name="queue">The pickup queue this handler serves; the same instance the delivery pipeline writes to and the 3.0 handler reads.</param>
    /// <param name="logger">Optional logger. Lines carry message ids and type names only.</param>
    public MessagePickup40Handler(MessagePickupQueue queue, ILogger<MessagePickup40Handler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _pickup = queue.Coordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProtocolUri => MessagePickup40.ProtocolUri;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles { get; } = Array.AsReadOnly(new[] { MessagePickup.MediatorRole });

    /// <inheritdoc />
    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        // (a) Authcrypt or nothing (FR-PICK-01). Before the type or body is read, so every
        //     unauthenticated message gets the same treatment whatever it contains.
        if (!_pickup.TryGetPeer(context.Received, message.Id, out var peerDid, out var localDid))
            return null;

        // A session bound to another pair is not this peer's connection.
        var session = _pickup.SessionFor(context.Session, peerDid, localDid, message.Id);

        // (b) status-request → status, on the request's thread (4.0 L33).
        if (MessagePickup40.TryParseStatusRequest(message, out var statusFilter))
        {
            var thid = PickupReplies.ThreadOf(message);
            var view = await _pickup.StatusAsync(peerDid, statusFilter, session, message.Id, ct).ConfigureAwait(false);
            return Status(message, localDid, peerDid, view, thid);
        }

        // (c) delivery-request → delivery (pushed through the session, size-bounded) | an empty
        //     delivery (L210, L259). Without a session nothing is read or leased: there is no
        //     connection for either answer (L52, L69).
        if (MessagePickup40.TryParseDeliveryRequest(message, out var limit, out var deliveryFilter))
        {
            var thid = PickupReplies.ThreadOf(message);
            if (session is null)
            {
                MediatorLog.Write(_logger, LogLevel.Debug, "Message-pickup 4.0 delivery request {MessageId} not answered: it has no return-route session to carry the delivery; nothing was read or leased.", MediatorLog.LogId(message.Id));
                return null;
            }
            var outcome = await _pickup.DeliverRequestedAsync(
                peerDid, limit, deliveryFilter, session,
                messages => MessagePickup40.CreateDelivery(localDid, peerDid, messages, thid, deliveryFilter),
                message.Id, ct).ConfigureAwait(false);
            return outcome.Reply == PickupReply.Status
                ? PickupReplies.Build(_logger, message, () => MessagePickup40.CreateDelivery(localDid, peerDid, Array.Empty<DeliveredMessage>(), thid, deliveryFilter))
                : null;
        }

        // (d) messages-received → applied, no reply (L62, L294), whatever return_route it carries.
        //     The id bound is part of the parse: a longer list is refused from its length, before
        //     any entry is read. Nothing is echoed, but the thread is checked as for every request,
        //     so a thread id no request may carry is refused before the store is touched.
        if (MessagePickup40.TryParseMessagesReceived(message, _pickup.MaxAckIds, out var messageIds))
        {
            _ = PickupReplies.ThreadOf(message);
            await _pickup.AcknowledgeAsync(peerDid, messageIds, session, answerWithStatus: false, message.Id, ct).ConfigureAwait(false);
            return null;
        }

        // (e) live-delivery-change → status | the 4.0 problem report (L340-L369). Live pushes on
        //     this session are framed as 4.0 deliveries without a thread (L34, L308).
        if (MessagePickup40.TryParseLiveDeliveryChange(message, out var liveDelivery))
        {
            var thid = PickupReplies.ThreadOf(message);
            var outcome = await _pickup.SetLiveDeliveryAsync(
                peerDid, liveDelivery, session,
                static (from, to, delivered) => MessagePickup40.CreateDelivery(from, to, new[] { delivered }),
                message.Id, ct).ConfigureAwait(false);
            return outcome.Reply switch
            {
                PickupReply.Status => Status(message, localDid, peerDid, outcome.Status, thid),
                PickupReply.LiveModeNotSupported => PickupReplies.Build(_logger, message, () => MessagePickup40.CreateLiveModeNotSupported(localDid, peerDid, thid)),
                _ => null,
            };
        }

        // (f) status, delivery, problem-report, or a name this version does not define: not for the mediator role.
        MediatorLog.Write(_logger, LogLevel.Debug, "Message-pickup message {MessageId} ignored: not a request the mediator role answers.", MediatorLog.LogId(message.Id));
        return null;
    }

    private Message? Status(Message request, string localDid, string peerDid, PickupStatusView? view, string thid)
        => view is null ? null : PickupReplies.Build(_logger, request, () => MessagePickup40.CreateStatus(localDid, peerDid, ToStatus(view), thid));

    // 4.0 units: UTC epoch milliseconds, and no longest_waited_seconds (L37, L39, L164). The time
    // members only when something is waiting.
    private static PickupStatus40 ToStatus(PickupStatusView view)
    {
        var queue = view.Queue;
        long? oldest = null, newest = null;
        if (queue.MessageCount > 0 && queue.OldestReceivedAt is { } oldestAt && queue.NewestReceivedAt is { } newestAt)
        {
            oldest = Math.Max(0, oldestAt.ToUnixTimeMilliseconds());
            newest = Math.Max(0, newestAt.ToUnixTimeMilliseconds());
        }
        return new PickupStatus40(
            MessageCount: queue.MessageCount,
            RecipientDid: view.RecipientDid,
            NewestReceivedTimeMs: newest,
            OldestReceivedTimeMs: oldest,
            TotalBytes: queue.TotalBytes,
            LiveDelivery: view.LiveDelivery);
    }
}
