using DidComm.Consistency;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The mediator's <see cref="IForwardSink"/> (FR-ROUTE-11, FR-PICK-08/09): every forward the
/// routing mediator relays is stored in the recipient's pickup queue, and a stored message that is
/// due is then handed to <see cref="PushDispatcher"/>, whose workers push it over the recipient's
/// live connection (when it has one with live delivery on) or, when direct push is on, to the
/// recipient's own endpoint. Otherwise it waits for a <c>delivery-request</c>.
/// </summary>
/// <remarks>
/// <para>
/// It runs on <see cref="ForwardHandler"/>'s background pump, after the sender already holds its
/// 202, so nothing here can change the transport answer or travel back toward the sender: every
/// outcome other than <c>Queued</c> is a logged drop, counted once on
/// <c>didcomm.forward.outcomes</c>. The queue looks the recipient up again when the message
/// arrives (a recipient removed or a grant revoked since the forward was admitted is a drop), stores
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
/// a separate, best-effort step after the message is stored: only a message that is queued and
/// already due is handed over, and only when something could push it (direct push is on, or the
/// recipient has a live session with live delivery on right now); the hand-over never blocks, and
/// whatever happens to the push the message stays queued until a live session or a direct push
/// delivers it. Each handed-over message ends with one outcome on
/// <c>didcomm.mediator.push.outcomes</c>.
/// </para>
/// <para>
/// <strong>Dependencies (L-017).</strong> The queue, the push dispatcher, the counter and the
/// settings; nothing that leads back to the protocol dispatcher, the handlers or the facade. The
/// settings are copied at construction.
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
    private readonly bool _directPush;
    private long _unknown;
    private long _expired;
    private long _tooLarge;
    private long _quota;
    private long _faulted;

    /// <summary>Create the sink.</summary>
    /// <param name="queue">The pickup queue every forward goes into.</param>
    /// <param name="pushes">The push workers: live pushes, and direct pushes when direct push is on.</param>
    /// <param name="metrics">The forward outcome counter shared with the forward handler.</param>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="logger">Optional logger.</param>
    public MediatorForwardSink(
        MessagePickupQueue queue,
        PushDispatcher pushes,
        ForwardMetrics metrics,
        IOptions<MediatorOptions> options,
        ILogger<MediatorForwardSink>? logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pushes);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        _queue = queue;
        _pushes = pushes;
        _metrics = metrics;
        _logger = logger;
        _directPush = options.Value.DirectPush;
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

    // Best-effort and after the message is stored: a not-yet-due message waits in the queue and is
    // picked up once due (never pushed early, live or direct); a message nothing could push (direct
    // push off and no live session now) gets no job; a failure here changes nothing about the
    // forward, which is already counted as Queued. Synchronous: it never awaits a session.
    private void HandToPush(ForwardProcessingResult result, ProtocolContext context, string subject, PickupDeliveryResult delivery, string forwardId)
    {
        if (delivery.MessageId is not { } messageId || delivery.GrantPeerDid is not { } grantPeerDid)
            return;
        try
        {
            if (result.NotBefore is { } notBefore && notBefore > context.Options.Now())
            {
                MediatorLog.Write(_logger, LogLevel.Debug, "Forward {ForwardId} is not due yet: not pushed; it waits in the queue.", forwardId);
                return;
            }
            if (!_directPush && !_queue.Coordinator.HasLiveTarget(grantPeerDid))
                return;
            _pushes.TryEnqueue(new PushJob(grantPeerDid, subject, messageId, forwardId));
        }
        catch (Exception ex)
        {
            MediatorLog.Write(_logger, LogLevel.Warning, "Forward {ForwardId} queued; the push hand-over failed with {ExceptionType}. The message waits for pickup.", forwardId, ex.GetType().Name);
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
