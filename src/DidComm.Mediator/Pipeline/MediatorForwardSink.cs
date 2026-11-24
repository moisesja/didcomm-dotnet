using DidComm.Consistency;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The mediator's <see cref="IForwardSink"/> (FR-ROUTE-11, FR-PICK-08/09): every forward the
/// routing mediator relays is stored in the recipient's pickup queue, and when the stored message is
/// due and the recipient has a live connection with live delivery on — or, with
/// <see cref="MediatorOptions.DirectPush"/> on, whether or not it has one — its grant is woken in
/// <see cref="PushDispatcher"/>, whose workers push what the store then holds: over that connection,
/// else to the endpoint the host registered for the recipient. Otherwise it waits for a
/// <c>delivery-request</c>.
/// </summary>
/// <remarks>
/// <para>
/// It runs on <see cref="ForwardHandler"/>'s background pump, after the sender already holds its
/// 202, so nothing here can change the transport answer or travel back toward the sender: every
/// outcome other than <c>Queued</c> is a logged drop, counted once on
/// <c>didcomm.forward.outcomes</c>. The queue refuses a message that is not in encrypted form
/// (<c>Dropped-PayloadNotEncrypted</c>: in pass-through mode an attachment its sender did not
/// encrypt; in rewrap mode the payload is the mediator's own anoncrypted <c>forward</c>, so it
/// passes), looks the recipient up again when the message arrives (a recipient removed or a grant
/// revoked since the forward was admitted is a drop), stores
/// it under the grant's quota with the forward's own schedule (<c>delay_milli</c> as a not-before,
/// <c>expires_time</c> judged with <c>DidCommOptions.ExpiresClockSkew</c>), and never pushes a
/// message before it is due.
/// </para>
/// <para>
/// <strong>The pump only stores.</strong> It asks the queue to store the message without a live push
/// (<see cref="PickupDeliveryOptions.AllowLive"/> <c>false</c>) and never awaits a session: a live
/// push packs a <c>delivery</c> for the peer, which resolves the peer's DID, and the peer controls
/// how long that takes. The push workers do it instead, per grant and bounded, so one recipient
/// that is slow to take a push never holds up the storing of other recipients' messages. Pushing is
/// a separate, best-effort step after the message is stored: the grant is woken only for a message
/// that is queued and already due, and only when the recipient has a live session with live
/// delivery on right now, or when direct push is on (the push then goes to the recipient's own
/// endpoint when it finds no live session); the wake-up is O(1) and never blocks, it carries
/// nothing of the message but its grant's DID (the push takes what the store holds), and a message
/// whose push fails stays queued for pickup. It is tried live again at the grant's next wake-up only
/// when the session answered that it did not take it, or the push was never attempted; a push whose
/// send was attempted and then faulted, timed out or was refused (live or direct) is delivered
/// through pickup only (owner decisions 2026-11-21, OD-5, and OD-2 on #110). A message held by
/// <c>delay_milli</c> wakes nothing when it is stored and is never pushed early; once due it goes
/// with the grant's next push, or by pickup (no due-time scheduler, D2).
/// </para>
/// <para>
/// <strong>Dependencies (L-017).</strong> The queue, the push dispatcher and the counter; nothing
/// that leads back to the protocol dispatcher, the handlers or the facade.
/// </para>
/// <para>
/// <strong>Logging.</strong> The forward's id (shortened when long), outcome names and exception type
/// names; never a DID, the queue's record id or an exception message. Drops are rate-limited per
/// reason (the first and every 1000th at Warning, Error for faults; the rest at Debug); deliveries
/// are logged at Debug.
/// </para>
/// </remarks>
internal sealed class MediatorForwardSink : IForwardSink
{
    private readonly MessagePickupQueue _queue;
    private readonly PushDispatcher _pushes;
    private readonly ForwardMetrics _metrics;
    private readonly ILogger? _logger;
    private long _unknown;
    private long _expired;
    private long _tooLarge;
    private long _quota;
    private long _payloadNotEncrypted;
    private long _faulted;

