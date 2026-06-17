using DidComm.Transports;

namespace DidComm.Transports.Http;

/// <summary>
/// Configuration knobs for <see cref="HttpDidCommTransport"/> (PRD §9.2 / FR-TRN-04..08).
/// Bound via DI as a typed options instance through
/// <c>builder.UseHttpTransport(opts =&gt; ...)</c>.
/// </summary>
public sealed class HttpTransportOptions
{
    /// <summary>Per-request timeout, applied via Polly's timeout policy. Defaults to 30 s.</summary>
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
    /// </remarks>
    public OutboundEndpointPolicy? OutboundEndpointPolicy { get; set; }
}
