using System.Net;
using System.Net.Sockets;
using DidComm.Exceptions;

namespace DidComm.Transports;

/// <summary>
/// Enforces an <see cref="OutboundEndpointPolicy"/> against outbound DIDComm endpoints to defend
/// against SSRF. The host of a DID-document <c>serviceEndpoint</c> is attacker-controlled, so before
/// the library POSTs a packed envelope it confirms the destination is not a private / loopback /
/// link-local / metadata address. Two entry points share the same classification:
/// <list type="bullet">
/// <item><see cref="Validate"/> — pre-send check on a fully resolved <see cref="Uri"/> (used by the
/// facade and the WebSocket transport).</item>
/// <item><see cref="ConnectAsync"/> — connect-time check that pins the TCP connection to a vetted IP
/// (used by the HTTP transport's <c>SocketsHttpHandler.ConnectCallback</c>), which additionally
/// defeats 307-redirect-to-internal and DNS-rebinding.</item>
/// </list>
/// </summary>
public sealed class OutboundEndpointGuard
{
    private readonly OutboundEndpointPolicy _policy;
    private readonly Func<string, IPAddress[]> _resolveDns;

    /// <summary>Initialize the guard.</summary>
    /// <param name="policy">The policy to enforce.</param>
    /// <param name="resolveDns">DNS resolution seam (defaults to <see cref="Dns.GetHostAddresses(string)"/>); overridden in tests to avoid real network I/O.</param>
    public OutboundEndpointGuard(OutboundEndpointPolicy policy, Func<string, IPAddress[]>? resolveDns = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _resolveDns = resolveDns ?? Dns.GetHostAddresses;
    }

    /// <summary>
    /// Throw <see cref="TransportException"/> when <paramref name="endpoint"/> targets a blocked
    /// address. No-op when <see cref="OutboundEndpointPolicy.BlockPrivateNetworks"/> is <c>false</c>
    /// or the host is allowlisted. IP-literal hosts are classified directly; DNS names are resolved
    /// when <see cref="OutboundEndpointPolicy.ResolveDnsNames"/> is enabled and rejected if
    /// <em>any</em> resolved address is private/reserved.
    /// </summary>
    /// <param name="endpoint">The fully resolved outbound endpoint URI.</param>
    public void Validate(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!_policy.BlockPrivateNetworks)
            return;

        var host = endpoint.DnsSafeHost;
        if (string.IsNullOrEmpty(host) || _policy.AllowedHosts.Contains(host))
            return;

        IPAddress[] candidates;
        if (IPAddress.TryParse(host, out var literal))
        {
            candidates = new[] { literal };
        }
        else if (_policy.ResolveDnsNames)
        {
            try
            {
                candidates = _resolveDns(host);
            }
            catch (SocketException ex)
            {
                throw new TransportException(
                    $"Refusing to send to '{endpoint}': host '{host}' did not resolve ({ex.SocketErrorCode}).",
                    ex,
                    httpStatusCode: null,
                    scheme: endpoint.Scheme);
            }
        }
        else
        {
            return;
        }

        foreach (var address in candidates)
        {
            if (IsPrivateOrReserved(address))
                throw Blocked(endpoint, host, address, endpoint.Scheme);
        }
    }

    /// <summary>
    /// Resolve <paramref name="endpoint"/>, drop any private/reserved addresses (honoring the
    /// policy), and open a TCP connection pinned to a vetted IP. Pinning the connection to an
    /// already-validated address is what makes this resistant to DNS rebinding and to 307 redirects
    /// pointing at internal hosts. Throws <see cref="TransportException"/> when no allowed address
    /// remains.
    /// </summary>
    /// <param name="endpoint">The host/port the HTTP stack wants to connect to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<Socket> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var host = endpoint.Host;

        IPAddress[] candidates = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        var allowAll = !_policy.BlockPrivateNetworks || _policy.AllowedHosts.Contains(host);
        var allowed = new List<IPAddress>(candidates.Length);
        foreach (var address in candidates)
        {
            if (allowAll || !IsPrivateOrReserved(address))
                allowed.Add(address);
        }

        if (allowed.Count == 0)
        {
            throw new TransportException(
                $"Refusing to connect to '{host}:{endpoint.Port}': every resolved address is private or reserved (SSRF defense). " +
                "Allowlist the host via the transport's OutboundEndpointPolicy.AllowedHosts, or set BlockPrivateNetworks = false, if this destination is trusted.",
                httpStatusCode: null,
                scheme: null);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), endpoint.Port, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Classify <paramref name="address"/> as a non-routable / reserved destination: loopback,
    /// IPv4 link-local <c>169.254/16</c> (which includes the <c>169.254.169.254</c> cloud-metadata
    /// address), RFC 1918 private ranges, RFC 6598 CGNAT <c>100.64/10</c>, the unspecified address,
    /// multicast/reserved, IPv6 loopback/link-local/site-local/multicast, and unique-local
    /// <c>fc00::/7</c>. IPv4-mapped IPv6 addresses are unwrapped first so they cannot dodge the IPv4
    /// rules.
    /// </summary>
    /// <param name="address">The address to classify.</param>
    /// <returns><c>true</c> when the address must not be a DIDComm send target by default.</returns>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0                                    // 0.0.0.0/8 (incl. the unspecified address)
                || b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16 link-local + metadata
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64.0.0/10 CGNAT (RFC 6598)
                || b[0] >= 224;                                 // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return true;
            if (address.Equals(IPAddress.IPv6Any))              // unspecified ::
                return true;
            // fc00::/7 unique-local addresses.
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static TransportException Blocked(Uri endpoint, string host, IPAddress address, string scheme) =>
        new(
            $"Refusing to send to '{endpoint}': host '{host}' resolves to a private or reserved address ({address}). " +
            "This is blocked by default to prevent SSRF via attacker-controlled DID service endpoints. " +
            "Allowlist the host via DidCommOptions.OutboundEndpointPolicy.AllowedHosts, or set BlockPrivateNetworks = false, if this destination is trusted.",
            httpStatusCode: null,
            scheme: scheme);
}
