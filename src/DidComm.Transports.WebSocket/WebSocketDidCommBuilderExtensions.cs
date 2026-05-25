using DidComm.Extensions.DependencyInjection;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DidComm.Transports.WebSocket;

/// <summary>
/// Wires <see cref="WebSocketDidCommTransport"/> into the DI graph from the
/// <see cref="DidCommBuilder"/> chain (Phase 5 / FR-TRN-09..11).
/// </summary>
public static class WebSocketDidCommBuilderExtensions
{
    /// <summary>
    /// Register the WebSocket transport as a DI singleton (so the connection pool persists
    /// across sends) alongside an idempotent <see cref="ITransportRouter"/> registration.
    /// </summary>
    /// <param name="builder">The DidComm DI builder.</param>
    /// <param name="configure">Optional <see cref="WebSocketTransportOptions"/> configuration callback.</param>
    public static DidCommBuilder UseWebSocketTransport(this DidCommBuilder builder, Action<WebSocketTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.AddOptions<WebSocketTransportOptions>();

        builder.Services.AddSingleton<WebSocketDidCommTransport>();
        builder.Services.AddSingleton<IDidCommTransport>(sp => sp.GetRequiredService<WebSocketDidCommTransport>());
        builder.Services.TryAddSingleton<ITransportRouter, TransportRouter>();
        return builder;
    }
}
