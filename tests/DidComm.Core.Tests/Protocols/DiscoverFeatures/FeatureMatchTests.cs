using DidComm.Protocols.DiscoverFeatures;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.DiscoverFeatures;

public sealed class FeatureMatchTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("*", "https://didcomm.org/trust-ping/2.0", true)]
    [InlineData("https://didcomm.org/*", "https://didcomm.org/trust-ping/2.0", true)]
    [InlineData("https://didcomm.org/*", "https://didcomm.org/empty/1.0", true)]
    [InlineData("https://didcomm.org/*", "https://aries-rfcs.org/x", false)]
    [InlineData("https://didcomm.org/trust-ping/2.0", "https://didcomm.org/trust-ping/2.0", true)]
    [InlineData("https://didcomm.org/trust-ping/2.0", "https://didcomm.org/trust-ping/2.1", false)]
    [InlineData("", "anything", false)]
    public void Matches_implements_spec_wildcard_semantics(string match, string value, bool expected)
    {
        FeatureMatch.Matches(match, value).Should().Be(expected);
    }
}
