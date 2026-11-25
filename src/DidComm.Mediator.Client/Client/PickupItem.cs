using DidComm.Facade;

namespace DidComm.Mediator.Client;

/// <summary>
/// One message a mediator delivered (Message Pickup 3.0 or 4.0, FR-PICK-11, FR-PICK-12): either it
/// unpacked, and <see cref="Unpacked"/> holds it, or it did not, and <see cref="Error"/> says why.
/// </summary>
/// <remarks>
/// <para>
/// A failed item is not acknowledged: the mediator keeps it and delivers it again after its lease
/// ends (60 seconds at this library's mediator). A delivered message that is not a DIDComm message
/// in encrypted form (a JWE, as delivered) is always a failed item: Message Pickup allows no other
/// kind of attachment, so it is never unpacked. When you have given up on it, discard it with
/// <see cref="PickupClient.AckAsync"/> and its <see cref="Id"/>.
/// </para>
/// <para>
/// <strong>Not every failed item has been tried.</strong> When <see cref="NotAttempted"/> is
/// <c>true</c>, the client did not try to open the message in this delivery and knows nothing about
/// it. Do not discard such an item: it is delivered again and tried then. A host that discards every
/// failed item it is handed also deletes messages that nobody has tried to open.
/// </para>
/// <para>
/// <strong>Who sent it.</strong> The message in <see cref="Unpacked"/> was delivered encrypted, but
/// it can be anoncrypted (or, inside a forward the mediator re-wrapped for you, not protected at
/// all), and then its <c>from</c> is anyone's claim: check <see cref="UnpackResult.Authenticated"/>
/// before you trust it.
/// </para>
/// <para>
/// <strong>No replay check.</strong> The message has not been through the replay check that messages
/// arriving on the transport get (FR-RR-08). If you hand it on to your own protocol handlers, keep
/// track of the message ids you have handled.
/// </para>
/// <para>
/// <strong>One message to several of your DIDs.</strong> When a sender encrypted one message to
/// several DIDs whose keys this agent holds, the mediator queues a copy for each. Fetched or
/// received with the same <c>from</c>, you get one item for the message, not one per DID: the other
/// copies are acknowledged as repeats (while the clients remember the message,
/// <see cref="MediatorClientOptions.DedupeCapacity"/>). <see cref="UnpackResult.RecipientKid"/>
/// names the first of those DIDs the envelope lists: its key opened every copy. A fetch filtered on
/// another of those DIDs reports that DID's copy as a failed item, "for another of your DIDs",
/// because a filter accepts only what one of the filter DID's own keys opened; that copy stays
/// queued until you fetch it without a filter, discard it with <see cref="PickupClient.AckAsync"/>,
/// or it ages out at the mediator.
/// </para>
/// </remarks>
public sealed class PickupItem
{
    /// <summary>Initialize an item; exactly one of <paramref name="unpacked"/> and <paramref name="error"/> is set.</summary>
    /// <param name="id">The mediator's id for the message.</param>
    /// <param name="unpacked">The innermost unpacked message, or <c>null</c>.</param>
    /// <param name="error">Why it did not unpack, or <c>null</c>.</param>
    /// <param name="notAttempted">Whether the client made no attempt to open the message in this delivery (<see cref="NotAttempted"/>); only a failed item can say so.</param>
    /// <exception cref="ArgumentException">Both or neither of <paramref name="unpacked"/> and <paramref name="error"/> are set, or <paramref name="notAttempted"/> is set for an item that unpacked.</exception>
    internal PickupItem(string id, UnpackResult? unpacked, Exception? error, bool notAttempted = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        if ((unpacked is null) == (error is null))
            throw new ArgumentException("Exactly one of the unpack result and the error is set.", nameof(unpacked));
        if (notAttempted && error is null)
            throw new ArgumentException("Only a failed item can be one that was not attempted.", nameof(notAttempted));
        Id = id;
        Unpacked = unpacked;
        Error = error;
        NotAttempted = notAttempted;
    }

    /// <summary>
    /// The mediator's id for the message (the <c>delivery</c> attachment id). Opaque: pass it back to
    /// <see cref="PickupClient.AckAsync"/> as it is, and read nothing into it. At most 2048
    /// unreserved URI characters.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The message, unpacked: the innermost one when the mediator re-wrapped it for you (the
    /// forward layers it added are opened, FR-ROUTE-06). <c>null</c> when it did not unpack.
    /// </summary>
    public UnpackResult? Unpacked { get; }

    /// <summary>
    /// Why the message did not unpack (for example it was not encrypted for any DID whose keys this
    /// agent holds, it did not open within <see cref="MediatorClientOptions.UnpackTimeout"/>, or a
    /// <c>recipientDid</c> filter's DID did not open it). Set exactly when <see cref="Unpacked"/> is
    /// <c>null</c>; such an item was not acknowledged.
    /// </summary>
    /// <remarks>
    /// When the unpack failed, this is the unpack's own exception, and its message can quote DIDs and
    /// key ids (the texts of this library's unpack do). Do not write it verbatim to a log or a store
    /// that must not hold identifiers: log its type instead. The exceptions this client creates
    /// itself (a timeout, a filter mismatch) name at most its own request id.
    /// </remarks>
    public Exception? Error { get; }

    /// <summary>
    /// <c>true</c> when the client did not try to open this message in this delivery, and so knows
    /// nothing about it. Leave such an item queued: do not discard it with
    /// <see cref="PickupClient.AckAsync"/>. It is delivered again and tried then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is <c>true</c> in two cases only, each a failed item whose <see cref="Error"/> is a
    /// <see cref="TimeoutException"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>The time for opening the delivery
    /// (<see cref="MediatorClientOptions.UnpackTimeout"/>) ran out before this message's turn came:
    /// the messages in front of it in the same delivery used the time up.</description></item>
    /// <item><description>The message was refused for capacity ("unpack capacity exhausted"): as many
    /// messages as may be opened at once for its sender in this mailbox, or for the whole container,
    /// were still being opened.</description></item>
    /// </list>
    /// <para>
    /// It is <c>false</c> for every other item: one that opened; one that failed on its own; one
    /// that ran out of time while it was being opened; one that was not opened again because it did
    /// not open when it was delivered before; and one that is not a message this client opens at all
    /// (malformed, or not a DIDComm message in encrypted form). A message the client started to open
    /// counts as tried from the moment the client handed it to the thread pool, even when the time
    /// ran out before your resolvers were called for it.
    /// </para>
    /// <para>
    /// <strong>What to do with it.</strong> Nothing: the mediator keeps the message and delivers it
    /// again (after its lease, 60 seconds at this library's mediator), and the client tries it then.
    /// A message that ran out of time before its turn gets its turn once the messages that were in
    /// front of it are gone or are no longer opened (the ones that did not open are remembered, or
    /// you discarded them). A message refused for capacity is tried once the openings that were in
    /// its way have ended; while they have not (a DID resolver that ignores cancellation can keep
    /// them running), it comes back not attempted again. Discarding such an item deletes a message
    /// nobody has tried to open, which may be an honest sender's.
    /// </para>
    /// </remarks>
    public bool NotAttempted { get; }
}
