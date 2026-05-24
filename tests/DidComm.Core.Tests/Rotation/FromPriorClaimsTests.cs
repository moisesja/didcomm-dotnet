using DidComm.Protocols.Rotation;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Rotation;

public sealed class FromPriorClaimsTests
{
    [Fact]
    public void Equality_StructurallyEqualClaims_AreEqual()
    {
        var a = new FromPriorClaims("did:example:new", "did:example:prior", 1700000000);
        var b = new FromPriorClaims("did:example:new", "did:example:prior", 1700000000);

        a.Should().Be(b);
    }

    [Fact]
    public void Inequality_DiffersByIat()
    {
        var a = new FromPriorClaims("did:example:new", "did:example:prior", 1700000000);
        var b = new FromPriorClaims("did:example:new", "did:example:prior", 1700000001);

        a.Should().NotBe(b);
    }
}
