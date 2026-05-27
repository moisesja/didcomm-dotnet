using System.Text.Json;
using DidComm.Messages;
using DidComm.Protocols.Trace;
using FluentAssertions;
using Xunit;

// L-014.
using TraceConst = DidComm.Protocols.Trace.Trace;

namespace DidComm.Tests.Protocols.Trace;

public sealed class TraceObserverTests
{
    private static Message WithTraceHeader(string? reportUri, string? traceId = null)
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:peer:alice").WithTo("did:peer:bob")
            .Build();
        var headerObj = new Dictionary<string, object?>();
        if (reportUri is not null) headerObj[TraceConst.ReportUriField] = reportUri;
        if (traceId is not null) headerObj[TraceConst.TraceIdField] = traceId;
        var json = JsonSerializer.Serialize(headerObj);
        msg.AdditionalHeaders = new Dictionary<string, JsonElement>
        {
            [TraceConst.HeaderName] = JsonDocument.Parse(json).RootElement.Clone(),
        };
        return msg;
    }

    [Fact]
    public void Default_off_means_ShouldReport_returns_false()
    {
        var msg = WithTraceHeader("https://trace.example.com/report");
        var options = new TraceOptions(); // Enabled = false
        TraceObserver.ShouldReport(msg, options, out var uri).Should().BeFalse();
        uri.Should().BeNull();
    }

    [Fact]
    public void Enabled_plus_matching_allowlist_entry_returns_true_with_parsed_uri()
    {
        var msg = WithTraceHeader("https://trace.example.com/report");
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add("https://trace.example.com/report");
        TraceObserver.ShouldReport(msg, options, out var uri).Should().BeTrue();
        uri.Should().Be(new Uri("https://trace.example.com/report"));
    }

    [Fact]
    public void Enabled_plus_uri_not_on_allowlist_drops_silently()
    {
        var msg = WithTraceHeader("https://attacker.example.com/report");
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add("https://trace.example.com/report"); // different URI
        TraceObserver.ShouldReport(msg, options, out var uri).Should().BeFalse();
        uri.Should().BeNull();
    }

    [Fact]
    public void Message_without_trace_header_returns_false_even_when_enabled()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty").Build();
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add("https://trace.example.com/report");
        TraceObserver.ShouldReport(msg, options, out _).Should().BeFalse();
    }

    [Fact]
    public void Malformed_header_missing_report_uri_returns_false()
    {
        var msg = WithTraceHeader(reportUri: null, traceId: "abc"); // header object without report_uri
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add("https://trace.example.com/report");
        TraceObserver.ShouldReport(msg, options, out _).Should().BeFalse();
    }

    [Fact]
    public void Non_absolute_report_uri_returns_false()
    {
        var msg = WithTraceHeader("/relative/path");
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add("/relative/path");
        TraceObserver.ShouldReport(msg, options, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://trace.example.com", "https://trace.example.com/")]      // operator omits trailing slash; inbound has it
    [InlineData("https://trace.example.com/", "https://trace.example.com")]      // and vice versa
    [InlineData("https://trace.example.com:443/r", "https://trace.example.com/r")] // default port stripping
    [InlineData("HTTPS://Trace.Example.Com/r", "https://trace.example.com/r")]   // case differences in scheme/host
    public void Allowlist_match_is_normalised_via_AbsoluteUri(string allowlistEntry, string inboundReportUri)
    {
        // Bug fix: the previous Uri.ToString()-vs-raw-string comparison silently dropped legitimate
        // matches due to Uri normalisation (trailing slash, default-port stripping, case folding).
        // After the fix, both sides are compared via Uri.AbsoluteUri so these variants match.
        var msg = WithTraceHeader(inboundReportUri);
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add(allowlistEntry);
        TraceObserver.ShouldReport(msg, options, out var uri).Should().BeTrue(
            $"'{allowlistEntry}' and '{inboundReportUri}' canonicalise to the same AbsoluteUri");
        uri.Should().NotBeNull();
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.example.com/x")]
    public void Non_http_schemes_are_rejected_even_when_on_the_allowlist(string rawUri)
    {
        // Defense-in-depth: a typo'd or attacker-injected non-HTTP URI on the allowlist must not
        // unlock ShouldReport — FR-PROTO-11 trace-reports are HTTP/HTTPS only.
        var msg = WithTraceHeader(rawUri);
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add(rawUri);
        TraceObserver.ShouldReport(msg, options, out var uri).Should().BeFalse();
        uri.Should().BeNull();
    }

    [Theory]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://10.0.0.1/x")]
    [InlineData("http://192.168.0.1/x")]
    [InlineData("http://169.254.169.254/x")] // AWS / GCP metadata
    [InlineData("http://[::1]/x")]
    public void Loopback_or_private_ip_literal_is_rejected_even_when_on_the_allowlist(string rawUri)
    {
        // FR-PROTO-11a defense-in-depth: an operator typo (or compromise) that allowlists an
        // internal-IP literal must not turn the tracer into an SSRF amplifier.
        var msg = WithTraceHeader(rawUri);
        var options = new TraceOptions { Enabled = true };
        options.AllowedReportingUris.Add(rawUri);
        TraceObserver.ShouldReport(msg, options, out _).Should().BeFalse();
    }

    [Fact]
    public void BuildReportBody_includes_observed_metadata_and_trace_id()
    {
        var msg = WithTraceHeader("https://trace.example.com/report", traceId: "trace-xyz");
        msg.Thid = "thread-1";
        var options = new TraceOptions { Enabled = true, IncludeRoutingDetails = true };
        var body = TraceObserver.BuildReportBody(msg, options);
        body["observed_message_id"]!.GetValue<string>().Should().Be(msg.Id);
        body["observed_message_type"]!.GetValue<string>().Should().Be(msg.Type);
        body["observed_thid"]!.GetValue<string>().Should().Be("thread-1");
        body["trace_id"]!.GetValue<string>().Should().Be("trace-xyz");
        body["routing_detail_opt_in"]!.GetValue<bool>().Should().BeTrue();
    }
}
