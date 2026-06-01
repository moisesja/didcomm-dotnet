using System.Text.Json.Nodes;
using DidComm.Messages;
using DidComm.Protocols.OutOfBand;
using FluentAssertions;
using Xunit;

// L-014: alias the static API class to dodge namespace shadowing.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.Tests.Protocols.OutOfBand;

public sealed class OutOfBandTests
{
    // The literal base64url invitation from the DIDComm v2.1 spec (§Out Of Band Messages,
    // "Example Out-of-Band Message Encoding"). Used as a decode fixture for FR-OOB-02: our
    // decoder must parse the spec's own example. (Byte-for-byte ENCODE equality isn't asserted —
    // JSON key order isn't canonical in the spec; see OutOfBand XML docs.)
    private const string SpecExampleOob =
        "eyJ0eXBlIjoiaHR0cHM6Ly9kaWRjb21tLm9yZy9vdXQtb2YtYmFuZC8yLjAvaW52aXRhdGlvbiIsImlkIjoiNjkyMTJhM2EtZDA2OC00ZjlkLWEyZGQtNDc0MWJjYTg5YWYzIiwiZnJvbSI6ImRpZDpleGFtcGxlOmFsaWNlIiwiYm9keSI6eyJnb2FsX2NvZGUiOiIiLCJnb2FsIjoiIn0sImF0dGFjaG1lbnRzIjpbeyJpZCI6InJlcXVlc3QtMCIsIm1lZGlhX3R5cGUiOiJhcHBsaWNhdGlvbi9qc29uIiwiZGF0YSI6eyJqc29uIjoiPGpzb24gb2YgcHJvdG9jb2wgbWVzc2FnZT4ifX1dfQ";

    [Fact]
    public void CreateInvitation_emits_spec_invitation_shape()
    {
        var invitation = OutOfBandApi.CreateInvitation(
            from: "did:example:alice",
            goal: "To issue a credential",
            goalCode: "issue-vc",
            accept: new[] { "didcomm/v2", "didcomm/aip2;env=rfc587" });

        invitation.Message.Type.Should().Be(OutOfBandApi.InvitationType);
        invitation.From.Should().Be("did:example:alice");
        invitation.GoalCode.Should().Be("issue-vc");
        invitation.Goal.Should().Be("To issue a credential");
        invitation.Accept.Should().Equal("didcomm/v2", "didcomm/aip2;env=rfc587");
        // goal_code / goal / accept live in body, not as top-level headers.
        invitation.Message.Body!["goal_code"]!.GetValue<string>().Should().Be("issue-vc");
    }

