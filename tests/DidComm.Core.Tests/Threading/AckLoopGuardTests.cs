using System.Text.Json.Nodes;
using DidComm.Messages;
using DidComm.Threading;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Threading;

public sealed class AckLoopGuardTests
{
    private static MessageBuilder Builder() => new MessageBuilder()
        .WithType("https://didcomm.org/empty/1.0/empty");

    [Fact]
    public void IsPureAck_recognises_ack_only_empty_envelope()
    {
        var m = Builder().WithAck("id-1").Build();
        AckLoopGuard.IsPureAck(m).Should().BeTrue();
    }

    [Fact]
    public void IsPureAck_false_when_message_has_body()
    {
        var m = Builder()
            .WithAck("id-1")
            .WithBody(JsonNode.Parse("""{"content":"hi"}""")!.AsObject())
            .Build();
        AckLoopGuard.IsPureAck(m).Should().BeFalse();
    }

    [Fact]
    public void IsPureAck_false_when_no_ack_header()
    {
        var m = Builder().Build();
        AckLoopGuard.IsPureAck(m).Should().BeFalse();
    }

    [Fact]
    public void RequestsAck_true_when_please_ack_present()
    {
        var m = Builder().WithPleaseAck().Build();
        AckLoopGuard.RequestsAck(m).Should().BeTrue();
    }

    [Fact]
    public void RequestsAck_false_when_please_ack_absent()
    {
        var m = Builder().Build();
        AckLoopGuard.RequestsAck(m).Should().BeFalse();
    }

    [Fact]
    public void IsSafeToSend_rejects_pure_ack_that_also_requests_ack()
    {
        // FR-THR-04 rule 2: pure ACK MUST NOT request an ACK back.
        var bad = Builder()
            .WithAck("id-prev")
            .WithPleaseAck()
            .Build();
        AckLoopGuard.IsSafeToSend(bad).Should().BeFalse();
    }

    [Fact]
    public void IsSafeToSend_allows_pure_ack_without_please_ack()
    {
        var ok = Builder().WithAck("id-prev").Build();
        AckLoopGuard.IsSafeToSend(ok).Should().BeTrue();
    }

    [Fact]
    public void IsSafeToSend_allows_substantive_message_that_requests_ack()
    {
        var ok = Builder()
            .WithBody(JsonNode.Parse("""{"content":"please ack this real message"}""")!.AsObject())
            .WithPleaseAck()
            .Build();
        AckLoopGuard.IsSafeToSend(ok).Should().BeTrue();
    }
}