    /// <summary>Create the sink.</summary>
    /// <param name="queue">The pickup queue every forward goes into.</param>
    /// <param name="pushes">The push workers, for live pushes and, with direct push on, direct ones.</param>
    /// <param name="metrics">The forward outcome counter shared with the forward handler.</param>
    /// <param name="logger">Optional logger.</param>
    public MediatorForwardSink(
        MessagePickupQueue queue,
        PushDispatcher pushes,
        ForwardMetrics metrics,
        ILogger<MediatorForwardSink>? logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pushes);
        ArgumentNullException.ThrowIfNull(metrics);
        _queue = queue;
        _pushes = pushes;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var forwardId = MediatorLog.LogId(context.Received.Message.Id);
        try
        {
            // The queue is keyed by the bare DID: a `next` that names a routing key URL reaches the
            // DID it belongs to (PRD §4.3). The handler has already screened `next`.
            var subject = DidSubject.DidSubjectOf(result.NextHop);
            if (subject is null)
            {
                Drop(ForwardOutcomes.DroppedUnknown, ref _unknown, forwardId, "next is not a DID or DID URL");
                return;
            }

            // Store only: the pump never awaits a live session (PR #109 review R1-2). A live push is
            // the push workers' job, below.
            var delivery = await _queue.DeliverAsync(
                subject,
                result.OnwardPacked,
                new PickupDeliveryOptions(result.NotBefore, result.ExpiresTime, AllowLive: false),
                ct).ConfigureAwait(false);

            switch (delivery.Outcome)
            {
                case PickupDeliveryOutcome.Queued:
                    _metrics.Record(ForwardOutcomes.Queued);
                    MediatorLog.Write(_logger, LogLevel.Debug, "Forward {ForwardId} queued.", forwardId);
                    HandToPush(result, context, subject, delivery, forwardId);
                    return;
                case PickupDeliveryOutcome.NotRegistered:
                    Drop(ForwardOutcomes.DroppedUnknown, ref _unknown, forwardId, "the recipient is not registered (any more)");
                    return;
                case PickupDeliveryOutcome.Expired:
                    Drop(ForwardOutcomes.DroppedExpired, ref _expired, forwardId, "it expired before it could be stored (FR-ROUTE-09)");
                    return;
                case PickupDeliveryOutcome.PayloadTooLarge:
                    Drop(ForwardOutcomes.DroppedTooLarge, ref _tooLarge, forwardId, "it is larger than the pickup queue accepts (FR-PICK-10)");
                    return;
                case PickupDeliveryOutcome.QueueFull:
                    Drop(ForwardOutcomes.DroppedQuota, ref _quota, forwardId, "the recipient's quota, or the store's, is used up (FR-PICK-10)");
                    return;
                case PickupDeliveryOutcome.NotEncrypted:
                    // Pass-through mode relays the attachment as the sender wrapped it, and a pickup
                    // delivery carries only messages in encrypted form (Message Pickup 3.0 L172,
                    // 4.0 L261). In rewrap mode this cannot happen by design: the payload is the
                    // mediator's own anoncrypted forward (OnwardPacked, a JWE) whatever it wraps.
                    Drop(ForwardOutcomes.DroppedPayloadNotEncrypted, ref _payloadNotEncrypted, forwardId, "the message it carries for the recipient is not in encrypted form (Message Pickup)");
                    return;
                default:
                    // Faulted; PushedLive cannot happen, as no live push was allowed.
                    Fault(forwardId, "the queue reported a fault");
                    return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The handler is shutting down; it discards the forward (fail closed).
            throw;
        }
        catch (Exception ex)
        {
            // Type name only: a fault's text can name the recipient's DID.
            Fault(forwardId, ex.GetType().Name);
        }
    }

    // Best-effort and after the message is stored: a not-yet-due message wakes nothing and is never
    // pushed early; once due it goes with the grant's next push or is picked up. With direct push
    // off, a message for a recipient with no live session now wakes nothing; with it on, every due
    // message wakes its grant, and the push decides live or direct. A failure here changes nothing
    // about the forward, which is already counted as Queued. Synchronous: it never awaits a session.
    private void HandToPush(ForwardProcessingResult result, ProtocolContext context, string subject, PickupDeliveryResult delivery, string forwardId)
    {
        if (delivery.MessageId is null || delivery.GrantPeerDid is not { } grantPeerDid)
            return;
        try
        {
            if (result.NotBefore is { } notBefore && notBefore > context.Options.Now())
            {
                MediatorLog.Write(_logger, LogLevel.Debug, "Forward {ForwardId} is not due yet: not pushed; it waits in the queue.", forwardId);
                return;
            }
            if (!_pushes.PushesWithoutLiveSession && !_queue.Coordinator.HasLiveTarget(grantPeerDid))
                return;
            _pushes.Wake(grantPeerDid);
        }
        catch (Exception ex)
        {
            MediatorLog.Write(_logger, LogLevel.Warning, "Forward {ForwardId} queued; waking its push failed with {ExceptionType}. The message waits for pickup.", forwardId, ex.GetType().Name);
        }
    }

    private void Drop(string outcome, ref long counter, string forwardId, string reason)
    {
        _metrics.Record(outcome);
        var level = MediatorLog.RateLimited(ref counter, out var n);
        MediatorLog.Write(_logger, level, "Forward {ForwardId} dropped: {Reason}; nothing is sent back to the sender. {Count} such drops.", forwardId, reason, n);
    }

    private void Fault(string forwardId, string cause)
    {
        _metrics.Record(ForwardOutcomes.Faulted);
        var level = MediatorLog.RateLimited(ref _faulted, out var n);
        MediatorLog.Write(_logger, level == LogLevel.Warning ? LogLevel.Error : level, "Forward {ForwardId} not delivered: {Cause}. {Count} such faults.", forwardId, cause, n);
    }
}
