using DidComm.Exceptions;
using DidComm.Protocols.ProblemReport;
using FluentAssertions;
using Xunit;

// L-014: alias the static API class to dodge namespace shadowing.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.Tests.Protocols.ProblemReport;

public sealed class ProblemReportTests
{
    [Fact]
    public void Create_emits_spec_body_shape()
    {
        var msg = ProblemReportApi.Create(
            from: "did:peer:bob",
            to: "did:peer:alice",
            code: "e.p.xfer.cant-use-endpoint",
            pthid: "failing-thread-id",
            comment: "Unable to use the {1} endpoint for {2}.",
            args: new[] { "https://agents.r.us/inbox", "did:peer:bob" },
            escalateTo: "mailto:admin@example.com");

        msg.Type.Should().Be(ProblemReportApi.MessageType);
        msg.Pthid.Should().Be("failing-thread-id");
        msg.From.Should().Be("did:peer:bob");
        msg.To.Should().Equal("did:peer:alice");
        msg.Body!["code"]!.GetValue<string>().Should().Be("e.p.xfer.cant-use-endpoint");
        msg.Body["comment"]!.GetValue<string>().Should().Be("Unable to use the {1} endpoint for {2}.");
        msg.Body["args"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("https://agents.r.us/inbox", "did:peer:bob");
        msg.Body["escalate_to"]!.GetValue<string>().Should().Be("mailto:admin@example.com");
    }

    [Fact]
    public void Create_rejects_malformed_code_eagerly()
    {
        ((Action)(() => ProblemReportApi.Create(
                from: "did:peer:bob", to: "did:peer:alice",
                code: "not.valid", pthid: "pthid")))
            .Should().Throw<ProtocolException>().WithMessage("*FR-PROTO-08*");
    }

    [Fact]
    public void Create_with_ack_makes_it_an_explicit_ACK()
    {
        var msg = ProblemReportApi.Create(
            from: "did:peer:bob", to: "did:peer:alice",
            code: "w.m.req.expired", pthid: "pthid",
            ack: new[] { "prev-id-1", "prev-id-2" });
        msg.Ack.Should().Equal("prev-id-1", "prev-id-2");
    }

    [Fact]
    public void RenderComment_interpolates_args_at_read_time()
    {
        var msg = ProblemReportApi.Create(
            from: "did:peer:bob", to: "did:peer:alice",
            code: "e.p.xfer.cant-use-endpoint", pthid: "pthid",
            comment: "Cannot use {1} for {2}.",
            args: new[] { "https://x", "did:peer:bob" });
        ProblemReportApi.RenderComment(msg).Should().Be("Cannot use https://x for did:peer:bob.");
    }

    [Fact]
    public void Read_helpers_return_empty_when_body_absent()
    {
        var msg = new DidComm.Messages.MessageBuilder()
            .WithType(ProblemReportApi.MessageType)
            .WithPthid("pthid")
            .Build();
        ProblemReportApi.ReadCode(msg).Should().BeNull();
        ProblemReportApi.ReadComment(msg).Should().BeNull();
        ProblemReportApi.ReadArgs(msg).Should().BeEmpty();
        ProblemReportApi.RenderComment(msg).Should().Be(string.Empty);
    }

    [Fact]
    public void Escalate_promotes_warning_to_error_with_preserved_scope()
    {
        var warning = ProblemCode.Parse("w.m.xfer.slow");
        var msg = ProblemReportApi.Escalate(
            from: "did:peer:bob", to: "did:peer:alice",
            originalCode: warning,
            escalatedDescriptor: "xfer.failed",
            pthid: "pthid");
        // Per FR-PROTO-09: reply scope ≥ original. We preserve the original scope here.
        ProblemReportApi.ReadCode(msg).Should().Be("e.m.xfer.failed");
    }

    [Fact]
    public void Escalate_rejects_non_warning_input()
    {
        var error = ProblemCode.Parse("e.p.xfer.cant");
        ((Action)(() => ProblemReportApi.Escalate(
                from: "did:peer:bob", to: "did:peer:alice",
                originalCode: error,
                escalatedDescriptor: "xfer.failed",
                pthid: "pthid")))
            .Should().Throw<ArgumentException>().WithMessage("*FR-PROTO-09*");
    }
}
