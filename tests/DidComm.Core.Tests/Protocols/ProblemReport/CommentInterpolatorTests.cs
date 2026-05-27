using DidComm.Protocols.ProblemReport;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.ProblemReport;

public sealed class CommentInterpolatorTests
{
    [Fact]
    public void Spec_example_round_trips()
    {
        // The DIDComm v2.1 spec example: 1-based positional indices.
        var result = InvokeInterpolate(
            "{1} cannot be used because {2} does not respond.",
            new[] { "foo.com", "bar.com" });
        result.Should().Be("foo.com cannot be used because bar.com does not respond.");
    }

    [Fact]
    public void Missing_arg_renders_as_literal_question_mark()
    {
        var result = InvokeInterpolate("Need {1} and {2} and {3}.", new[] { "A", "B" });
        result.Should().Be("Need A and B and ?.");
    }

    [Fact]
    public void Extra_args_are_appended_in_extras_block()
    {
        var result = InvokeInterpolate("Only refs {1}.", new[] { "A", "B", "C" });
        result.Should().Be("Only refs A. [extra: B, C]");
    }

    [Fact]
    public void Null_comment_returns_empty_string()
    {
        InvokeInterpolate(null, new[] { "A" }).Should().Be(string.Empty);
    }

    [Fact]
    public void Null_or_empty_args_returns_comment_verbatim()
    {
        InvokeInterpolate("Hello.", null).Should().Be("Hello.");
        InvokeInterpolate("Hello.", Array.Empty<string>()).Should().Be("Hello.");
    }

    [Fact]
    public void Doubled_brace_escapes_to_literal_brace()
    {
        // `{{` → `{`, `}}` → `}`, matches String.Format. The arg X is unreferenced so it
        // appears in the trailing extras block per FR-PROTO-07.
        InvokeInterpolate("{{1}}", new[] { "X" }).Should().Be("{1} [extra: X]");
    }

    [Fact]
    public void Doubled_brace_escape_collapses_even_when_args_are_empty()
    {
        // Brace-collapse is uniform regardless of whether args were supplied — `{{` → `{`
        // and `}}` → `}` always (matches String.Format semantics).
        InvokeInterpolate("{{ok}}", null).Should().Be("{ok}");
        InvokeInterpolate("{{ok}}", Array.Empty<string>()).Should().Be("{ok}");
    }

    [Fact]
    public void Zero_or_negative_placeholder_renders_as_question_mark()
    {
        // `{0}` is not a valid 1-based positional reference; treat as missing-arg per FR-PROTO-07
        // so a 0-based-mindset caller doesn't leak the placeholder text into the rendered output.
        InvokeInterpolate("{0}", new[] { "X" }).Should().Be("? [extra: X]");
        InvokeInterpolate("{0}", Array.Empty<string>()).Should().Be("?");
    }

    [Fact]
    public void Non_positional_placeholder_passes_through_verbatim()
    {
        // `{foo}` is not a positional ref; pass through.
        InvokeInterpolate("see {foo} and {1}", new[] { "A" }).Should().Be("see {foo} and A");
    }

    [Fact]
    public void Unclosed_brace_does_not_crash()
    {
        InvokeInterpolate("ends with {", new[] { "A" }).Should().Be("ends with { [extra: A]");
    }

    private static string InvokeInterpolate(string? comment, IReadOnlyList<string>? args)
    {
        // CommentInterpolator is internal; reflect to call it from the test assembly.
        var method = typeof(CommentInterpolator).GetMethod(
            "Interpolate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return (string)method!.Invoke(null, new object?[] { comment, args })!;
    }
}
