using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pickup;

/// <summary>
/// The mediator's pickup queue as the delivery pipeline and the host see it (FR-PICK-05, -08, -09,
/// -10): hand it a message for a registered recipient and it is pushed to that recipient's live
/// connection or kept for pickup. Registered as a singleton by <c>AddMessagePickup()</c>;
/// <see cref="MessagePickupHandler"/> serves the recipient's side of the same queue.
/// </summary>
/// <remarks>
/// <para>
/// Records are kept in the <see cref="IMessageStore"/>; this class adds the rules around it: who a
/// message belongs to (the grant its recipient DID is registered under, looked up when the message
/// arrives), what may be retained (the size and quota bounds) and where it goes first (a live
/// session, else the queue). To drop what is held for a DID or a grant, call the store's
/// <see cref="IMessageStore.PurgeRecipientAsync"/> / <see cref="IMessageStore.PurgeGrantAsync"/>.
/// </para>
/// <para>
/// Thread-safe. It takes no dependency on the protocol dispatcher, a handler or
/// <c>DidCommClient</c>, so a forward sink may take it in its constructor.
/// </para>
/// </remarks>
public sealed class MessagePickupQueue
{
    /// <summary>Create the queue. Called by the container; hosts register it with <c>AddMessagePickup()</c>.</summary>
    /// <param name="store">Where queued messages are kept.</param>
    /// <param name="mediationStore">The grant and recipient registry: decides whose queue a message goes to and who may read it.</param>
    /// <param name="sessions">The live return-route sessions, for live delivery.</param>
    /// <param name="options">Batch, lease and quota settings.</param>
    /// <param name="didCommOptions">Supplies <c>MaxReceiveBytes</c>, the clock and the expiry skew.</param>
    /// <param name="idGenerator">Generates the ids of queued messages; the default is a UUID v4. Ids must be unreserved URI characters, at most 64, and should not be guessable from one another.</param>
    /// <param name="logger">Optional logger. Lines carry message ids of requests, type names and counts only.</param>
    /// <exception cref="ArgumentException"><paramref name="options"/> fail <see cref="MessagePickupOptions.Validate"/>, or the delivery budget in effect cannot carry any message.</exception>
    public MessagePickupQueue(
        IMessageStore store,
        IMediationStore mediationStore,
        IReturnRouteSessionRegistry sessions,
        IOptions<MessagePickupOptions> options,
        IOptions<DidCommOptions> didCommOptions,
        IMessageIdGenerator? idGenerator = null,
        ILogger<MessagePickupQueue>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(didCommOptions);
        Coordinator = new PickupCoordinator(store, mediationStore, sessions, options.Value, didCommOptions.Value, idGenerator, logger);
    }

    /// <summary>The role's logic, shared with the handlers so that live flags, leases and counters are one set.</summary>
    internal PickupCoordinator Coordinator { get; }

    /// <summary>
    /// The largest message the queue accepts, in bytes: the largest payload whose single-message
    /// <c>delivery</c> still fits <see cref="MessagePickupOptions.MaxDeliveryBytes"/> once packed,
    /// and never more than <c>DidCommOptions.MaxReceiveBytes</c> (FR-PICK-03/10). A little over
    /// half of the delivery budget; log it at startup so operators know what their mediator relays.
    /// </summary>
    public int MaxAdmissiblePayloadBytes => Coordinator.Sizing.MaxAdmissiblePayloadBytes;

