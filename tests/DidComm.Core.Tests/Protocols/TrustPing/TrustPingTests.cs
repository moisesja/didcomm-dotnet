using System.Text.Json.Nodes;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using FluentAssertions;
using Xunit;

// Disambiguate the test namespace `DidComm.Tests.Protocols.TrustPing` from the static class
// `DidComm.Protocols.TrustPing.TrustPing` (lesson L-014).
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Tests.Protocols.TrustPing;

public sealed class TrustPingTests
{
    [Fact]
    public void CreatePing_defaults_response_requested_true()
    {
        var ping = TrustPingApi.CreatePing(from: "did:peer:alice", to: "did:peer:bob");
        ping.Type.Should().Be(TrustPingApi.PingType);
        ping.From.Should().Be("did:peer:alice");
        ping.To.Should().Equal("did:peer:bob");
        TrustPingApi.IsResponseRequested(ping).Should().BeTrue();
    }

    [Fact]
    public void CreatePing_with_responseRequested_false_round_trips_through_body()
    {
        var ping = TrustPingApi.CreatePing(from: "did:peer:alice", to: "did:peer:bob", responseRequested: false);
        TrustPingApi.IsResponseRequested(ping).Should().BeFalse();
        ping.Body!["response_requested"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void IsResponseRequested_defaults_true_when_body_absent()
    {
        var ping = new MessageBuilder().WithType(TrustPingApi.PingType)
            .WithFrom("did:peer:alice").WithTo("did:peer:bob").Build();
        ping.Body.Should().BeNull();
        TrustPingApi.IsResponseRequested(ping).Should().BeTrue();
    }

    [Fact]
    public void IsResponseRequested_defaults_true_when_field_missing_or_non_boolean()
    {
        var ping = new MessageBuilder().WithType(TrustPingApi.PingType)
            .WithFrom("did:peer:alice").WithTo("did:peer:bob")
            .WithBody(JsonNode.Parse("""{"other":"foo"}""")!.AsObject()).Build();
        TrustPingApi.IsResponseRequested(ping).Should().BeTrue();
    }

    [Fact]
    public void CreateResponse_flips_from_to_and_sets_thid_to_ping_id()
    {
        var ping = TrustPingApi.CreatePing(from: "did:peer:alice", to: "did:peer:bob");
        var resp = TrustPingApi.CreateResponse(ping);
        resp.Type.Should().Be(TrustPingApi.ResponseType);
        resp.From.Should().Be("did:peer:bob");
        resp.To.Should().Equal("did:peer:alice");
        resp.Thid.Should().Be(ping.Id);
    }

    [Fact]
    public void CreateResponse_requires_ping_from_and_to()
    {
        var noFrom = new MessageBuilder().WithType(TrustPingApi.PingType).WithTo("did:peer:bob").Build();
        ((Action)(() => TrustPingApi.CreateResponse(noFrom))).Should().Throw<ArgumentException>();

        var noTo = new MessageBuilder().WithType(TrustPingApi.PingType).WithFrom("did:peer:alice").Build();
        ((Action)(() => TrustPingApi.CreateResponse(noTo))).Should().Throw<ArgumentException>();
    }
}
