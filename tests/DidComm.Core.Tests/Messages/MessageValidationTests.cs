using DidComm.Exceptions;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Messages;

public sealed class MessageValidationTests
{
    private static Message MinimalValid() => new()
    {
        Id = "msg-1",
        Type = "https://didcomm.org/empty/1.0/empty",
    };

    [Fact]
    public void Empty_id_throws()
    {
        var msg = MinimalValid();
        msg.Id = string.Empty;
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-MSG-02*");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has#hash")]
    [InlineData("has,comma")]
    public void Id_with_reserved_chars_throws(string id)
    {
        var msg = MinimalValid();
        msg.Id = id;
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>();
    }

    [Fact]
    public void Empty_type_throws()
    {
        var msg = MinimalValid();
        msg.Type = string.Empty;
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-MSG-05*");
    }

    [Fact]
    public void Invalid_mturi_in_type_throws()
    {
        var msg = MinimalValid();
        msg.Type = "not-a-mturi";
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-PROTO-01*");
    }

    [Fact]
    public void To_entry_with_fragment_throws()
    {
        var msg = MinimalValid();
        msg.To = new[] { "did:example:bob#key-1" };
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-MSG-07*");
    }

    [Fact]
    public void From_with_fragment_throws()
    {
        var msg = MinimalValid();
        msg.From = "did:example:alice#key-1";
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-MSG-08*");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("with/slash")]
    public void Thid_with_reserved_chars_throws(string thid)
    {
        var msg = MinimalValid();
        msg.Thid = thid;
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-MSG-11*");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("with/slash")]
    public void Pthid_with_reserved_chars_throws(string pthid)
    {
        var msg = MinimalValid();
        msg.Pthid = pthid;
        Action act = msg.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-MSG-11*");
    }

    [Fact]
    public void Minimal_valid_message_passes()
    {
        MinimalValid().Validate();
    }

    [Theory]
    [InlineData("application/didcomm-plain+json")]
    [InlineData("didcomm-plain+json")]
    [InlineData("APPLICATION/DIDCOMM-PLAIN+JSON")]
    public void Media_type_accepts_with_or_without_application_prefix(string mediaType)
    {
        MediaTypes.Matches(mediaType, MediaTypes.Plaintext).Should().BeTrue();
    }

    [Fact]
    public void Media_type_rejects_unrelated_value()
    {
        MediaTypes.Matches("application/json", MediaTypes.Plaintext).Should().BeFalse();
    }
}
