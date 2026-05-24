using DidComm.Exceptions;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Exceptions;

public sealed class Phase3ExceptionsTests
{
    [Fact]
    public void UnsupportedDidMethodException_CarriesMethodDidAndReason()
    {
        var ex = new UnsupportedDidMethodException("web", "did:web:example.com", "rejected per DD-08");

        ex.Method.Should().Be("web");
        ex.Did.Should().Be("did:web:example.com");
        ex.Reason.Should().Be("rejected per DD-08");
        ex.Message.Should().Contain("web").And.Contain("did:web:example.com");
        ex.Should().BeAssignableTo<DidCommException>();
    }

    [Fact]
    public void DidResolutionException_FormatsDidAndReason()
    {
        var ex = new DidResolutionException("did:key:zUnknown", "method unknown");

        ex.Did.Should().Be("did:key:zUnknown");
        ex.Reason.Should().Be("method unknown");
        ex.Message.Should().Contain("did:key:zUnknown").And.Contain("method unknown");
        ex.Should().BeAssignableTo<DidCommException>();
    }

    [Fact]
    public void DidResolutionException_CarriesInnerException()
    {
        var inner = new InvalidOperationException("upstream");
        var ex = new DidResolutionException("did:peer:0zXyz", "boom", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void SecretNotFoundException_CarriesKid()
    {
        var ex = new SecretNotFoundException("did:example:alice#missing");

        ex.Kid.Should().Be("did:example:alice#missing");
        ex.Message.Should().Contain("did:example:alice#missing");
        ex.Should().BeAssignableTo<DidCommException>();
    }
}
