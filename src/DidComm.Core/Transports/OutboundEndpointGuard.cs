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
/// The connect-time check sees only the socket it opens, so the transports connect directly. When a
/// host names a proxy it trusts (the transports' <c>TrustedProxy</c>), the socket is the proxy's and
/// only <see cref="Validate"/> still applies to the destination; past the proxy, egress filtering is
/// the proxy's job.
/// </summary>
public sealed class OutboundEndpointGuard
{
    private readonly OutboundEndpointPolicy _policy;
    private readonly Func<string, IPAddress[]> _resolveDns;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _resolveDnsAsync;

    /// <summary>Initialize the guard.</summary>
    /// <param name="policy">The policy to enforce.</param>
    /// <param name="resolveDns">DNS resolution seam (defaults to <see cref="Dns.GetHostAddresses(string)"/>); overridden in tests to avoid real network I/O.</param>
    public OutboundEndpointGuard(OutboundEndpointPolicy policy, Func<string, IPAddress[]>? resolveDns = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _resolveDns = resolveDns ?? Dns.GetHostAddresses;
        // A test's lookup seam answers the asynchronous check too.
        if (resolveDns is not null)
            _resolveDnsAsync = (host, _) => Task.FromResult(resolveDns(host));
    }

    /// <summary>
    /// Throw <see cref="TransportException"/> when <paramref name="endpoint"/> targets a blocked
    /// address. No-op when <see cref="OutboundEndpointPolicy.BlockPrivateNetworks"/> is <c>false</c>
    /// or the host is allowlisted. IP-literal hosts are classified directly; DNS names are resolved
    /// when <see cref="OutboundEndpointPolicy.ResolveDnsNames"/> is enabled and rejected if
    /// <em>any</em> resolved address is private/reserved. A name that does not resolve, or that the
    /// lookup refuses outright (longer than 255 characters), is refused too. The host is classified
    /// as it is sent on the wire (<see cref="Uri.IdnHost"/>, after IDNA mapping), so a host written
    /// with look-alike characters that maps to an IP literal (<c>127。0。0。1</c>) is classified as
    /// that literal.
    /// </summary>
    /// <remarks>
    /// The name lookup blocks the calling thread and takes no cancellation token. The library's own
    /// sends use an asynchronous form of this check that honours the send's cancellation token.
    /// </remarks>
    /// <param name="endpoint">The fully resolved outbound endpoint URI.</param>
    /// <exception cref="TransportException">The destination is blocked, or its name cannot be put in its wire form or resolved.</exception>
    public void Validate(Uri endpoint)
    {
        if (HostToCheck(endpoint) is not { } host)
            return;
        if (IPAddress.TryParse(host, out var literal))
        {
            Check(endpoint, host, new[] { literal });
            return;
        }
        if (!_policy.ResolveDnsNames)
            return;

        IPAddress[] candidates;
        try
        {
            candidates = _resolveDns(host);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw LookupFailed(endpoint, host, ex);
        }
        Check(endpoint, host, candidates);
    }

    /// <summary>
    /// <see cref="Validate"/>, with the name looked up asynchronously: the same refusals, and a
    /// cancellation of <paramref name="ct"/> ends the wait for the lookup at once.
    /// </summary>
    /// <param name="endpoint">The fully resolved outbound endpoint URI.</param>
    /// <param name="ct">The send's cancellation token.</param>
    /// <exception cref="TransportException">The destination is blocked, or its name cannot be put in its wire form or resolved.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled during the lookup.</exception>
    internal async ValueTask ValidateAsync(Uri endpoint, CancellationToken ct)
    {
        if (HostToCheck(endpoint) is not { } host)
            return;
        if (IPAddress.TryParse(host, out var literal))
        {
            Check(endpoint, host, new[] { literal });
            return;
        }
        if (!_policy.ResolveDnsNames)
            return;

        IPAddress[] candidates;
        try
        {
            var resolve = _resolveDnsAsync ?? _policy.ResolveDnsAsync ?? Dns.GetHostAddressesAsync;
            var lookup = resolve(host, ct);
            if (!lookup.IsCompleted)
            {
                // The wait ends with the token, whether or not the lookup itself does; a lookup left
                // behind that fails later is observed here, never by the finalizer.
                _ = lookup.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            candidates = await lookup.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw LookupFailed(endpoint, host, ex);
        }
        Check(endpoint, host, candidates);
    }

    /// <summary>
    /// The host of <paramref name="endpoint"/> in its wire form when it has to be classified;
    /// <c>null</c> when the policy lets it through as it is (blocking off, an empty host, an
    /// allow-listed host).
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    private string? HostToCheck(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!_policy.BlockPrivateNetworks)
            return null;

        // The wire form: what the HTTP stack writes in the request line or the CONNECT authority, and
        // what a proxy acts on. DnsSafeHost keeps the written Unicode form, which can hide a literal.
        var host = HostForm(endpoint, static e => e.IdnHost);
        if (string.IsNullOrEmpty(host) || _policy.AllowedHosts.Contains(host)
            || _policy.AllowedHosts.Contains(HostForm(endpoint, static e => e.DnsSafeHost)))
        {
            // An entry matches either spelling of the same host (an ASCII host is spelled the same
            // both ways; a Unicode entry keeps matching its written form).
            return null;
        }
        return host;
    }

