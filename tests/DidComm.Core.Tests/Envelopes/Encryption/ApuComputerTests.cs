using DidComm.Jose;
using DidComm.Jose.Encryption;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Envelopes.Encryption;

public sealed class ApuComputerTests
{
    [Fact]
    public void Computes_base64url_of_utf8_skid_bytes()
    {
        const string skid = "did:example:alice#key-1";
        var expected = Base64Url.EncodeUtf8(skid);

        ApuComputer.Compute(skid).Should().Be(expected);
    }

    [Fact]
    public void Empty_skid_throws()
    {
        Action act = () => ApuComputer.Compute(string.Empty);
        act.Should().Throw<ArgumentException>();
    }
}
