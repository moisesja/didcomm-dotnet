using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports;

/// <summary>FR-RR-06: recognising <c>didcomm:transport/queue</c>; the refusal text names no DID.</summary>
public sealed class QueueTransportTests
{
    [Fact]
    public void Uri_constant_is_the_spec_value()
    {
        QueueTransport.Uri.Should().Be("didcomm:transport/queue");
    }

    [Theory]
    [InlineData("didcomm:transport/queue", true)]
    [InlineData("DIDComm:transport/queue", false)]
    [InlineData("didcomm:transport/queue/", false)]
    [InlineData("https://example.com/didcomm:transport/queue", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void String_form_is_an_ordinal_match(string? uri, bool expected)
    {
        QueueTransport.IsQueueTransport(uri).Should().Be(expected);
    }

    [Fact]
    public void Uri_form_matches_the_absolute_queue_uri_only()
    {
        Uri.TryCreate("didcomm:transport/queue", UriKind.Absolute, out var queue).Should().BeTrue();
        QueueTransport.IsQueueTransport(queue).Should().BeTrue();
        queue!.Scheme.Should().Be("didcomm");

        QueueTransport.IsQueueTransport(new Uri("https://agents.r.us/inbox")).Should().BeFalse();
        QueueTransport.IsQueueTransport(new Uri("transport/queue", UriKind.Relative)).Should().BeFalse();
        QueueTransport.IsQueueTransport((Uri?)null).Should().BeFalse();
    }

    [Fact]
    public void Refusal_message_names_no_DID()
    {
        QueueTransport.RefusalMessage.Should().NotContain("did:");
        QueueTransport.RefusalMessage.Should().Contain("didcomm:transport/queue").And.Contain("FR-RR-06");
    }
}
