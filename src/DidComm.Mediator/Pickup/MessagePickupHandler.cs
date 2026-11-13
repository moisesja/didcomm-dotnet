using DidComm.Exceptions;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// The mediator role of Message Pickup 3.0 (FR-PICK-01..10). Answers <c>status-request</c> with
/// <c>status</c>, <c>delivery-request</c> with a <c>delivery</c> (or <c>status</c> when nothing is
/// waiting), <c>messages-received</c> with <c>status</c>, and <c>live-delivery-change</c> with
/// <c>status</c> or a problem report. Registered by <c>AddMessagePickup()</c>; Discover Features
/// then discloses the protocol with <c>roles: ["mediator"]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is a peer.</strong> Only a request with an authcrypt layer is acted on, and the peer
/// is the DID of its authcrypt sender key, never the plaintext <c>from</c>. A plaintext, anoncrypt,
/// signed-only or <c>anoncrypt(sign)</c> request is dropped before its type or body is read:
/// nothing is removed or changed, no reply is produced, and one rate-limited log line records the
/// id.
/// </para>
/// <para>
/// <strong>Whose messages.</strong> A peer sees the messages held under its own mediation grant and
/// nothing else. A <c>recipient_did</c> narrows that to one DID registered under the grant; a DID
/// the peer has not registered — whether another grant holds it, nobody does, or the value is not a
/// bare DID — is answered like an empty queue (<c>status</c> echoing the value with
/// <c>message_count: 0</c>), so the answer says nothing about other recipients. Registration is
/// checked again when messages are delivered: what is queued for a DID that no longer belongs to
/// the peer's grant is not delivered, and is removed when an unfiltered <c>delivery-request</c>
/// comes to it.
/// </para>
/// <para>
/// <strong>Deliveries.</strong> A <c>delivery</c> carries up to the request's <c>limit</c>, capped
/// by <see cref="MessagePickupOptions.MaxDeliveryBatch"/> and by
/// <see cref="MessagePickupOptions.MaxDeliveryBytes"/>: the handler pushes it through the request's
/// return-route session with a size bound (<c>IReturnRouteSession.TrySendAsync(message,
/// maxPackedBytes, ct)</c>) and, when the packed message is over the bound, takes the last message
/// out and tries again. A message that is over the bound even alone (the peer's own DID document
/// makes its envelopes that large) is not delivered and not removed: it is passed over — set aside
/// in the store for a period that starts at <see cref="MessagePickupOptions.LeaseTimeout"/> and
/// doubles each time the message is tried and does not fit — and the same request goes on to what
/// is queued behind it, answering <c>status</c> only if nothing else can be delivered. That mark
/// belongs to the message, not to a lease or a connection, so later requests, however far apart
/// and on whatever connection, are not held up by the same messages again. One request does a
/// bounded amount of work: at most twice <see cref="MessagePickupOptions.MaxDeliveryBatch"/> lease
/// rounds and as many packs. Over HTTP the pushed <c>delivery</c> is the response body. Delivered
/// messages stay stored, under a lease, until a <c>messages-received</c> names them; a lease that
/// runs out (<see cref="MessagePickupOptions.LeaseTimeout"/>), or whose WebSocket closes, makes
/// them deliverable again, so a recipient may see a message more than once. A request that arrives
/// without a return-route session leases nothing and is answered <c>status</c>: there is no
/// connection to carry a batch on. Replies carry the request's thread (<c>thid</c> if present, else
/// <c>id</c>) as <c>thid</c>.
/// </para>
/// <para>
/// <strong>Live delivery.</strong> <c>live-delivery-change</c> with <c>true</c> on a WebSocket
/// session turns on pushes for that session: a message arriving through
/// <see cref="MessagePickupQueue.DeliverAsync"/> is then sent as a <c>delivery</c> with one
/// attachment and no <c>thid</c>, and held until acknowledged. The flag belongs to the session and
/// ends with it. On a connection that cannot do it (HTTP, or no session) the answer is a problem
/// report with code <c>e.m.live-mode-not-supported</c>.
/// </para>
/// <para>
/// <strong>Errors.</strong> A malformed body from an authenticated peer throws
/// <see cref="MalformedMessageException"/> out of dispatch, like any malformed message; its text
/// names the member and the rule and never a value from the message. A store, registry or session
/// fault never leaves the handler: nothing is answered and leases the request took are released.
/// Messages of the protocol that a mediator does not act on (<c>status</c>, <c>delivery</c>, or a
/// name it does not know) are ignored.
/// </para>
/// </remarks>
public sealed class MessagePickupHandler : IProtocolHandler, IProtocolHandlerRoles
{
    private readonly PickupCoordinator _pickup;
    private readonly ILogger? _logger;

