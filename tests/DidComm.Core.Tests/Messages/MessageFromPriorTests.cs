using System.Text.Json;
using DidComm.Json;
using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Messages;

public sealed class MessageFromPriorTests
{
    [Fact]
    public void FromPrior_RoundTripsThroughJson()
    {
        const string jwt = "eyJhbGciOiJFZERTQSJ9.payload.signature";

        var message = new Message
        {
            Id = "msg-1",
            Type = "https://example.com/p/1.0/m",
            From = "did:example:newAlice",
            FromPrior = jwt,
        };

        var json = JsonSerializer.Serialize(message, DidCommJson.Default);
        json.Should().Contain("\"from_prior\":");

        var round = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default);
        round!.FromPrior.Should().Be(jwt);
    }

    [Fact]
    public void Builder_WithFromPrior_PopulatesHeader()
    {
        const string jwt = "eyJhbGciOiJFZERTQSJ9.payload.signature";

        var message = new MessageBuilder()
            .WithType("https://example.com/p/1.0/m")
            .WithFrom("did:example:newAlice")
            .WithFromPrior(jwt)
            .Build();

        message.FromPrior.Should().Be(jwt);
    }

    [Fact]
    public void FromPrior_OmittedWhenNull()
    {
        var message = new Message
        {
            Id = "msg-2",
            Type = "https://example.com/p/1.0/m",
        };

        var json = JsonSerializer.Serialize(message, DidCommJson.Default);
        json.Should().NotContain("from_prior");
    }
}
