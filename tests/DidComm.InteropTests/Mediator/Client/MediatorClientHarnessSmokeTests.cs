using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The #96 A0 harness smoke test: the recipient container builds over HTTP and over a WebSocket,
/// resolves both clients over one shared channel (which is also the dispatcher's correlator), keeps
/// the configured settings, and holds the keys of the DIDs the harness mints. Nothing here reaches
/// a stubbed member.
/// </summary>
public sealed class MediatorClientHarnessSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_clients_resolve_over_one_channel_that_is_also_the_correlator(bool webSocket)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseHttp = !webSocket;
            o.UseWebSocket = webSocket;
            o.Client = c => c.RequestTimeout = TimeSpan.FromSeconds(7);
        });

        var mediation = harness.Mediation;
        var pickup = harness.Pickup;
        var channel = harness.Channel;

        mediation.Exchange.Should().BeSameAs(channel);
        pickup.Exchange.Should().BeSameAs(channel, "both clients share one pending map and one correlator");
        harness.Services.GetServices<IInboundCorrelator>().Should().ContainSingle(c => ReferenceEquals(c, channel));
        harness.Dispatcher.Should().NotBeNull("the dispatcher's factory resolves the channel as a correlator without a cycle");
        harness.Services.GetRequiredService<MediationClient>().Should().BeSameAs(mediation, "singleton");
        harness.Services.GetRequiredService<PickupClient>().Should().BeSameAs(pickup, "singleton");

        channel.ReceivesInbound.Should().Be(webSocket, "only the WebSocket container registers UseInboundDispatch()");
        channel.Options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(7));
        channel.Options.Should().NotBeSameAs(harness.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MediatorClientOptions>>().Value,
            "the channel keeps a validated copy");

        var peer = await harness.NewPeerAsync();
        var recipient = await harness.NewRecipientAsync();
        (await harness.Secrets.FindPresentAsync(peer.Kids.Concat(recipient.Kids))).Should().BeEquivalentTo(peer.Kids.Concat(recipient.Kids));
        harness.WebSocketEndpoint.Scheme.Should().Be("ws");
        harness.HttpEndpoint.Scheme.Should().Be("http");
    }

    [Fact]
    public async Task A_repeated_AddMediatorClient_registers_one_correlator()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.DidComm = b => b.AddMediatorClient());

        harness.Services.GetServices<IInboundCorrelator>().OfType<DidComm.Mediator.Client.Internal.MediatorClientChannel>().Should().ContainSingle();
    }
}
