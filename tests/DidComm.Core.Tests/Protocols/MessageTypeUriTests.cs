using DidComm.Exceptions;
using DidComm.Protocols;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols;

public sealed class MessageTypeUriTests
{
    [Theory]
    [InlineData("https://didcomm.org/routing/2.0/forward",
                "https://didcomm.org", "routing", 2, 0, "forward")]
    [InlineData("https://didcomm.org/trust-ping/2.0/ping-response",
                "https://didcomm.org", "trust-ping", 2, 0, "ping-response")]
    [InlineData("https://didcomm.org/empty/1.0/empty",
                "https://didcomm.org", "empty", 1, 0, "empty")]
    [InlineData("https://didcomm.org/report-problem/2.0/problem-report",
                "https://didcomm.org", "report-problem", 2, 0, "problem-report")]
    [InlineData("http://example.com/protocols/lets_do_lunch/1.0/proposal",
                "http://example.com/protocols", "lets_do_lunch", 1, 0, "proposal")]
    public void Parse_captures_the_four_components(
        string uri, string docUri, string protocol, int major, int minor, string message)
    {
        var parsed = MessageTypeUri.Parse(uri);

        parsed.DocUri.Should().Be(docUri);
        parsed.ProtocolName.Should().Be(protocol);
        parsed.Version.Should().Be(new ProtocolVersion(major, minor));
        parsed.MessageType.Should().Be(message);
        parsed.Value.Should().Be(uri);
        parsed.ProtocolIdentifier.Should().Be($"{docUri}/{protocol}/{major}.{minor}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("https://didcomm.org/routing/forward")]            // no version
    [InlineData("https://didcomm.org/routing/2/forward")]          // bare major
    [InlineData("https://didcomm.org/routing/2.0")]                // no message
    public void Invalid_inputs_are_rejected(string value)
    {
        MessageTypeUri.IsValid(value).Should().BeFalse();
        Action act = () => MessageTypeUri.Parse(value);
        act.Should().Throw<ProtocolException>();
    }

    [Fact]
    public void Matches_is_case_and_punctuation_insensitive_on_protocol_and_message()
    {
        var a = MessageTypeUri.Parse("https://didcomm.org/trust-ping/2.0/ping-response");
        var b = MessageTypeUri.Parse("https://DIDCOMM.org/trustping/2.0/PINGRESPONSE");

        a.Matches(b).Should().BeTrue();
    }

    [Fact]
    public void Matches_requires_compatible_major()
    {
        var v2 = MessageTypeUri.Parse("https://didcomm.org/routing/2.0/forward");
        var v3 = MessageTypeUri.Parse("https://didcomm.org/routing/3.0/forward");

        v2.Matches(v3).Should().BeFalse();
    }
}
