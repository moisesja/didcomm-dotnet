using System.Text.Json;
using DidComm.Consistency;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// FR-RR-01 parse table for <see cref="ReturnRouteHeader.TryRead"/>: every row of the plan's table,
/// "never throws" over every <see cref="JsonValueKind"/>, and the thread cap pinned to the DID cap.
/// </summary>
public sealed class ReturnRouteHeaderTests
{
    private static Message WithHeaders(params (string Key, string Json)[] headers)
    {
        var msg = new Message { Id = "m1", Type = "https://didcomm.org/test/1.0/m" };
        if (headers.Length > 0)
        {
            msg.AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, json) in headers)
                msg.AdditionalHeaders[key] = JsonDocument.Parse(json).RootElement.Clone();
        }
        return msg;
    }

    [Fact]
    public void Absent_headers_dictionary_is_None_and_well_formed()
    {
        var msg = WithHeaders();
        msg.AdditionalHeaders.Should().BeNull();

        ReturnRouteHeader.TryRead(msg, out var header).Should().BeTrue();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Fact]
    public void Absent_return_route_key_is_None_and_well_formed()
    {
        ReturnRouteHeader.TryRead(WithHeaders(("other", "\"x\"")), out var header).Should().BeTrue();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Fact]
    public void Explicit_none_is_None_and_well_formed()
    {
        ReturnRouteHeader.TryRead(WithHeaders(("return_route", "\"none\"")), out var header).Should().BeTrue();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Fact]
    public void All_parses_with_a_null_thread()
    {
        ReturnRouteHeader.TryRead(WithHeaders(("return_route", "\"all\"")), out var header).Should().BeTrue();
        header.Mode.Should().Be(ReturnRouteMode.All);
        header.Thread.Should().BeNull();
    }

    [Fact]
    public void Thread_with_a_thread_value_parses()
    {
        var msg = WithHeaders(("return_route", "\"thread\""), ("return_route_thread", "\"thid-1\""));
        ReturnRouteHeader.TryRead(msg, out var header).Should().BeTrue();
        header.Should().Be(new ReturnRouteHeader(ReturnRouteMode.Thread, "thid-1"));
    }

    [Fact]
    public void Thread_without_a_thread_value_is_malformed_None()
    {
        ReturnRouteHeader.TryRead(WithHeaders(("return_route", "\"thread\"")), out var header).Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[\"a\"]")]
    public void Thread_with_a_non_string_thread_value_is_malformed_None(string threadJson)
    {
        var msg = WithHeaders(("return_route", "\"thread\""), ("return_route_thread", threadJson));
        ReturnRouteHeader.TryRead(msg, out var header).Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Thread_with_a_blank_thread_value_is_malformed_None(string threadJson)
    {
        var msg = WithHeaders(("return_route", "\"thread\""), ("return_route_thread", threadJson));
        ReturnRouteHeader.TryRead(msg, out var header).Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Fact]
    public void Thread_at_the_cap_parses_and_one_over_is_malformed_None()
    {
        var atCap = new string('a', ReturnRouteHeader.MaxThreadLength);
        var overCap = new string('a', ReturnRouteHeader.MaxThreadLength + 1);

        ReturnRouteHeader.TryRead(WithHeaders(("return_route", "\"thread\""), ("return_route_thread", $"\"{atCap}\"")), out var ok).Should().BeTrue();
        ok.Mode.Should().Be(ReturnRouteMode.Thread);
        ok.Thread.Should().HaveLength(2048);

        ReturnRouteHeader.TryRead(WithHeaders(("return_route", "\"thread\""), ("return_route_thread", $"\"{overCap}\"")), out var bad).Should().BeFalse();
        bad.Should().Be(ReturnRouteHeader.None);
    }

    [Theory]
    [InlineData("\"All\"")]
    [InlineData("\"\"")]
    [InlineData("\"ALL \"")]
    [InlineData("\"thread \"")]
    public void Other_strings_are_case_sensitive_malformed_None(string valueJson)
    {
        ReturnRouteHeader.TryRead(WithHeaders(("return_route", valueJson)), out var header).Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("{\"a\":1}")]
    [InlineData("[\"all\"]")]
    public void Non_string_kinds_are_malformed_None(string valueJson)
    {
        ReturnRouteHeader.TryRead(WithHeaders(("return_route", valueJson)), out var header).Should().BeFalse();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Theory]
    [InlineData(JsonValueKind.Undefined)]
    [InlineData(JsonValueKind.Object)]
    [InlineData(JsonValueKind.Array)]
    [InlineData(JsonValueKind.String)]
    [InlineData(JsonValueKind.Number)]
    [InlineData(JsonValueKind.True)]
    [InlineData(JsonValueKind.False)]
    [InlineData(JsonValueKind.Null)]
    public void TryRead_never_throws_for_any_JsonValueKind_in_either_header(JsonValueKind kind)
    {
        var element = kind switch
        {
            JsonValueKind.Undefined => default,
            JsonValueKind.Object => JsonDocument.Parse("{}").RootElement.Clone(),
            JsonValueKind.Array => JsonDocument.Parse("[]").RootElement.Clone(),
            JsonValueKind.String => JsonDocument.Parse("\"thread\"").RootElement.Clone(),
            JsonValueKind.Number => JsonDocument.Parse("7").RootElement.Clone(),
            JsonValueKind.True => JsonDocument.Parse("true").RootElement.Clone(),
            JsonValueKind.False => JsonDocument.Parse("false").RootElement.Clone(),
            _ => JsonDocument.Parse("null").RootElement.Clone(),
        };
        var msg = new Message
        {
            Id = "m",
            Type = "https://didcomm.org/test/1.0/m",
            AdditionalHeaders = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["return_route"] = element,
                ["return_route_thread"] = element,
            },
        };

        var act = () => ReturnRouteHeader.TryRead(msg, out _);
        act.Should().NotThrow();
        ReturnRouteHeader.TryRead(msg, out var header);
        header.Mode.Should().NotBe(ReturnRouteMode.All);
    }

    [Fact]
    public void TryRead_throws_only_for_a_null_message()
    {
        var act = () => ReturnRouteHeader.TryRead(null!, out _);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MaxThreadLength_equals_the_DID_length_cap()
    {
        ReturnRouteHeader.MaxThreadLength.Should().Be(IdentifierLimits.MaxDidLength);
        ReturnRouteHeader.MaxThreadLength.Should().Be(2048);
    }

    [Fact]
    public void Read_equals_TryRead_out_value_for_well_formed_and_malformed_input()
    {
        var good = WithHeaders(("return_route", "\"thread\""), ("return_route_thread", "\"t\""));
        var bad = WithHeaders(("return_route", "\"ALL\""));

        ReturnRouteHeader.TryRead(good, out var g);
        ReturnRouteHeader.Read(good).Should().Be(g);
        ReturnRouteHeader.TryRead(bad, out var b);
        ReturnRouteHeader.Read(bad).Should().Be(b);
        ReturnRouteHeader.Read(bad).Should().Be(ReturnRouteHeader.None);
    }

    [Fact]
    public void Constants_match_the_extension_wording()
    {
        ReturnRouteHeader.HeaderName.Should().Be("return_route");
        ReturnRouteHeader.ThreadHeaderName.Should().Be("return_route_thread");
        ReturnRouteHeader.NoneValue.Should().Be("none");
        ReturnRouteHeader.AllValue.Should().Be("all");
        ReturnRouteHeader.ThreadValue.Should().Be("thread");
    }
}
