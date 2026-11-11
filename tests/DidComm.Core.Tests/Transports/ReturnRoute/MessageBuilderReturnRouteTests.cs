using System.Text.Json;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>FR-RR-01 on the builder: <see cref="MessageBuilder.WithReturnRoute"/> wire shape and validation.</summary>
public sealed class MessageBuilderReturnRouteTests
{
    private static MessageBuilder Builder() => new MessageBuilder()
        .WithType("https://didcomm.org/test/1.0/m")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob");

    [Fact]
    public void All_serializes_byte_exact_and_round_trips()
    {
        var msg = Builder().WithReturnRoute(ReturnRouteMode.All).Build();

        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        json.Should().Contain("\"return_route\":\"all\"");
        json.Should().NotContain("return_route_thread");

        ReturnRouteHeader.Read(msg).Mode.Should().Be(ReturnRouteMode.All);
        var back = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        ReturnRouteHeader.Read(back).Mode.Should().Be(ReturnRouteMode.All);
    }

    [Fact]
    public void None_serializes_an_explicit_none()
    {
        var msg = Builder().WithReturnRoute(ReturnRouteMode.None).Build();

        JsonSerializer.Serialize(msg, DidCommJson.Default).Should().Contain("\"return_route\":\"none\"");
        ReturnRouteHeader.TryRead(msg, out var header).Should().BeTrue();
        header.Should().Be(ReturnRouteHeader.None);
    }

    [Fact]
    public void Thread_writes_both_headers_and_round_trips()
    {
        var msg = Builder().WithReturnRoute(ReturnRouteMode.Thread, "thid-42").Build();

        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        json.Should().Contain("\"return_route\":\"thread\"");
        json.Should().Contain("\"return_route_thread\":\"thid-42\"");

        var back = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        ReturnRouteHeader.Read(back).Should().Be(new ReturnRouteHeader(ReturnRouteMode.Thread, "thid-42"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Thread_without_a_usable_thread_is_rejected(string? thread)
    {
        var act = () => Builder().WithReturnRoute(ReturnRouteMode.Thread, thread);
        act.Should().Throw<ArgumentException>().WithParameterName("thread");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("a#b")]
    [InlineData("é")]
    public void Thread_with_reserved_characters_is_rejected(string thread)
    {
        var act = () => Builder().WithReturnRoute(ReturnRouteMode.Thread, thread);
        act.Should().Throw<ArgumentException>().WithParameterName("thread");
    }

    [Fact]
    public void Thread_over_the_cap_is_rejected_and_at_the_cap_is_accepted()
    {
        var over = () => Builder().WithReturnRoute(ReturnRouteMode.Thread, new string('a', ReturnRouteHeader.MaxThreadLength + 1));
        over.Should().Throw<ArgumentException>().WithParameterName("thread");

        var msg = Builder().WithReturnRoute(ReturnRouteMode.Thread, new string('a', ReturnRouteHeader.MaxThreadLength)).Build();
        ReturnRouteHeader.Read(msg).Thread.Should().HaveLength(ReturnRouteHeader.MaxThreadLength);
    }

    [Theory]
    [InlineData(ReturnRouteMode.All)]
    [InlineData(ReturnRouteMode.None)]
    public void A_thread_with_a_non_thread_mode_is_rejected(ReturnRouteMode mode)
    {
        var act = () => Builder().WithReturnRoute(mode, "thid");
        act.Should().Throw<ArgumentException>().WithParameterName("thread");
    }

    [Fact]
    public void Unknown_mode_is_rejected()
    {
        var act = () => Builder().WithReturnRoute((ReturnRouteMode)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Switching_from_Thread_to_All_removes_the_stale_thread_header()
    {
        var msg = Builder().WithReturnRoute(ReturnRouteMode.Thread, "thid").WithReturnRoute(ReturnRouteMode.All).Build();

        msg.AdditionalHeaders.Should().NotContainKey("return_route_thread");
        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        json.Should().Contain("\"return_route\":\"all\"").And.NotContain("return_route_thread");
    }

    [Fact]
    public void Header_survives_Build_validation_and_a_serialize_deserialize_round_trip()
    {
        // FR-MSG-15: unknown headers are preserved verbatim across the round trip.
        var msg = Builder().WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(1_700_000_000).Build();
        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        var back = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        back.Validate();
        var again = JsonSerializer.Serialize(back, DidCommJson.Default);

        again.Should().Contain("\"return_route\":\"all\"");
        ReturnRouteHeader.Read(back).Mode.Should().Be(ReturnRouteMode.All);
    }
}
