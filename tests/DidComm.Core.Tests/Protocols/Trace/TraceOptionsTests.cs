using DidComm.Protocols.Trace;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Trace;

public sealed class TraceOptionsTests
{
    [Fact]
    public void Default_is_off()
    {
        var o = new TraceOptions();
        o.Enabled.Should().BeFalse();
        o.AllowedReportingUris.Should().BeEmpty();
        o.IncludeRoutingDetails.Should().BeFalse();
    }

    [Fact]
    public void Validate_passes_when_disabled_with_empty_allowlist()
    {
        // FR-PROTO-11a default-off path: nothing to validate when Enabled = false.
        var o = new TraceOptions();
        o.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_throws_when_enabled_without_allowlist()
    {
        var o = new TraceOptions { Enabled = true };
        o.Invoking(x => x.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*FR-PROTO-11a*");
    }

    [Fact]
    public void Validate_passes_when_enabled_with_at_least_one_allowed_uri()
    {
        var o = new TraceOptions { Enabled = true };
        o.AllowedReportingUris.Add("https://trace.example.com/report");
        o.Invoking(x => x.Validate()).Should().NotThrow();
    }
}
