namespace DidComm.Protocols.ProblemReport;

/// <summary>
/// Knobs for the Report Problem 2.0 handler.
/// </summary>
public sealed class ProblemReportOptions
{
    /// <summary>
    /// The per-thread error budget before the FR-PROTO-10 cascade guard fires. Once a single
    /// thread accumulates more than this many inbound error problem-reports, the handler
    /// emits one <c>e.p.req.max-errors-exceeded</c> notice and stops responding on that
    /// thread.
    /// </summary>
    /// <remarks>
    /// Default is <c>5</c>, matching <c>sicpa-dlab/didcomm-python</c>. The spec is silent on
    /// the exact threshold; this trades off "stop quickly" against "tolerate transient bursts".
    /// </remarks>
    public int CascadeThreshold { get; set; } = 5;

    /// <summary>
    /// Validate the options. Called by <see cref="ProblemReportHandler"/>'s ctor so a misconfig
    /// surfaces loudly at DI resolution rather than as silently-degraded cascade-guard behaviour.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <see cref="CascadeThreshold"/> is negative or equals <see cref="int.MaxValue"/> (the handler does <c>count &gt; CascadeThreshold</c>; <c>int.MaxValue</c> makes the trip unreachable).</exception>
    public void Validate()
    {
        if (CascadeThreshold < 0)
            throw new InvalidOperationException(
                $"ProblemReportOptions.CascadeThreshold must be >= 0 (got {CascadeThreshold}). FR-PROTO-10 requires a bounded per-thread error budget.");
        if (CascadeThreshold == int.MaxValue)
            throw new InvalidOperationException(
                "ProblemReportOptions.CascadeThreshold cannot equal int.MaxValue — the cascade-trip comparison would never fire.");
    }
}
