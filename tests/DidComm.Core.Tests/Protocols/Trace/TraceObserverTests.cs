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
