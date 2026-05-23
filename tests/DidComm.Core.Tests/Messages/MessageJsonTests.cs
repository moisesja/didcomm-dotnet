using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Json;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Messages;

public sealed class MessageJsonTests
{
    // Spec Appendix C.1 plaintext example (§Plaintext Message Example).
    // Mirrored in tests/DidComm.InteropTests/fixtures/payloads/c1-lets-do-lunch.json;
    // inlined here so the round-trip test does not depend on file-system layout.
    private const string AppendixC1Json = """
        {
          "id": "1234567890",
          "typ": "application/didcomm-plain+json",
          "type": "http://example.com/protocols/lets_do_lunch/1.0/proposal",
          "from": "did:example:alice",
          "to": ["did:example:bob"],
          "created_time": 1516269022,
          "expires_time": 1516385931,
          "body": {"messagespecificattribute": "and its value"}
        }
        """;

    [Fact]
    public void Appendix_C1_round_trips_structurally()
    {
        var msg = JsonSerializer.Deserialize<Message>(AppendixC1Json, DidCommJson.Default)!;

        msg.Id.Should().Be("1234567890");
        msg.Typ.Should().Be(MediaTypes.Plaintext);
        msg.Type.Should().Be("http://example.com/protocols/lets_do_lunch/1.0/proposal");
        msg.From.Should().Be("did:example:alice");
        msg.To.Should().Equal("did:example:bob");
        msg.CreatedTime.Should().Be(1516269022L);
        msg.ExpiresTime.Should().Be(1516385931L);
        msg.Body.Should().NotBeNull();
        msg.Body!["messagespecificattribute"]!.GetValue<string>().Should().Be("and its value");

        // Re-serialize and re-parse: structurally identical. Compare canonical forms via the
        // deterministic writer because JsonNode's Parent/Root pointers trip FluentAssertions'
        // equivalence walker with a cyclic-reference complaint.
        var roundTripped = JsonSerializer.Deserialize<Message>(
            JsonSerializer.Serialize(msg, DidCommJson.Default), DidCommJson.Default)!;
        roundTripped.Id.Should().Be(msg.Id);
        roundTripped.Type.Should().Be(msg.Type);
        roundTripped.Typ.Should().Be(msg.Typ);
        roundTripped.From.Should().Be(msg.From);
        roundTripped.To.Should().Equal(msg.To!);
        roundTripped.CreatedTime.Should().Be(msg.CreatedTime);
        roundTripped.ExpiresTime.Should().Be(msg.ExpiresTime);
        DeterministicJsonWriter.WriteString(roundTripped.Body)
            .Should().Be(DeterministicJsonWriter.WriteString(msg.Body));
    }

    [Fact]
    public void Appendix_C1_canonical_form_is_byte_stable()
    {
        var msg = JsonSerializer.Deserialize<Message>(AppendixC1Json, DidCommJson.Default)!;

        var node1 = JsonSerializer.SerializeToNode(msg, DidCommJson.Default);
        var node2 = JsonSerializer.SerializeToNode(msg, DidCommJson.Default);

        DeterministicJsonWriter.WriteUtf8(node1)
            .Should().Equal(DeterministicJsonWriter.WriteUtf8(node2));
    }

    [Fact]
    public void Body_absent_unpacks_with_null_body()
    {
        const string json = """
            {
              "id": "id-1",
              "type": "https://didcomm.org/empty/1.0/empty",
              "typ": "application/didcomm-plain+json"
            }
            """;

        var msg = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        msg.Body.Should().BeNull();
        msg.Validate(); // does not throw
    }

    [Fact]
    public void Unknown_headers_do_not_fail_unpack_and_survive_round_trip()
    {
        const string json = """
            {
              "id": "id-1",
              "type": "https://didcomm.org/empty/1.0/empty",
              "typ": "application/didcomm-plain+json",
              "future_header": "future-value",
              "ext": {"k": 1}
            }
            """;

        var msg = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        msg.AdditionalHeaders.Should().ContainKeys("future_header", "ext");

        var reSerialized = JsonSerializer.Serialize(msg, DidCommJson.Default);
        var node = JsonNode.Parse(reSerialized)!.AsObject();
        node["future_header"]!.GetValue<string>().Should().Be("future-value");
        node["ext"]!["k"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Epoch_seconds_are_emitted_as_integers_not_strings()
    {
        var msg = new Message
        {
            Id = "id-1",
            Type = "https://didcomm.org/empty/1.0/empty",
            CreatedTime = 1700000000,
            ExpiresTime = 1700001000,
        };

        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        json.Should().Contain("\"created_time\":1700000000");
        json.Should().Contain("\"expires_time\":1700001000");
        json.Should().NotContain("\"created_time\":\"1700000000\"");
    }

    [Fact]
    public void Epoch_seconds_tolerate_string_input_on_read()
    {
        const string json = """
            {"id":"id-1","type":"https://didcomm.org/empty/1.0/empty","created_time":"1700000000"}
            """;

        var msg = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        msg.CreatedTime.Should().Be(1700000000L);
    }

    [Fact]
    public void Null_optional_headers_are_omitted_from_output()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .Build();

        var node = JsonNode.Parse(JsonSerializer.Serialize(msg, DidCommJson.Default))!.AsObject();
        node.Should().NotContainKey("from");
        node.Should().NotContainKey("to");
        node.Should().NotContainKey("body");
        node.Should().NotContainKey("created_time");
    }
}
