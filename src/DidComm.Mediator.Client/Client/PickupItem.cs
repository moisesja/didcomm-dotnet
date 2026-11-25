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
/// <strong>Not every failed item counts against its message.</strong> When
/// <see cref="Inconclusive"/> is <c>true</c>, the client has not counted this delivery against the
/// message: it did not get to it, or did not give it the whole time. Leave such an item queued: it
/// is delivered again and started again then, once what was in its way is gone. A host that
/// discards every failed item it is handed also deletes messages that were not given their chance
/// to open. When it is <c>false</c>, that alone is not a reason to discard either:
/// <see cref="Inconclusive"/> says what it does and does not mean.
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
    /// <param name="inconclusive">Whether the client has not counted this delivery against the message (<see cref="Inconclusive"/>); only a failed item can say so.</param>
    /// <exception cref="ArgumentException">Both or neither of <paramref name="unpacked"/> and <paramref name="error"/> are set, or <paramref name="inconclusive"/> is set for an item that unpacked.</exception>
    internal PickupItem(string id, UnpackResult? unpacked, Exception? error, bool inconclusive = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        if ((unpacked is null) == (error is null))
            throw new ArgumentException("Exactly one of the unpack result and the error is set.", nameof(unpacked));
        if (inconclusive && error is null)
            throw new ArgumentException("Only a failed item can be inconclusive.", nameof(inconclusive));
        Id = id;
        Unpacked = unpacked;
        Error = error;
        Inconclusive = inconclusive;
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
    /// <c>true</c> when the client has not counted this delivery against the message: leave the item
    /// queued, and do not discard it with <see cref="PickupClient.AckAsync"/>. The message did not
    /// open, but it was not given its full chance in this delivery. It is delivered again and
    /// started again then, once what was in its way is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is <c>true</c> for a failed item in three cases:
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Its turn did not come.</strong> The time for opening the delivery
    /// (<see cref="MediatorClientOptions.UnpackTimeout"/>) ran out before the client started on this
    /// message: the messages in front of it in the same delivery used the time up, or no thread was
    /// free for it. Its <see cref="Error"/> is a <see cref="TimeoutException"/> that says "before its
    /// turn came".</description></item>
    /// <item><description><strong>It was refused for capacity.</strong> As many messages as may be
    /// opened at once for its sender in this mailbox, or for the whole container, were still being
    /// opened. Its <see cref="Error"/> is a <see cref="TimeoutException"/> that says "unpack capacity
    /// exhausted".</description></item>
    /// <item><description><strong>It was started late and cut.</strong> The client started on it
    /// later than an eighth of the time into the delivery (2.5 seconds by default), because other
    /// messages were in front of it or it waited for a thread, and it was still being opened when the
    /// time ran out. Its <see cref="Error"/> is a <see cref="TimeoutException"/> that says "while it
    /// was being opened". It did not have the whole time, so the client does not hold that against
    /// it.</description></item>
    /// </list>
    /// <para>
    /// These are exactly the failed messages the client does not remember as "did not open" (see
    /// <see cref="PickupClient.FetchAsync"/>, "Messages that did not open"): the flag and that memory
    /// are one decision. One more case follows from that, for a host that passes a clock of its own:
    /// if that clock throws at the moment the client would record that a message did not open,
    /// nothing is recorded, and the item is inconclusive whatever its error.
    /// </para>
    /// <para>
    /// <strong>What <c>false</c> means, and what it does not.</strong> For a failed item,
    /// <c>false</c> means one of these: the message failed on its own (it could not be decrypted,
    /// its sender's DID did not resolve); it was given the whole time and did not open; it was not
    /// opened again because it did not open when it was delivered before (the client holds it back
    /// for a pause); it is not a message this client opens at all (malformed, or not a DIDComm
    /// message in encrypted form); or a <c>recipientDid</c> filter turned it away. <c>false</c>
    /// alone is not a reason to discard an item. The message a filter turned away is the example: it
    /// opened, for another of your DIDs, and a fetch without the filter returns it. Discard a failed
    /// item when you have given up on its message, and never while this is <c>true</c>.
    /// </para>
    /// <para>
    /// <strong>What happens to an inconclusive message.</strong> The mediator keeps it and delivers
    /// it again (after its lease, 60 seconds at this library's mediator; a mediator that does not
    /// set delivered messages aside delivers it again at the next fetch), and the client starts on
    /// it again then, once what was in its way is gone. For a message whose turn did not come, or
    /// that was started late: once the messages that were in front of it have opened, are held
    /// back, or were discarded by you, and a thread is free for it in time (while your resolvers
    /// keep the thread pool busy, it can come back inconclusive again). For a message refused for
    /// capacity: once the openings that were in its way have ended (a DID resolver that ignores
    /// cancellation can keep them running; until they end, the message comes back inconclusive
    /// again). Discarding an inconclusive item deletes a message that was not given its chance,
    /// which may be an honest sender's.
    /// </para>
    /// <para>
    /// One narrow window: a message whose turn on a thread came at the very moment the time ran out
    /// is reported as "before its turn came", although your resolvers are then still called for it,
    /// once, with a cancellation token that is already cancelled. Whatever they return is discarded.
    /// </para>
    /// </remarks>
    public bool Inconclusive { get; }
}
