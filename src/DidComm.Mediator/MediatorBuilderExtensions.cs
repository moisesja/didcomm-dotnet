using System.Diagnostics.Metrics;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator;

/// <summary>Registers a complete DIDComm mediator on a <see cref="DidCommBuilder"/>.</summary>
public static class MediatorBuilderExtensions
{
    // The factories of the registrations this method recognises as its own on a repeated call, so a
    // registration made by anyone else (an earlier AddRoutingMediator(), a host's own collaborator)
    // is told apart from ours by reference, not by guessing from the type.
    private static readonly Func<IServiceProvider, object> s_forwardProcessorOptions = static sp =>
    {
        var mediator = sp.GetRequiredService<IOptions<MediatorOptions>>().Value;
        var options = new ForwardProcessorOptions(mediator.RewrapMode) { MaxDelay = mediator.MaxForwardDelay };
        options.Validate();
        return options;
    };

    private static readonly Func<IServiceProvider, object> s_noRecipientRoutingKeys =
        static _ => new NoRecipientRoutingKeys();

    private static readonly Func<IServiceProvider, object> s_forwardSink =
        static sp => sp.GetRequiredService<MediatorForwardSink>();

    /// <summary>
    /// Make this agent a complete mediator (FR-MED-07, FR-PICK-09/10): Coordinate Mediation 3.0,
    /// the routing mediator that relays forwards, Message Pickup 3.0, the built-in protocols (Trust
    /// Ping answers health checks; Discover Features discloses the mediator roles), and the delivery
    /// pipeline that puts each relayed message in the recipient's queue (pushing it live, or
    /// directly to the recipient's endpoint, when it can). Map its endpoints with
    /// <c>MapDidCommMediator()</c> from the <c>DidComm.Mediator.AspNetCore</c> package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Routing DIDs.</strong> The mediator needs at least one routing DID, set in
    /// <see cref="MediatorOptions.CoordinateMediation"/> or supplied by an
    /// <see cref="IMediatorIdentity"/> registered before this call (<see cref="MediatorIdentity"/>
    /// is a ready-made one). The first is the primary one: grants list it first and the mediator's
    /// out-of-band invitation is issued from it. Each routing DID's document must have a
    /// <c>DIDCommMessaging</c> service whose first entry names a plain transport URI
    /// (<c>http</c>, <c>https</c>, <c>ws</c> or <c>wss</c>, never a DID), and at least one
    /// <c>keyAgreement</c> key whose private key the registered secrets resolver holds; the
    /// mediator checks this when it starts and refuses to start otherwise.
    /// </para>
    /// <para>
    /// <strong>Settings.</strong> <paramref name="configure"/> may run several times, on
    /// different instances, so it must only assign values (see <see cref="MediatorOptions"/>).
    /// Calling this method again is harmless: registrations are not duplicated and the delegates
    /// compose in order.
    /// </para>
    /// <para>
    /// <strong>Register collaborators first.</strong> Every registration here is a <c>TryAdd</c>:
    /// an <see cref="IMediationStore"/>, <see cref="IMessageStore"/>, <see cref="IMediationPolicy"/>,
    /// <see cref="IMediatorIdentity"/>, <see cref="IRecipientEndpointDirectory"/> or
    /// <see cref="IRecipientRoutingKeyProvider"/> of the host's own must be registered before this
    /// call to take effect; a replay guard or session registry of its own before <c>AddDidComm</c>.
    /// None of them may take the protocol dispatcher, the handler registry, a protocol handler or
    /// <c>DidCommClient</c> in its constructor (resolve them from <see cref="IServiceProvider"/>
    /// when called). This method configures the routing mediator itself: do not call
    /// <c>AddRoutingMediator()</c> as well, and do not register an <see cref="IForwardSink"/>.
    /// </para>
    /// <para>
    /// <strong>Start-up.</strong> In a generic host (<c>WebApplication</c>, <c>Host</c>) a hosted
    /// service checks the routing DIDs and every setting before the server starts listening —
    /// including the forward settings in effect, so a <c>ForwardProcessorOptions</c> or a routing-key
    /// provider registered after this call that would lift the delay cap, change the re-wrap mode or
    /// be ignored by re-wrapping, or a forward recipient directory left at the routing mediator's
    /// empty default, fails start-up — builds the out-of-band invitation, and removes expired and
    /// aged-out messages every
    /// <see cref="MediatorOptions.PurgeInterval"/>. Outside a generic host nothing starts: each
    /// part checks its own settings when it is first used, but the routing DIDs are not checked
    /// against their DID documents, no invitation is built (the invitation endpoint answers
    /// <c>404</c>), and nothing is purged unless the host calls
    /// <see cref="MessagePickupQueue.PurgeExpiredAsync"/> itself.
    /// </para>
    /// <para>
    /// <strong>Operating it.</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Put rate limiting or admission control in front of the endpoint, for
    /// example <c>app.MapDidCommMediator("/didcomm").RequireRateLimiting("mediator")</c>. Anyone can
    /// post forwards, and a flood of messages from freshly made DIDs fills the replay protection
    /// budget, which then refuses in-scope messages for everyone for up to
    /// <c>Window + 2 × ClockSkew</c>.</description></item>
    /// <item><description>Enrolment is open by default and grants never expire: anyone who can
    /// authcrypt a <c>mediate-request</c> gets one, and self-made grants can fill the grant store
    /// and the store-wide queue bounds. Register an <see cref="IMediationPolicy"/> that allows only
    /// known peers or rate-limits enrolment, and end a peer's mediation with
    /// <see cref="MessagePickupQueue.RevokeAndPurgeAsync"/>.</description></item>
    /// <item><description>Size <see cref="ReplayProtectionOptions.MaxEntries"/> to the accepted rate
    /// times (<see cref="ReplayProtectionOptions.Window"/> +
    /// <see cref="ReplayProtectionOptions.ClockSkew"/>), about 91 messages a second at the
    /// defaults.</description></item>
    /// <item><description>A host clock that steps back by more than <c>Window + ClockSkew</c>
    /// (6 minutes at the defaults) makes the mediator refuse in-scope traffic until the clock
    /// catches up or the process restarts; the refusals are logged.</description></item>
    /// <item><description>A live session whose peer stops reading holds up to
    /// <see cref="ReturnRouteSessionOptions.MaxPendingPushes"/> packed deliveries, each at most
    /// <see cref="MessagePickupOptions.MaxDeliveryBytes"/>.</description></item>
    /// <item><description>Every relayed message is stored first; live and direct pushes follow on
    /// background workers (<see cref="MediatorOptions.PushConcurrency"/>), one at a time per
    /// recipient and in order, so a recipient that is slow to take a push holds up no one else. Every
    /// message stored while its recipient is live gets a push; one that cannot be pushed stays queued
    /// for pickup.</description></item>
    /// <item><description>Expiry of relayed and queued messages is judged with
    /// <c>DidCommOptions.ExpiresClockSkew</c>, not with the replay clock skew.</description></item>
    /// <item><description>Direct push makes one attempt per message, one POST to exactly the
    /// directory's endpoint with no retry and no redirect followed: a recipient that relies on it
    /// must also pick up. A plain <c>http</c> push endpoint needs <c>"http"</c> in
    /// <c>HttpTransportOptions.AllowedSchemes</c>. Forwards are outside replay protection, so with
    /// direct push on each replay of a captured forward is pushed again; recipients should drop
    /// messages whose id they have already seen.</description></item>
    /// <item><description>Push endpoints can be secrets (capability URLs). The library's own log lines
    /// never carry them, but <c>IHttpClientFactory</c>'s request logging writes the full URI at
    /// Information: filter the <c>System.Net.Http.HttpClient.*</c> categories. A
    /// <c>TransportException</c>'s message names the endpoint; log it by type.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="builder">The DIDComm builder.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A setting is invalid (<see cref="MediatorOptions.Validate"/>; every invalid setting is reported this way, including the replay protection and session settings); no routing DID is configured and nothing else can supply one (no registered <see cref="IMediatorIdentity"/> and no other configuration of <see cref="CoordinateMediationOptions"/>); or <see cref="MediatorOptions.RewrapMode"/> is <see cref="RewrapMode.ReanoncryptToNext"/> while the host registered an <see cref="IRecipientRoutingKeyProvider"/>, whose routing keys would then be ignored.</exception>
    /// <exception cref="InvalidOperationException"><c>AddRoutingMediator()</c> was already called on this collection, an <see cref="IForwardSink"/> is already registered, or another registration conflicts with the mediator's (see <c>AddCoordinateMediation()</c>).</exception>
    public static DidCommBuilder AddMediator(this DidCommBuilder builder, Action<MediatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // Run the delegate once on a scratch instance: bad settings fail here, at registration, as
        // ArgumentException whatever the role's own exception type.
        var probe = new MediatorOptions();
        configure?.Invoke(probe);
        try
        {
            probe.Validate();
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message, nameof(configure), ex);
        }

