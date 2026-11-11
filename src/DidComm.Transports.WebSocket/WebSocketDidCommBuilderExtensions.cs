using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Transports.WebSocket;

/// <summary>
/// Wires <see cref="WebSocketDidCommTransport"/> into the DI graph from the
/// <see cref="DidCommBuilder"/> chain (Phase 5 / FR-TRN-09..11).
/// </summary>
/// <example>
/// Registered in <c>samples/05-WebSocketChat</c> and <c>samples/02-Cookbook</c> (section R).
/// </example>
public static class WebSocketDidCommBuilderExtensions
{
    /// <summary>
    /// Register the WebSocket transport as a DI singleton (so the connection pool persists
    /// across sends) alongside an idempotent <see cref="ITransportRouter"/> registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport is built by an explicit factory with the bound
    /// <see cref="WebSocketTransportOptions"/>, the logger and <see cref="DidCommOptions"/> when
    /// registered, and the registered <see cref="IInboundFrameConsumer"/>, if any
    /// (<see cref="DidCommBuilder.UseInboundDispatch"/>). So the consumer is wired whatever else is
    /// registered, and <see cref="WebSocketTransportOptions.ReceiveInbound"/> without one fails on the
    /// first resolution with an <see cref="InvalidOperationException"/> that says what to register.
    /// </para>
    /// <para>
    /// The consumer is built while the transport is, and this factory is the only registration in the
    /// library that resolves it. So a consumer that takes the transport, an
    /// <see cref="IDidCommTransport"/>, the <see cref="ITransportRouter"/> or <see cref="DidCommClient"/>
    /// in its constructor (or depends on something that does) re-enters this factory before it has
    /// returned, or, when the facade was resolved first, the facade's own guarded factory. Either
    /// re-entry is detected on the resolving thread and fails the first resolution with an
    /// <see cref="InvalidOperationException"/> instead of deadlocking it. Such a consumer must resolve
    /// what it needs through an <see cref="IServiceProvider"/> when first needed. A transport the
    /// host builds in its own factory has no such guard.
    /// </para>
    /// </remarks>
    /// <param name="builder">The DidComm DI builder.</param>
    /// <param name="configure">Optional <see cref="WebSocketTransportOptions"/> configuration callback.</param>
    public static DidCommBuilder UseWebSocketTransport(this DidCommBuilder builder, Action<WebSocketTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddOptions<WebSocketTransportOptions>();

        // JC-19: an explicit factory, not type activation. With two public constructors, activation
        // would pick the four-parameter one only when everything it takes happened to be registered,
        // silently leaving the inbound consumer unwired otherwise. The values are the ones activation
        // resolved before (options, optional logger, optional core options) plus the consumer.
        // Guarded (ADV-02): the consumer is resolved inside this factory, so a consumer that depends
        // back on the transport (directly, as IDidCommTransport, or through the router or the facade)
        // re-enters it. The container cannot see inside a factory, so without the guard that cycle
        // deadlocks the first resolution instead of failing it.
        builder.Services.AddSingleton(GuardReentry(sp => new WebSocketDidCommTransport(
            sp.GetRequiredService<IOptions<WebSocketTransportOptions>>(),
            sp.GetService<ILogger<WebSocketDidCommTransport>>(),
            sp.GetService<IOptions<DidCommOptions>>(),
            sp.GetService<IInboundFrameConsumer>())));
        builder.Services.AddSingleton<IDidCommTransport>(sp => sp.GetRequiredService<WebSocketDidCommTransport>());
        builder.Services.TryAddSingleton<ITransportRouter, TransportRouter>();
        return builder;
    }

    // The factories currently running on this thread, per provider (the DI library's
    // DependencyCycleGuard pattern; that type is internal to its assembly, JC-22).
    [ThreadStatic]
    private static HashSet<(object Site, IServiceProvider Provider)>? t_running;

    /// <summary>
    /// Wrap the transport factory so that re-entering it for the same provider, while it is already
    /// running on this thread, throws <see cref="InvalidOperationException"/> instead of recursing.
    /// </summary>
    /// <remarks>
    /// The container cannot see inside a factory delegate, so it cannot detect a cycle through one:
    /// the cycle re-enters the singleton's factory on the same thread and recurses until the
    /// container continues on a fresh thread, which then blocks on the singleton lock the first
    /// thread holds — a silent deadlock on first resolution. A singleton factory runs at most once
    /// successfully, so a second entry for the same provider while the first is still on the stack
    /// can only be such a cycle. It is marked a few frames after the first entry, long before the
    /// container would move to another thread, so a thread-local mark sees it. Nothing is cached when
    /// it throws, and the mark is removed when the factory returns or fails.
    /// </remarks>
    /// <param name="factory">The transport factory.</param>
    private static Func<IServiceProvider, WebSocketDidCommTransport> GuardReentry(Func<IServiceProvider, WebSocketDidCommTransport> factory)
    {
        var site = new object();
        return sp =>
        {
            var running = t_running ??= new HashSet<(object, IServiceProvider)>();
            var key = (site, sp);
            if (!running.Add(key))
            {
                throw new InvalidOperationException(
                    $"A circular dependency was detected while resolving '{typeof(WebSocketDidCommTransport).FullName}': it was requested again by a service its own construction depends on. " +
                    "The transport depends on the registered IInboundFrameConsumer, so an IInboundFrameConsumer, or anything it depends on, must not take " +
                    "WebSocketDidCommTransport, IDidCommTransport, ITransportRouter, DidCommClient, ProtocolDispatcher, ProtocolHandlerRegistry or a protocol handler " +
                    "in its constructor. Resolve that side lazily: inject IServiceProvider and resolve it when first needed.");
            }
            try
            {
                return factory(sp);
            }
            finally
            {
                running.Remove(key);
            }
        };
    }
}