    /// <summary>Refuse <paramref name="endpoint"/> when any of <paramref name="candidates"/> is private or reserved.</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="host">Its host, in wire form.</param>
    /// <param name="candidates">The literal, or the addresses the name resolved to.</param>
    private static void Check(Uri endpoint, string host, IPAddress[] candidates)
    {
        foreach (var address in candidates)
        {
            if (IsPrivateOrReserved(address))
                throw Blocked(endpoint, host, address, endpoint.Scheme);
        }
    }

    /// <summary>The refusal of a name the lookup could not resolve (<see cref="SocketException"/>) or would not take (<see cref="ArgumentException"/>).</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="host">Its host, in wire form.</param>
    /// <param name="ex">What the lookup threw.</param>
    private static TransportException LookupFailed(Uri endpoint, string host, Exception ex) =>
        ex is SocketException socket
            ? new TransportException(
                $"Refusing to send to '{endpoint}': host '{host}' did not resolve ({socket.SocketErrorCode}).",
                socket,
                httpStatusCode: null,
                scheme: endpoint.Scheme)
            : HostNotUsable(endpoint, ex);

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
    /// <c>fc00::/7</c>, Teredo <c>2001::/32</c> and the RFC 8215 local-use NAT64 prefix
    /// <c>64:ff9b:1::/48</c> as a whole. IPv6 forms that embed an IPv4 target are classified by
    /// that target: IPv4-mapped <c>::ffff:a.b.c.d</c>, IPv4-translated <c>::ffff:0:a.b.c.d</c>
    /// (RFC 2765/6052), IPv4-compatible <c>::a.b.c.d</c>, 6to4 <c>2002::/16</c> and the well-known
    /// NAT64 prefix <c>64:ff9b::/96</c>, so they cannot dodge the IPv4 rules.
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
            // RFC 8215 local-use NAT64 prefix 64:ff9b:1::/48 — reserved as a whole. It is not
            // globally routable (a domain's own translator serves it), and where the IPv4 sits in
            // an address depends on the prefix length that domain chose inside the /48 (RFC 6052
            // §2.2: /48, /56, /64 or /96), so reading one position would let another hide a
            // loopback or metadata target.
            if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B && v6[4] == 0x00 && v6[5] == 0x01)
                return true;
            // The IsIPv4MappedToIPv6 unwrap at the top only catches ::ffff:0:0/96. Other IPv6 forms
            // also embed an IPv4 target: IPv4-translated ::ffff:0:0:0/96, IPv4-compatible ::/96
            // (e.g. ::a9fe:a9fe), 6to4 2002::/16, and NAT64 64:ff9b::/96. Re-run the IPv4 classification on the embedded address so a loopback
            // / metadata / RFC 1918 v4 can't hide inside an IPv6 literal.
            if (TryExtractEmbeddedIPv4(v6, out var embedded) && IsPrivateOrReserved(embedded))
                return true;
            return false;
        }

        return false;
    }

    /// <summary>
    /// One spelling of <paramref name="endpoint"/>'s host, normalized; a host that cannot be put in
    /// that form is refused with <see cref="HostNotUsable"/>.
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="form">Reads the spelling (<see cref="Uri.IdnHost"/> or <see cref="Uri.DnsSafeHost"/>).</param>
    private static string HostForm(Uri endpoint, Func<Uri, string> form)
    {
        try
        {
            return NormalizeHost(form(endpoint));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw HostNotUsable(endpoint, ex);
        }
    }

    /// <summary>
    /// The refusal of a destination host name the guard cannot put in its wire form or look up. The
    /// text names no host: such a name is unbounded and comes from a DID document or a wire message.
    /// </summary>
    /// <param name="endpoint">The endpoint (only its scheme is used).</param>
    /// <param name="inner">What refused the name.</param>
    private static TransportException HostNotUsable(Uri endpoint, Exception inner) =>
        new("Refusing to send: the destination host name cannot be looked up.", inner, httpStatusCode: null, scheme: endpoint.Scheme);

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
        // IPv4-translated ::ffff:0:0:0/96 (RFC 2765 SIIT, RFC 6052): ::ffff:0:a.b.c.d — the low 32
        // bits. Not the IPv4-mapped ::ffff:0:0/96 form, which the caller unwraps first.
        if (v6[0] == 0 && v6[1] == 0 && v6[2] == 0 && v6[3] == 0 && v6[4] == 0 && v6[5] == 0 && v6[6] == 0 && v6[7] == 0 &&
            v6[8] == 0xFF && v6[9] == 0xFF && v6[10] == 0 && v6[11] == 0)
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
