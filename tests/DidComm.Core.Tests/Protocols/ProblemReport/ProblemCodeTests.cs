using DidComm.Exceptions;
using DidComm.Protocols.ProblemReport;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.ProblemReport;

public sealed class ProblemCodeTests
{
    [Theory]
    [InlineData("e.p.xfer", "e", "p", "xfer")]
    [InlineData("e.p.xfer.cant-use-endpoint", "e", "p", "xfer.cant-use-endpoint")]
    [InlineData("w.m.req.expired", "w", "m", "req.expired")]
    [InlineData("e.p.me.res.net.unreachable", "e", "p", "me.res.net.unreachable")]
    [InlineData("e.deliver.xfer.failed", "e", "deliver", "xfer.failed")] // state-name scope
    public void Parse_splits_into_sorter_scope_descriptor(string input, string sorter, string scope, string descriptor)
    {
        var code = ProblemCode.Parse(input);
        code.Sorter.Should().Be(sorter);
        code.Scope.Should().Be(scope);
        code.Descriptor.Should().Be(descriptor);
        code.Value.Should().Be(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notacode")]
    [InlineData("e.p")]            // missing descriptor
    [InlineData("z.p.xfer")]       // bad sorter
    [InlineData("e..xfer")]        // empty scope
    [InlineData("e.p..xfer")]      // empty descriptor segment
    public void TryParse_returns_false_on_malformed_input(string? input)
    {
        ProblemCode.TryParse(input, out var code).Should().BeFalse();
        code.Should().BeNull();
    }

    [Fact]
    public void Parse_throws_ProtocolException_on_malformed_input()
    {
        ((Action)(() => ProblemCode.Parse("not.valid"))).Should().Throw<ProtocolException>().WithMessage("*FR-PROTO-08*");
    }

    [Theory]
    [InlineData("e.p.xfer.cant-use-endpoint", "e.p.xfer", true)]
    [InlineData("e.p.xfer.cant-use-endpoint", "e.p", true)]
    [InlineData("e.p.xfer.cant-use-endpoint", "e", true)]
    [InlineData("e.p.xfer.cant-use-endpoint", "e.p.xfer.cant-use-endpoint", true)]
    [InlineData("e.p.xfer.cant-use-endpoint", "e.p.xferable", false)] // not a structural prefix
    [InlineData("e.p.xfer.cant-use-endpoint", "w.p.xfer", false)]     // sorter mismatch
    [InlineData("e.p.xfer.cant-use-endpoint", "e.m.xfer", false)]     // scope mismatch
    [InlineData("e.p.xfer", "e.p.xfer.cant", false)]                  // prefix longer than code
    public void StartsWith_implements_structural_prefix(string codeStr, string prefix, bool expected)
    {
        ProblemCode.Parse(codeStr).StartsWith(prefix).Should().Be(expected);
    }

    [Fact]
    public void IsError_and_IsWarning_are_mutually_exclusive()
    {
        ProblemCode.Parse("e.p.xfer").IsError.Should().BeTrue();
        ProblemCode.Parse("w.p.xfer").IsWarning.Should().BeTrue();
        ProblemCode.Parse("e.p.xfer").IsWarning.Should().BeFalse();
        ProblemCode.Parse("w.p.xfer").IsError.Should().BeFalse();
    }

    [Fact]
    public void Scope_flags_recognise_p_and_m()
    {
        ProblemCode.Parse("e.p.xfer").IsProtocolScoped.Should().BeTrue();
        ProblemCode.Parse("e.m.xfer").IsMessageScoped.Should().BeTrue();
        ProblemCode.Parse("e.somestate.xfer").IsProtocolScoped.Should().BeFalse();
        ProblemCode.Parse("e.somestate.xfer").IsMessageScoped.Should().BeFalse();
    }
}
