using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Mediator;

/// <summary>
/// Settings of a complete mediator registered with <c>AddMediator()</c>: the settings of each role
/// it is made of (Coordinate Mediation, Message Pickup, replay protection, return-route sessions),
/// reached through the sub-objects, plus the settings of the delivery pipeline that joins them.
/// </summary>
/// <remarks>
/// <para>
/// Each sub-object is the same options type its role reads on its own (for example
/// <see cref="Pickup"/> is a <see cref="MessagePickupOptions"/>), so nothing is set in two places.
/// Assigning a property here has the same effect as configuring that role directly, and composes
/// with the host's own <c>Configure&lt;T&gt;</c> calls in registration order: a property the
/// <c>AddMediator()</c> delegate does not assign keeps the value set elsewhere.
/// </para>
/// <para>
/// <strong>The configure delegate passed to <c>AddMediator()</c> may run several times, on
/// different instances</strong> (once at registration to check the settings, and once per role
/// when the container builds that role's options), so it must only assign values: no side
/// effects, no reading back what an earlier run assigned, nothing that depends on how often it
/// runs.
/// </para>
/// </remarks>
public sealed class MediatorOptions
{
    /// <summary>Create the settings with every default: fresh role settings and the pipeline defaults listed on each property.</summary>
    public MediatorOptions()
        : this(new CoordinateMediationOptions(), new MessagePickupOptions(), new ReplayProtectionOptions(), new ReturnRouteSessionOptions())
    {
    }

    private MediatorOptions(
        CoordinateMediationOptions coordinateMediation,
        MessagePickupOptions pickup,
        ReplayProtectionOptions replay,
        ReturnRouteSessionOptions sessions)
    {
        CoordinateMediation = coordinateMediation;
        Pickup = pickup;
        Replay = replay;
        Sessions = sessions;
    }

    /// <summary>
    /// Coordinate Mediation settings (FR-MED-*): the routing DIDs handed out in grants (the
    /// mediator needs at least one, here or from a registered <see cref="IMediatorIdentity"/>; the
    /// first is the primary one), the <c>recipient-query</c> page size, and the bounds of the
    /// in-memory grant store.
    /// </summary>
    public CoordinateMediationOptions CoordinateMediation { get; }

    /// <summary>
    /// Message Pickup settings (FR-PICK-*): batch size and packed delivery size, lease timeout,
    /// per-grant and store-wide quotas, and the mailbox age limit.
    /// </summary>
    public MessagePickupOptions Pickup { get; }

    /// <summary>
    /// Replay protection settings (FR-RR-08): the replay window, the clock skew and the bounds of
    /// the in-memory replay guard. They apply to every DIDComm endpoint of the host, not only the
    /// mediator's.
    /// </summary>
    public ReplayProtectionOptions Replay { get; }

    /// <summary>
    /// Return-route session settings (FR-RR-03): how many live sessions a peer and the whole host
    /// may hold, and how many pushes may wait on one session. They apply to every DIDComm endpoint
    /// of the host.
    /// </summary>
    public ReturnRouteSessionOptions Sessions { get; }

    /// <summary>
    /// How a relayed forward is passed on (FR-ROUTE-06): <see cref="RewrapMode.PassThrough"/>, the
    /// default, sends the inner message as received; <see cref="RewrapMode.ReanoncryptToNext"/>
    /// wraps it again so its size does not shrink at this hop. Re-wrapping cannot be combined with
    /// per-recipient extra routing keys (a registered <see cref="IRecipientRoutingKeyProvider"/>).
    /// </summary>
    public RewrapMode RewrapMode { get; set; } = RewrapMode.PassThrough;

    /// <summary>
    /// The longest delay a forward's <c>delay_milli</c> can ask the mediator to hold a message for
    /// (FR-ROUTE-10). A longer positive delay is shortened to it; for a negative value, which asks
    /// for a random delay of up to its magnitude, the magnitude is capped to it before the delay is
    /// drawn. No forward is refused because of its delay. It also bounds how long a sender can fill a
    /// recipient's quota with messages the recipient cannot see yet. Default 1 hour; zero makes
    /// every message due at once; at most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public TimeSpan MaxForwardDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often the mediator removes expired and aged-out messages from the pickup queue and
    /// reports its counters in the log. Such messages are never delivered whether or not the purge
    /// has run; the purge gives their memory back. Default 5 minutes; from 1 second to 24 hours.
    /// </summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the mediator also pushes each queued message straight to the recipient's own
    /// endpoint, found through a registered <see cref="IRecipientEndpointDirectory"/> (FR-PICK-09).
    /// Default <c>false</c>. Each message gets one push attempt, in the background, after it is
    /// queued: an <c>http(s)</c> 2xx answer removes it from the queue; anything else leaves it there
    /// for pickup, so a recipient that relies on push must also pick up now and then.
    /// </summary>
    public bool DirectPush { get; set; }

