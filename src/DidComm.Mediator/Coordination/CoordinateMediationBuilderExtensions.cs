using DidComm.Extensions.DependencyInjection;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Coordination;

/// <summary>Registers the Coordinate Mediation mediator role (3.0, and 2.0 unless switched off) on a <see cref="DidCommBuilder"/>.</summary>
public static class CoordinateMediationBuilderExtensions
{
    /// <summary>
    /// Make this agent a Coordinate Mediation mediator (FR-MED-01, FR-MED-10): registers
    /// <see cref="CoordinateMediationHandler"/> (3.0) and, unless
    /// <see cref="CoordinateMediationOptions.EnableV2"/> is <c>false</c>,
    /// <see cref="CoordinateMediation20Handler"/> (2.0, for the wallets of the Identus and Blocktrust
    /// ecosystems); and — unless the host registered its own first —
    /// <see cref="InMemoryMediationStore"/> as <see cref="IMediationStore"/>,
    /// <see cref="DefaultMediationPolicy"/> as <see cref="IMediationPolicy"/> and an
    /// <see cref="IMediatorIdentity"/> over <see cref="CoordinateMediationOptions.RoutingDids"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both versions, one set of grants.</strong> The two handlers share the store, the
    /// policy and the identity, so a peer that enrolled through either version is one tenant, and
    /// Discover Features discloses both protocols with <c>roles: ["mediator"]</c>. A 2.0 grant
    /// carries the first routing DID only, so list the primary one first
    /// (<see cref="CoordinateMediationOptions.RoutingDids"/>). A host that brings its own 2.0 handler
    /// sets <see cref="CoordinateMediationOptions.EnableV2"/> to <c>false</c>, or two handlers claim
    /// the same protocol and the last registered wins.
    /// </para>
    /// <para>
    /// <strong>Forwards.</strong> The store also becomes the forward recipient directory
    /// (<see cref="IForwardRecipientRegistry"/>), so <c>AddRoutingMediator()</c> relays a forward
    /// exactly when its <c>next</c> is a registered recipient DID. For that, call this method
    /// <em>before</em> <c>AddRoutingMediator()</c>: the first directory registered wins. If one is
    /// already registered this method throws rather than leave the mediator silently dropping every
    /// forward for the recipients it enrols; a host that wants its own directory sets
    /// <see cref="CoordinateMediationOptions.UseAsForwardRecipientRegistry"/> to <c>false</c>. A
    /// directory registered <em>after</em> this call cannot be seen here; it is caught when the
    /// handler is first built (the first resolution of the protocol dispatcher), with the same
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// Calling the method again is harmless: registrations are not duplicated and
    /// <paramref name="configure"/> delegates run in order. A store, policy or identity
    /// implementation must not take the protocol dispatcher, the handler registry or a protocol
    /// handler in its constructor (that closes a dependency cycle; resolve them from
    /// <see cref="IServiceProvider"/> when called).
    /// </para>
    /// </remarks>
    /// <param name="builder">The DIDComm builder.</param>
    /// <param name="configure">Optional settings.</param>
    /// <exception cref="ArgumentException">The settings fail <see cref="CoordinateMediationOptions.Validate"/>.</exception>
    /// <exception cref="InvalidOperationException">Another <see cref="IForwardRecipientRegistry"/> is already registered and <see cref="CoordinateMediationOptions.UseAsForwardRecipientRegistry"/> is <c>true</c>. (Thrown later, when the handlers are first built: a directory registered after this call replaced the store, or <see cref="CoordinateMediationOptions.EnableV2"/> in the settings in effect disagrees with what the calls registered.)</exception>
    public static DidCommBuilder AddCoordinateMediation(this DidCommBuilder builder, Action<CoordinateMediationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // Run the delegate once on a scratch instance: bad settings fail here, at registration,
        // not on the first message; and the registration-time switch is read from it.
        var probe = new CoordinateMediationOptions();
        configure?.Invoke(probe);
        probe.Validate();

        var options = services.AddOptions<CoordinateMediationOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.TryAddSingleton<IMediationStore>(sp =>
            new InMemoryMediationStore(sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value));
        services.TryAddSingleton<IMediationPolicy, DefaultMediationPolicy>();
        services.TryAddSingleton<IMediatorIdentity>(sp =>
            new ConfiguredMediatorIdentity(sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value.RoutingDids));

        if (probe.UseAsForwardRecipientRegistry)
            UseStoreAsForwardRecipientRegistry(services);

        // The handler is registered here, ahead of AddProtocol (whose own registration is a TryAdd),
        // so that building it re-checks the directory: a registration made AFTER this call cannot be
        // seen now, and would otherwise win silently.
        services.TryAddSingleton(sp =>
        {
            VerifyForwardRecipientRegistry(services);
            VerifyVersion2Registration(services, sp);
            return new CoordinateMediationHandler(
                sp.GetRequiredService<IMediationStore>(),
                sp.GetRequiredService<IMediationPolicy>(),
                sp.GetRequiredService<IMediatorIdentity>(),
                sp.GetRequiredService<IOptions<CoordinateMediationOptions>>(),
                sp.GetService<ILogger<CoordinateMediationHandler>>());
        });
        builder.AddProtocol<CoordinateMediationHandler>();

        // Coordinate Mediation 2.0 (FR-MED-10): the same collaborators, so one registry serves both
        // versions. Decided from this call's settings, because it decides what is registered; the
        // settings in effect are compared with it when the handlers are built.
        if (probe.EnableV2)
        {
            services.TryAddSingleton(sp =>
            {
                VerifyForwardRecipientRegistry(services);
                VerifyVersion2Registration(services, sp);
                return new CoordinateMediation20Handler(
                    sp.GetRequiredService<IMediationStore>(),
                    sp.GetRequiredService<IMediationPolicy>(),
                    sp.GetRequiredService<IMediatorIdentity>(),
                    sp.GetRequiredService<IOptions<CoordinateMediationOptions>>(),
                    sp.GetService<ILogger<CoordinateMediation20Handler>>());
            });
            builder.AddProtocol<CoordinateMediation20Handler>();
        }

        return builder;
    }

