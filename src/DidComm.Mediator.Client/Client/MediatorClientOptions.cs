namespace DidComm.Mediator.Client;

/// <summary>
/// Settings of the recipient-side mediator clients, <see cref="MediationClient"/> and
/// <see cref="PickupClient"/>: how long a request waits for the mediator's answer, how many
/// requests may wait at once, which Coordinate Mediation and Message Pickup versions to speak, how
/// many delivered messages to remember so a repeat is not returned twice, how often a live stream
/// polls, and how long opening one delivery may take (FR-MED-09, FR-PICK-11, FR-PICK-12).
/// </summary>
/// <remarks>
/// The settings are copied and checked once, when the clients are built (by
/// <c>AddMediatorClient()</c> or by a client's constructor). Changing this object afterwards
/// changes nothing. The delegate passed to <c>AddMediatorClient()</c> may run more than once, on
/// different instances, so it must only assign values.
/// </remarks>
public sealed class MediatorClientOptions
{
    /// <summary>
    /// How long each step of a call may take: finding the mediator's endpoint in its DID document,
    /// then each request the call sends, from the send until the mediator's answer (a call may send
    /// more than one: a Discover Features query first, or one request per page). Every step gets the
    /// whole of it, so a call can take longer than this in total. Default 30 seconds; greater than
    /// zero and at most 10 minutes. When a step runs out the call fails with
    /// <see cref="TimeoutException"/>. A request that ran out names its id, and the mediator may
    /// still have acted on it, so retry: every request these clients send is safe to repeat. Running
    /// out while finding the endpoint names no request: nothing was sent yet.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest number of requests that may wait for an answer at the same time, for both
    /// clients of one container together; a message that expects no answer (a Message Pickup 4.0
    /// <c>messages-received</c>) counts while it is being sent. A request beyond it is refused with
    /// <see cref="InvalidOperationException"/> before anything is sent. Default 64; 1 to 4096.
    /// </summary>
    public int MaxPendingRequests { get; set; } = 64;

    /// <summary>
    /// Which Coordinate Mediation version <see cref="MediationClient"/> speaks (FR-MED-10). Default
    /// <see cref="Client.CoordinateMediationVersion.Negotiate"/>: ask the mediator with Discover
    /// Features and prefer 3.0. Pin <see cref="Client.CoordinateMediationVersion.V3"/> or
    /// <see cref="Client.CoordinateMediationVersion.V2"/> for a mediator that does not answer
    /// Discover Features queries.
    /// </summary>
    public CoordinateMediationVersion CoordinateMediationVersion { get; set; } = CoordinateMediationVersion.Negotiate;

    /// <summary>
    /// Which Message Pickup version <see cref="PickupClient"/> speaks (FR-PICK-12). Default
    /// <see cref="Client.MessagePickupVersion.Negotiate"/>: ask the mediator with Discover Features
    /// and prefer 4.0. Pin <see cref="Client.MessagePickupVersion.V4"/> or
    /// <see cref="Client.MessagePickupVersion.V3"/> for a mediator that does not answer Discover
    /// Features queries. A live stream (<see cref="PickupClient.ReceiveLiveAsync"/>) settles the
    /// version once, when it starts, and keeps it until it ends.
    /// </summary>
    public MessagePickupVersion MessagePickupVersion { get; set; } = MessagePickupVersion.Negotiate;

    /// <summary>
    /// How many delivered messages the clients remember, so a message the mediator delivers again
    /// (delivery is at-least-once) is acknowledged but not returned a second time. Default 4096;
    /// 0 turns this off; at most 1 000 000. A message is remembered by a fixed-size hash of the
    /// mailbox it came from (the <c>from</c> of the fetch or live stream), its sender, the DID it was
    /// decrypted for and its id (of its bytes when the sender is anonymous), so a message returned
    /// from one mailbox is never a repeat in another, and the memory used stays bounded whatever the
    /// wire values look like. The oldest are
    /// forgotten first, and a forgotten message that comes back is returned again. 0 turns off the
    /// memory from one call or stream to the next; a fetch still returns a message that comes twice
    /// in one delivery only once, but a live stream then yields it twice (that once-per-delivery rule
    /// is the fetch's own).
    /// </summary>
    public int DedupeCapacity { get; set; } = 4096;

