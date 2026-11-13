namespace DidComm.Mediator.Pickup;

/// <summary>
/// Where the mediator keeps the messages it holds for its recipients until they are picked up
/// (FR-PICK-03..05, -09, -10). <see cref="MessagePickupQueue"/> writes it when a message arrives
/// for a registered recipient; <see cref="MessagePickupHandler"/> reads it to answer
/// <c>status-request</c>, <c>delivery-request</c> and <c>messages-received</c>.
/// </summary>
/// <remarks>
/// <para>
/// The library ships <see cref="InMemoryMessageStore"/> only (D1): a persistent store is the
/// host's, registered before <c>AddMessagePickup()</c>. An implementation is a singleton and MUST
/// be thread-safe. The contract it has to keep:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Everything is confined to a grant.</strong> Each operation names a <c>grantPeerDid</c> — the DID subject of the recipient that enrolled (<c>MediationGrant.PeerDid</c>) — and touches only records stored under it. A message id means something only together with its grant: an id held under another grant is a plain miss, reported and handled exactly like an id nobody holds. Keys are bare DIDs of at most 2048 characters, compared ordinally; the store parses nothing.</description></item>
/// <item><description><strong>A record is in exactly one place.</strong> It is stored once by <see cref="EnqueueAsync"/> and leaves by <see cref="AcknowledgeAsync"/> or a purge. A lease is a mark on the record (the lease's id, an owner token and an expiry instant), never a move: ending a lease — by <see cref="ReleaseAsync"/>, <see cref="ReleaseLeasesAsync"/> or the passing of its expiry — never copies, re-inserts or re-orders the record.</description></item>
/// <item><description><strong>A lease is given back only by its taker.</strong> <see cref="ReleaseAsync"/> names the lease it ends (<see cref="MessageLease.Id"/>) and leaves a record alone when it carries another lease by now — the first one ran out and somebody else took the record — so a late clean-up can never make a message that was just delivered deliverable again.</description></item>
/// <item><description><strong>A record that was passed over stays where it is.</strong> <see cref="PassOverAsync"/> marks a record the taker could not deliver for now: it stays stored and counted, in its position, and <see cref="LeaseAsync"/> does not hand it out again before the instant given — whatever happens to leases, sessions or connections in the meantime. The mark and the number of times it was set (<see cref="QueuedMessage.PassOvers"/>) belong to the record and last as long as it does; they are what lets a recipient's later requests get past such records instead of meeting the same ones first every time.</description></item>
/// <item><description><strong>Enqueue is one atomic check-and-insert</strong> against the store's bounds, reported by <see cref="EnqueueOutcome"/>, never by throwing. Every retained record counts against the bounds whatever its state: waiting, leased, or not yet due. A record that is expired at the instant of the call does not: expired records never keep a new message out.</description></item>
/// <item><description><strong>Lease is one atomic select-and-mark.</strong> Two concurrent calls never return the same record. Selection is in arrival order (the order of the enqueue calls, not of <see cref="QueuedMessage.ReceivedAt"/>).</description></item>
/// <item><description><strong>Time comes from the caller.</strong> Every time-dependent operation takes the instant to judge by; the store reads no clock. A record is expired when <c>now &gt;= ExpiresAt</c>, not yet due when <c>NotBefore &gt; now</c>, and leased when its lease's <c>ExpiresAt &gt; now</c>.</description></item>
/// <item><description><strong>Expiry is the only clock rule a store applies.</strong> <see cref="QueuedMessage.ExpiresAt"/> already carries both the message's own expiry and the mailbox age limit (<see cref="MessagePickupOptions.MaxMessageAge"/>): the queue sets it to the earlier of the two when it stores the record. An expired record is never handed out, never reported and never counts against a bound, whatever its state (waiting, leased or passed over), and may be removed at any time. A store needs nothing beyond <c>ExpiresAt</c> to enforce the age limit; the limit in force when a record was stored stays fixed on it.</description></item>
/// <item><description><strong>No throw after a partial change.</strong> A method that changes several records either throws before anything changed, or its return value is the truth.</description></item>
/// <item><description><strong>Payloads are opaque.</strong> The bytes are kept as given and returned unchanged.</description></item>
/// </list>
/// <para>
/// A method that throws is treated as a store fault: the handler sends no reply, a delivery to the
/// queue is reported as <see cref="PickupDeliveryOutcome.Faulted"/>, the exception never leaves
/// dispatch and only its type name is logged. An implementation must not take the protocol
/// dispatcher, the handler registry, a protocol handler, <c>DidCommClient</c> or
/// <see cref="MessagePickupQueue"/> in its constructor (that closes a dependency cycle).
/// </para>
/// <para>
/// <strong>Removed DIDs and revoked grants.</strong> Nothing is delivered for a recipient DID that
/// no longer belongs to the requesting peer's grant; the pickup role checks the registration when
/// it delivers and removes the records it finds unregistered as they come up (a host that removes
/// a DID can drop them at once with <see cref="PurgeRecipientAsync"/>). A host that revokes a grant
/// (<c>IMediationStore.RevokeAsync</c>) calls <see cref="PurgeGrantAsync"/> to drop what was queued
/// under it; until then those records are retained and count against the store-wide bounds. They
/// are not delivered while the peer holds no grant — and are again if the same peer is granted
/// mediation again, since records are kept under the peer's DID.
/// </para>
/// </remarks>
public interface IMessageStore
{
    /// <summary>
    /// Store <paramref name="message"/> under its grant, at the end of the grant's arrival order —
    /// already leased when <paramref name="lease"/> is given (a live delivery: the record is
    /// admitted and marked in one step, so no <see cref="LeaseAsync"/> can take it in between).
    /// Records that are expired at <paramref name="now"/> do not count against the bounds, the
    /// grant's or the store's.
    /// </summary>
    /// <param name="message">The record to store. Its <see cref="QueuedMessage.Id"/> is unique within the grant.</param>
    /// <param name="lease">The lease the record starts with, or <c>null</c> for a record that is simply waiting.</param>
    /// <param name="now">The instant to judge expiry by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="EnqueueOutcome.Enqueued"/> when the record is now stored; otherwise why it was not, and nothing changed.</returns>
    ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Select the next deliverable records of a grant and mark them leased, in one step. Walks the
    /// grant's records in arrival order — only those for <see cref="MessageLeaseRequest.RecipientDid"/>
    /// when it is set — and takes each record that is not expired, is due and is not under a live
    /// lease and is not passed over for now (<see cref="PassOverAsync"/>), until
    /// <see cref="MessageLeaseRequest.MaxMessages"/> are taken or the next one would
    /// carry the payload total past <see cref="MessageLeaseRequest.MaxPayloadBytes"/>. It stops
    /// there and does not look for a smaller record further on, so the result is always a prefix of
    /// what is deliverable; the first deliverable record is taken whatever its size. Expired
    /// records are never returned.
    /// </summary>
    /// <param name="request">Whose records, how many, how many bytes, judged at which instant, under which lease.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The leased records in arrival order; empty when nothing is deliverable.</returns>
    ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct);

    /// <summary>
    /// Remove the records of <paramref name="grantPeerDid"/> with these ids, whatever their state
    /// (waiting, leased, lease expired). Ids the grant does not hold — unknown, already removed, or
    /// held under another grant — are ignored.
    /// </summary>
    /// <param name="grantPeerDid">The grant the ids belong to.</param>
    /// <param name="messageIds">The ids to remove. Each is at most 2048 characters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many records were removed.</returns>
    ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct);

    /// <summary>
    /// Give back records taken under the lease <paramref name="leaseId"/>: the records of
    /// <paramref name="grantPeerDid"/> with these ids that still carry that lease are deliverable
    /// again at once, in their original position. Ids the grant does not hold are ignored, and so
    /// is a record that is not leased or carries a different lease — it was taken again since, and
    /// that lease is not this caller's to end.
    /// </summary>
    /// <param name="grantPeerDid">The grant the ids belong to.</param>
    /// <param name="messageIds">The ids to release.</param>
    /// <param name="leaseId">The <see cref="MessageLease.Id"/> the records were taken under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many leases were ended.</returns>
    ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct);

    /// <summary>
    /// Set aside records taken under the lease <paramref name="leaseId"/> that could not be
    /// delivered for now: the records of <paramref name="grantPeerDid"/> with these ids that still
    /// carry that lease lose it, are not handed out by <see cref="LeaseAsync"/> before
    /// <paramref name="until"/>, and have their <see cref="QueuedMessage.PassOvers"/> raised by
    /// one. They stay stored, counted and in position. Unlike a lease, the mark has no owner and is
    /// not ended by <see cref="ReleaseAsync"/> or <see cref="ReleaseLeasesAsync"/>: only the passing
    /// of <paramref name="until"/> ends it. Ids the grant does not hold are ignored, and so is a
    /// record that carries a different lease by now.
    /// </summary>
    /// <param name="grantPeerDid">The grant the ids belong to.</param>
    /// <param name="messageIds">The ids to pass over.</param>
    /// <param name="leaseId">The <see cref="MessageLease.Id"/> the records were taken under.</param>
    /// <param name="until">The first instant at which the records may be handed out again.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many records were passed over.</returns>
    ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct);

    /// <summary>
    /// End every lease whose <see cref="MessageLease.Owner"/> is <paramref name="leaseOwner"/>,
    /// across all grants: the records delivered over a connection that has now closed become
    /// deliverable again.
    /// </summary>
    /// <param name="leaseOwner">The owner token the leases were taken with.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many leases were ended.</returns>
    ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct);

    /// <summary>
    /// What the grant holds that a recipient can see at <paramref name="now"/>: the records that
    /// are due and not expired, leased ones included (a delivered message counts until it is
    /// acknowledged). Records that are not yet due are retained but not reported.
    /// </summary>
    /// <param name="grantPeerDid">The grant to report on.</param>
    /// <param name="recipientDid">Report on this recipient DID only, or <c>null</c> for the whole grant.</param>
    /// <param name="now">The instant to judge by.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Remove every record stored under <paramref name="grantPeerDid"/> for
    /// <paramref name="recipientDid"/>: what a host calls after removing that DID from the grant,
    /// and what the pickup role calls when it finds the DID no longer registered to the peer.
    /// </summary>
    /// <param name="grantPeerDid">The grant.</param>
    /// <param name="recipientDid">The recipient DID whose records go.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many records were removed.</returns>
    ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct);

    /// <summary>Remove every record stored under <paramref name="grantPeerDid"/>: what a host calls after revoking the grant.</summary>
    /// <param name="grantPeerDid">The grant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many records were removed.</returns>
    ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct);

    /// <summary>Remove every record, of every grant, that is expired at <paramref name="now"/> (FR-ROUTE-09).</summary>
    /// <param name="now">The instant to judge by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many records were removed.</returns>
    ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>A message held for pickup, as <see cref="IMessageStore"/> keeps it.</summary>