        // Conflicts, before anything is added: a refused call leaves the collection as it was.
        RejectEarlierRoutingMediator(services);
        RejectForeignSink(services);
        RejectRewrapWithRoutingKeyProvider(services, probe);
        RequireRoutingDids(services, probe);

        services.TryAddSingleton<MediatorMarker>();

        // One delegate reaches every role's options through a view whose slot for that role is the
        // instance being configured (JC-8): it assigns only what the host set, so it composes with
        // the host's own Configure<T> calls by registration order.
        var mediatorOptions = services.AddOptions<MediatorOptions>();
        if (configure is not null)
        {
            mediatorOptions.Configure(configure);
            services.AddOptions<ReplayProtectionOptions>().Configure(replay => configure(MediatorOptions.Projecting(replay: replay)));
            services.AddOptions<ReturnRouteSessionOptions>().Configure(sessions => configure(MediatorOptions.Projecting(sessions: sessions)));
        }

        // Before AddCoordinateMediation, whose own identity registration is a TryAdd this pre-empts.
        services.TryAddSingleton<IMediatorIdentity>(sp =>
            new MediatorIdentity(sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value.RoutingDids));

        // Coordinate Mediation before the routing mediator: it makes the grant store the forward
        // recipient directory, and the reverse order throws.
        builder.AddCoordinateMediation(configure is null ? null : cm => configure(MediatorOptions.Projecting(cm: cm)));
        builder.AddMessagePickup(configure is null ? null : pickup => configure(MediatorOptions.Projecting(pickup: pickup)));

