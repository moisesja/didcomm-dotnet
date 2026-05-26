using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Messages;

public sealed class MessageAckLangJsonTests
{
    [Fact]
    public void PleaseAck_roundtrips_with_current_message_sentinel()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithPleaseAck() // [""] sentinel = "ack this current message"
            .Build();

        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        var node = JsonNode.Parse(json)!.AsObject();
        node["please_ack"]!.AsArray().Should().HaveCount(1);
        node["please_ack"]![0]!.GetValue<string>().Should().Be(string.Empty);

        var roundTripped = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        roundTripped.PleaseAck.Should().ContainSingle().Which.Should().Be(string.Empty);
    }

    [Fact]
    public void Ack_roundtrips_with_explicit_ids()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithAck("id-1", "id-2", "id-3")
            .Build();

        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        var node = JsonNode.Parse(json)!.AsObject();
        node["ack"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("id-1", "id-2", "id-3");

        var roundTripped = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        roundTripped.Ack.Should().Equal("id-1", "id-2", "id-3");
    }

    [Fact]
    public void Lang_and_AcceptLang_roundtrip()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithLang("fr")
            .WithAcceptLang("fr", "en-GB", "en")
            .Build();

        var json = JsonSerializer.Serialize(msg, DidCommJson.Default);
        var node = JsonNode.Parse(json)!.AsObject();
        node["lang"]!.GetValue<string>().Should().Be("fr");
        // Spec uses the hyphenated form `accept-lang`, not snake_case.
        node["accept-lang"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("fr", "en-GB", "en");

        var roundTripped = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!;
        roundTripped.Lang.Should().Be("fr");
        roundTripped.AcceptLang.Should().Equal("fr", "en-GB", "en");
    }

    [Fact]
    public void Unset_ack_lang_fields_are_omitted_from_output()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .Build();

        var node = JsonNode.Parse(JsonSerializer.Serialize(msg, DidCommJson.Default))!.AsObject();
        node.Should().NotContainKey("please_ack");
        node.Should().NotContainKey("ack");
        node.Should().NotContainKey("lang");
        node.Should().NotContainKey("accept-lang");
    }

    [Fact]
    public void Ack_with_invalid_id_chars_fails_validation()
    {
        var msg = new Message
        {
            Id = "msg-1",
            Type = "https://didcomm.org/empty/1.0/empty",
            Ack = new List<string> { "has space" },
        };
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-THR-03*");
    }

    [Fact]
    public void Ack_with_empty_id_fails_validation()
    {
        var msg = new Message
        {
            Id = "msg-1",
            Type = "https://didcomm.org/empty/1.0/empty",
            Ack = new List<string> { string.Empty },
        };
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-THR-03*");
    }

    [Fact]
    public void PleaseAck_empty_string_is_allowed_as_current_message_sentinel()
    {
        var msg = new Message
        {
            Id = "msg-1",
            Type = "https://didcomm.org/empty/1.0/empty",
            PleaseAck = new List<string> { string.Empty },
        };
        Action act = msg.Validate;
        act.Should().NotThrow();
    }

    [Fact]
    public void Empty_lang_fails_validation()
    {
        var msg = new Message
        {
            Id = "msg-1",
            Type = "https://didcomm.org/empty/1.0/empty",
            Lang = string.Empty,
        };
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-I18N-03*");
    }

    [Fact]
    public void Empty_accept_lang_entry_fails_validation()
    {
        var msg = new Message
        {
            Id = "msg-1",
            Type = "https://didcomm.org/empty/1.0/empty",
            AcceptLang = new List<string> { "fr", string.Empty },
        };
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-I18N-01*");
    }
}
