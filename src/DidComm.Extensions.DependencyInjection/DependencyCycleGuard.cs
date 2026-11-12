namespace DidComm.Extensions.DependencyInjection;

/// <summary>
/// Turns a dependency cycle that runs through one of this library's DI factories into an
/// <see cref="InvalidOperationException"/> instead of a hang. The container cannot see inside a
/// factory delegate, so it cannot detect a cycle through one: the cycle re-enters the same
/// singleton's factory on the same thread and recurses until the container continues the
/// resolution on a fresh thread, which then blocks on the singleton lock the first thread holds
/// while the first thread waits for it — a silent deadlock on first resolution. Typical shapes: a
/// host-registered <c>IReturnRouteSessionRegistry</c> or <c>IInboundReplayGuard</c> that takes the
/// <c>ProtocolDispatcher</c> (or the protocol handlers) while a handler takes that service; a
/// handler that takes the dispatcher; and a host <c>IInboundFrameConsumer</c> or a custom
/// <c>IDidCommTransport</c> that takes <c>DidCommClient</c>, which depends on every transport (and
/// the WebSocket transport on the consumer). The WebSocket transport's own factory carries the same
/// guard (<c>WebSocketDidCommBuilderExtensions</c>, a copy: this type is internal to its assembly),
/// so a consumer that takes the transport or the router fails fast too.
/// </summary>
/// <remarks>
/// A guarded factory marks itself as running, for the provider it was called with, on the
/// resolving thread for the duration of its own call. A singleton factory runs at most once
/// successfully, so a second entry of the same factory for the same provider while the first is
/// still on the stack can only be such a cycle; it throws, the resolution unwinds, and the container
/// caches nothing. The cycle re-enters the factory a few frames after its first entry — long before
/// the container would move to another thread — so a thread-local mark sees it.
/// </remarks>
internal static class DependencyCycleGuard
{
    [ThreadStatic]
    private static HashSet<(object Site, IServiceProvider Provider)>? t_running;

    /// <summary>
    /// Wrap <paramref name="factory"/> so that re-entering it for the same provider, while it is
    /// already running on this thread, throws <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <typeparam name="T">The factory's result type (kept, so a typed descriptor keeps its implementation type).</typeparam>
    /// <param name="service">The service the factory builds; named in the exception message.</param>
    /// <param name="factory">The factory to guard.</param>
    internal static Func<IServiceProvider, T> Guard<T>(Type service, Func<IServiceProvider, T> factory)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(factory);
        var site = new object();
        return sp =>
        {
            var running = t_running ??= new HashSet<(object, IServiceProvider)>();
            var key = (site, sp);
            if (!running.Add(key))
            {
                throw new InvalidOperationException(
                    $"A circular dependency was detected while resolving '{service.FullName}': it was requested again by a service its own construction depends on. " +
                    "A protocol handler, or a service a handler depends on (for example a host-registered IReturnRouteSessionRegistry or IInboundReplayGuard), " +
                    "must not take ProtocolDispatcher, ProtocolHandlerRegistry or the protocol handlers in its constructor. " +
                    "An IInboundFrameConsumer or an IDidCommTransport, or anything either depends on, must not take DidCommClient, ProtocolDispatcher, " +
                    "ProtocolHandlerRegistry or the protocol handlers in its constructor, because DidCommClient depends on every registered transport " +
                    "(and the WebSocket transport on the IInboundFrameConsumer). Resolve that side lazily: inject IServiceProvider and resolve it when first needed.");
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