        // The routing mediator's settings come from IOptions<MediatorOptions>, so repeated calls and
        // later delegates compose; registered ahead of AddRoutingMediator, whose own are TryAdds.
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ForwardProcessorOptions), s_forwardProcessorOptions));
        services.TryAdd(ServiceDescriptor.Singleton(typeof(IRecipientRoutingKeyProvider), s_noRecipientRoutingKeys));
        services.TryAddSingleton(sp => new ForwardMetrics(sp.GetService<IMeterFactory>()));
        services.TryAddSingleton(sp => new MediatorMetrics(sp.GetService<IMeterFactory>()));

        // The pipeline's parts take nothing that leads back to the dispatcher, the handlers or the
        // facade (L-017): the push workers resolve the directory and DidCommClient on first use.
        services.TryAddSingleton(BuildPushDispatcher);
        services.TryAddSingleton(sp => new MediatorForwardSink(
            sp.GetRequiredService<MessagePickupQueue>(),
            sp.GetRequiredService<PushDispatcher>(),
            sp.GetRequiredService<ForwardMetrics>(),
            sp.GetRequiredService<IOptions<MediatorOptions>>(),
            sp.GetService<ILogger<MediatorForwardSink>>()));
        services.TryAdd(ServiceDescriptor.Singleton(typeof(IForwardSink), s_forwardSink));
        services.TryAddSingleton<MediatorInvitationHolder>();

        // Validates the settings this call can see; its TryAdds of the options and the defaults
        // registered above are no-ops.
        builder.AddRoutingMediator(new ForwardProcessorOptions(probe.RewrapMode) { MaxDelay = probe.MaxForwardDelay });

        // Trust Ping for health checks, Discover Features for the role disclosures (idempotent).
        builder.AddBuiltInProtocols();

        // The start-up checks and the purge schedule; it resolves everything when the host starts it.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MediatorHostedService>(sp => new MediatorHostedService(
            sp,
            sp.GetRequiredService<IOptions<MediatorOptions>>(),
            sp.GetService<ILogger<MediatorHostedService>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System)));
        return builder;
    }

    [ThreadStatic]
    private static bool t_buildingPushDispatcher;

    // The dispatcher is built from the stores; a store that asks for the dispatcher (or the sink)
    // back would re-enter this factory without end. Fail on the first re-entry instead.
    private static PushDispatcher BuildPushDispatcher(IServiceProvider sp)
    {
        if (t_buildingPushDispatcher)
        {
            throw new InvalidOperationException(
                "AddMediator() found a circular dependency: an IMessageStore or IMediationStore (or something they depend on) takes the mediator's " +
                "forward sink or push dispatcher in its constructor, and the push dispatcher is built from those stores. Resolve it from " +
                "IServiceProvider when it is needed instead.");
        }
        t_buildingPushDispatcher = true;
        try
        {
            return new PushDispatcher(
                sp,
                sp.GetRequiredService<IOptions<MediatorOptions>>(),
                sp.GetRequiredService<MessagePickupQueue>(),
                sp.GetRequiredService<IMessageStore>(),
                sp.GetRequiredService<IMediationStore>(),
                sp.GetRequiredService<IOptions<DidCommOptions>>(),
                sp.GetRequiredService<MediatorMetrics>(),
                sp.GetService<ILogger<PushDispatcher>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System);
        }
        finally
        {
            t_buildingPushDispatcher = false;
        }
    }

    // An AddRoutingMediator() made before this call registered the forward options (or the handler)
    // with its own settings, which would win over the mediator's: refuse it rather than run a relay
    // with settings the host did not give the mediator.
    private static void RejectEarlierRoutingMediator(IServiceCollection services)
    {
        var ours = false;
        var routing = false;
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService)
                continue;
            if (descriptor.ServiceType == typeof(ForwardProcessorOptions))
            {
                if (ReferenceEquals(descriptor.ImplementationFactory, s_forwardProcessorOptions))
                    ours = true;
                else
                    routing = true;
            }
            else if (descriptor.ServiceType == typeof(ForwardHandler) || descriptor.ServiceType == typeof(ForwardProcessor))
            {
                routing = true;
            }
        }

        if (routing && !ours)
        {
            throw new InvalidOperationException(
                "AddRoutingMediator() was called before AddMediator(), which configures the routing mediator itself (the forward settings come from " +
                "MediatorOptions.RewrapMode and MaxForwardDelay). Remove the AddRoutingMediator() call; register an IRecipientRoutingKeyProvider " +
                "before AddMediator() for per-recipient routing keys.");
        }
    }

    // The sink is the pipeline: a host's own sink would take every forward away from the queue
    // without a word. One registered after this call is caught when the mediator starts.
    private static void RejectForeignSink(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(IForwardSink) || descriptor.IsKeyedService)
                continue;
            if (ReferenceEquals(descriptor.ImplementationFactory, s_forwardSink))
                continue;
            throw new InvalidOperationException(
                "An IForwardSink is already registered, and AddMediator() relays every forward through its own sink (the pickup queue, live " +
                "delivery and direct push). Remove that registration; to push messages to recipients' own endpoints, register an " +
                "IRecipientEndpointDirectory and set MediatorOptions.DirectPush = true.");
        }
    }

    // Re-wrapping never consults the per-recipient routing keys (FR-ROUTE-05/06 are one
    // transformation per hop), so a host provider would be ignored without a word.
    private static void RejectRewrapWithRoutingKeyProvider(IServiceCollection services, MediatorOptions probe)
    {
        if (probe.RewrapMode != RewrapMode.ReanoncryptToNext)
            return;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(IRecipientRoutingKeyProvider) || descriptor.IsKeyedService)
                continue;
            if (ReferenceEquals(descriptor.ImplementationFactory, s_noRecipientRoutingKeys))
                continue;
            throw new ArgumentException(
                "MediatorOptions.RewrapMode = ReanoncryptToNext cannot be combined with an IRecipientRoutingKeyProvider: re-wrapping never adds the " +
                "recipient's extra routing keys, so they would be ignored. Choose one: remove the provider, or keep RewrapMode.PassThrough.",
                nameof(MediatorOptions.RewrapMode));
        }
    }

    // At least one routing DID (FR-MED-07): without one a grant would carry whichever mediator DID
    // the request happened to be addressed to, unchecked. Refused here when nothing else can supply
    // one; otherwise the check is left to the first resolution and to start-up, which see the
    // values in effect.
    private static void RequireRoutingDids(IServiceCollection services, MediatorOptions probe)
    {
        if (probe.CoordinateMediation.RoutingDids.Count > 0)
            return;
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService)
                continue;
            if (descriptor.ServiceType == typeof(IMediatorIdentity)
                || descriptor.ServiceType == typeof(IConfigureOptions<CoordinateMediationOptions>)
                || descriptor.ServiceType == typeof(IPostConfigureOptions<CoordinateMediationOptions>))
            {
                return;
            }
        }

        throw new ArgumentException(
            "A mediator needs at least one routing DID (FR-MED-07): add one to MediatorOptions.CoordinateMediation.RoutingDids, or register an " +
            "IMediatorIdentity before AddMediator().",
            nameof(MediatorOptions.CoordinateMediation));
    }
}
