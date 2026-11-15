using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The WebSocket transport's half of the #96 receive-route rule (D-4, QA Q1-1): it says that what a
/// peer pushes back reaches the inbound consumer only with <c>ReceiveInbound</c> on and STOMP off
/// (under STOMP no session socket is opened and pooled sockets are never read), and the mediator
/// client, asking it, refuses such a route end to end before connecting or sending anything.
/// </summary>
public sealed class MediatorClientReceiveRouteInteropTests
{
    [Fact]
    public async Task The_WebSocket_transport_says_pushes_reach_the_consumer_only_with_ReceiveInbound_on_and_STOMP_off()
    {
        await using (var off = new WebSocketDidCommTransport(Options.Create(new WebSocketTransportOptions())))
            ((IInboundReceivingTransport)off).ReceivesInbound.Should().BeFalse("ReceiveInbound is off by default");

        await using (var offWithConsumer = new WebSocketDidCommTransport(Options.Create(new WebSocketTransportOptions()), logger: null, coreOptions: null, new NullConsumer()))
            ((IInboundReceivingTransport)offWithConsumer).ReceivesInbound.Should().BeFalse("a consumer alone does not turn receiving on");

        var options = new WebSocketTransportOptions { ReceiveInbound = true };
        await using var on = new WebSocketDidCommTransport(Options.Create(options), logger: null, coreOptions: null, new NullConsumer());
        ((IInboundReceivingTransport)on).ReceivesInbound.Should().BeTrue();

        options.UseStomp = true;
        ((IInboundReceivingTransport)on).ReceivesInbound.Should().BeFalse("STOMP is read as every send reads it: no session socket, nothing read back");

        options.UseStomp = false;
        options.ReceiveInbound = false;
        ((IInboundReceivingTransport)on).ReceivesInbound.Should().BeTrue("ReceiveInbound is read once, at construction, as the receive loops read it");
    }

    [Fact]
    public async Task Under_STOMP_a_WebSocket_route_is_refused_before_anything_is_sent()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.WebSocket = w => w.UseStomp = true;
            o.Client = c =>
            {
                c.RequestTimeout = TimeSpan.FromSeconds(2);
                c.CoordinateMediationVersion = CoordinateMediationVersion.V3;
            };
        });
        var bob = await harness.NewPeerAsync();

        var thrown = await Record.ExceptionAsync(() => harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).WaitAsync(TimeSpan.FromSeconds(30)));

        thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("ReceiveInbound").And.Contain("Nothing was sent");
        harness.Connects.Should().Be(0, "no connection was opened");
        (await mediator.Mediation.GetGrantAsync(bob.Did, default)).Should().BeNull();
    }

    [Fact]
    public async Task With_the_documented_set_up_the_route_is_admitted_and_the_request_answered()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V3;
        });
        var bob = await harness.NewPeerAsync();

        var grant = await harness.Mediation.RequestMediationAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).WaitAsync(TimeSpan.FromSeconds(30));

        grant.Should().NotBeNull();
        harness.Connects.Should().BeGreaterThan(0);
    }

    private sealed class NullConsumer : IInboundFrameConsumer
    {
        public ValueTask ConsumeAsync(InboundFrame frame, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