    /// <summary>How many direct pushes may run at the same time. Default 4; from 1 to 64.</summary>
    public int DirectPushConcurrency { get; set; } = 4;

    /// <summary>
    /// How long one direct push may take before it is abandoned and the message left queued.
    /// Default 30 seconds; greater than zero and at most 5 minutes.
    /// </summary>
    public TimeSpan DirectPushTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Check every setting: each role's own checks, the ranges listed on the pipeline settings, and
    /// that the mailbox age limit (<see cref="MessagePickupOptions.MaxMessageAge"/>), when set, is
    /// longer than <see cref="MaxForwardDelay"/> (otherwise a delayed message could age out before
    /// it is due). Messages name the setting, never its value. Called by <c>AddMediator()</c> and
    /// again when the mediator starts.
    /// </summary>
    /// <exception cref="ArgumentException">A Coordinate Mediation or Message Pickup setting is invalid, a pipeline setting is out of range, <see cref="RewrapMode"/> is not a defined value, or the mailbox age limit is not longer than <see cref="MaxForwardDelay"/>.</exception>
    /// <exception cref="InvalidOperationException">A replay protection or session setting is invalid (those roles report their settings this way).</exception>
    public void Validate()
    {
        CoordinateMediation.Validate();
        Pickup.Validate();
        Replay.Validate();
        Sessions.Validate();

        if (!Enum.IsDefined(RewrapMode))
            throw new ArgumentException("The value is not a defined RewrapMode.", nameof(RewrapMode));
        if (MaxForwardDelay < TimeSpan.Zero || MaxForwardDelay > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentException("The value must be zero or more and at most int.MaxValue milliseconds.", nameof(MaxForwardDelay));
        if (PurgeInterval < TimeSpan.FromSeconds(1) || PurgeInterval > TimeSpan.FromHours(24))
            throw new ArgumentException("The value must be at least 1 second and at most 24 hours.", nameof(PurgeInterval));
        if (DirectPushConcurrency < 1 || DirectPushConcurrency > 64)
            throw new ArgumentException("The value must be at least 1 and at most 64.", nameof(DirectPushConcurrency));
        if (DirectPushTimeout <= TimeSpan.Zero || DirectPushTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("The value must be greater than zero and at most 5 minutes.", nameof(DirectPushTimeout));

        if (Pickup.MaxMessageAge is { } maxMessageAge && maxMessageAge <= MaxForwardDelay)
        {
            throw new ArgumentException(
                $"{nameof(Pickup)}.{nameof(MessagePickupOptions.MaxMessageAge)} must be longer than {nameof(MaxForwardDelay)}, or a delayed message could age out before it is due.",
                $"{nameof(Pickup)}.{nameof(MessagePickupOptions.MaxMessageAge)}");
        }
    }

    /// <summary>
    /// A view for configuring one role's options through the <c>AddMediator()</c> delegate: each
    /// slot given is the instance being configured, so the delegate's assignments land on it; the
    /// slots not given are scratch instances whose values are discarded.
    /// </summary>
    /// <param name="cm">The Coordinate Mediation options being configured, or <c>null</c> for a scratch instance.</param>
    /// <param name="pickup">The Message Pickup options being configured, or <c>null</c> for a scratch instance.</param>
    /// <param name="replay">The replay protection options being configured, or <c>null</c> for a scratch instance.</param>
    /// <param name="sessions">The return-route session options being configured, or <c>null</c> for a scratch instance.</param>
    internal static MediatorOptions Projecting(
        CoordinateMediationOptions? cm = null,
        MessagePickupOptions? pickup = null,
        ReplayProtectionOptions? replay = null,
        ReturnRouteSessionOptions? sessions = null)
        => new(
            cm ?? new CoordinateMediationOptions(),
            pickup ?? new MessagePickupOptions(),
            replay ?? new ReplayProtectionOptions(),
            sessions ?? new ReturnRouteSessionOptions());
}
