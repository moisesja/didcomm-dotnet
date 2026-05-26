namespace DidComm.Protocols.Trace;

/// <summary>
/// Trace 2.0 (FR-PROTO-11) protocol constants. The protocol carries a <c>trace</c> message
/// header on regular messages; when a recipient honors it (only when explicitly enabled per
/// FR-PROTO-11a), it POSTs a <c>trace-report</c> to the URI carried in the header.
/// </summary>
public static class Trace
{
    /// <summary>Protocol identifier URI for Trace 2.0.</summary>
    public const string ProtocolUri = "https://didcomm.org/trace/2.0";

    /// <summary>Message type URI for a trace-report message.</summary>
    public const string TraceReportType = "https://didcomm.org/trace/2.0/trace-report";

    /// <summary>The DIDComm header name that signals a trace request — read off <see cref="DidComm.Messages.Message.AdditionalHeaders"/>.</summary>
    public const string HeaderName = "trace";

    /// <summary>The header's REQUIRED member naming the URI to which the trace-report should be POSTed.</summary>
    public const string ReportUriField = "report_uri";

    /// <summary>The header's OPTIONAL trace identifier (correlation token).</summary>
    public const string TraceIdField = "trace_id";
}
