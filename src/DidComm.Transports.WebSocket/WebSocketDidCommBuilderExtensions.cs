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
    /// The transport is built by an explicit factory with the bound
    /// <see cref="WebSocketTransportOptions"/>, the logger and <see cref="DidCommOptions"/> when
    /// registered, and the registered <see cref="IInboundFrameConsumer"/>, if any
    /// (<see cref="DidCommBuilder.UseInboundDispatch"/>). So the consumer is wired whatever else is
    /// registered, and <see cref="WebSocketTransportOptions.ReceiveInbound"/> without one fails on the
    /// first resolution with an <see cref="InvalidOperationException"/> that says what to register.
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
        builder.Services.AddSingleton(sp => new WebSocketDidCommTransport(
            sp.GetRequiredService<IOptions<WebSocketTransportOptions>>(),
            sp.GetService<ILogger<WebSocketDidCommTransport>>(),
            sp.GetService<IOptions<DidCommOptions>>(),
            sp.GetService<IInboundFrameConsumer>()));
        builder.Services.AddSingleton<IDidCommTransport>(sp => sp.GetRequiredService<WebSocketDidCommTransport>());
        builder.Services.TryAddSingleton<ITransportRouter, TransportRouter>();
        return builder;
    }
}
