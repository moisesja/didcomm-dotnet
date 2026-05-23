using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Json;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Messages;

public sealed class AttachmentTests
{
    [Fact]
    public void Round_trips_a_fully_populated_attachment()
    {
        var original = new Attachment
        {
            Id = "report.pdf",
            Description = "the report",
            Filename = "report.pdf",
            MediaType = "application/pdf",
            Format = "https://example.org/formats/report-v1",
            LastModifiedTime = 1_700_000_000,
            ByteCount = 1024,
            Data = new AttachmentData { Base64 = "aGVsbG8=" },
        };

        var json = JsonSerializer.Serialize(original, DidCommJson.Default);
        var reparsed = JsonSerializer.Deserialize<Attachment>(json, DidCommJson.Default)!;

        reparsed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Data_with_no_member_is_rejected()
    {
        var att = new Attachment { Data = new AttachmentData() };
        Action act = att.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-ATT-02*");
    }

    [Fact]
    public void Links_without_hash_is_rejected()
    {
        var att = new Attachment
        {
            Data = new AttachmentData { Links = new[] { "https://example.org/blob" } },
        };
        Action act = att.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-ATT-03*");
    }

    [Fact]
    public void Links_with_hash_validates()
    {
        var att = new Attachment
        {
            Data = new AttachmentData
            {
                Links = new[] { "https://example.org/blob" },
                Hash = "zQmExampleMultihash",
            },
        };
        att.Validate();
    }

    [Fact]
    public void Reserved_char_in_id_is_rejected()
    {
        var att = new Attachment
        {
            Id = "bad id with spaces",
            Data = new AttachmentData { Base64 = "aGVsbG8=" },
        };
        Action act = att.Validate;
        act.Should().Throw<MalformedMessageException>().WithMessage("*FR-ATT-04*");
    }

    [Fact]
    public void Absent_id_is_accepted()
    {
        var att = new Attachment { Data = new AttachmentData { Base64 = "aGVsbG8=" } };
        att.Validate();
    }

    [Fact]
    public void Jws_attachment_round_trips_through_json()
    {
        var jws = JsonNode.Parse(@"{""signatures"":[{""protected"":""eyJhbGciOiJFZERTQSJ9"",""signature"":""abc""}]}");
        var att = new Attachment
        {
            Id = "signed.json",
            Data = new AttachmentData { Jws = jws },
        };

        var json = JsonSerializer.Serialize(att, DidCommJson.Default);
        var reparsed = JsonSerializer.Deserialize<Attachment>(json, DidCommJson.Default)!;

        reparsed.Data.Jws.Should().NotBeNull();
        reparsed.Data.Jws!.ToJsonString().Should().Be(jws!.ToJsonString());
    }
}