/// <param name="Id">The mediator's id for the message: what a <c>delivery</c> carries as the attachment <c>id</c> and a <c>messages-received</c> sends back. Unique within the grant, opaque, never derived from the message.</param>
/// <param name="GrantPeerDid">The grant the message is held under: the DID subject of the recipient that enrolled (<c>MediationGrant.PeerDid</c>). The first half of the record's key.</param>
/// <param name="RecipientDid">The registered DID the message was addressed to (a forward's <c>next</c>, as a DID subject).</param>
/// <param name="Payload">The encrypted message, exactly as received.</param>
/// <param name="ReceivedAt">When the mediator accepted the message.</param>
/// <param name="NotBefore">The message must not be delivered before this instant (a forward's <c>delay_milli</c>, FR-ROUTE-10); <c>null</c> for none.</param>
/// <param name="ExpiresAt">The first instant at which the message is expired and must no longer be delivered, and may be removed: the earlier of the forward's expiry (FR-ROUTE-09) and the mailbox age limit (<see cref="MessagePickupOptions.MaxMessageAge"/>, FR-PICK-10), counted from <see cref="ReceivedAt"/> on the mediator's clock; <c>null</c> for neither.</param>
/// <param name="PassOvers">How many times the message has been passed over (<see cref="IMessageStore.PassOverAsync"/>): found undeliverable for now and set aside. Zero when enqueued; kept by the store and returned with the record by <see cref="IMessageStore.LeaseAsync"/>.</param>
public sealed record QueuedMessage(
    string Id,
    string GrantPeerDid,
    string RecipientDid,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? ExpiresAt = null,
    int PassOvers = 0)
{
    /// <summary>The payload length and the times only: no DID, id or payload is printed, so a logged instance carries no wire value.</summary>
    public override string ToString()
        => $"QueuedMessage {{ Payload = {Payload.Length} bytes, ReceivedAt = {ReceivedAt:O}, NotBefore = {NotBefore:O}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>
/// A lease on stored messages: they were handed to a recipient and are not handed out again until
/// the lease ends (FR-PICK-04).
/// </summary>
/// <param name="Owner">An opaque token for the connection the messages were delivered over, so that all of its leases can be ended when it closes (<see cref="IMessageStore.ReleaseLeasesAsync"/>); <c>null</c> for a lease that ends by time only (a delivery over HTTP).</param>
/// <param name="ExpiresAt">The first instant at which the lease no longer holds.</param>
public sealed record MessageLease(string? Owner, DateTimeOffset ExpiresAt)
{
    /// <summary>The expiry only: neither the owner token nor the lease id is printed, so a logged lease gives nothing away.</summary>
    public override string ToString() => $"MessageLease {{ ExpiresAt = {ExpiresAt:O} }}";

    /// <summary>
    /// Identifies this lease among all leases ever taken: a fresh random token for every lease
    /// created with <c>new</c> (a <c>with</c> copy is the same lease and keeps the id), unless set.
    /// The taker passes it to <see cref="IMessageStore.ReleaseAsync"/> to give back exactly what it
    /// took. Never on the wire.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>What <see cref="IMessageStore.LeaseAsync"/> is asked for.</summary>
/// <param name="GrantPeerDid">The grant whose records are wanted.</param>
/// <param name="RecipientDid">Only records for this recipient DID, or <c>null</c> for any record of the grant.</param>
/// <param name="MaxMessages">The largest number of records to take. Zero takes nothing.</param>
/// <param name="MaxPayloadBytes">The largest payload total to take, in bytes; the first deliverable record is taken whatever its size.</param>
/// <param name="Now">The instant to judge expiry, not-before times and existing leases by.</param>
/// <param name="Lease">The lease to put on the records taken.</param>
public sealed record MessageLeaseRequest(
    string GrantPeerDid,
    string? RecipientDid,
    int MaxMessages,
    long MaxPayloadBytes,
    DateTimeOffset Now,
    MessageLease Lease)
{
    /// <summary>The numbers only: no DID is printed.</summary>
    public override string ToString()
        => $"MessageLeaseRequest {{ MaxMessages = {MaxMessages}, MaxPayloadBytes = {MaxPayloadBytes}, Now = {Now:O} }}";
}

/// <summary>What a grant holds that its recipient can see, as <see cref="IMessageStore.GetStatusAsync"/> reports it.</summary>
/// <param name="MessageCount">The number of due, unexpired records (leased ones included).</param>
/// <param name="TotalBytes">Their payload total in bytes.</param>
/// <param name="OldestReceivedAt">The earliest <see cref="QueuedMessage.ReceivedAt"/> among them; <c>null</c> when there are none.</param>
/// <param name="NewestReceivedAt">The latest <see cref="QueuedMessage.ReceivedAt"/> among them; <c>null</c> when there are none.</param>
public sealed record QueueStatus(int MessageCount, long TotalBytes, DateTimeOffset? OldestReceivedAt, DateTimeOffset? NewestReceivedAt)
{
    /// <summary>The status of an empty queue.</summary>
    public static QueueStatus Empty { get; } = new(0, 0, null, null);
}

/// <summary>What <see cref="IMessageStore.EnqueueAsync"/> did. The store reports the fact; nothing here reaches the sender of the message.</summary>
public enum EnqueueOutcome
{
    /// <summary>The record is stored.</summary>
    Enqueued,

    /// <summary>Not stored: the grant already holds its maximum number of records.</summary>
    GrantMessageLimitReached,

    /// <summary>Not stored: the record would take the grant past its byte budget.</summary>
    GrantByteLimitReached,

    /// <summary>Not stored: the store as a whole is at its record or byte limit.</summary>
    StoreLimitReached,

    /// <summary>Not stored: the grant already holds a record with this id. The caller generates another id.</summary>
    DuplicateId,
}