    /// <summary>Create the handler. Called by the container; hosts register it with <c>AddMessagePickup()</c>.</summary>
    /// <param name="queue">The pickup queue this handler serves; the same instance the delivery pipeline writes to.</param>
    /// <param name="logger">Optional logger. Lines carry message ids and type names only.</param>
    public MessagePickupHandler(MessagePickupQueue queue, ILogger<MessagePickupHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _pickup = queue.Coordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProtocolUri => MessagePickup.ProtocolUri;

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

        // (b) status-request → status.
        if (MessagePickup.TryParseStatusRequest(message, out var statusFilter))
        {
            var thid = ThreadOf(message);
            var view = await _pickup.StatusAsync(peerDid, statusFilter, session, message.Id, ct).ConfigureAwait(false);
            return Status(message, localDid, peerDid, view, thid);
        }

        // (c) delivery-request → delivery (pushed through the session, size-bounded) | status.
        if (MessagePickup.TryParseDeliveryRequest(message, out var limit, out var deliveryFilter))
        {
            var thid = ThreadOf(message);
            var outcome = await _pickup.DeliverRequestedAsync(
                peerDid, limit, deliveryFilter, session,
                messages => MessagePickup.CreateDelivery(localDid, peerDid, messages, thid, deliveryFilter),
                message.Id, ct).ConfigureAwait(false);
            return outcome.Reply == PickupReply.Status ? Status(message, localDid, peerDid, outcome.Status, thid) : null;
        }

        // (d) messages-received → status. The id bound is part of the parse: a longer list is
        //     refused from its length, before any entry is read.
        if (MessagePickup.TryParseMessagesReceived(message, _pickup.MaxAckIds, out var messageIds))
        {
            var thid = ThreadOf(message);
            var view = await _pickup.AcknowledgeAsync(peerDid, messageIds, session, answerWithStatus: true, message.Id, ct).ConfigureAwait(false);
            return Status(message, localDid, peerDid, view, thid);
        }

        // (e) live-delivery-change → status | problem report.
        if (MessagePickup.TryParseLiveDeliveryChange(message, out var liveDelivery))
        {
            var thid = ThreadOf(message);
            var outcome = await _pickup.SetLiveDeliveryAsync(
                peerDid, liveDelivery, session,
                static (from, to, delivered) => MessagePickup.CreateDelivery(from, to, new[] { delivered }),
                message.Id, ct).ConfigureAwait(false);
            return outcome.Reply switch
            {
                PickupReply.Status => Status(message, localDid, peerDid, outcome.Status, thid),
                PickupReply.LiveModeNotSupported => Reply(message, () => MessagePickup.CreateLiveModeNotSupported(localDid, peerDid, thid)),
                _ => null,
            };
        }

        // (f) status, delivery, or a name this version does not define: not for the mediator role.
        MediatorLog.Write(_logger, LogLevel.Debug, "Message-pickup message {MessageId} ignored: not a request the mediator role answers.", MediatorLog.LogId(message.Id));
        return null;
    }

    // The thread a reply is put on (DD-11). Echoed, so bounded: a longer one makes the request malformed.
    private static string ThreadOf(Message request)
    {
        var thid = MessagePickup.ResponseThreadId(request);
        if (thid.Length > PickupSizing.MaxThreadIdLength)
            throw new MalformedMessageException($"A message-pickup request's thread id exceeds the maximum length of {PickupSizing.MaxThreadIdLength} characters.");
        return thid;
    }

    private Message? Status(Message request, string localDid, string peerDid, PickupStatusView? view, string thid)
        => view is null ? null : Reply(request, () => MessagePickup.CreateStatus(localDid, peerDid, ToStatus(view), thid));

    // 3.0 units: whole UTC epoch seconds, and longest_waited_seconds. The time members only when
    // something is waiting.
    private static PickupStatus ToStatus(PickupStatusView view)
    {
        var queue = view.Queue;
        long? oldest = null, newest = null, waited = null;
        if (queue.MessageCount > 0 && queue.OldestReceivedAt is { } oldestAt && queue.NewestReceivedAt is { } newestAt)
        {
            oldest = Math.Max(0, oldestAt.ToUnixTimeSeconds());
            newest = Math.Max(0, newestAt.ToUnixTimeSeconds());
            waited = Math.Max(0, (long)Math.Floor((view.Now - oldestAt).TotalSeconds));
        }
        return new PickupStatus(queue.MessageCount, view.RecipientDid, waited, newest, oldest, queue.TotalBytes, view.LiveDelivery);
    }

    // Building a reply validates what goes on the wire. Values that fail came from a host store,
    // not from the peer, so any failure here is a server fault: logged by type, no reply, never
    // thrown through dispatch.
    private Message? Reply(Message request, Func<Message> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            MediatorLog.Write(_logger, LogLevel.Error, "Message-pickup message {MessageId} not answered: the reply could not be built ({ExceptionType}).", MediatorLog.LogId(request.Id), ex.GetType().Name);
            return null;
        }
    }
}
