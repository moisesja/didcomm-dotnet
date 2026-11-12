using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Pickup;

/// <summary>What the mediator decided to answer a pickup request with.</summary>
internal enum PickupReply
{
    /// <summary>Nothing is sent: a store fault, or a push the session did not take (logged).</summary>
    None,

    /// <summary>Answer with the queue state in <see cref="PickupOutcome.Status"/>.</summary>
    Status,

    /// <summary>A <c>delivery</c> was pushed through the session; the handler returns nothing more.</summary>
    Delivered,

    /// <summary>Live delivery was requested on a connection that cannot do it: answer the problem report.</summary>
    LiveModeNotSupported,
}

/// <summary>The queue state a <c>status</c> reports, before a protocol version puts it into its own units and member names.</summary>
/// <param name="Queue">What the store reports for the scope.</param>
/// <param name="Now">The instant the state was read at.</param>
/// <param name="LiveDelivery">Whether live delivery is on for the session carrying the request.</param>
/// <param name="RecipientDid">The filter of the request, to be echoed; <c>null</c> when it had none.</param>
internal sealed record PickupStatusView(QueueStatus Queue, DateTimeOffset Now, bool LiveDelivery, string? RecipientDid);

/// <summary>A <see cref="PickupReply"/> with the status it carries, if any.</summary>
internal readonly record struct PickupOutcome(PickupReply Reply, PickupStatusView? Status)
{
    public static PickupOutcome None => new(PickupReply.None, null);
    public static PickupOutcome Delivered => new(PickupReply.Delivered, null);
    public static PickupOutcome LiveModeNotSupported => new(PickupReply.LiveModeNotSupported, null);
    public static PickupOutcome Of(PickupStatusView? status) => status is null ? None : new(PickupReply.Status, status);
}

/// <summary>
/// The mediator role of Message Pickup without the wire format: who may see which queue, what a
/// status request, a delivery request, an acknowledgement and a live-mode change do to the store
/// and to the session, and how a newly arrived message reaches a recipient (live session, else the
/// queue). <see cref="MessagePickupHandler"/> (3.0) puts message names and bodies around it; a 4.0
/// handler can do the same over the same instance (FR-PICK-12), sharing leases, live flags and
/// counters.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here throws for a store, registry, session or id-generator fault: the fault is logged by
/// exception type name and turned into "no reply" (a request) or
/// <see cref="PickupDeliveryOutcome.Faulted"/> (a delivery). Whatever a store returns is copied and
/// checked inside the fault boundary before it is used. Leases taken by a request that then fails
/// are released. The one thing that leaves is cancellation of the caller's token, as a plain
/// <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// Log lines carry the inbound message id (shortened when long), exception type names, enum names
/// and counts — never a DID, a <c>recipient_did</c>, a message id of the queue or a thread id.
/// </para>
/// </remarks>
internal sealed class PickupCoordinator
{
    private readonly IMessageStore _store;
    private readonly IMediationStore _mediation;
    private readonly IReturnRouteSessionRegistry _sessions;
    private readonly PickupSessionTracker _tracker;
    private readonly PickupSizing _sizing;
    private readonly MessagePickupOptions _options;
    private readonly DidCommOptions _didCommOptions;
    private readonly IMessageIdGenerator _ids;
    private readonly ILogger? _logger;
    private long _unauthenticated;
    private long _tooLarge;
    private long _queueFull;
    private long _undeliverable;

