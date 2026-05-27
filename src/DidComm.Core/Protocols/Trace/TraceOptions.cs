namespace DidComm.Protocols.Trace;

/// <summary>
/// Knobs for Trace 2.0 (FR-PROTO-11 / FR-PROTO-11a).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default.</strong> Operators that want to honor inbound <c>trace</c>
/// headers must set <see cref="Enabled"/> to <c>true</c> AND provide at least one entry in
/// <see cref="AllowedReportingUris"/>. The allowlist is required-when-enabled by design —
/// FR-PROTO-11a calls for "explicitly configured safeguards (privacy / loop protection)",
/// and a wide-open trace reporter that follows arbitrary <c>report_uri</c> values would
/// turn the library into an SSRF amplifier and a tracking-vector helper. Construction-time
/// validation (<see cref="Validate"/>) makes the misconfig loud.
/// </para>
/// </remarks>
public sealed class TraceOptions
{
    /// <summary>
    /// Whether to honor inbound <c>trace</c> headers at all. Default <c>false</c> per
    /// FR-PROTO-11a; flipping to <c>true</c> requires <see cref="AllowedReportingUris"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Exact-match allowlist of report URIs the trace observer is willing to POST to. An
    /// inbound <c>trace</c> header whose <c>report_uri</c> is not on this list is silently
    /// dropped, even when <see cref="Enabled"/> is <c>true</c>.
    /// </summary>
    public HashSet<string> AllowedReportingUris { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether emitted trace-reports include routing-layer detail (mediator hops, transport
    /// scheme). Defaults to <c>false</c>; turning on leaks operational metadata so consumers
    /// should opt in deliberately.
    /// </summary>
    public bool IncludeRoutingDetails { get; set; }

    /// <summary>
    /// Throws when the configuration violates FR-PROTO-11a's "explicitly configured
    /// safeguards" rule. Called from DI when the operator opts in via
    /// <c>DidCommBuilder.EnableTracing(...)</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <see cref="Enabled"/> is <c>true</c> and <see cref="AllowedReportingUris"/> is empty.</exception>
    public void Validate()
    {
        if (Enabled && AllowedReportingUris.Count == 0)
        {
            throw new InvalidOperationException(
                "TraceOptions.Enabled = true requires at least one entry in AllowedReportingUris (FR-PROTO-11a — 'explicitly configured safeguards'). "
                + "Add specific report_uri values you trust before enabling tracing.");
        }
    }
}