    /// <summary>
    /// Accept a message for <paramref name="recipientDid"/>. The grant the DID is registered under
    /// is looked up now; the message is stored under that grant (counting against its quota) and,
    /// when the recipient has a live connection with live delivery turned on, pushed over it as a
    /// <c>delivery</c> and held until acknowledged. Otherwise it waits for a <c>delivery-request</c>.
    /// </summary>
    /// <remarks>
    /// A message addressed to several recipients is delivered by one call per recipient: each call
    /// stores its own record with its own id, acknowledged separately (FR-PICK-05). The payload
    /// memory is kept as given, not copied: do not reuse the buffer.
    /// </remarks>
    /// <param name="recipientDid">The registered DID the message is for: a bare DID (the DID subject of a forward's <c>next</c>). A DID URL is not normalised; it is simply not registered.</param>
    /// <param name="payload">The encrypted message, exactly as it is to reach the recipient. Not empty.</param>
    /// <param name="options">When the message is due and when it expires; <c>null</c> for "now" and "never".</param>
    /// <param name="ct">Cancellation token. Cancelling after the message was stored leaves it stored.</param>
    /// <returns>
    /// What happened. The message is retained exactly when the outcome is
    /// <see cref="PickupDeliveryOutcome.PushedLive"/> or <see cref="PickupDeliveryOutcome.Queued"/>;
    /// then <see cref="PickupDeliveryResult.MessageId"/> and <see cref="PickupDeliveryResult.GrantPeerDid"/>
    /// identify it (for <see cref="RemoveAsync"/>). None of this is for the message's sender.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled. Nothing else is thrown: a store, registry or session fault is <see cref="PickupDeliveryOutcome.Faulted"/> (or <see cref="PickupDeliveryOutcome.Queued"/> when the message had already been stored).</exception>
    public ValueTask<PickupDeliveryResult> DeliverAsync(string recipientDid, ReadOnlyMemory<byte> payload, PickupDeliveryOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientDid);
        if (payload.IsEmpty)
            throw new ArgumentException("A message to deliver must not be empty.", nameof(payload));
        return Coordinator.DeliverAsync(recipientDid, payload, options ?? PickupDeliveryOptions.Default, ct);
    }

    /// <summary>
    /// Remove a retained message without waiting for the recipient's acknowledgement: for a
    /// pipeline that delivered it another way (a direct push that succeeded, FR-PICK-09).
    /// </summary>
    /// <param name="grantPeerDid">The grant the message is held under (<see cref="PickupDeliveryResult.GrantPeerDid"/>).</param>
    /// <param name="messageId">The message's id (<see cref="PickupDeliveryResult.MessageId"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the message was held and is now removed; <c>false</c> when it was not held, or the store failed (logged).</returns>
    public ValueTask<bool> RemoveAsync(string grantPeerDid, string messageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grantPeerDid);
        ArgumentNullException.ThrowIfNull(messageId);
        return Coordinator.RemoveAsync(grantPeerDid, messageId, ct);
    }

    /// <summary>
    /// Remove every retained message whose expiry has passed (FR-ROUTE-09), judged by the configured
    /// clock. Expired messages are never delivered whether or not this runs; calling it now and
    /// then gives their memory back early. A store fault propagates to the caller.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many messages were removed.</returns>
    public ValueTask<int> PurgeExpiredAsync(CancellationToken ct = default) => Coordinator.PurgeExpiredAsync(ct);
}

/// <summary>When a message handed to <see cref="MessagePickupQueue.DeliverAsync"/> is due and when it expires.</summary>
/// <param name="NotBefore">Do not deliver before this instant (a forward's <c>delay_milli</c>, FR-ROUTE-10); <c>null</c> for at once. A message that is not yet due is never pushed live: it is queued and can be picked up once due.</param>
/// <param name="ExpiresTime">The message's <c>expires_time</c> in UTC epoch seconds (FR-ROUTE-09), judged with <c>DidCommOptions.ExpiresClockSkew</c> exactly as the forward path judges it; <c>null</c> for no expiry. An expired message is not accepted, and one that expires while queued is never delivered.</param>
/// <param name="AllowLive"><c>false</c> to queue the message even when the recipient has a live connection.</param>
public sealed record PickupDeliveryOptions(DateTimeOffset? NotBefore = null, long? ExpiresTime = null, bool AllowLive = true)
{
    internal static PickupDeliveryOptions Default { get; } = new();
}

/// <summary>What <see cref="MessagePickupQueue.DeliverAsync"/> did with a message.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="MessageId">The id the message is held under, when it is retained; else <c>null</c>.</param>
/// <param name="GrantPeerDid">The grant the recipient DID is registered under, when one was found; else <c>null</c>.</param>
public readonly record struct PickupDeliveryResult(PickupDeliveryOutcome Outcome, string? MessageId, string? GrantPeerDid)
{
    /// <summary>The outcome only: no DID or id is printed.</summary>
    public override string ToString() => $"PickupDeliveryResult {{ Outcome = {Outcome} }}";
}

/// <summary>The outcomes of <see cref="MessagePickupQueue.DeliverAsync"/>.</summary>
public enum PickupDeliveryOutcome
{
    /// <summary>Stored and pushed over the recipient's live connection; held (leased) until the recipient acknowledges it, the lease runs out or the connection closes — after which it is deliverable by <c>delivery-request</c>.</summary>
    PushedLive,

    /// <summary>Stored, waiting for a <c>delivery-request</c>. Also the outcome when a live push was tried and not taken.</summary>
    Queued,

    /// <summary>Not stored: no grant has this recipient DID registered (or the value is not a bare DID).</summary>
    NotRegistered,

    /// <summary>Not stored: the message's <c>expires_time</c> has passed.</summary>
    Expired,

    /// <summary>Not stored: the message is larger than <see cref="MessagePickupQueue.MaxAdmissiblePayloadBytes"/>.</summary>
    PayloadTooLarge,

    /// <summary>Not stored: the grant's quota, or the store's, is used up.</summary>
    QueueFull,

    /// <summary>Not stored as far as is known: the mediation store, the message store or the id generator failed (logged by type).</summary>
    Faulted,
}
