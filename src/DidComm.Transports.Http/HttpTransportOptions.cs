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

    /// <summary>Number of consecutive failures before the circuit opens. Defaults to 5.</summary>
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
}
