namespace DidComm.Mediator.Client;

/// <summary>
/// Settings of the recipient-side mediator clients, <see cref="MediationClient"/> and
/// <see cref="PickupClient"/>: how long a request waits for the mediator's answer, how many
/// requests may wait at once, which Coordinate Mediation version to speak, how many delivered
/// messages to remember so a repeat is not returned twice, how often a live stream polls, and how
/// long one delivered message may take to unpack (FR-MED-09, FR-PICK-11).
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
    /// clients of one container together. A request beyond it is refused with
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
    /// How many delivered messages the clients remember, so a message the mediator delivers again
    /// (delivery is at-least-once) is acknowledged but not returned a second time. Default 4096;
    /// 0 turns this off; at most 1 000 000. A message is remembered by a fixed-size hash of its
    /// sender, the DID it was decrypted for and its id (of its bytes when the sender is anonymous),
    /// so the memory used stays bounded whatever the wire values look like. The oldest are
    /// forgotten first, and a forgotten message that comes back is returned again.
    /// </summary>
    public int DedupeCapacity { get; set; } = 4096;

    /// <summary>
    /// How often a live stream (<see cref="PickupClient.ReceiveLiveAsync"/>) turns live delivery on
    /// again and collects what is queued, besides taking what the mediator pushes. Default 1 minute;
    /// 1 second to 24 hours. It is also how a stream notices a dropped connection, and the only way
    /// it receives a message whose delivery its sender delayed (a mediator never pushes a delayed
    /// message when it falls due), so keep it at or below the longest delay you expect your senders
    /// to ask for (this library's mediator allows up to an hour by default).
    /// </summary>
    public TimeSpan LivePollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long unpacking one delivered message may take, every re-wrap layer included (looking up
    /// its sender's DID is usually the slow part). Default 30 seconds; greater than zero and at most
    /// 10 minutes. A message that takes longer is reported as a failed item and left with the
    /// mediator.
    /// </summary>
    public TimeSpan UnpackTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
            DedupeCapacity = DedupeCapacity,
            LivePollInterval = LivePollInterval,
            UnpackTimeout = UnpackTimeout,
        };
        copy.Validate();
        return copy;
    }
}
