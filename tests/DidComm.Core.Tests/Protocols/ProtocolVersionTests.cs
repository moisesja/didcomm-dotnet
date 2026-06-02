using DidComm.Protocols;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols;

public sealed class ProtocolVersionTests
{
    [Theory]
    [InlineData("2.0", 2, 0)]
    [InlineData("2.1", 2, 1)]
    [InlineData("10.42", 10, 42)]
    public void TryParse_accepts_well_formed_versions(string input, int major, int minor)
    {
        ProtocolVersion.TryParse(input, out var v).Should().BeTrue();
        v.Should().Be(new ProtocolVersion(major, minor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("2.")]
    [InlineData(".1")]
    [InlineData("2.1.0")]
    [InlineData("a.b")]
    [InlineData("2.1-rc1")]
    [InlineData("02.0")]   // leading zero is non-canonical
    [InlineData("2.00")]
    [InlineData("007.1")]
    public void TryParse_rejects_malformed(string input)
    {
        ProtocolVersion.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Same_major_is_compatible_and_negotiates_to_min_minor()
    {
        var a = new ProtocolVersion(2, 3);
        var b = new ProtocolVersion(2, 1);

        a.IsCompatibleWith(b).Should().BeTrue();
        a.NegotiateWith(b).Should().Be(new ProtocolVersion(2, 1));
        b.NegotiateWith(a).Should().Be(new ProtocolVersion(2, 1));
    }

    [Fact]
    public void Different_major_is_not_compatible()
    {
        var v2 = new ProtocolVersion(2, 0);
        var v3 = new ProtocolVersion(3, 0);
        v2.IsCompatibleWith(v3).Should().BeFalse();
        v2.NegotiateWith(v3).Should().BeNull();
    }
}
