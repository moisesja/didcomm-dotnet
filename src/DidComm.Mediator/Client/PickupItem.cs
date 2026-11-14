using DidComm.Facade;

namespace DidComm.Mediator.Client;

/// <summary>
/// One message a mediator delivered (Message Pickup 3.0, FR-PICK-11): either it unpacked, and
/// <see cref="Unpacked"/> holds it, or it did not, and <see cref="Error"/> says why.
/// </summary>
/// <remarks>
/// <para>
/// A failed item is not acknowledged: the mediator keeps it and delivers it again after its lease
/// ends (60 seconds at this library's mediator). When you have given up on it, discard it with
/// <see cref="PickupClient.AckAsync"/> and its <see cref="Id"/>.
/// </para>
/// <para>
/// <strong>Who sent it.</strong> The message in <see cref="Unpacked"/> can be plaintext or
/// anoncrypted, and then its <c>from</c> is anyone's claim: check
/// <see cref="UnpackResult.Authenticated"/> before you trust it.
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
    /// <exception cref="ArgumentException">Both or neither of <paramref name="unpacked"/> and <paramref name="error"/> are set.</exception>
    internal PickupItem(string id, UnpackResult? unpacked, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(id);
        if ((unpacked is null) == (error is null))
            throw new ArgumentException("Exactly one of the unpack result and the error is set.", nameof(unpacked));
        Id = id;
        Unpacked = unpacked;
        Error = error;
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
}
