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
/// <c>AddMediator()</c> delegate does not assign keeps the value set elsewhere, and a
/// <c>Configure&lt;T&gt;</c> registered after <c>AddMediator()</c> wins for the properties it sets.
/// </para>
/// <para>
/// <strong>The configure delegate passed to <c>AddMediator()</c> may run several times, on
/// different instances</strong> (once at registration to check the settings, and once per role
/// when the container builds that role's options), so it must only assign values: no side
/// effects, no reading back what an earlier run assigned, nothing that depends on how often it
/// runs. Adding to <see cref="CoordinateMediationOptions.RoutingDids"/> counts as assigning: each
/// run adds to a different list.
/// </para>
/// <para>
/// <strong>When the settings are checked.</strong> <c>AddMediator()</c> checks the values its
/// delegate assigns (<see cref="Validate"/>) and refuses bad ones with
/// <see cref="ArgumentException"/>. Each role checks its own effective settings when it is first
/// built, and the mediator's hosted service checks all of them together, including the rules that
/// span two roles, before the host starts listening. Settings are copied when each part is built:
/// changing an options object afterwards changes nothing.
/// </para>
/// <para>
/// <strong>Operating a mediator.</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Replay protection (<see cref="Replay"/>) admits each in-scope message once,
/// from a bounded memory of what it has seen. Size <see cref="ReplayProtectionOptions.MaxEntries"/>
/// to the accepted rate times (<see cref="ReplayProtectionOptions.Window"/> +
/// <see cref="ReplayProtectionOptions.ClockSkew"/>): the default of 32 768 carries about 91
/// messages a second. When it is full, new in-scope messages are refused; the mediator counts the
/// refusals and reports them in its log.</description></item>
/// <item><description>If the host clock steps back by more than
/// <see cref="ReplayProtectionOptions.Window"/> + <see cref="ReplayProtectionOptions.ClockSkew"/>
/// (6 minutes at the defaults), in-scope traffic is refused until the clock catches up or the
/// process restarts. The refusals are counted and reported in the log.</description></item>
/// <item><description>A live session whose peer stops reading holds up to
/// <see cref="ReturnRouteSessionOptions.MaxPendingPushes"/> packed deliveries, each at most
/// <see cref="MessagePickupOptions.MaxDeliveryBytes"/>; size <see cref="Sessions"/> with that in
/// mind.</description></item>
/// <item><description>A forwarded message is always stored first; a recipient with live delivery
/// on gets it pushed afterwards, by the push workers (<see cref="PushConcurrency"/>,
/// <see cref="PushTimeout"/>), so a recipient that is slow to take a push holds up no one else's
/// messages. Each recipient's pushes go one at a time, in the order its messages were stored, and
/// every message stored while it is live is pushed: the push backlog is sized from the queue's
/// quotas (<see cref="MessagePickupOptions.MaxQueueMessages"/>,
/// <see cref="MessagePickupOptions.MaxStoreMessages"/>). A message whose push fails or reaches
/// <see cref="PushTimeout"/> stays queued, so a recipient in live mode should still ask for its
/// queued messages (a <c>status-request</c> shows them) after a reconnect or a slow
/// period.</description></item>
/// <item><description>Whether a forward has expired, when it is relayed and while it waits in the
/// queue, is judged with <c>DidCommOptions.ExpiresClockSkew</c>, never with the replay
/// <see cref="ReplayProtectionOptions.ClockSkew"/>.</description></item>
/// <item><description>Direct push (<see cref="DirectPush"/>) makes one attempt per message: one
/// request to exactly the directory's endpoint, never retried and never redirected. A recipient
/// that relies on it must also pick up, or the messages a push could not deliver stay in its queue
/// until they expire or reach the mailbox age limit.</description></item>
/// <item><description>Forwards are outside replay protection. With direct push on, each replay of a
/// captured forward is stored and pushed again, and since a delivered push frees its place in the
/// queue, the recipient's quota does not limit how often: recipients should drop messages whose id
/// they have already seen.</description></item>
/// </list>
/// <para>
/// <c>AddMediator()</c> describes what to put in front of the mediator's endpoint and how to set
/// up enrolment.
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
    /// mediator's, and reach a replay guard the host registers itself only if it reads
    /// <c>IOptions&lt;ReplayProtectionOptions&gt;</c>.
    /// </summary>
    public ReplayProtectionOptions Replay { get; }

    /// <summary>
    /// Return-route session settings (FR-RR-03): how many live sessions a peer and the whole host
    /// may hold, and how many pushes may wait on one session. They apply to every DIDComm endpoint
    /// of the host, and reach a session registry the host registers itself only if it reads
    /// <c>IOptions&lt;ReturnRouteSessionOptions&gt;</c>.
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
    /// queued: a single POST to exactly the directory's endpoint, with no retry, no redirect
    /// followed and outside the HTTP transport's circuit breaker (so one recipient's failing
    /// endpoint cannot stop the host's other sends). An <c>http(s)</c> 2xx answer removes the
    /// message from the queue; anything else, a redirect included, leaves it there for pickup, so a
    /// recipient that relies on push must also pick up now and then. A push sends
    /// only what the queue still holds for delivery when the push starts: it takes the message
    /// under a lease of its own first, so a message that has expired, aged out, been acknowledged,
    /// or is being fetched by the recipient is not pushed. A plain <c>http</c> endpoint also needs
    /// <c>"http"</c> in <c>HttpTransportOptions.AllowedSchemes</c> (the HTTP transport sends to
    /// <c>https</c> only by default). Forwards are outside replay protection, so each replay of a
    /// captured forward is pushed again (a delivered push frees its place in the quota): recipients
    /// should drop messages whose id they have already seen.
    /// </summary>
    public bool DirectPush { get; set; }

    /// <summary>
    /// How many pushes may run at the same time: live pushes over a recipient's WebSocket
    /// connection and, when <see cref="DirectPush"/> is on, direct pushes to its endpoint. Default 4;
    /// from 1 to 64. Pushes run in the background, never on the forward pump, so a recipient that is
    /// slow to take a push (a slow endpoint, or a DID that is slow to resolve) never holds up the
    /// storing of other recipients' messages. Recipients take turns, with at most one push in flight
    /// per mediation grant, so a slow recipient holds one of them at a time, never all.
    /// </summary>
    public int PushConcurrency { get; set; } = 4;

    /// <summary>
    /// How long one push (live or direct) may take before it is abandoned and the message left
    /// queued for pickup. Default 30 seconds; greater than zero and at most 5 minutes.
    /// </summary>
    public TimeSpan PushTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Check every setting: each role's own checks, the ranges listed on the pipeline settings, and
    /// that the mailbox age limit (<see cref="MessagePickupOptions.MaxMessageAge"/>), when set, is
    /// longer than <see cref="MaxForwardDelay"/> (otherwise a delayed message could age out before
    /// it is due). Messages name the setting, never its value. <c>AddMediator()</c> calls it on the
    /// values its delegate assigns, and the mediator checks the values in effect again when it
    /// starts.
    /// </summary>
    /// <remarks>
    /// The exception type is the one each role uses for its own settings: replay protection and
    /// return-route sessions report a bad setting with <see cref="InvalidOperationException"/>,
    /// everything else with <see cref="ArgumentException"/>. <c>AddMediator()</c> reports all of
    /// them as <see cref="ArgumentException"/>, with the role's exception as the inner exception.
    /// </remarks>
    /// <exception cref="ArgumentException">A Coordinate Mediation or Message Pickup setting is invalid, a pipeline setting is out of range, <see cref="RewrapMode"/> is not a defined value, or the mailbox age limit is not longer than <see cref="MaxForwardDelay"/>.</exception>
    /// <exception cref="InvalidOperationException">A replay protection or session setting is invalid (those roles report their settings this way).</exception>
    public void Validate()
    {
        CoordinateMediation.Validate();
        Pickup.Validate();
        Replay.Validate();
        Sessions.Validate();
        ValidatePipeline();
    }

    /// <summary>
    /// The pipeline's own checks: the ranges of its settings and the rule between the mailbox age
    /// limit and <see cref="MaxForwardDelay"/>. Messages name the setting, never its value.
    /// </summary>
    /// <exception cref="ArgumentException">A pipeline setting is out of range, <see cref="RewrapMode"/> is not a defined value, or the mailbox age limit is not longer than <see cref="MaxForwardDelay"/>.</exception>
    internal void ValidatePipeline()
    {
        ValidatePipelineRanges();

        if (Pickup.MaxMessageAge is { } maxMessageAge && maxMessageAge <= MaxForwardDelay)
        {
            throw new ArgumentException(
                $"{nameof(Pickup)}.{nameof(MessagePickupOptions.MaxMessageAge)} must be longer than {nameof(MaxForwardDelay)}, or a delayed message could age out before it is due.",
                $"{nameof(Pickup)}.{nameof(MessagePickupOptions.MaxMessageAge)}");
        }
    }

    /// <summary>
    /// The ranges of the pipeline settings alone, for a part that reads only those (the sub-objects
    /// of the registered <c>IOptions&lt;MediatorOptions&gt;</c> are not the roles' settings in effect).
    /// </summary>
    /// <exception cref="ArgumentException">A pipeline setting is out of range or <see cref="RewrapMode"/> is not a defined value.</exception>
    internal void ValidatePipelineRanges()
    {
        if (!Enum.IsDefined(RewrapMode))
            throw new ArgumentException("The value is not a defined RewrapMode.", nameof(RewrapMode));
        if (MaxForwardDelay < TimeSpan.Zero || MaxForwardDelay > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentException("The value must be zero or more and at most int.MaxValue milliseconds.", nameof(MaxForwardDelay));
        if (PurgeInterval < TimeSpan.FromSeconds(1) || PurgeInterval > TimeSpan.FromHours(24))
            throw new ArgumentException("The value must be at least 1 second and at most 24 hours.", nameof(PurgeInterval));
        if (PushConcurrency < 1 || PushConcurrency > 64)
            throw new ArgumentException("The value must be at least 1 and at most 64.", nameof(PushConcurrency));
        if (PushTimeout <= TimeSpan.Zero || PushTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("The value must be greater than zero and at most 5 minutes.", nameof(PushTimeout));
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

    /// <summary>
    /// The settings in effect, as one object: the role options the container built (which carry the
    /// host's own <c>Configure&lt;T&gt;</c> calls as well as the <c>AddMediator()</c> delegate) and
    /// the pipeline settings of <paramref name="pipeline"/>. Used to check the rules that span two
    /// roles on the values that will actually run.
    /// </summary>
    /// <param name="pipeline">The mediator's own options, for the pipeline settings.</param>
    /// <param name="cm">The Coordinate Mediation options in effect.</param>
    /// <param name="pickup">The Message Pickup options in effect.</param>
    /// <param name="replay">The replay protection options in effect.</param>
    /// <param name="sessions">The return-route session options in effect.</param>
    internal static MediatorOptions Effective(
        MediatorOptions pipeline,
        CoordinateMediationOptions cm,
        MessagePickupOptions pickup,
        ReplayProtectionOptions replay,
        ReturnRouteSessionOptions sessions)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(cm);
        ArgumentNullException.ThrowIfNull(pickup);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(sessions);
        return new MediatorOptions(cm, pickup, replay, sessions)
        {
            RewrapMode = pipeline.RewrapMode,
            MaxForwardDelay = pipeline.MaxForwardDelay,
            PurgeInterval = pipeline.PurgeInterval,
            DirectPush = pipeline.DirectPush,
            PushConcurrency = pipeline.PushConcurrency,
            PushTimeout = pipeline.PushTimeout,
        };
    }
}
