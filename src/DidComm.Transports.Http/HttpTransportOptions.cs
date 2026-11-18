using DidComm.Transports;

namespace DidComm.Transports.Http;

/// <summary>
/// Configuration knobs for <see cref="HttpDidCommTransport"/> (PRD §9.2 / FR-TRN-04..08).
/// Bound via DI as a typed options instance through
/// <c>builder.UseHttpTransport(opts =&gt; ...)</c>.
/// </summary>
public sealed class HttpTransportOptions
{
    /// <summary>
    /// Per-request timeout, applied via Polly's timeout policy. Defaults to 30 s. It also bounds, on
    /// its own budget, the read of an in-band return-route response body (FR-RR-07): a body not
    /// received within it is discarded and the send reports <c>ResponseDiscarded</c>.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max retries on 5xx + timeout (FR-TRN-08). Defaults to 3.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay for exponential backoff between retries. Defaults to 1 s; jittered ±50 % at runtime.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Number of consecutive failures before the circuit opens. Defaults to 5. Values below 2 are clamped up to 2 (Polly's minimum throughput floor).</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>How long the circuit stays open after tripping. Defaults to 30 s.</summary>
    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Allowed URI schemes (lowercase). Defaults to <c>{"https"}</c> per PRD §9.2; tests and
    /// dev loopbacks can opt in to <c>"http"</c> explicitly. Prevents accidentally sending an
    /// envelope over plaintext.
    /// </summary>
    public IReadOnlyList<string> AllowedSchemes { get; set; } = new[] { "https" };

    /// <summary>Max number of 307 redirects to follow before throwing. Defaults to 5.</summary>
    public int MaxRedirectHops { get; set; } = 5;

    /// <summary>
    /// SSRF-defense policy enforced at TCP connect time (via the named client's
    /// <c>SocketsHttpHandler.ConnectCallback</c>). Because it pins each connection — including every
    /// followed 307 redirect — to a vetted IP, it also defeats redirect-to-internal and DNS rebinding.
    /// </summary>
    /// <remarks>
    /// <c>null</c> (the default) means <b>inherit</b> the single source of truth,
    /// <c>DidCommOptions.OutboundEndpointPolicy</c> (which itself defaults to blocking private /
    /// loopback / link-local / metadata destinations), so configuring the policy in one place applies
    /// to the pre-send check and the transport connect-time pin alike (#27). Set a non-null value only
    /// to give this transport a policy distinct from the core one.
    /// <para>
    /// The connect-time pin applies to direct connections. With <see cref="TrustedProxy"/> set, the
    /// connection goes to the proxy instead; see there for what is still checked.
    /// </para>
    /// </remarks>
    public OutboundEndpointPolicy? OutboundEndpointPolicy { get; set; }

    /// <summary>
    /// A proxy you trust to carry this transport's connections. <c>null</c> (the default) means
    /// every connection is <b>direct</b>: the transport never uses a proxy from the environment
    /// (<c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c>, <c>ALL_PROXY</c>, <see cref="HttpClient.DefaultProxy"/>
    /// or the operating system's proxy settings). Set it when this host can reach other agents only
    /// through a proxy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the host takes on.</b> Once a connection goes through a proxy, the library can no
    /// longer see where it ends up: the proxy opens the connection to the destination and does its
    /// own name lookup. Your proxy must therefore refuse private, loopback, link-local and
    /// cloud-metadata destinations (egress filtering). A DID document's service endpoint is written
    /// by whoever controls that DID, so without that filtering a peer could have your proxy reach
    /// your internal network.
    /// </para>
    /// <para>
    /// <b>What the library still checks</b>, in the transport, before each send and each followed
    /// 307 redirect: the scheme (<see cref="AllowedSchemes"/>), an IP-literal destination (refused
    /// when the <see cref="OutboundEndpointPolicy"/> blocks it), and, while
    /// <see cref="Transports.OutboundEndpointPolicy.ResolveDnsNames"/> is on, a local lookup of the
    /// destination's name. That lookup is best effort, because the proxy looks the name up again; a
    /// name that does not resolve locally is refused. A refusal is a
    /// <see cref="Exceptions.TransportException"/>, thrown before the proxy is contacted and never
    /// retried. Where only the proxy can resolve names, set <c>ResolveDnsNames = false</c> (on
    /// <c>DidCommOptions.OutboundEndpointPolicy</c>, which <c>DidCommClient</c> checks too); then only
    /// IP literals are checked here.
    /// </para>
    /// <para>
    /// <b>The proxy itself</b> is your explicit choice, so its own address is never refused, even
    /// on a private or loopback network; don't add it to
    /// <see cref="Transports.OutboundEndpointPolicy.AllowedHosts"/> (that would also allow it as a
    /// destination). Supported schemes: <c>http</c>, <c>https</c>, <c>socks4</c>, <c>socks4a</c>,
    /// <c>socks5</c>. Put credentials in <see cref="System.Net.WebProxy.Credentials"/> (or set
    /// <see cref="System.Net.WebProxy.UseDefaultCredentials"/>); an address carrying a user name or
    /// password is refused. A destination on <see cref="System.Net.WebProxy.BypassList"/> (or a
    /// local one with <see cref="System.Net.WebProxy.BypassProxyOnLocal"/>) is connected to directly,
    /// with the full connect-time check, as when no proxy is set, even when it names the proxy's own
    /// host and port: only a connection that uses the proxy as a proxy is exempt.
    /// </para>
    /// <para>
    /// The value is checked when the transport is created and copied once per container, so later
    /// changes to the same <see cref="System.Net.WebProxy"/> instance have no effect. An invalid
    /// value throws <see cref="InvalidOperationException"/>. When the host replaces the
    /// <c>"didcomm"</c> client's primary handler, that handler decides how and where to connect
    /// (the proxy is not applied), and the destination checks above still run.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.UseHttpTransport(o => o.TrustedProxy = new WebProxy("http://egress.internal:3128"));
    /// </code>
    /// </example>
    public System.Net.WebProxy? TrustedProxy { get; set; }
}
