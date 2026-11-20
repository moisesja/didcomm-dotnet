using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pickup;

/// <summary>Registers the Message Pickup mediator role (3.0, and 4.0 unless switched off) on a <see cref="DidCommBuilder"/>.</summary>
public static class MessagePickupBuilderExtensions
{
    /// <summary>
    /// Make this agent a Message Pickup mediator (FR-PICK-01, FR-PICK-12): registers
    /// <see cref="MessagePickupHandler"/> (3.0) and, unless
    /// <see cref="MessagePickupOptions.EnableV4"/> is <c>false</c>,
    /// <see cref="MessagePickup40Handler"/> (4.0); <see cref="MessagePickupQueue"/>; and — unless
    /// the host registered its own first — <see cref="InMemoryMessageStore"/> as
    /// <see cref="IMessageStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both versions, one mailbox.</strong> The two handlers serve the same queue, so a
    /// recipient's messages are held once whichever version it picks them up with, and Discover
    /// Features discloses both protocols with <c>roles: ["mediator"]</c>. A host that brings its own
    /// 4.0 handler sets <see cref="MessagePickupOptions.EnableV4"/> to <c>false</c>, or two handlers
    /// claim the same protocol and the last registered wins. To serve 4.0 only, do not call this
    /// method: register an <see cref="IMessageStore"/>, <see cref="MessagePickupQueue"/> and
    /// <c>AddProtocol&lt;MessagePickup40Handler&gt;()</c> yourself.
    /// </para>
    /// <para>
    /// Pickup serves the recipients enrolled through Coordinate Mediation, so it needs an
    /// <see cref="IMediationStore"/>: call <c>AddCoordinateMediation()</c> as well (before or after
    /// this method), or register your own store. Without one, the first resolution of
    /// <see cref="MessagePickupQueue"/> (the first resolution of the protocol dispatcher) throws
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// Messages enter the queue through <see cref="MessagePickupQueue.DeliverAsync"/>, which the
    /// mediator's forward sink (registered by <c>AddMediator()</c>) calls for each forward addressed
    /// to a registered recipient, and which a host can call itself. Replies and deliveries travel on the return-route session of the request, so the
    /// receive endpoints must offer one: the HTTP endpoint does for every authenticated
    /// <c>return_route: all</c> request; the WebSocket endpoint only when its
    /// <c>AllowSameSocketReplies</c> option is on — without it pickup over a WebSocket stays silent
    /// and live delivery cannot be turned on. Calling this method again is harmless: registrations are not duplicated and
    /// <paramref name="configure"/> delegates run in order. An <see cref="IMessageStore"/>
    /// implementation must not take the protocol dispatcher, the handler registry, a protocol
    /// handler, <c>DidCommClient</c> or <see cref="MessagePickupQueue"/> in its constructor (that
    /// closes a dependency cycle; for the queue the first resolution then fails with
    /// <see cref="InvalidOperationException"/>).
    /// </para>
    /// </remarks>
    /// <param name="builder">The DIDComm builder.</param>
    /// <param name="configure">Optional settings.</param>
    /// <exception cref="ArgumentException">The settings fail <see cref="MessagePickupOptions.Validate"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown later, when the handlers are first built: <see cref="MessagePickupOptions.EnableV4"/> in the settings in effect disagrees with what the calls registered.</exception>
    public static DidCommBuilder AddMessagePickup(this DidCommBuilder builder, Action<MessagePickupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // Run the delegate once on a scratch instance: bad settings fail here, at registration,
        // not on the first message; and the registration-time switch is read from it.
        var probe = new MessagePickupOptions();
        configure?.Invoke(probe);
        probe.Validate();

        var options = services.AddOptions<MessagePickupOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.TryAddSingleton<IMessageStore>(sp =>
            new InMemoryMessageStore(sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value));

        services.TryAddSingleton(sp =>
        {
            // The queue is built from the stores; a store that asks for the queue back would
            // re-enter this factory without end. Fail on the first re-entry instead.
            if (t_buildingQueue)
            {
                throw new InvalidOperationException(
                    "AddMessagePickup() found a circular dependency: an IMessageStore or IMediationStore (or something they depend on) takes MessagePickupQueue in its constructor, " +
                    "and the queue is built from those stores. Resolve the queue from IServiceProvider when it is needed instead.");
            }
            t_buildingQueue = true;
            try
            {
                return BuildQueue(sp);
            }
            finally
            {
                t_buildingQueue = false;
            }
        });

        // Registered here, ahead of AddProtocol (whose own registration is a TryAdd), so the
        // optional logger is resolved the same way as everywhere else, and building the handler
        // checks the 4.0 switch against what was registered.
        services.TryAddSingleton(sp =>
        {
            VerifyVersion4Registration(services, sp);
            return new MessagePickupHandler(
                sp.GetRequiredService<MessagePickupQueue>(),
                sp.GetService<ILogger<MessagePickupHandler>>());
        });
        builder.AddProtocol<MessagePickupHandler>();

        // Message Pickup 4.0 (FR-PICK-12): the same queue, so one mailbox serves both versions.
        // Decided from this call's settings, because it decides what is registered; the settings
        // in effect are compared with it when the handlers are built.
        if (probe.EnableV4)
        {
            services.TryAddSingleton(sp =>
            {
                VerifyVersion4Registration(services, sp);
                return new MessagePickup40Handler(
                    sp.GetRequiredService<MessagePickupQueue>(),
                    sp.GetService<ILogger<MessagePickup40Handler>>());
            });
            builder.AddProtocol<MessagePickup40Handler>();
        }

        return builder;
    }

