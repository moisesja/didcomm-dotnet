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

        var host = NormalizeHost(endpoint.DnsSafeHost);
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
        var host = NormalizeHost(endpoint.Host);

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
            var v6 = address.GetAddressBytes();
            // fc00::/7 unique-local addresses.
            if ((v6[0] & 0xFE) == 0xFC)
                return true;
            // Teredo 2001:0000::/32 tunnels to an arbitrary IPv4 endpoint — treat as reserved.
            if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x00 && v6[3] == 0x00)
                return true;
            // The IsIPv4MappedToIPv6 unwrap at the top only catches ::ffff:0:0/96. Other IPv6 forms
            // also embed an IPv4 target: IPv4-compatible ::/96 (e.g. ::a9fe:a9fe), 6to4 2002::/16, and
            // NAT64 64:ff9b::/96. Re-run the IPv4 classification on the embedded address so a loopback
            // / metadata / RFC 1918 v4 can't hide inside an IPv6 literal.
            if (TryExtractEmbeddedIPv4(v6, out var embedded) && IsPrivateOrReserved(embedded))
                return true;
            return false;
        }

        return false;
    }

    // Strip a single trailing dot (the DNS root label). "127.0.0.1." would otherwise fail the
    // IP-literal check and be treated as a DNS name, and "example.com." would not match an
    // "example.com" allowlist entry — both let a target dodge classification.
    private static string NormalizeHost(string host) =>
        host.Length > 1 && host[^1] == '.' ? host[..^1] : host;

    private static bool TryExtractEmbeddedIPv4(byte[] v6, out IPAddress embedded)
    {
        embedded = IPAddress.None;
        // 6to4 2002::/16 — the embedded IPv4 is bytes 2..5.
        if (v6[0] == 0x20 && v6[1] == 0x02)
        {
            embedded = new IPAddress(new[] { v6[2], v6[3], v6[4], v6[5] });
            return true;
        }
        // NAT64 well-known prefix 64:ff9b::/96 — embedded IPv4 is the low 32 bits.
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B &&
            v6[4] == 0 && v6[5] == 0 && v6[6] == 0 && v6[7] == 0 &&
            v6[8] == 0 && v6[9] == 0 && v6[10] == 0 && v6[11] == 0)
        {
            embedded = new IPAddress(new[] { v6[12], v6[13], v6[14], v6[15] });
            return true;
        }
        // IPv4-compatible ::/96 (deprecated): 0:0:0:0:0:0:a.b.c.d — the top 96 bits are zero.
        for (var i = 0; i < 12; i++)
            if (v6[i] != 0)
                return false;
        embedded = new IPAddress(new[] { v6[12], v6[13], v6[14], v6[15] });
        return true;
    }

    private static TransportException Blocked(Uri endpoint, string host, IPAddress address, string scheme) =>
        new(
            $"Refusing to send to '{endpoint}': host '{host}' resolves to a private or reserved address ({address}). " +
            "This is blocked by default to prevent SSRF via attacker-controlled DID service endpoints. " +
            "Allowlist the host via DidCommOptions.OutboundEndpointPolicy.AllowedHosts, or set BlockPrivateNetworks = false, if this destination is trusted.",
            httpStatusCode: null,
            scheme: scheme);
}
