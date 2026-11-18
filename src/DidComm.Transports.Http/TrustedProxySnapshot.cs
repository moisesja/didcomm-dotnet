using Microsoft.Extensions.Options;

namespace DidComm.Transports.Http;

/// <summary>
/// The container's one reading of <see cref="HttpTransportOptions.TrustedProxy"/>: checked and
/// copied the first time the <c>"didcomm"</c> client's handler is built, then reused by every later
/// build (the factory rebuilds handlers periodically). So, as in the WebSocket transport, later
/// changes to the configured <see cref="System.Net.WebProxy"/> never reach the transport.
/// </summary>
internal sealed class TrustedProxySnapshot
{
    /// <summary>Read the bound options once.</summary>
    /// <param name="options">The bound transport options.</param>
    public TrustedProxySnapshot(IOptions<HttpTransportOptions> options) =>
        Route = TrustedProxyRoute.From(options.Value.TrustedProxy);

    /// <summary>The named proxy, or <c>null</c> for direct connections.</summary>
    public TrustedProxyRoute? Route { get; }
}
