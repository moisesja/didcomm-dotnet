using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>FR-RR-08 scope helpers: the four mediator-service PIURIs and the forward exemption.</summary>
public sealed class MediatorServiceProtocolsTests
{
    [Theory]
    [InlineData("https://didcomm.org/coordinate-mediation/2.0/mediate-request")]
    [InlineData("https://didcomm.org/coordinate-mediation/3.0/mediate-request")]
    [InlineData("https://didcomm.org/messagepickup/3.0/status-request")]
    [InlineData("https://didcomm.org/message-pickup/4.0/status-request")]
    [InlineData("https://didcomm.org/coordinate-mediation/3.1/x")]       // minor bump
    [InlineData("https://didcomm.org/coordinate_mediation/3.0/x")]        // punctuation-insensitive
    [InlineData("https://didcomm.org/Coordinate-Mediation/3.0/x")]        // case-insensitive
    public void Mediator_service_types_match_by_name_and_major(string type)
    {
        MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://didcomm.org/trust-ping/2.0/ping")]
    [InlineData("https://didcomm.org/basicmessage/2.0/message")]
    [InlineData("https://didcomm.org/coordinate-mediation/4.0/x")]         // other major
    [InlineData("https://didcomm.org/routing/2.0/forward")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://didcomm.org//coordinate-mediation/3.0/x")]       // L-032 double slash
    public void Other_types_and_garbage_are_not_mediator_service_types_and_never_throw(string? type)
    {
        var act = () => MediatorServiceProtocols.IsMediatorServiceType(type);
        act.Should().NotThrow();
        MediatorServiceProtocols.IsMediatorServiceType(type).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://didcomm.org/routing/2.0/forward", true)]
    [InlineData("https://didcomm.org/routing/2.1/forward", true)]
    [InlineData("https://didcomm.org/messagepickup/3.0/status-request", false)]
    [InlineData("https://didcomm.org/message-pickup/4.0/delivery", false)]
    [InlineData("https://didcomm.org/routing/3.0/forward", false)]
    [InlineData(null, false)]
    [InlineData("nope", false)]
    public void IsReplayExempt_is_routing_2_only(string? type, bool expected)
    {
        MediatorServiceProtocols.IsReplayExempt(type).Should().Be(expected);
    }

    [Fact]
    public void All_lists_the_four_identifiers()
    {
        MediatorServiceProtocols.All.Should().Equal(
            MediatorServiceProtocols.CoordinateMediation20,
            MediatorServiceProtocols.CoordinateMediation30,
            MediatorServiceProtocols.MessagePickup30,
            MediatorServiceProtocols.MessagePickup40);
    }
}
