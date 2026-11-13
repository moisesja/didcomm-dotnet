using DidComm.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Observability;

/// <summary>#95 AD-4: the one rule every package uses to write a stranger's message id into a log line.</summary>
public sealed class LogValuesTests
{
    [Fact]
    public void An_id_up_to_128_characters_is_written_as_it_is_and_a_longer_one_by_its_length()
    {
        LogValues.Id(null).Should().Be("(none)");
        LogValues.Id("").Should().Be("");
        LogValues.Id(new string('a', 128)).Should().Be(new string('a', 128));
        LogValues.Id(new string('a', 129)).Should().Be("(id of 129 characters)");
        LogValues.Id(new string('a', 200_000)).Should().Be("(id of 200000 characters)");
    }

    [Fact]
    public void The_innermost_type_name_walks_the_whole_chain()
    {
        var chain = new InvalidOperationException("outer", new AggregateException(new FormatException("inner")));

        LogValues.InnermostTypeName(chain).Should().Be(nameof(FormatException));
        LogValues.InnermostTypeName(new TimeoutException()).Should().Be(nameof(TimeoutException));
    }
}
