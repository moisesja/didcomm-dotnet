using System.Net;
using System.Net.Sockets;

namespace DidComm.Transports.Http;

/// <summary>
/// The checked copy of <see cref="HttpTransportOptions.TrustedProxy"/>: the proxy the handler uses,
/// and the test that tells the connect callback a socket is going to that proxy rather than to a
/// destination. Keep in step with the WebSocket transport's copy of this class (same rules, same
/// messages; only the option name differs). Without a proxy it also words the direct-connect hint.
/// </summary>
internal sealed class TrustedProxyRoute
{
    /// <summary>The option this route is read from, as named in error messages.</summary>
    internal const string OptionName = "HttpTransportOptions.TrustedProxy";

    /// <summary>The proxy schemes <c>SocketsHttpHandler</c> supports.</summary>
    private static readonly string[] Schemes = { "http", "https", "socks4", "socks4a", "socks5" };

    /// <summary>
    /// Dials the named proxy: the guard's own connect, with the private-address filter off, because
    /// the proxy's address is the host's explicit choice.
    /// </summary>
    private static readonly OutboundEndpointGuard ProxyDialer =
        new(new OutboundEndpointPolicy { BlockPrivateNetworks = false });

    private readonly string _host;
    private readonly IPAddress? _address;
    private readonly int _port;

    private TrustedProxyRoute(WebProxy proxy, string host, int port)
    {
        Proxy = proxy;
        _host = host;
        _address = IPAddress.TryParse(host, out var address) ? address : null;
        _port = port;
    }

    /// <summary>The copy of the configured proxy that the handler uses.</summary>
    public WebProxy Proxy { get; }

    /// <summary>
    /// Check <paramref name="configured"/> and copy it, so later changes to the host's instance
    /// cannot move a handler already built. <c>null</c> (no proxy) stays <c>null</c>: connect directly.
    /// </summary>
    /// <param name="configured">The configured proxy, or <c>null</c>.</param>
    /// <returns>The route, or <c>null</c> for direct connections.</returns>
    /// <exception cref="InvalidOperationException">The value breaks one of the rules; the message never contains the address.</exception>
    public static TrustedProxyRoute? From(WebProxy? configured)
    {
        if (configured is null)
            return null;

        var address = configured.Address
            ?? throw Invalid("has no Address. Set the proxy's address, for example new WebProxy(\"http://proxy.internal:3128\").");
        if (!address.IsAbsoluteUri)
            throw Invalid("must have an absolute Address (scheme, host and port).");
        if (Array.IndexOf(Schemes, address.Scheme) < 0)
            throw Invalid("must use one of the schemes http, https, socks4, socks4a or socks5.");
        if (address.UserInfo.Length != 0)
            throw Invalid("must not carry a user name or password in its Address. Set WebProxy.Credentials instead.");
        var host = Normalize(address.IdnHost);
        if (host.Length == 0)
            throw Invalid("must name a host in its Address.");
        if (address.Port <= 0)
            throw Invalid("must name a port in its Address.");
        if (address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw Invalid("must be a scheme, host and port only, with no path, query or fragment.");

        WebProxy copy;
        try
        {
            // Credentials also carries UseDefaultCredentials (the default credentials object).
            copy = new WebProxy(address, configured.BypassProxyOnLocal, configured.BypassList, configured.Credentials);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{OptionName}.BypassList contains an entry that is not a valid pattern.", ex);
        }
        return new TrustedProxyRoute(copy, host, address.Port);
    }

    /// <summary>
    /// Whether the handler is connecting to the named proxy. A host spelled differently is not the
    /// proxy, so a mismatch falls back to the full guard (fails closed).
    /// </summary>
    /// <param name="endpoint">The endpoint the handler wants a socket to.</param>
    public bool IsProxyEndpoint(DnsEndPoint endpoint)
    {
        if (endpoint.Port != _port)
            return false;
        var host = Normalize(endpoint.Host);
        return _address is not null
            ? IPAddress.TryParse(host, out var address) && address.Equals(_address)
            : string.Equals(host, _host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Open the TCP connection to the named proxy, whatever network it is on.</summary>
    /// <param name="endpoint">The proxy's endpoint, as <see cref="IsProxyEndpoint"/> accepted it.</param>
    /// <param name="ct">Cancels the connect.</param>
    public static ValueTask<Socket> ConnectToProxyAsync(DnsEndPoint endpoint, CancellationToken ct) =>
        ProxyDialer.ConnectAsync(endpoint, ct);

    /// <summary>
    /// The same socket error with one sentence added, for a direct connection that failed: a host
    /// that reaches other agents only through a proxy learns why, and what to set. The error code,
    /// and so the exception's classification, is unchanged.
    /// </summary>
    /// <param name="error">The connect's failure.</param>
    public static SocketException WithDirectHint(SocketException error) =>
        new((int)error.SocketErrorCode,
            $"{error.Message.TrimEnd('.', ' ')}. This transport connects directly: it does not use HTTP_PROXY, HTTPS_PROXY, ALL_PROXY " +
            $"or the system proxy. To send through a proxy, set {OptionName}.");

    // The handler hands an IPv6 proxy host over with brackets on the tunnel path and without them on
    // the forwarding path, and keeps a trailing dot; compare the bare forms.
    private static string Normalize(string host)
    {
        if (host.Length > 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];
        if (host.Length > 1 && host[^1] == '.')
            host = host[..^1];
        return host;
    }

    private static InvalidOperationException Invalid(string rule) => new($"{OptionName} {rule}");
}