    // EnableV2 is read when AddCoordinateMediation() runs, so a value that arrives another way
    // (Configure<CoordinateMediationOptions>, configuration binding, two calls that disagree) cannot
    // change what was registered. Rather than serve, or silently not serve, a protocol the settings
    // say otherwise about, building either handler fails. Looks at registrations and the options
    // only; no handler is resolved.
    private static void VerifyVersion2Registration(IServiceCollection services, IServiceProvider sp)
    {
        var registered = false;
        foreach (var descriptor in services)
            registered |= descriptor.ServiceType == typeof(CoordinateMediation20Handler) && !descriptor.IsKeyedService;

        var enabled = sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value.EnableV2;
        if (registered && !enabled)
        {
            throw new InvalidOperationException(
                "CoordinateMediationOptions.EnableV2 is false in the settings in effect, but the Coordinate Mediation 2.0 role is registered " +
                "(by an AddCoordinateMediation() or AddMediator() call, which reads the setting when it runs, or by " +
                "AddProtocol<CoordinateMediation20Handler>()). To serve 3.0 only, set EnableV2 = false in the delegate of every " +
                "AddCoordinateMediation() and AddMediator() call, and do not register the 2.0 handler yourself.");
        }
        if (!registered && enabled)
        {
            throw new InvalidOperationException(
                "CoordinateMediationOptions.EnableV2 is true in the settings in effect, but no AddCoordinateMediation() or AddMediator() " +
                "call registered the Coordinate Mediation 2.0 role: the setting is read when that call runs, and a call set it to false. " +
                "Set EnableV2 the same way in the delegate of every AddCoordinateMediation() and AddMediator() call.");
        }
    }

    // Looks at registrations only; nothing is resolved, so it cannot close a dependency cycle.
    // If this method made the store the directory, the directory the container will hand out (the
    // last registration) must still be that one.
    private static void VerifyForwardRecipientRegistry(IServiceCollection services)
    {
        var ours = false;
        ServiceDescriptor? last = null;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(IForwardRecipientRegistry) || descriptor.IsKeyedService)
                continue;
            last = descriptor;
            ours |= descriptor.ImplementationType == typeof(MediationForwardRecipientRegistry);
        }

        if (ours && last!.ImplementationType != typeof(MediationForwardRecipientRegistry))
        {
            throw new InvalidOperationException(
                "An IForwardRecipientRegistry was registered after AddCoordinateMediation() and replaces the mediation store as the forward " +
                "recipient directory, so forwards for recipients enrolled through Coordinate Mediation would be dropped. Remove that " +
                "registration; or, to keep your own directory, set CoordinateMediationOptions.UseAsForwardRecipientRegistry = false on every " +
                "AddCoordinateMediation() call and have it consult IMediationStore.FindGrantByRecipientDidAsync.");
        }
    }

    private static void UseStoreAsForwardRecipientRegistry(IServiceCollection services)
    {
        ServiceDescriptor? existing = null;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IForwardRecipientRegistry) && !descriptor.IsKeyedService)
                existing = descriptor; // the last one is the one the container resolves
        }

        if (existing is null)
        {
            // Registered by implementation type so a repeated call recognises it below.
            services.AddSingleton<IForwardRecipientRegistry, MediationForwardRecipientRegistry>();
            return;
        }
        if (existing.ImplementationType == typeof(MediationForwardRecipientRegistry))
            return;

        throw new InvalidOperationException(
            "An IForwardRecipientRegistry is already registered, so forwards for recipients enrolled through Coordinate Mediation " +
            "would be dropped. Call AddCoordinateMediation() before AddRoutingMediator(); or, to keep your own directory, set " +
            "CoordinateMediationOptions.UseAsForwardRecipientRegistry = false and have it consult IMediationStore.FindGrantByRecipientDidAsync.");
    }
}
