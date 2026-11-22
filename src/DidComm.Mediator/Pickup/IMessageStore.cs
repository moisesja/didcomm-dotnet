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
/// <item><description><strong>Payloads are DIDComm messages in encrypted form.</strong> A Message Pickup delivery may carry nothing else (3.0 L172, 4.0 L261), and <see cref="MessagePickupQueue.DeliverAsync"/> refuses anything else before it calls the store (<see cref="PickupDeliveryOutcome.NotEncrypted"/>); a host that writes records into its store directly takes on that check, because the delivery paths do not judge a stored payload again.</description></item>
/// <item><description><strong>A record is in exactly one place.</strong> It is stored once by <see cref="EnqueueAsync"/> and leaves by <see cref="AcknowledgeAsync"/> or a purge. A lease is a mark on the record (the lease's id, an owner token and an expiry instant), never a move: ending a lease — by <see cref="ReleaseAsync"/>, <see cref="ReturnUnsentAsync"/>, <see cref="ReleaseLeasesAsync"/> or the passing of its expiry — never copies, re-inserts or re-orders the record.</description></item>
/// <item><description><strong>A lease is given back only by its taker.</strong> <see cref="ReleaseAsync"/> (and <see cref="ReturnUnsentAsync"/>) names the lease it ends (<see cref="MessageLease.Id"/>) and leaves a record alone when it carries another lease by now — the first one ran out and somebody else took the record — so a late clean-up can never make a message that was just delivered deliverable again.</description></item>
/// <item><description><strong>A record that was passed over stays where it is.</strong> <see cref="PassOverAsync"/> marks a record the taker could not deliver for now: it stays stored and counted, in its position, and <see cref="LeaseAsync"/> does not hand it out again before the instant given — whatever happens to leases, sessions or connections in the meantime. The mark and the number of times it was set (<see cref="QueuedMessage.PassOvers"/>) belong to the record and last as long as it does; they are what lets a recipient's later requests get past such records instead of meeting the same ones first every time.</description></item>
/// <item><description><strong>A message a live push took is never pushed live again, unless that push was not sent.</strong> A message pushed live and returned unconfirmed waits until the recipient asks for it with a <c>delivery-request</c> (owner decision on #100, 2026-11-20; Message Pickup 4.0 L304: "Messages already in the queue are not affected by Live Mode; they MUST still be requested with delivery-request messages", L308; 3.0 L203). The mediator says which leases are for a live push (<see cref="MessageLease.LivePush"/>). The store sets the live-push mark (<see cref="QueuedMessage.LivePushReturned"/>) when such a lease takes a record — every record <see cref="LeaseAsync"/> takes under it, and the record <see cref="EnqueueAsync"/> admits under it — in the same step as the lease, and remembers which lease set it (the lease's <see cref="MessageLease.Id"/>). From then on the mark stays for as long as the record is stored, whatever ends that lease (<see cref="ReleaseAsync"/>, <see cref="ReleaseLeasesAsync"/>, the passing of its expiry) and whatever takes the record afterwards. Only the lease that set the mark clears it, and only by saying its push was not sent: <see cref="ReturnUnsentAsync"/>, or <see cref="PassOverAsync"/> (the record did not fit). Either clears a mark only on a record whose mark was set by the lease it names — also when that lease has ended by then, so the outcome never depends on whether a connection's close or the mediator's own call lands first — and never a mark set by another lease. <see cref="LeaseAsync"/> never hands a marked record to a request whose lease is a live-push lease; any other request (a <c>delivery-request</c>, over HTTP or a WebSocket) takes it exactly as before, in its position and within the same budgets. A <c>delivery-request</c>'s lease never sets a mark and has none to clear. The mark changes no count, bound or status. The records <see cref="LeaseAsync"/> returns describe each record as it was before the take, so the records of a live push come back unmarked. A store that loses the mark, or clears it any other way, makes the mediator push such a message live again; a record it hands to a live-push request while it carries the mark is refused by the mediator (not pushed; pickup still works).</description></item>
/// <item><description><strong>Enqueue is one atomic check-and-insert</strong> against the store's bounds, reported by <see cref="EnqueueOutcome"/>, never by throwing. Every retained record counts against the bounds whatever its state: waiting, leased, or not yet due. A record that is expired at the instant of the call does not: expired records never keep a new message out.</description></item>
/// <item><description><strong>Every record has an arrival sequence the store assigns.</strong> When <see cref="EnqueueAsync"/> stores a record, the store gives it a <see cref="QueuedMessage.Sequence"/> greater than that of every record it stored before (one counter for the whole store will do; a value the caller set is ignored), returns it with the record, reports the highest one assigned so far in every status (<see cref="QueueStatus.HighWaterSequence"/>) and honours <see cref="MessageLeaseRequest.AfterSequence"/>. The mediator decides "arrived after" by it — whether a record arrived after a session turned live delivery on — never by comparing two clock readings. Records a store hands out outside it are refused by the mediator (not pushed; pickup still works).</description></item>
/// <item><description><strong>Lease is one atomic select-and-mark.</strong> Two concurrent calls never return the same record. Selection is in arrival order (the order of the enqueue calls, which is that of <see cref="QueuedMessage.Sequence"/>, not that of <see cref="QueuedMessage.ReceivedAt"/>).</description></item>
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
/// a DID can drop them at once with <see cref="PurgeRecipientAsync"/>). A host revokes a grant with
/// <see cref="MessagePickupQueue.RevokeAndPurgeAsync"/>, which revokes it and then calls
/// <see cref="PurgeGrantAsync"/>; a message being stored for the peer at that moment is removed
/// again by the queue's own registration check (unless that check, or the removal, fails). A host
/// that revokes through
/// <c>IMediationStore.RevokeAsync</c> alone calls <see cref="PurgeGrantAsync"/> itself; until then
/// those records are retained and count against the store-wide bounds. They are not delivered while
/// the peer holds no grant — and are again if the same peer is granted mediation again, since
/// records are kept under the peer's DID.
/// </para>
/// </remarks>
public interface IMessageStore
{
    /// <summary>
    /// Store <paramref name="message"/> under its grant, at the end of the grant's arrival order —
    /// already leased when <paramref name="lease"/> is given (a live delivery: the record is
    /// admitted and leased in one step, so no <see cref="LeaseAsync"/> can take it in between).
    /// When that lease is a live-push lease (<see cref="MessageLease.LivePush"/>), the record also
    /// gets the live-push mark in the same step (<see cref="QueuedMessage.LivePushReturned"/>), set
    /// by that lease. Records that are expired at <paramref name="now"/> do not count against the
    /// bounds, the grant's or the store's.
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
    /// lease and is not passed over for now (<see cref="PassOverAsync"/>) — and, when
    /// <see cref="MessageLeaseRequest.AfterSequence"/> is set, has a greater <see cref="QueuedMessage.Sequence"/>, and, when
    /// the request's lease is a live-push lease (<see cref="MessageLease.LivePush"/>), did not come
    /// back unacknowledged from a live push (<see cref="QueuedMessage.LivePushReturned"/>) — until
    /// <see cref="MessageLeaseRequest.MaxMessages"/> are taken or the next one would
    /// carry the payload total past <see cref="MessageLeaseRequest.MaxPayloadBytes"/>. It stops
    /// there and does not look for a smaller record further on, so the result is always a prefix of
    /// what is deliverable; the first deliverable record is taken whatever its size. Expired
    /// records are never returned. Under a live-push lease, every record taken also gets the
    /// live-push mark in the same step, set by that lease (see <see cref="IMessageStore"/>).
    /// </summary>
    /// <param name="request">Whose records, how many, how many bytes, judged at which instant, under which lease.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The leased records in arrival order, each as it was before the take (a live push's records without the mark it just set); empty when nothing is deliverable.</returns>
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
    /// that lease is not this caller's to end. It never clears the live-push mark
    /// (<see cref="QueuedMessage.LivePushReturned"/>): a live push given back this way may have
    /// reached the connection, so it stays out of live pushes. A push that was not sent is given
    /// back with <see cref="ReturnUnsentAsync"/> instead.
    /// </summary>
    /// <param name="grantPeerDid">The grant the ids belong to.</param>
    /// <param name="messageIds">The ids to release.</param>
    /// <param name="leaseId">The <see cref="MessageLease.Id"/> the records were taken under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many leases were ended.</returns>
    ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct);

    /// <summary>
    /// Give back records taken under the lease <paramref name="leaseId"/> whose push the taker knows
    /// was not sent: what <see cref="ReleaseAsync"/> does, and in addition, for each of the records
    /// of <paramref name="grantPeerDid"/> with these ids whose live-push mark
    /// (<see cref="QueuedMessage.LivePushReturned"/>) was set by <paramref name="leaseId"/>, the mark
    /// is cleared, so a later live push may take the record again. The mark is cleared also when
    /// that lease has already ended — by the connection's close (<see cref="ReleaseLeasesAsync"/>),
    /// by its expiry, or by a <c>delivery-request</c> that took the record since — so the outcome
    /// never depends on which of those lands first; a lease that is not <paramref name="leaseId"/>
    /// is never ended, and a mark set by another lease is never cleared. Ids the grant does not hold
    /// are ignored. The mediator calls it only for a push it never handed to the connection, or that
    /// the connection refused (<see cref="DidComm.Transports.ReturnRoute.ReturnRouteSendResult.NotSent"/>).
    /// </summary>
    /// <param name="grantPeerDid">The grant the ids belong to.</param>
    /// <param name="messageIds">The ids to give back.</param>
    /// <param name="leaseId">The <see cref="MessageLease.Id"/> the records were taken under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many records it changed: those whose lease it ended or whose mark it cleared, each counted once.</returns>
    ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct);

    /// <summary>
    /// Set aside records taken under the lease <paramref name="leaseId"/> that could not be
    /// delivered for now: the records of <paramref name="grantPeerDid"/> with these ids that still
    /// carry that lease lose it, are not handed out by <see cref="LeaseAsync"/> before
    /// <paramref name="until"/>, and have their <see cref="QueuedMessage.PassOvers"/> raised by
    /// one. They stay stored, counted and in position. Unlike a lease, the mark has no owner and is
    /// not ended by <see cref="ReleaseAsync"/> or <see cref="ReleaseLeasesAsync"/>: only the passing
    /// of <paramref name="until"/> ends it. Ids the grant does not hold are ignored, and so is a
    /// record that carries a different lease by now. A record that did not fit was not sent, so,
    /// as <see cref="ReturnUnsentAsync"/> does, each of these records whose live-push mark
    /// (<see cref="QueuedMessage.LivePushReturned"/>) was set by <paramref name="leaseId"/> has
    /// that mark cleared — also when the lease has ended by now; a mark set by another lease is
    /// never cleared.
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
    /// deliverable again. It never clears the live-push mark (<see cref="QueuedMessage.LivePushReturned"/>):
    /// a record a live push took keeps it, so only a request whose lease is not a live-push lease
    /// takes it from now on.
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
    /// <paramref name="recipientDid"/>: what a host calls after removing that DID from the grant.
    /// The library never calls it: the pickup role, when it finds records of a DID no longer
    /// registered to the peer, removes just those records by id with <see cref="AcknowledgeAsync"/>.
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
    /// <summary>
    /// The record's arrival sequence: assigned by the store when it stores the record, greater than
    /// that of every record it stored before, and returned with the record from then on (see
    /// <see cref="IMessageStore"/>). A value set by the caller of <see cref="IMessageStore.EnqueueAsync"/> is ignored.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>
    /// The live-push mark: a live push took the message (a lease with
    /// <see cref="MessageLease.LivePush"/> leased or admitted it) and has not said that push was not
    /// sent. Set by the store in the same step as that lease; cleared only by that same lease, with
    /// <see cref="IMessageStore.ReturnUnsentAsync"/> or <see cref="IMessageStore.PassOverAsync"/>;
    /// otherwise kept for as long as the record is stored, whatever ends the lease (an
    /// acknowledgement removes the record). Such a record is never handed to a live-push lease
    /// request, only to a <c>delivery-request</c>: a message pushed live and returned unconfirmed
    /// is never pushed live again (see <see cref="IMessageStore"/>). Returned with the record as it
    /// was before the take, so the records of a live push come back without the mark that push
    /// just set. A value set by the caller of <see cref="IMessageStore.EnqueueAsync"/> is ignored:
    /// a new record has never been pushed.
    /// </summary>
    public bool LivePushReturned { get; init; }

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
    /// The taker passes it to <see cref="IMessageStore.ReleaseAsync"/>,
    /// <see cref="IMessageStore.ReturnUnsentAsync"/> or <see cref="IMessageStore.PassOverAsync"/>
    /// to act on exactly what it took; a store remembers it as the lease that set a record's
    /// live-push mark. Never on the wire.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Whether this lease is for a live push: the mediator sets it on the lease of a record it
    /// pushes over a live connection, never on a <c>delivery-request</c>'s lease. The store marks
    /// every record such a lease takes (<see cref="QueuedMessage.LivePushReturned"/>), in the same
    /// step, and keeps the mark whatever ends the lease — its connection's close, its expiry, a
    /// release by id — unless this same lease says its push was not sent
    /// (<see cref="IMessageStore.ReturnUnsentAsync"/>, <see cref="IMessageStore.PassOverAsync"/>).
    /// A request under a live-push lease never takes a marked record (see <see cref="IMessageStore"/>).
    /// A <c>with</c> copy keeps it.
    /// </summary>
    public bool LivePush { get; init; }
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
    /// <summary>
    /// Only records whose <see cref="QueuedMessage.Sequence"/> is greater than this one — that
    /// arrived after it; <c>null</c>, the default, for any. The mediator's live push asks for the
    /// records that arrived after the status read that answered the session's turning live
    /// delivery on (what was queued before is the recipient's to pick up, FR-PICK-06). A store that
    /// ignores it hands out records the mediator then refuses as outside the contract.
    /// </summary>
    public long? AfterSequence { get; init; }

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

    /// <summary>
    /// The highest <see cref="QueuedMessage.Sequence"/> the store had assigned when it read this
    /// status (zero before the first): every record it stores afterwards has a greater one. Read in
    /// the same step as the counts, so a record either is counted here or arrives after this mark.
    /// </summary>
    public long HighWaterSequence { get; init; }
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
