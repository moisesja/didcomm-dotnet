using DidComm.Extensions.DependencyInjection;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Coordination;

/// <summary>Registers the Coordinate Mediation 3.0 mediator role on a <see cref="DidCommBuilder"/>.</summary>
public static class CoordinateMediationBuilderExtensions
{
    /// <summary>
    /// Make this agent a Coordinate Mediation 3.0 mediator (FR-MED-01): registers
    /// <see cref="CoordinateMediationHandler"/>, and — unless the host registered its own first —
    /// <see cref="InMemoryMediationStore"/> as <see cref="IMediationStore"/>,
    /// <see cref="DefaultMediationPolicy"/> as <see cref="IMediationPolicy"/> and an
    /// <see cref="IMediatorIdentity"/> over <see cref="CoordinateMediationOptions.RoutingDids"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Forwards.</strong> The store also becomes the forward recipient directory
    /// (<see cref="IForwardRecipientRegistry"/>), so <c>AddRoutingMediator()</c> relays a forward
    /// exactly when its <c>next</c> is a registered recipient DID. For that, call this method
    /// <em>before</em> <c>AddRoutingMediator()</c>: the first directory registered wins. If one is
    /// already registered this method throws rather than leave the mediator silently dropping every
    /// forward for the recipients it enrols; a host that wants its own directory sets
    /// <see cref="CoordinateMediationOptions.UseAsForwardRecipientRegistry"/> to <c>false</c>.
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
    /// <exception cref="InvalidOperationException">Another <see cref="IForwardRecipientRegistry"/> is already registered and <see cref="CoordinateMediationOptions.UseAsForwardRecipientRegistry"/> is <c>true</c>.</exception>
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

        return builder.AddProtocol<CoordinateMediationHandler>();
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
