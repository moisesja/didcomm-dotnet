using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Phase 4 Checkpoint A — covers FR-ROUTE-01 (forward message shape). The canonical body+attachments
/// shape is the spec's "common version of a forward message" example (spec §Routing Protocol 2.0
/// / Messages); the tests pin both the happy-path build and the negative-path parser checks
/// against that shape. Spec endpoint-example JSON fixtures are exercised from the interop
/// project (where the fixture directory is copied to the test output).
/// </summary>
public sealed class ForwardMessageTests
{
    private const string SamplePackedJwe = """{"protected":"eyJhbGciOiJFQ0RILUVTK0EyNTZLVyJ9","recipients":[],"iv":"","ciphertext":"","tag":""}""";

    [Fact]
    public void Create_emits_canonical_spec_shape()
    {
        var msg = ForwardMessage.Create(
            mediator: "did:example:mediator",
            next: "did:foo:1234abcd",
            packedPayloads: new[] { SamplePackedJwe },
            expiresTimeEpochSeconds: 1516385931);

        msg.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        msg.To.Should().ContainSingle().Which.Should().Be("did:example:mediator");
        msg.ExpiresTime.Should().Be(1516385931);
        msg.Body!["next"]!.GetValue<string>().Should().Be("did:foo:1234abcd");
        msg.Attachments.Should().ContainSingle();
        msg.Attachments![0].MediaType.Should().Be(ForwardConstants.PayloadMediaType);
        msg.Attachments[0].Data.Json.Should().NotBeNull();
    }

    [Fact]
    public void Create_assigns_a_new_id_via_the_default_generator()
    {
        var msg = ForwardMessage.Create("did:example:m", "did:example:n", new[] { SamplePackedJwe });
        msg.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_supports_multiple_packed_payloads_in_attachment_order()
    {
        var first = SamplePackedJwe.Replace("ciphertext\":\"\"", "ciphertext\":\"AAAA\"");
        var second = SamplePackedJwe.Replace("ciphertext\":\"\"", "ciphertext\":\"BBBB\"");

        var msg = ForwardMessage.Create("did:example:m", "did:example:n", new[] { first, second });

        msg.Attachments.Should().HaveCount(2);
        msg.Attachments![0].Data.Json!["ciphertext"]!.GetValue<string>().Should().Be("AAAA");
        msg.Attachments![1].Data.Json!["ciphertext"]!.GetValue<string>().Should().Be("BBBB");
    }

    [Fact]
    public void Create_rejects_empty_payload_list()
    {
        Action act = () => ForwardMessage.Create("did:example:m", "did:example:n", Array.Empty<string>());
        act.Should().Throw<ArgumentException>().WithMessage("*at least one packed payload*FR-ROUTE-01*");
    }

    [Fact]
    public void Create_rejects_empty_next()
    {
        Action act = () => ForwardMessage.Create("did:example:m", string.Empty, new[] { SamplePackedJwe });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_empty_mediator()
    {
        Action act = () => ForwardMessage.Create(string.Empty, "did:example:n", new[] { SamplePackedJwe });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_non_json_payload_with_actionable_message()
    {
        Action act = () => ForwardMessage.Create("did:example:m", "did:example:n", new[] { "not-json-bytes" });
        act.Should().Throw<ArgumentException>().WithMessage("*Routing Protocol 2.0 expects packed DIDComm envelopes*");
    }

    [Fact]
    public void TryParse_round_trips_a_created_forward()
    {
        var built = ForwardMessage.Create("did:example:m", "did:example:n", new[] { SamplePackedJwe });

        var recognised = ForwardMessage.TryParse(built, out var next, out var payloads);

        recognised.Should().BeTrue();
        next.Should().Be("did:example:n");
        payloads.Should().ContainSingle();
        payloads[0].Data.Json.Should().NotBeNull();
    }

    [Fact]
    public void TryParse_returns_false_for_non_forward_message_type()
    {
        var other = new MessageBuilder()
            .WithType("https://didcomm.org/trust-ping/2.0/ping")
            .WithBody(new JsonObject())
            .Build();

        var recognised = ForwardMessage.TryParse(other, out var next, out var payloads);

        recognised.Should().BeFalse();
        next.Should().BeEmpty();
        payloads.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_throws_when_forward_type_is_missing_next()
    {
        var malformed = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject()) // no "next"
            .WithAttachment(new Attachment { Data = new AttachmentData { Json = JsonNode.Parse(SamplePackedJwe) } })
            .Build();

        Action act = () => ForwardMessage.TryParse(malformed, out _, out _);
        act.Should().Throw<MalformedMessageException>().WithMessage("*REQUIRED 'next'*FR-ROUTE-01*");
    }

    [Fact]
    public void TryParse_throws_when_forward_type_is_missing_attachments()
    {
        var malformed = new MessageBuilder()
            .WithType(ForwardConstants.ForwardTypeUri)
            .WithBody(new JsonObject { ["next"] = "did:example:n" })
            .Build();

        Action act = () => ForwardMessage.TryParse(malformed, out _, out _);
        act.Should().Throw<MalformedMessageException>().WithMessage("*REQUIRED 'attachments'*FR-ROUTE-01*");
    }

}
