using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Messages;

public sealed class MessageBuilderTests
{
    [Fact]
    public void Build_auto_populates_id_and_typ()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .Build();

        msg.Id.Should().NotBeNullOrEmpty();
        msg.Typ.Should().Be(MediaTypes.Plaintext);
        msg.Type.Should().Be("https://didcomm.org/empty/1.0/empty");
    }

    [Fact]
    public void Explicit_id_is_preserved()
    {
        var msg = new MessageBuilder()
            .WithId("hand-picked-id")
            .WithType("https://didcomm.org/empty/1.0/empty")
            .Build();

        msg.Id.Should().Be("hand-picked-id");
    }

    [Fact]
    public void Build_runs_validation_and_rejects_missing_type()
    {
        var builder = new MessageBuilder();
        Action act = () => builder.Build();
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-MSG-05*");
    }

    [Fact]
    public void Builder_carries_body_and_attachments_through()
    {
        var body = new JsonObject { ["k"] = 42 };
        var attachment = new Attachment
        {
            Id = "attach.1",
            Data = new AttachmentData { Base64 = "aGVsbG8=" },
        };
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob", "did:example:carol")
            .WithThid("thread-1")
            .WithBody(body)
            .WithAttachment(attachment)
            .Build();

        msg.From.Should().Be("did:example:alice");
        msg.To.Should().Equal("did:example:bob", "did:example:carol");
        msg.Thid.Should().Be("thread-1");
        msg.Body.Should().BeSameAs(body);
        msg.Attachments.Should().ContainSingle().Which.Should().BeSameAs(attachment);
    }
}
