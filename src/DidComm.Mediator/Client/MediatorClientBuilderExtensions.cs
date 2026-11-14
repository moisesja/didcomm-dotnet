using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client.Internal;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Client;

/// <summary>Registers the recipient-side mediator clients on a <see cref="DidCommBuilder"/>.</summary>
public static class MediatorClientBuilderExtensions
{
    /// <summary>
    /// Register <see cref="MediationClient"/> and <see cref="PickupClient"/> (FR-MED-09, FR-PICK-11)
    /// as singletons that share one channel, which matches each mediator answer to the request it
    /// answers. No ASP.NET Core is needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Transports.</strong> Nothing is added for you. Over HTTP register
    /// <c>UseHttpTransport()</c>: the answer comes back in the response. Over a WebSocket register
    /// <c>UseWebSocketTransport(o =&gt; o.ReceiveInbound = true)</c> and <c>UseInboundDispatch()</c>:
    /// the answers and live pushes arrive on the connection, and the inbound dispatcher hands them to
    /// the clients. Without both, a WebSocket route is refused before anything is sent (so is one
    /// under <c>UseStomp</c>, which reads nothing back). With a transport or
    /// <c>ITransportRouter</c> of your own the clients cannot tell whether answers come back, and
    /// they send.
    /// </para>
    /// <para>
    /// <strong>Settings.</strong> <paramref name="configure"/> may run several times, on different
    /// instances (once here to check the settings, once when the container builds them), so it must
    /// only assign values. Calling this method again is harmless: nothing is registered twice and
    /// the delegates compose in order.
    /// </para>
    /// <para>
    /// <strong>With a mediator in the same host.</strong> It composes with <c>AddMediator()</c>: the
    /// mediator's handlers ignore the answers this client receives, and the clients ignore traffic
    /// that is not an answer to their own requests.
    /// </para>
    /// <para>
    /// Take <see cref="MediationClient"/> or <see cref="PickupClient"/> in a protocol handler's
    /// constructor if you need to, but never call them from a handler running for a message received
    /// on the same WebSocket connection (see the client remarks).
    /// </para>
    /// </remarks>
    /// <param name="builder">The DIDComm builder.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its range (<see cref="MediatorClientOptions.Validate"/>).</exception>
    /// <example>
    /// <code>
    /// services.AddDidComm(b =&gt; b
    ///     .UseNetDidResolver()
    ///     .UseSecretsResolver(secrets)
    ///     .UseWebSocketTransport(o =&gt; o.ReceiveInbound = true)
    ///     .UseInboundDispatch()
    ///     .AddMediatorClient());
    /// </code>
    /// </example>
    public static DidCommBuilder AddMediatorClient(this DidCommBuilder builder, Action<MediatorClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // Run the delegate once on a scratch instance: bad settings fail here, at registration.
        var probe = new MediatorClientOptions();
        configure?.Invoke(probe);
        probe.Validate();

        var options = services.AddOptions<MediatorClientOptions>();
        if (configure is not null)
            options.Configure(configure);

        // One channel per container. Its factory resolves the facade and reads registrations only
        // (IServiceProviderIsService), never the dispatcher, the registry or a handler (L-017): the
        // dispatcher's factory resolves the correlators, and so this channel.
        services.TryAddSingleton(sp => new MediatorClientChannel(
            sp.GetRequiredService<DidCommClient>(),
            sp.GetRequiredService<IOptions<MediatorClientOptions>>().Value,
            receivesInbound: sp.GetService<IServiceProviderIsService>()?.IsService(typeof(IInboundFrameConsumer)) == true,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILoggerFactory>()));
        // The same instance as an inline correlator (the Discover Features client's pattern);
        // TryAddEnumerable keeps a repeated call from registering it twice.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInboundCorrelator, MediatorClientChannel>(sp => sp.GetRequiredService<MediatorClientChannel>()));
        services.TryAddSingleton(sp => new MediationClient(
            sp.GetRequiredService<MediatorClientChannel>(),
            sp.GetService<ILogger<MediationClient>>()));
        services.TryAddSingleton(sp => new PickupClient(
            sp.GetRequiredService<MediatorClientChannel>(),
            sp.GetService<ILogger<PickupClient>>()));
        return builder;
    }
}
