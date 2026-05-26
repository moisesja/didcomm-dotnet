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
}