    // EnableV4 is read when AddMessagePickup() runs, so a value that arrives another way
    // (Configure<MessagePickupOptions>, configuration binding, two calls that disagree) cannot
    // change what was registered. Rather than serve, or silently not serve, a protocol the settings
    // say otherwise about, building either handler fails. Looks at registrations and the options
    // only; no handler is resolved.
    private static void VerifyVersion4Registration(IServiceCollection services, IServiceProvider sp)
    {
        var registered = false;
        foreach (var descriptor in services)
            registered |= descriptor.ServiceType == typeof(MessagePickup40Handler) && !descriptor.IsKeyedService;

        var enabled = sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value.EnableV4;
        if (registered && !enabled)
        {
            throw new InvalidOperationException(
                "MessagePickupOptions.EnableV4 is false in the settings in effect, but the Message Pickup 4.0 role is registered " +
                "(by an AddMessagePickup() or AddMediator() call, which reads the setting when it runs, or by " +
                "AddProtocol<MessagePickup40Handler>()). To serve 3.0 only, set EnableV4 = false in the delegate of every " +
                "AddMessagePickup() and AddMediator() call, and do not register the 4.0 handler yourself.");
        }
        if (!registered && enabled)
        {
            throw new InvalidOperationException(
                "MessagePickupOptions.EnableV4 is true in the settings in effect, but no AddMessagePickup() or AddMediator() " +
                "call registered the Message Pickup 4.0 role: the setting is read when that call runs, and a call set it to false. " +
                "Set EnableV4 the same way in the delegate of every AddMessagePickup() and AddMediator() call.");
        }
    }

    [ThreadStatic]
    private static bool t_buildingQueue;

    private static MessagePickupQueue BuildQueue(IServiceProvider sp)
        => new(
            sp.GetRequiredService<IMessageStore>(),
            sp.GetService<IMediationStore>() ?? throw new InvalidOperationException(
                "AddMessagePickup() needs an IMediationStore: the pickup queue serves the recipients enrolled through Coordinate Mediation. " +
                "Call AddCoordinateMediation() on the same builder, or register your own IMediationStore."),
            sp.GetRequiredService<IReturnRouteSessionRegistry>(),
            sp.GetRequiredService<IOptions<MessagePickupOptions>>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>(),
            sp.GetService<ILogger<MessagePickupQueue>>(),
            // The queue mints its own random ids; only the internal test seam can replace them.
            sp.GetService<PickupIdSource>()?.Next);
}
