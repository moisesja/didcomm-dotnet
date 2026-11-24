namespace DidComm.Transports;

/// <summary>
/// SSRF-defense policy for outbound DIDComm sends. A recipient's DID-document
/// <c>serviceEndpoint</c> host is attacker-influenced; without a gate a malicious DID could steer a
/// sender's outbound request at cloud-metadata services (e.g. <c>169.254.169.254</c>) or internal
/// hosts. <see cref="OutboundEndpointGuard"/> applies this policy to endpoints <em>resolved from a
/// DID</em>; caller-supplied <c>SendOptions.ServiceEndpointOverride</c> values are trusted and not
/// checked.
/// </summary>
public sealed class OutboundEndpointPolicy
{
    /// <summary>
    /// When <c>true</c> (the default), reject destinations that resolve to a private, loopback,
    /// link-local, unique-local, CGNAT, or otherwise reserved address. Set to <c>false</c> only when
    /// the host application deliberately routes DIDComm over a trusted private network.
    /// </summary>
    public bool BlockPrivateNetworks { get; set; } = true;

    /// <summary>
    /// Hosts (DNS names or IP literals) that bypass the <see cref="BlockPrivateNetworks"/> check —
    /// e.g. a known internal mediator. Compared case-insensitively against the endpoint's host, as
    /// written (<c>Uri.DnsSafeHost</c>) or as sent (<c>Uri.IdnHost</c>). Empty by default.
    /// </summary>
    public ISet<string> AllowedHosts { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When <c>true</c> (the default), resolve DNS host names so endpoints that <em>name</em> an
    /// internal host (rather than its IP literal) are also caught. When <c>false</c>, only IP-literal
    /// hosts are classified during the pre-send <see cref="OutboundEndpointGuard.Validate"/> check
    /// (connect-time enforcement in the transports always resolves regardless of this flag, on a
    /// direct connection). Through a transport's <c>TrustedProxy</c> there is no connect-time check
    /// of the destination: this flag then decides whether names are looked up locally at all, and
    /// with <c>false</c> names are left to the proxy's own filtering.
    /// </summary>
    public bool ResolveDnsNames { get; set; } = true;

    /// <summary>
    /// The asynchronous name lookup the guards built from this policy use in
    /// <see cref="OutboundEndpointGuard.ValidateAsync"/>; <c>null</c> (the default) for
    /// <see cref="System.Net.Dns.GetHostAddressesAsync(string, CancellationToken)"/>. A seam for tests.
    /// </summary>
    internal Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? ResolveDnsAsync { get; set; }
}