    public PickupCoordinator(
        IMessageStore store,
        IMediationStore mediation,
        IReturnRouteSessionRegistry sessions,
        MessagePickupOptions options,
        DidCommOptions didCommOptions,
        IMessageIdGenerator? idGenerator,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mediation);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(didCommOptions);
        options.Validate();
        _store = store;
        _mediation = mediation;
        _sessions = sessions;
        _options = options;
        _didCommOptions = didCommOptions;
        _sizing = new PickupSizing(options, didCommOptions.MaxReceiveBytes);
        _tracker = new PickupSessionTracker(store, logger);
        _ids = idGenerator ?? UuidV4MessageIdGenerator.Instance;
        _logger = logger;
        Log(LogLevel.Information,
            "Message pickup: a delivery is at most {MaxDeliveryBytes} packed bytes and {MaxDeliveryBatch} messages; the largest message accepted into the queue is {MaxAdmissiblePayloadBytes} bytes.",
            _sizing.MaxDeliveryBytes, options.MaxDeliveryBatch, _sizing.MaxAdmissiblePayloadBytes);
    }

    /// <summary>The largest <c>message_id_list</c> a <c>messages-received</c> may carry.</summary>
    public int MaxAckIds => _options.EffectiveMaxAckIds;

    /// <summary>The sizing in effect.</summary>
    public PickupSizing Sizing => _sizing;

    // ---- who is asking ----------------------------------------------------------------------

    /// <summary>
    /// The peer of a request (FR-PICK-01): the authcrypt sender. <c>false</c> for plaintext,
    /// anoncrypt, signed-only and <c>anoncrypt(sign)</c>: the caller drops the message without
    /// looking at its type or body.
    /// </summary>
    public bool TryGetPeer(UnpackResult received, string messageId, out string peerDid, out string localDid)
    {
        if (MediatorPeer.TryGet(received, out peerDid, out localDid))
            return true;

        // Anyone can send these, so the line is rate-limited: the first and every 1000th at Warning.
        var level = MediatorLog.RateLimited(ref _unauthenticated, out var n);
        Log(level, "Message-pickup message {MessageId} dropped: no authcrypt sender (FR-PICK-01); not applied, not answered. {Count} such drops.", MediatorLog.LogId(messageId), n);
        return false;
    }

    /// <summary>
    /// The session a reply to this request may travel on: <paramref name="session"/> when it is
    /// bound to exactly this (peer, local) pair, else <c>null</c>. Both comparisons always run.
    /// </summary>
    public static IReturnRouteSession? SessionFor(IReturnRouteSession? session, string peerDid, string localDid)
    {
        if (session is null)
            return null;
        var same = string.Equals(session.PeerDid, peerDid, StringComparison.Ordinal)
                   & string.Equals(session.LocalDid, localDid, StringComparison.Ordinal);
        return same ? session : null;
    }

    // ---- requests ---------------------------------------------------------------------------

    /// <summary><c>status-request</c>: the state of the peer's queue, or of one of its DIDs. <c>null</c> means no reply.</summary>
    public async Task<PickupStatusView?> StatusAsync(string peerDid, string? recipientDid, IReturnRouteSession? session, string messageId, CancellationToken ct)
    {
        try
        {
            var now = _didCommOptions.Now();
            var owned = await OwnsAsync(peerDid, recipientDid, ct).ConfigureAwait(false);
            return await ViewAsync(peerDid, recipientDid, owned, session, now, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MediatorLog.ThrowIfCancelled(ct);
            Log(LogLevel.Error, "Message-pickup status request {MessageId} not answered: {ExceptionType} from a store.", MediatorLog.LogId(messageId), ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// <c>delivery-request</c>: lease up to <paramref name="limit"/> deliverable messages (clamped
    /// to the batch maximum and the byte budget), push them as one <c>delivery</c> built by
    /// <paramref name="buildDelivery"/> through the session's size-bounded send, and trim the batch
    /// while the packed message is over the budget. Nothing to deliver, a limit of zero, or no
    /// session to carry a batch: the status instead.
    /// </summary>
    public async Task<PickupOutcome> DeliverRequestedAsync(
        string peerDid,
        int limit,
        string? recipientDid,
        IReturnRouteSession? session,
        Func<IReadOnlyList<DeliveredMessage>, Message> buildDelivery,
        string messageId,
        CancellationToken ct)
    {
        try
        {
            var now = _didCommOptions.Now();
            var owned = await OwnsAsync(peerDid, recipientDid, ct).ConfigureAwait(false);

            // The count bound, before any record is touched: whatever the request asked for.
            var max = Math.Min(Math.Max(0, limit), _options.MaxDeliveryBatch);
            if (owned && max > 0 && session is not null)
            {
                var pushed = await LeaseAndPushAsync(peerDid, max, recipientDid, session, buildDelivery, now, messageId, ct).ConfigureAwait(false);
                if (pushed is { } result)
                    return result;
            }
            return PickupOutcome.Of(await ViewAsync(peerDid, recipientDid, owned, session, now, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            MediatorLog.ThrowIfCancelled(ct);
            Log(LogLevel.Error, "Message-pickup delivery request {MessageId} not answered: {ExceptionType} from a store or the session; any lease it took was released.", MediatorLog.LogId(messageId), ex.GetType().Name);
            return PickupOutcome.None;
        }
    }

    /// <summary>
    /// <c>messages-received</c>: remove the acknowledged messages of the peer. Ids the peer's grant
    /// does not hold are ignored. Returns the status to answer with (when
    /// <paramref name="answerWithStatus"/>), or <c>null</c> for no reply.
    /// </summary>
    public async Task<PickupStatusView?> AcknowledgeAsync(string peerDid, IReadOnlyList<string> messageIds, IReturnRouteSession? session, bool answerWithStatus, string messageId, CancellationToken ct)
    {
        try
        {
            // Confined to the caller's own key by the store: no id of another grant can match.
            if (messageIds.Count > 0)
                await _store.AcknowledgeAsync(peerDid, messageIds, ct).ConfigureAwait(false);
            if (!answerWithStatus)
                return null;

            var now = _didCommOptions.Now();
            var owned = await OwnsAsync(peerDid, null, ct).ConfigureAwait(false);
            return await ViewAsync(peerDid, null, owned, session, now, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MediatorLog.ThrowIfCancelled(ct);
            Log(LogLevel.Error, "Message-pickup acknowledgement {MessageId} not answered: {ExceptionType} from a store.", MediatorLog.LogId(messageId), ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// <c>live-delivery-change</c>: turn live delivery on (with the framing to use for this
    /// session's pushes) or off, and report the status. Turning it on needs a live session;
    /// without one the answer is the problem report and nothing changes.
    /// </summary>
    public async Task<PickupOutcome> SetLiveDeliveryAsync(
        string peerDid,
        bool liveDelivery,
        IReturnRouteSession? session,
        Func<string, string, DeliveredMessage, Message> buildLiveDelivery,
        string messageId,
        CancellationToken ct)
    {
        try
        {
            var state = _tracker.Track(session);
            if (liveDelivery && state is null)
                return PickupOutcome.LiveModeNotSupported;

            // The status is read first: if the store cannot be read there is no reply, and the
            // flag has not moved.
            var now = _didCommOptions.Now();
            var owned = await OwnsAsync(peerDid, null, ct).ConfigureAwait(false);
            var queue = await ReadQueueAsync(peerDid, null, owned, now, ct).ConfigureAwait(false);

            var live = state is not null && state.SetLive(liveDelivery ? buildLiveDelivery : null);
            return PickupOutcome.Of(new PickupStatusView(queue, now, live, null));
        }
        catch (Exception ex)
        {
            MediatorLog.ThrowIfCancelled(ct);
            Log(LogLevel.Error, "Message-pickup live-delivery change {MessageId} not answered: {ExceptionType} from a store or the session; live delivery unchanged.", MediatorLog.LogId(messageId), ex.GetType().Name);
            return PickupOutcome.None;
        }
    }

    // ---- the pipeline's side ----------------------------------------------------------------

    /// <summary>See <see cref="MessagePickupQueue.DeliverAsync"/>.</summary>
    public async ValueTask<PickupDeliveryResult> DeliverAsync(string recipientDid, ReadOnlyMemory<byte> payload, PickupDeliveryOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The size bound, before any lookup: a payload that could never be delivered alone within
        // max_delivery_bytes (or that is over MaxReceiveBytes) is not retained (FR-PICK-03/10).
        if (payload.Length > _sizing.MaxAdmissiblePayloadBytes)
        {
            var level = MediatorLog.RateLimited(ref _tooLarge, out var n);
            Log(level, "Message pickup: a message of {Bytes} bytes was not queued: the largest admissible message is {MaxAdmissiblePayloadBytes} bytes (FR-PICK-03). {Count} such refusals.", payload.Length, _sizing.MaxAdmissiblePayloadBytes, n);
            return new PickupDeliveryResult(PickupDeliveryOutcome.PayloadTooLarge, null, null);
        }

        string? peerDid = null;
        string? id = null;
        var stored = false;
        var leased = false;
        try
        {
            var now = _didCommOptions.Now();
            var expiresAt = ExpiryInstant(options.ExpiresTime);
            if (expiresAt is { } at && now >= at)
                return new PickupDeliveryResult(PickupDeliveryOutcome.Expired, null, null);

            // A DID URL is not normalised into its DID, and an over-long value is never looked up.
            if (!BareDid.IsBareDid(recipientDid))
                return new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null);
            var grant = await _mediation.FindGrantByRecipientDidAsync(recipientDid, ct).ConfigureAwait(false);
            if (grant is null || !BareDid.IsBareDid(grant.PeerDid))
                return new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null);
            peerDid = grant.PeerDid;

            // A message that is not yet due is never pushed: it is queued and picked up once due.
            var due = options.NotBefore is not { } notBefore || notBefore <= now;
            var target = options.AllowLive && due ? FindLiveTarget(peerDid) : null;
            var lease = target is null ? null : new MessageLease(target.Value.State.OwnerToken, now + _options.LeaseTimeout);

            var outcome = EnqueueOutcome.DuplicateId;
            for (var attempt = 0; attempt < 2 && outcome == EnqueueOutcome.DuplicateId; attempt++)
            {
                id = NewMessageId();
                var record = new QueuedMessage(id, peerDid, recipientDid, payload, now, options.NotBefore, expiresAt);
                outcome = await _store.EnqueueAsync(record, lease, now, ct).ConfigureAwait(false);
            }

            switch (outcome)
            {
                case EnqueueOutcome.Enqueued:
                    stored = true;
                    leased = lease is not null;
                    break;
                case EnqueueOutcome.GrantMessageLimitReached or EnqueueOutcome.GrantByteLimitReached or EnqueueOutcome.StoreLimitReached:
                    var level = MediatorLog.RateLimited(ref _queueFull, out var n);
                    Log(level, "Message pickup: a message was not queued: {Outcome} (FR-PICK-10). {Count} such refusals.", outcome.ToString(), n);
                    return new PickupDeliveryResult(PickupDeliveryOutcome.QueueFull, null, peerDid);
                default:
                    Log(LogLevel.Error, "Message pickup: a message was not queued: the store answered {Outcome}.", outcome.ToString());
                    return new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, peerDid);
            }

            if (target is null)
                return new PickupDeliveryResult(PickupDeliveryOutcome.Queued, id, peerDid);

            // Admitted and leased to the live session in one step; now the push (FR-PICK-08/09).
            var (session, state) = target.Value;
            var sent = ReturnRouteSendResult.NotSent;
            if (state.LiveDelivery is { } build)
            {
                var delivery = build(session.LocalDid, peerDid, new DeliveredMessage(id!, payload));
                sent = await session.TrySendAsync(delivery, _sizing.MaxDeliveryBytes, ct).ConfigureAwait(false);
            }
            if (sent == ReturnRouteSendResult.Sent && !state.IsClosed)
                return new PickupDeliveryResult(PickupDeliveryOutcome.PushedLive, id, peerDid);

            // Not pushed (no free push slot, a downgraded or closing session, an envelope over the
            // budget): the record stays where it is and loses its lease, so a delivery-request gets it.
            await ReleaseQuietlyAsync(peerDid, new[] { id! }).ConfigureAwait(false);
            return new PickupDeliveryResult(PickupDeliveryOutcome.Queued, id, peerDid);
        }
        catch (Exception ex)
        {
            if (stored && leased)
                await ReleaseQuietlyAsync(peerDid!, new[] { id! }).ConfigureAwait(false);
            MediatorLog.ThrowIfCancelled(ct);
            Log(LogLevel.Error, "Message pickup: delivering a message failed with {ExceptionType}; retained: {Retained}.", ex.GetType().Name, stored);
            return stored
                ? new PickupDeliveryResult(PickupDeliveryOutcome.Queued, id, peerDid)
                : new PickupDeliveryResult(PickupDeliveryOutcome.Faulted, null, peerDid);
        }
    }

    /// <summary>See <see cref="MessagePickupQueue.RemoveAsync"/>.</summary>
    public async ValueTask<bool> RemoveAsync(string grantPeerDid, string messageId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await _store.AcknowledgeAsync(grantPeerDid, new[] { messageId }, ct).ConfigureAwait(false) > 0;
        }
        catch (Exception ex)
        {
            MediatorLog.ThrowIfCancelled(ct);
            Log(LogLevel.Error, "Message pickup: removing a queued message failed with {ExceptionType}.", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>See <see cref="MessagePickupQueue.PurgeExpiredAsync"/>.</summary>
    public ValueTask<int> PurgeExpiredAsync(CancellationToken ct) => _store.PurgeExpiredAsync(_didCommOptions.Now(), ct);

    /// <summary>Write a log line; a throwing logging provider changes nothing.</summary>
    public void Log(LogLevel level, string message, params object?[] args) => MediatorLog.Write(_logger, level, message, args);

    // ---- pieces -----------------------------------------------------------------------------

    // Whether the peer may see the scope it names (FR-PICK-02/03): its own grant, or a recipient
    // DID registered under its own grant. "Not registered" and "registered under another grant"
    // take the same store call and give the same answer; the comparison has no branch that depends
    // on which it was. A value that is not a bare DID is never looked up (and never normalised).
    private async ValueTask<bool> OwnsAsync(string peerDid, string? recipientDid, CancellationToken ct)
    {
        if (recipientDid is null)
            return await _mediation.GetGrantAsync(peerDid, ct).ConfigureAwait(false) is not null;
        if (!BareDid.IsBareDid(recipientDid))
            return false;
        var grant = await _mediation.FindGrantByRecipientDidAsync(recipientDid, ct).ConfigureAwait(false);
        return string.Equals(grant?.PeerDid, peerDid, StringComparison.Ordinal);
    }

    private async ValueTask<PickupStatusView> ViewAsync(string peerDid, string? recipientDid, bool owned, IReturnRouteSession? session, DateTimeOffset now, CancellationToken ct)
    {
        var queue = await ReadQueueAsync(peerDid, recipientDid, owned, now, ct).ConfigureAwait(false);
        return new PickupStatusView(queue, now, IsLive(session), recipientDid);
    }

    // The message store is read only for a scope the peer owns; anything else is an empty queue.
    private async ValueTask<QueueStatus> ReadQueueAsync(string peerDid, string? recipientDid, bool owned, DateTimeOffset now, CancellationToken ct)
    {
        if (!owned)
            return QueueStatus.Empty;
        var queue = await _store.GetStatusAsync(peerDid, recipientDid, now, ct).ConfigureAwait(false);
        if (queue is null || queue.MessageCount < 0 || queue.TotalBytes < 0)
            throw new InvalidOperationException("The message store returned a status outside its contract.");
        return queue;
    }

    private bool IsLive(IReturnRouteSession? session)
        => session is not null && _tracker.Find(session)?.LiveDelivery is not null;

    // Lease, check, push, trim. Returns the outcome when the request is settled here, or null when
    // nothing could be delivered and the caller answers with the status. Whatever was leased and
    // not pushed is released on every path out, including a fault.
    private async Task<PickupOutcome?> LeaseAndPushAsync(
        string peerDid,
        int max,
        string? recipientDid,
        IReturnRouteSession session,
        Func<IReadOnlyList<DeliveredMessage>, Message> buildDelivery,
        DateTimeOffset now,
        string messageId,
        CancellationToken ct)
    {
        // Over a live session the lease also ends when the session closes; over HTTP by time only.
        var state = _tracker.Track(session);
        var lease = new MessageLease(state?.OwnerToken, now + _options.LeaseTimeout);
        var request = new MessageLeaseRequest(peerDid, recipientDid, max, _sizing.BatchPayloadBudget, now, lease);

        var leased = new List<QueuedMessage>();
        var held = new List<string>();
        try
        {
            var taken = await _store.LeaseAsync(request, ct).ConfigureAwait(false);
            // Copied before anything is checked: what the store handed out is held from here on.
            if (taken is not null)
            {
                foreach (var record in taken)
                {
                    if (record?.Id is { } heldId)
                        held.Add(heldId);
                    leased.Add(record!);
                }
            }
            RequireLeasedWithinContract(leased, peerDid, recipientDid, max);

            if (recipientDid is null)
                await DropUnregisteredAsync(peerDid, leased, held, ct).ConfigureAwait(false);

            while (leased.Count > 0)
            {
                var batch = new DeliveredMessage[leased.Count];
                for (var i = 0; i < batch.Length; i++)
                    batch[i] = new DeliveredMessage(leased[i].Id, leased[i].Payload);

                var sent = await session.TrySendAsync(buildDelivery(batch), _sizing.MaxDeliveryBytes, ct).ConfigureAwait(false);
                if (sent == ReturnRouteSendResult.Sent)
                {
                    held.Clear();
                    // A session that closed while the batch was in hand may have run its release
                    // before these leases existed; release again so nothing waits for the timeout.
                    if (state is { IsClosed: true })
                        _tracker.ReleaseLeasesInBackground(state.OwnerToken);
                    return PickupOutcome.Delivered;
                }
                if (sent != ReturnRouteSendResult.TooLarge)
                {
                    Log(LogLevel.Information, "Message-pickup delivery request {MessageId}: the session did not take the delivery; {Count} lease(s) released, no reply.", MediatorLog.LogId(messageId), leased.Count);
                    return PickupOutcome.None;
                }

                // Over the byte budget once packed (the estimate was short for this peer's
                // envelope): give the last message back and try again with one fewer.
                var last = leased[^1];
                leased.RemoveAt(leased.Count - 1);
                await _store.ReleaseAsync(peerDid, new[] { last.Id }, ct).ConfigureAwait(false);
                held.Remove(last.Id);
                if (leased.Count == 0)
                {
                    var level = MediatorLog.RateLimited(ref _undeliverable, out var n);
                    Log(level, "Message-pickup delivery request {MessageId}: a single queued message does not fit this peer's envelope within max_delivery_bytes; it stays queued. {Count} such requests.", MediatorLog.LogId(messageId), n);
                }
            }
            return null;
        }
        finally
        {
            if (held.Count > 0)
                await ReleaseQuietlyAsync(peerDid, held).ConfigureAwait(false);
        }
    }

    // What a store hands out is not trusted further than its contract: every record must be the
    // caller's own (the grant key), within the count asked for, for the DID asked for, and carry an
    // id that can go on the wire. One wrong record and nothing of the batch is delivered.
    private static void RequireLeasedWithinContract(List<QueuedMessage> leased, string peerDid, string? recipientDid, int max)
    {
        var ok = leased.Count <= max;
        foreach (var record in leased)
        {
            ok &= record is not null
                  && string.Equals(record.GrantPeerDid, peerDid, StringComparison.Ordinal)
                  && (recipientDid is null || string.Equals(record.RecipientDid, recipientDid, StringComparison.Ordinal))
                  && record.RecipientDid is not null
                  && IsUsableId(record.Id, PickupBodies.MaxIdLength);
        }
        if (!ok)
            throw new InvalidOperationException("The message store returned records outside the lease it was asked for.");
    }

    // Registration is checked at delivery time (owner decision, FR-PICK-03): in a batch that was not
    // filtered by DID, every distinct recipient DID must still be registered under the peer's
    // grant. Records of a DID that is not are purged (they can never be delivered, and would
    // otherwise sit at the head of the queue for ever) and leave the batch. Every DID is checked,
    // whatever the earlier ones answered. A fault here leaves through the caller's finally, which
    // releases the whole batch: the check is the input of a refusal and fails with it (L-054).
    private async ValueTask DropUnregisteredAsync(string peerDid, List<QueuedMessage> leased, List<string> held, CancellationToken ct)
    {
        HashSet<string>? gone = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in leased)
        {
            if (!seen.Add(record.RecipientDid))
                continue;
            var grant = await _mediation.FindGrantByRecipientDidAsync(record.RecipientDid, ct).ConfigureAwait(false);
            if (!string.Equals(grant?.PeerDid, peerDid, StringComparison.Ordinal))
                (gone ??= new HashSet<string>(StringComparer.Ordinal)).Add(record.RecipientDid);
        }
        if (gone is null)
            return;

        var purged = 0;
        foreach (var did in gone)
            purged += await _store.PurgeRecipientAsync(peerDid, did, ct).ConfigureAwait(false);
        for (var i = leased.Count - 1; i >= 0; i--)
        {
            if (gone.Contains(leased[i].RecipientDid))
            {
                held.Remove(leased[i].Id);
                leased.RemoveAt(i);
            }
        }
        Log(LogLevel.Information, "Message pickup: {Count} queued message(s) removed: their recipient DID is no longer registered to the requesting peer.", purged);
    }

    // The live session a new message for this peer is pushed to: the newest one that turned live
    // delivery on and still allows pushes. One target, because a lease has one owner. A registry
    // that cannot be read means "no live session": the message is queued.
    private (IReturnRouteSession Session, PickupSessionState State)? FindLiveTarget(string peerDid)
    {
        try
        {
            var sessions = _sessions.Find(peerDid);
            if (sessions is null)
                return null;
            for (var i = sessions.Count - 1; i >= 0; i--)
            {
                var session = sessions[i];
                if (session is null)
                    continue;
                // The registry is keyed by peer DID; the session's own identity is what the push is
                // encrypted to, so it is what must match.
                if (!string.Equals(session.PeerDid, peerDid, StringComparison.Ordinal) || !session.IsLive || session.Mode != ReturnRouteMode.All)
                    continue;
                var state = _tracker.Find(session);
                if (state?.LiveDelivery is not null)
                    return (session, state);
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, "Message pickup: the session registry could not be read ({ExceptionType}); the message is queued.", ex.GetType().Name);
        }
        return null;
    }

    private string NewMessageId()
    {
        var id = _ids.NewId();
        if (!IsUsableId(id, PickupSizing.MaxMintedIdLength))
            throw new InvalidOperationException("The message id generator returned an id that cannot be used as an attachment id.");
        return id;
    }

    private static bool IsUsableId(string? id, int maxLength)
        => !string.IsNullOrEmpty(id) && id.Length <= maxLength && UnreservedUriChars.IsUnreserved(id);

    // FR-ROUTE-09 as an instant: expires_time counts whole seconds and is expired once
    // now - skew > expires, i.e. from the start of second (expires + skew + 1).
    private DateTimeOffset? ExpiryInstant(long? expiresTime)
    {
        if (expiresTime is not long expires)
            return null;
        // The same arithmetic as the forward path (ForwardProcessingResult.IsExpiredAt), in a form a
        // store can compare against; values outside what an instant can hold are pinned to its ends.
        var first = (decimal)expires + (long)Math.Floor(_didCommOptions.ExpiresClockSkew.TotalSeconds) + 1;
        if (first <= DateTimeOffset.MinValue.ToUnixTimeSeconds())
            return DateTimeOffset.MinValue;
        if (first >= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return DateTimeOffset.MaxValue;
        return DateTimeOffset.FromUnixTimeSeconds((long)first);
    }

    // On a path that is already failing or cleaning up: never throws, never cancelled. A lease that
    // could not be released ends by its timeout.
    private async ValueTask ReleaseQuietlyAsync(string peerDid, IReadOnlyList<string> ids)
    {
        try
        {
            await _store.ReleaseAsync(peerDid, ids, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, "Message pickup: {Count} lease(s) could not be released ({ExceptionType}); they end by timeout.", ids.Count, ex.GetType().Name);
        }
    }
}