    [Fact]
    public void CreateInvitation_requires_from()
    {
        Action act = () => OutOfBandApi.CreateInvitation(from: "");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToUrl_uses_oob_parameter_with_no_padding_or_whitespace()
    {
        var invitation = OutOfBandApi.CreateInvitation(from: "did:example:alice", goal: "Connect");
        var url = OutOfBandApi.ToUrl(invitation, "https://example.com/path");

        url.Should().StartWith("https://example.com/path?_oob=");
        var encoded = url["https://example.com/path?_oob=".Length..];
        encoded.Should().NotContain("=");   // base64url, no padding
        encoded.Should().NotContain("\n").And.NotContain(" ");
    }

    [Fact]
    public void ToUrl_preserves_an_existing_query_string()
    {
        var invitation = OutOfBandApi.CreateInvitation(from: "did:example:alice");
        var url = OutOfBandApi.ToUrl(invitation, "https://example.com/path?ref=email");

        url.Should().StartWith("https://example.com/path?ref=email&_oob=");
    }

    [Fact]
    public void ToUrl_then_FromUrl_round_trips()
    {
        var original = OutOfBandApi.CreateInvitation(
            from: "did:example:alice",
            goal: "Connect",
            goalCode: "connect",
            accept: new[] { "didcomm/v2" });

        var url = OutOfBandApi.ToUrl(original, "https://example.com/path");
        var decoded = OutOfBandApi.FromUrl(url);

        decoded.Id.Should().Be(original.Id);
        decoded.From.Should().Be("did:example:alice");
        decoded.Goal.Should().Be("Connect");
        decoded.GoalCode.Should().Be("connect");
        decoded.Accept.Should().Equal("didcomm/v2");
    }

    [Fact]
    public void FromUrl_decodes_the_spec_example_fixture()
    {
        var decoded = OutOfBandApi.FromUrl($"https://example.com/path?_oob={SpecExampleOob}");

        decoded.Id.Should().Be("69212a3a-d068-4f9d-a2dd-4741bca89af3");
        decoded.From.Should().Be("did:example:alice");
        decoded.GoalCode.Should().Be(string.Empty);   // spec example uses empty strings
        decoded.Goal.Should().Be(string.Empty);
        decoded.Attachments.Should().HaveCount(1);
        decoded.Attachments[0].Id.Should().Be("request-0");
        decoded.Attachments[0].MediaType.Should().Be("application/json");
        decoded.Attachments[0].Data.Json!.GetValue<string>().Should().Be("<json of protocol message>");
    }

    [Fact]
    public void FromUrl_without_oob_parameter_throws()
    {
        Action act = () => OutOfBandApi.FromUrl("https://example.com/path?other=1");
        act.Should().Throw<FormatException>().WithMessage("*_oob*");
    }

    [Fact]
    public void FromUrl_with_invalid_base64_throws()
    {
        Action act = () => OutOfBandApi.FromUrl("https://example.com/path?_oob=!!!not-base64!!!");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void FromPlaintext_rejects_a_non_invitation_type()
    {
        var notAnInvitation =
            """{"type":"https://didcomm.org/trust-ping/2.0/ping","id":"abc","from":"did:example:alice"}""";
        Action act = () => OutOfBandApi.FromPlaintext(notAnInvitation);
        act.Should().Throw<FormatException>().WithMessage("*invitation*");
    }

    [Fact]
    public void CreateInvitation_carries_attachments_through_a_round_trip()
    {
        var attachment = new Attachment
        {
            Id = "request-0",
            MediaType = "application/json",
            Data = new AttachmentData { Json = JsonValue.Create("payload") },
        };
        var invitation = OutOfBandApi.CreateInvitation(from: "did:example:alice", attachments: new[] { attachment });

        var decoded = OutOfBandApi.FromUrl(OutOfBandApi.ToUrl(invitation, "https://example.com/oob"));

        decoded.Attachments.Should().HaveCount(1);
        decoded.Attachments[0].Id.Should().Be("request-0");
        decoded.Attachments[0].Data.Json!.GetValue<string>().Should().Be("payload");
    }

    [Fact]
    public void ToShortUrl_and_TryGetShortFormId_round_trip()
    {
        var url = OutOfBandApi.ToShortUrl("https://example.com/oob", "5f0e3ffb-3f92-4648-9868-0d6f8889e6f3");

        url.Should().Be("https://example.com/oob?_oobid=5f0e3ffb-3f92-4648-9868-0d6f8889e6f3");
        OutOfBandApi.TryGetShortFormId(url, out var id).Should().BeTrue();
        id.Should().Be("5f0e3ffb-3f92-4648-9868-0d6f8889e6f3");
    }

    [Fact]
    public void TryGetShortFormId_returns_false_for_an_inline_oob_url()
    {
        var inline = OutOfBandApi.ToUrl(OutOfBandApi.CreateInvitation(from: "did:example:alice"), "https://example.com/oob");
        OutOfBandApi.TryGetShortFormId(inline, out var id).Should().BeFalse();
        id.Should().BeEmpty();
    }

    [Fact]
    public void AddWebRedirect_then_ReadWebRedirect_round_trips()
    {
        var message = Message.Empty().WithFrom("did:example:verifier").WithTo("did:example:prover").Build();
        OutOfBandApi.AddWebRedirect(message, new WebRedirect("OK", "https://example.com/done"));

        var read = OutOfBandApi.ReadWebRedirect(message);
        read.Should().NotBeNull();
        read!.Status.Should().Be("OK");
        read.RedirectUrl.Should().Be("https://example.com/done");
    }

    [Fact]
    public void ReadWebRedirect_returns_null_when_absent()
    {
        var message = Message.Empty().WithFrom("did:example:a").WithTo("did:example:b").Build();
        OutOfBandApi.ReadWebRedirect(message).Should().BeNull();
    }

    [Fact]
    public void ReadWebRedirect_returns_null_when_malformed()
    {
        // A web_redirect missing 'redirectUrl' is malformed and must parse to null, not throw.
        var message = Message.Empty().WithFrom("did:example:a").WithTo("did:example:b").Build();
        message.AdditionalHeaders = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["web_redirect"] = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "OK" }),
        };
        OutOfBandApi.ReadWebRedirect(message).Should().BeNull();
    }
}
