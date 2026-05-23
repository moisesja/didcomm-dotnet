using System.Text;
using System.Text.Json.Nodes;
using DidComm.Json;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Json;

public sealed class DeterministicJsonTests
{
    [Fact]
    public void Member_order_is_normalized_to_ascii_lexicographic()
    {
        var orderA = JsonNode.Parse(@"{""c"":3,""a"":1,""b"":2}")!;
        var orderB = JsonNode.Parse(@"{""b"":2,""a"":1,""c"":3}")!;

        var bytesA = DeterministicJsonWriter.WriteUtf8(orderA);
        var bytesB = DeterministicJsonWriter.WriteUtf8(orderB);

        bytesA.Should().Equal(bytesB);
        Encoding.UTF8.GetString(bytesA).Should().Be(@"{""a"":1,""b"":2,""c"":3}");
    }

    [Fact]
    public void Sorting_recurses_through_nested_objects()
    {
        var node = JsonNode.Parse(@"{""z"":{""y"":2,""x"":1},""a"":[{""k"":2,""j"":1}]}")!;

        var canonical = DeterministicJsonWriter.WriteString(node);

        canonical.Should().Be(@"{""a"":[{""j"":1,""k"":2}],""z"":{""x"":1,""y"":2}}");
    }

    [Fact]
    public void Incidental_whitespace_does_not_change_output()
    {
        var pretty = JsonNode.Parse("{\n  \"b\": 2,\n  \"a\": 1\n}")!;
        var compact = JsonNode.Parse(@"{""a"":1,""b"":2}")!;

        DeterministicJsonWriter.WriteUtf8(pretty)
            .Should().Equal(DeterministicJsonWriter.WriteUtf8(compact));
    }

    [Fact]
    public void Primitives_array_and_null_pass_through()
    {
        var node = JsonNode.Parse(@"{""nums"":[1,2,3],""s"":""hi"",""b"":true,""n"":null}")!;
        DeterministicJsonWriter.WriteString(node)
            .Should().Be(@"{""b"":true,""n"":null,""nums"":[1,2,3],""s"":""hi""}");
    }
}
