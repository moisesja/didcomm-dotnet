using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pickup;

/// <summary>Registers the Message Pickup 3.0 mediator role on a <see cref="DidCommBuilder"/>.</summary>
public static class MessagePickupBuilderExtensions
{
    /// <summary>
    /// Make this agent a Message Pickup 3.0 mediator (FR-PICK-01): registers
    /// <see cref="MessagePickupHandler"/> and <see cref="MessagePickupQueue"/>, and — unless the
    /// host registered its own first — <see cref="InMemoryMessageStore"/> as
    /// <see cref="IMessageStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pickup serves the recipients enrolled through Coordinate Mediation, so it needs an
    /// <see cref="IMediationStore"/>: call <c>AddCoordinateMediation()</c> as well (before or after
    /// this method), or register your own store. Without one, the first resolution of
    /// <see cref="MessagePickupQueue"/> (the first resolution of the protocol dispatcher) throws
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// Messages enter the queue through <see cref="MessagePickupQueue.DeliverAsync"/>, which a
    /// delivery pipeline (a forward sink) calls for each message addressed to a registered
    /// recipient. Calling this method again is harmless: registrations are not duplicated and
    /// <paramref name="configure"/> delegates run in order. An <see cref="IMessageStore"/>
    /// implementation must not take the protocol dispatcher, the handler registry, a protocol
    /// handler or <c>DidCommClient</c> in its constructor (that closes a dependency cycle).
    /// </para>
    /// </remarks>
    /// <param name="builder">The DIDComm builder.</param>
    /// <param name="configure">Optional settings.</param>
    /// <exception cref="ArgumentException">The settings fail <see cref="MessagePickupOptions.Validate"/>.</exception>
    public static DidCommBuilder AddMessagePickup(this DidCommBuilder builder, Action<MessagePickupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // Run the delegate once on a scratch instance: bad settings fail here, at registration,
        // not on the first message.
        var probe = new MessagePickupOptions();
        configure?.Invoke(probe);
        probe.Validate();

        var options = services.AddOptions<MessagePickupOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.TryAddSingleton<IMessageStore>(sp =>
            new InMemoryMessageStore(sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value));

        services.TryAddSingleton(sp => new MessagePickupQueue(
            sp.GetRequiredService<IMessageStore>(),
            sp.GetService<IMediationStore>() ?? throw new InvalidOperationException(
                "AddMessagePickup() needs an IMediationStore: the pickup queue serves the recipients enrolled through Coordinate Mediation. " +
                "Call AddCoordinateMediation() on the same builder, or register your own IMediationStore."),
            sp.GetRequiredService<IReturnRouteSessionRegistry>(),
            sp.GetRequiredService<IOptions<MessagePickupOptions>>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>(),
            sp.GetService<IMessageIdGenerator>(),
            sp.GetService<ILogger<MessagePickupQueue>>()));

        // Registered here, ahead of AddProtocol (whose own registration is a TryAdd), so the
        // optional logger is resolved the same way as everywhere else.
        services.TryAddSingleton(sp => new MessagePickupHandler(
            sp.GetRequiredService<MessagePickupQueue>(),
            sp.GetService<ILogger<MessagePickupHandler>>()));

        return builder.AddProtocol<MessagePickupHandler>();
    }
}
