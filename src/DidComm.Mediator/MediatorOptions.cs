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
/// on (or, with <see cref="DirectPush"/>, any recipient) gets it pushed afterwards, by the push workers (<see cref="PushConcurrency"/>,
/// <see cref="PushTimeout"/>), so a recipient that is slow to take a push holds up no one else's
/// messages. A push takes what the queue holds when it runs: each recipient's messages go one at a
/// time, oldest first, and a live session is pushed only those that arrive after the status that
/// answered its turning live delivery on (by the store's arrival order, whatever the clock does;
/// what that status counted is picked up). A message with a <c>delay_milli</c> hold is never
/// pushed before it is due; once due it goes with the recipient's next push. A message whose live
/// push fails, or that arrives while the recipient's pushes are paused after one reached
/// <see cref="PushTimeout"/>, stays queued and is tried again when the recipient next receives a
/// message, so a recipient in live mode should still ask for its queued messages (a
/// <c>status-request</c> shows them) after a reconnect or a slow period.</description></item>
/// <item><description>Whether a forward has expired, when it is relayed and while it waits in the
/// queue, is judged with <c>DidCommOptions.ExpiresClockSkew</c>, never with the replay
/// <see cref="ReplayProtectionOptions.ClockSkew"/>.</description></item>
/// <item><description>By default the mediator pushes only over live sessions, and every other
/// message waits for pickup. With <see cref="DirectPush"/> on, a message for a recipient without a
/// live session is pushed, once, to the endpoint the host registered for that recipient in its
/// <see cref="IRecipientEndpointDirectory"/>; a push that is not answered with a 2xx leaves the
/// message for pickup. The host owns that directory: the mediator never sends to an address a
/// message carries, and a recipient's registered server can only move a push on with a temporary
/// (307) redirect, each hop checked by the outbound endpoint guard.</description></item>
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
    /// first is the primary one, and the only one a 2.0 grant carries), whether 2.0 is served too
    /// (<see cref="CoordinateMediationOptions.EnableV2"/>, on by default), the query page size, and
    /// the bounds of the in-memory grant store.
    /// </summary>
    public CoordinateMediationOptions CoordinateMediation { get; }

    /// <summary>
    /// Message Pickup settings (FR-PICK-*): whether 4.0 is served next to 3.0
    /// (<see cref="MessagePickupOptions.EnableV4"/>, on by default), batch size and packed delivery
    /// size, lease timeout, per-grant and store-wide quotas, and the mailbox age limit.
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
    /// How many push workers run: live pushes over a recipient's WebSocket connection, and, with
    /// <see cref="DirectPush"/> on, pushes to a recipient's registered endpoint. Default 4;
    /// from 1 to 64. Pushes run in the background, never on the forward pump, so a recipient that is
    /// slow to take a push (a DID that is slow to resolve, a connection that is slow to read) never
    /// holds up the storing of other recipients' messages. Recipients take turns, with at most one push in flight per mediation
    /// grant, so a slow recipient holds one of them at a time, never all. A push abandoned at
    /// <see cref="PushTimeout"/> frees its worker but counts until its call ends: at most twice this
    /// many recipients' pushes are unfinished at once, and while that many are, a new message starts
    /// no push and waits for pickup or its recipient's next message. A host collaborator (session,
    /// DID resolver or store) that never completes a cancelled call
    /// keeps its push unfinished, so twice this many such calls stop every push until one of them
    /// completes; pickup keeps working. A name lookup for a recipient's endpoint (<see cref="DirectPush"/>)
    /// is not counted among those: at the deadline the push stops waiting for it, settles, and pauses
    /// that recipient's direct pushes, while the lookup may run on until the system's resolver gives
    /// up. At most about this many × ceil(resolver
    /// timeout ÷ <see cref="PushTimeout"/>) such lookups run at once.
    /// </summary>
    public int PushConcurrency { get; set; } = 4;

    /// <summary>
    /// How long a push worker waits for one push before it gives up on it and serves other
    /// recipients; the message stays queued. Default 30 seconds; greater than zero and at most 5
    /// minutes. The abandoned call runs on until it ends, holding the message under its lease
    /// meanwhile (as any live delivery), and its recipient gets no other push before it ends. A recipient whose live push
    /// reaches the deadline has its pushes paused for twice as long, and one whose direct push fails
    /// (<see cref="DirectPush"/>: no answer, a 5xx, 408 or 429, a fault, the deadline, or an endpoint
    /// the address check refuses) has its direct pushes paused for twice as long, measured on the
    /// monotonic clock so a step of the wall clock does not change it (its messages wait meanwhile),
    /// so a recipient that stalls its own pushes cannot hold a push worker continuously. A direct push
    /// the endpoint refuses with any other 4xx does not pause: the next message is pushed at once. An
    /// endpoint that answers each push just before the deadline can therefore hold one worker for up
    /// to <see cref="MessagePickupOptions.MaxDeliveryBatch"/> × this per wake-up, as a slow but
    /// accepting endpoint always could.
    /// </summary>
    public TimeSpan PushTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the mediator pushes a stored message to the recipient's own endpoint when the
    /// recipient has no live session (step 3 of the delivery order, FR-PICK-09). Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What it does.</strong> Every relayed message is stored first. A push then looks for the
    /// recipient's live session, and when there is none, asks the host's
    /// <see cref="IRecipientEndpointDirectory"/> for the endpoint registered for the recipient's
    /// mediation grant and POSTs the stored message there, unchanged, as the HTTP transport sends any
    /// encrypted message. A 2xx answer delivers it, and the mediator removes it from the queue. A
    /// grant the directory has no endpoint for keeps its messages for pickup.
    /// </para>
    /// <para>
    /// <strong>Where the endpoint comes from.</strong> Only from the directory, which the operator
    /// fills (the DIDComm specification leaves this coordination between a recipient and its mediator
    /// out of its scope, routing.md "Mediator Process"); never from anything a message carries. It
    /// must be an absolute <c>http</c> or <c>https</c> URI that the HTTP transport accepts; anything
    /// else counts as no endpoint. Before a message is taken, the endpoint goes through the outbound
    /// endpoint check the host's own sends use (<c>DidCommOptions.OutboundEndpointPolicy</c>), with a
    /// host name looked up asynchronously, under <see cref="PushTimeout"/>. An endpoint it refuses (a
    /// private, loopback, link-local or metadata address, or a name that resolves to one or does not
    /// resolve) gets no POST, and that recipient's direct pushes pause as for a push that fails. The
    /// redirect hops a push follows are checked by the HTTP transport's own handler (when each
    /// connection opens, or through a <c>TrustedProxy</c> before each hop); on a host whose primary
    /// handler is its own, or whose connect callback does not check addresses, they are that
    /// handler's job, as for a transport the host builds itself (owner decision OD-5 on #110). On
    /// such a host — one whose own connection code (a connect callback that checks nothing, or a
    /// primary handler of its own) replaced the library's — the check before a push is a name check
    /// only: the name is looked up again at connect, and the push connects wherever that second
    /// answer points, so the host's connection code must check addresses at connect itself (owner
    /// decision OD-7 on #110). With <c>UseHttpTransport()</c>'s own handler every connection is
    /// checked when it opens.
    /// </para>
    /// <para>
    /// <strong>One attempt per message.</strong> A message is pushed at most once, live or direct:
    /// there is no retry. A temporary redirect (307) from the recipient's server is followed up to the
    /// HTTP transport's <c>MaxRedirectHops</c>, every hop checked by the outbound endpoint guard, and
    /// the whole chain is that one attempt; no other redirect is followed (owner decision OD-1 on
    /// #110). A push that is not answered with a 2xx at the end of the chain, or that fails, leaves the
    /// message queued for the recipient's next <c>delivery-request</c>: pickup is the later delivery
    /// (owner decision OD-2 on #110). The push has the same deadline as a live push
    /// (<see cref="PushTimeout"/>). Delivery is at-least-once: a recipient de-duplicates by message id.
    /// </para>
    /// <para>
    /// <strong>A push that does not deliver.</strong> Its message is never pushed again; the
    /// recipient's next <c>delivery-request</c> gets it. When the endpoint answered a 4xx other than
    /// 408 and 429 — its definite "not this message" — the next message goes on at once, with no
    /// pause, so messages anyone stored ahead of a real one cannot hold it back; a real message waits
    /// behind refused ones only when a worker's whole run of them delivers nothing (more than
    /// <see cref="MessagePickupOptions.MaxDeliveryBatch"/> in a row), and then until the next relayed
    /// message, or for pickup. Anything else that does not deliver — a 5xx, 408
    /// or 429, a redirect that is not followed, no answer, a timeout of the transport or the HTTP
    /// client, or the deadline — ends the worker's run and pauses that recipient's direct pushes for
    /// twice <see cref="PushTimeout"/>. Its live pushes go on: a recipient that connects live during
    /// the pause gets its messages over that session. So such a message never holds up later ones
    /// beyond the pause, and is never sent again: the messages behind it go at the first relayed
    /// message (or live-delivery change) after the pause, until the next push that fails — one run
    /// per pause, so under an endpoint that keeps failing, part of them go only by pickup.
    /// </para>
    /// <para>
    /// <strong>What it needs.</strong> <c>UseHttpTransport()</c> on the same builder (the push uses the
    /// mediator's own single-attempt instance of the HTTP transport, over the host's HTTP client
    /// settings and outbound endpoint policy, never the host's own transport) and an
    /// <see cref="IRecipientEndpointDirectory"/> registered before <c>AddMediator()</c>. Without
    /// either, or with an invalid <c>HttpTransportOptions.TrustedProxy</c>, the mediator refuses to
    /// start. Outside a generic host the same refusals come later: a missing directory when the push
    /// workers are first built (with the first relayed message), and the rest at each direct push,
    /// which then fails and leaves the message for pickup.
    /// </para>
    /// <para>
    /// <strong>With re-wrapping.</strong> With <see cref="RewrapMode.ReanoncryptToNext"/> the stored
    /// message is the mediator's own anoncrypted <c>forward</c> addressed to the recipient itself,
    /// and that is what is pushed. The recipient's endpoint opens it: the DIDComm specification
    /// expects receivers to (routing.md, "Rewrapping": "Receivers also need to know about it, because
    /// it causes them to receive a double-packaged message"), and the library's own
    /// <c>MapDidCommEndpoint</c> and <c>MapDidCommWebSocket</c> do (owner decision OD-3 on #110).
    /// </para>
    /// </remarks>
    public bool DirectPush { get; set; }

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
    /// The pipeline's own checks: the ranges of its settings, and the rule between the mailbox age
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
    /// The checks of the pipeline settings alone — their ranges — for a part that reads only those
    /// (the sub-objects of the registered <c>IOptions&lt;MediatorOptions&gt;</c> are not the roles'
    /// settings in effect).
    /// </summary>
    /// <exception cref="ArgumentException">A pipeline setting is out of range, or <see cref="RewrapMode"/> is not a defined value.</exception>
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
            PushConcurrency = pipeline.PushConcurrency,
            PushTimeout = pipeline.PushTimeout,
            DirectPush = pipeline.DirectPush,
        };
    }
}