    /// <summary>
    /// How often a live stream (<see cref="PickupClient.ReceiveLiveAsync"/>) turns live delivery on
    /// again and collects what is queued, besides taking what the mediator pushes, measured from the
    /// end of the previous collection (a collection always runs to its end first, however slowly the
    /// mediator answers). Default 1 minute; 1 second to 24 hours. It is also how a stream notices a dropped connection, and the only way
    /// it receives a message whose delivery its sender delayed (a mediator never pushes a delayed
    /// message when it falls due), so keep it at or below the longest delay you expect your senders
    /// to ask for (this library's mediator allows up to an hour by default). The mediator has no
    /// scheduler for due messages, now or in a future version: this poll, and the one when a stream
    /// starts, is the permanent design, not a stopgap (FR-PICK-08).
    /// </summary>
    public TimeSpan LivePollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long opening one delivery may take, all of its messages together and every re-wrap layer
    /// included (looking up a sender's DID is usually the slow part): each
    /// <see cref="PickupClient.FetchAsync"/>, and each delivery a live stream handles (a drain round
    /// or a push). Default 20 seconds; greater than zero and at most 10 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The messages of a delivery are opened four at a time, and each gets a share of the time left
    /// when it starts: what is left of this value divided by the rounds of four that it and the
    /// messages after it still need, never less than this value divided by the rounds the whole
    /// delivery needs (the whole of it for up to four messages, half for five to eight), and never
    /// less than a quarter of it. A message starts only while at least that quarter is left. A
    /// message that is not open when its share is up — its sender's DID resolves slowly, or never —
    /// is cut only while another message of the delivery is still waiting to start and could still
    /// start, or when a message was refused since it started because its sender's or the container's
    /// openings were at their bound; otherwise it keeps the rest of this value, until a later refusal
    /// cuts it. A cut message is reported as a failed item with a <see cref="TimeoutException"/>
    /// (naming the request, never a message) and is not acknowledged, and the next message takes its
    /// place; so slow messages cannot keep the ones behind them from being opened, and the time
    /// messages failing at once did not use goes to the ones after them. A delivery whose last
    /// messages never open lasts this whole value. The rest of the delivery is returned and
    /// acknowledged as usual. A live stream opens a delivery before it yields any of its items, so
    /// the time you spend on an item never counts against this.
    /// </para>
    /// <para>
    /// <strong>Messages that did not open are remembered</strong> (per mediator and mailbox): a
    /// message whose opening failed, however quickly, or ran for its share and was then cut, is not
    /// opened again when it is delivered again. A cut message counts as having run its share when its
    /// code started at most an eighth of this value late (our choice, not an owner decision), so an
    /// honest message whose code starts up to 2.5 seconds late (half the 5-second floor), delayed by a
    /// busy thread pool, and then runs out its share without opening, is still remembered. The pause
    /// is 30 times this value (10 minutes by default), doubling each time it fails again up to 256
    /// times (about 85 minutes). Such a message is returned at once as a failed item and is not
    /// acknowledged. So a loop that fetches until
    /// drained, or a live stream's drain, gets past messages that never open (up to 256 in front of
    /// yours): each delivery learns some of them within this value, and the ones it remembers cost
    /// nothing. An honest message that failed only for a passing reason is held back too, and comes
    /// back after its pause. See <see cref="PickupClient.FetchAsync"/> for the rule, how long that
    /// takes, and what remains with slow senders.
    /// </para>
    /// <para>
    /// <strong>Resolvers that block.</strong> Each message is opened on a thread-pool work item of its
    /// own, so the fetch or stream gets its result at this limit even when a DID or secrets resolver
    /// blocks its thread. Blocking host code cannot be bounded, though: the outcome is decided here,
    /// but the blocked thread and its work stay the resolver's until it returns, and the opening keeps
    /// its reservation meanwhile (below). Use resolvers that are asynchronous and honour their
    /// cancellation token.
    /// </para>
    /// <para>
    /// <strong>Keep it well below the mediator's lease.</strong> A mediator sets aside what it
    /// delivered for a while (this library's mediator for <c>MessagePickupOptions.LeaseTimeout</c>,
    /// 60 seconds by default) and delivers it again only after that. While a message the client gave
    /// up on is still set aside, the next fetch passes over it and gets what is queued behind it, and
    /// a live stream's drain learns as many rounds of messages that never open per cycle as fit in
    /// one lease. The
    /// default, a third of this library's default lease, leaves room for the fetch's request and
    /// acknowledgement. For a third-party mediator, keep it below that mediator's lease. If the lease
    /// runs out first, the mediator hands the messages still being opened to the next fetch as well (a
    /// message can then reach you twice), though this library's mediator still accepts the
    /// acknowledgement of the ones that were opened; and a live stream then learns only one round of
    /// them per cycle.
    /// </para>
    /// <para>
    /// <strong>A message that keeps failing.</strong> Discard it with
    /// <see cref="PickupClient.AckAsync"/>. A mediator that does not set fetched messages aside
    /// delivers it first on every fetch, and neither this limit nor the memory of messages that did
    /// not open gets you past more of them than one fetch holds.
    /// </para>
    /// <para>
    /// <strong>A DID resolver that ignores cancellation.</strong> The library hands every lookup the
    /// token of the message's share. A resolver that honours it ends at once. The lookup is host code
    /// and may outlive its share: one that does not honour the token keeps its reservation until it
    /// returns. Each opening is reserved before it starts, and at most 4 are held at a time per sender,
    /// 16 per mailbox for messages whose outer envelope names no sender, and 256 per container; a
    /// message beyond a bound fails at once ("unpack capacity exhausted") and comes back later. Many
    /// senders whose lookups ignore cancellation can still hold all 256, and until those lookups end
    /// every delivered message fails at once, for every DID and tenant of the container; a resolver
    /// that blocks its thread can hold up to 256 threads. Use a resolver that honours its
    /// cancellation token.
    /// </para>
    /// </remarks>
    public TimeSpan UnpackTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Check the settings. <c>AddMediatorClient()</c> and the clients' constructors call it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its range; the exception names the setting and the range.</exception>
    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "The value must be greater than zero and at most 10 minutes.");
        if (MaxPendingRequests is < 1 or > MaxPendingRequestsLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingRequests), "The value must be from 1 to 4096.");
        if (!Enum.IsDefined(CoordinateMediationVersion))
            throw new ArgumentOutOfRangeException(nameof(CoordinateMediationVersion), "The value must be Negotiate, V3 or V2.");
        if (!Enum.IsDefined(MessagePickupVersion))
            throw new ArgumentOutOfRangeException(nameof(MessagePickupVersion), "The value must be Negotiate, V4 or V3.");
        if (DedupeCapacity is < 0 or > MaxDedupeCapacity)
            throw new ArgumentOutOfRangeException(nameof(DedupeCapacity), "The value must be from 0 (off) to 1 000 000.");
        if (LivePollInterval < TimeSpan.FromSeconds(1) || LivePollInterval > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(LivePollInterval), "The value must be from 1 second to 24 hours.");
        if (UnpackTimeout <= TimeSpan.Zero || UnpackTimeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(UnpackTimeout), "The value must be greater than zero and at most 10 minutes.");
    }

    private const int MaxPendingRequestsLimit = 4096;
    private const int MaxDedupeCapacity = 1_000_000;
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>A checked copy: what the clients keep, so a later change to this instance changes nothing (L-060).</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its range.</exception>
    internal MediatorClientOptions ValidatedCopy()
    {
        var copy = new MediatorClientOptions
        {
            RequestTimeout = RequestTimeout,
            MaxPendingRequests = MaxPendingRequests,
            CoordinateMediationVersion = CoordinateMediationVersion,
            MessagePickupVersion = MessagePickupVersion,
            DedupeCapacity = DedupeCapacity,
            LivePollInterval = LivePollInterval,
            UnpackTimeout = UnpackTimeout,
        };
        copy.Validate();
        return copy;
    }
}
