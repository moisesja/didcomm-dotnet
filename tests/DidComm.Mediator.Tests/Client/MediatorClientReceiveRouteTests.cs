using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// A route the client cannot receive on is refused before anything is sent (#96 D-4, QA Q1-1): a
/// route that is not HTTP needs an inbound consumer (<c>UseInboundDispatch()</c>) and a transport
/// that hands what the peer pushes back to it. The transport asked is the one the facade's own
/// router would send through — the first registered one that handles the endpoint (L-041). When the
/// facade cannot tell (a router of the host's own, a transport that does not say) the route is
/// allowed; an HTTP route is never refused for this, its answer comes back in the response.
/// </summary>
public sealed class MediatorClientReceiveRouteTests
{
    private const string MediatorDid = "did:example:mediator";
    private static readonly Uri Wss = new("wss://mediator.example/ws");

    [Fact]
    public async Task A_WebSocket_route_whose_transport_discards_pushes_is_refused_naming_both_fixes_and_nothing_is_sent()
    {
        var transport = new ReceivingTransport("wss", receives: false);
        using var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), transport);
        var channel = sp.GetRequiredService<MediatorClientChannel>();
        var pickup = sp.GetRequiredService<PickupClient>();

        channel.ReceivesInbound.Should().BeTrue("an inbound consumer is registered");
        var thrown = (await FluentActions.Awaiting(() => pickup.GetStatusAsync(MediatorDid, "did:example:local", serviceEndpointOverride: Wss))
            .Should().ThrowAsync<InvalidOperationException>()).Which;

        thrown.Message.Should().Contain("UseWebSocketTransport(o => o.ReceiveInbound = true)").And.Contain("UseInboundDispatch()").And.Contain("Nothing was sent");
        transport.Requests.Should().BeEmpty("refused before anything is sent");
        transport.Asked.Should().BeGreaterThan(0, "the transport the send would use was asked");
        channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_WebSocket_route_whose_transport_hands_pushes_to_the_consumer_is_admitted()
    {
        using var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), new ReceivingTransport("wss", receives: true));
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        var route = await channel.ResolveRouteAsync(MediatorDid, Wss, requireWebSocket: true, default);

        route.IsWebSocket.Should().BeTrue();
    }

    [Fact]
    public async Task Without_an_inbound_consumer_a_WebSocket_route_is_refused_even_when_its_transport_would_hand_pushes_on()
    {
        var transport = new ReceivingTransport("wss", receives: true);
        using var sp = Build(b => b.AddMediatorClient(), transport);
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        var thrown = (await FluentActions.Awaiting(() => channel.ResolveRouteAsync(MediatorDid, Wss, false, default)).Should().ThrowAsync<InvalidOperationException>()).Which;

        thrown.Message.Should().Contain("UseInboundDispatch()");
    }

    [Fact]
    public async Task When_the_facade_cannot_tell_the_route_is_allowed()
    {
        // A transport that does not say.
        using (var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), new SilentTransport("wss")))
            (await sp.GetRequiredService<MediatorClientChannel>().ResolveRouteAsync(MediatorDid, Wss, true, default)).IsWebSocket.Should().BeTrue();

        // A router of the host's own, even over a transport that would say no: the facade does not
        // know which transport it picks.
        var discarding = new ReceivingTransport("wss", receives: false);
        using (var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), router: new HostRouter(discarding)))
        {
            (await sp.GetRequiredService<MediatorClientChannel>().ResolveRouteAsync(MediatorDid, Wss, true, default)).IsWebSocket.Should().BeTrue();
            discarding.Asked.Should().Be(0);
        }

        // No transport for the scheme at all: the send refuses that itself.
        using (var sp = Build(b => b.UseInboundDispatch().AddMediatorClient()))
            (await sp.GetRequiredService<MediatorClientChannel>().ResolveRouteAsync(MediatorDid, Wss, true, default)).IsWebSocket.Should().BeTrue();
    }

    [Fact]
    public async Task An_HTTP_route_is_never_refused_for_this_and_its_transport_is_not_asked()
    {
        var http = new ReceivingTransport("https", receives: false);
        using var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), http);
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        var route = await channel.ResolveRouteAsync(MediatorDid, new Uri("https://mediator.example/didcomm"), false, default);

        route.IsHttp.Should().BeTrue();
        http.Asked.Should().Be(0);
    }

    [Fact]
    public async Task A_route_of_another_scheme_whose_transport_discards_pushes_is_refused_like_a_WebSocket()
    {
        using var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), new ReceivingTransport("didcomm-test", receives: false));
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        await FluentActions.Awaiting(() => channel.ResolveRouteAsync(MediatorDid, new Uri("didcomm-test://mediator.example/"), false, default))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task The_transport_asked_is_the_first_registered_one_that_handles_the_endpoint_as_the_send_selects_it(bool first, bool second, bool admitted)
    {
        var a = new ReceivingTransport("wss", first);
        var b = new ReceivingTransport("wss", second);
        using var sp = Build(builder => builder.UseInboundDispatch().AddMediatorClient(), a, b);
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        var act = () => channel.ResolveRouteAsync(MediatorDid, Wss, false, default);

        if (admitted)
            (await act()).IsWebSocket.Should().BeTrue();
        else
            await act.Should().ThrowAsync<InvalidOperationException>();
        b.Asked.Should().Be(0, "only the transport the router would pick is asked");
        new TransportRouter(new IDidCommTransport[] { a, b }).Select(Wss).Should().BeSameAs(a, "the router's send picks the same one");
    }

    [Fact]
    public void The_facade_says_null_without_a_router_for_the_queue_URI_and_for_a_scheme_no_transport_handles()
    {
        using var sp = Build(_ => { }, new ReceivingTransport("wss", receives: false));
        var client = sp.GetRequiredService<DidCommClient>();

        client.ReceivesPushesOn(Wss).Should().BeFalse();
        client.ReceivesPushesOn(new Uri("didcomm:transport/queue")).Should().BeNull();
        client.ReceivesPushesOn(new Uri("ws://mediator.example/ws")).Should().BeNull();
        FluentActions.Invoking(() => client.ReceivesPushesOn(null!)).Should().Throw<ArgumentNullException>();

        var withoutRouter = new DidCommClient(
            sp.GetRequiredService<InMemorySecretsResolver>(),
            sp.GetRequiredService<Resolution.IDidKeyService>(),
            sp.GetRequiredService<Resolution.IServiceEndpointResolver>(),
            new DidCommOptions());
        withoutRouter.ReceivesPushesOn(Wss).Should().BeNull();
    }

    [Fact]
    public async Task The_answer_is_read_when_a_route_is_resolved_so_a_transport_that_changes_its_answer_changes_the_next_route()
    {
        var transport = new ReceivingTransport("wss", receives: false);
        using var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), transport);
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        await FluentActions.Awaiting(() => channel.ResolveRouteAsync(MediatorDid, Wss, false, default)).Should().ThrowAsync<InvalidOperationException>();
        transport.Receives = true;
        (await channel.ResolveRouteAsync(MediatorDid, Wss, false, default)).IsWebSocket.Should().BeTrue();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static ServiceProvider Build(Action<DidCommBuilder> configure, params IDidCommTransport[] transports)
        => Build(configure, router: null, transports);

    private static ServiceProvider Build(Action<DidCommBuilder> configure, ITransportRouter? router, params IDidCommTransport[] transports)
    {
        var collection = new ServiceCollection();
        var secrets = new InMemorySecretsResolver();
        collection.AddSingleton(secrets);
        foreach (var transport in transports)
            collection.AddSingleton(transport);
        if (router is not null)
            collection.AddSingleton(router);
        collection.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.Configure(o => o.OutboundEndpointPolicy.ResolveDnsNames = false);
            configure(b);
        });
        return collection.BuildServiceProvider();
    }

    /// <summary>A transport that says whether pushes reach the consumer, counts how often it was asked, and records sends.</summary>
    private sealed class ReceivingTransport(string scheme, bool receives) : IDidCommTransport, IInboundReceivingTransport
    {
        private int _asked;

        public bool Receives { get; set; } = receives;

        public int Asked => Volatile.Read(ref _asked);

        public ConcurrentQueue<TransportRequest> Requests { get; } = new();

        public string Scheme => scheme;

        public bool ReceivesInbound
        {
            get
            {
                Interlocked.Increment(ref _asked);
                return Receives;
            }
        }

        public bool CanHandle(Uri endpoint) => string.Equals(endpoint.Scheme, scheme, StringComparison.OrdinalIgnoreCase);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: null));
        }
    }

    /// <summary>A transport that does not say.</summary>
    private sealed class SilentTransport(string scheme) : IDidCommTransport
    {
        public string Scheme => scheme;

        public bool CanHandle(Uri endpoint) => string.Equals(endpoint.Scheme, scheme, StringComparison.OrdinalIgnoreCase);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
            => Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: null));
    }

    /// <summary>A router of the host's own over one transport.</summary>
    private sealed class HostRouter(IDidCommTransport transport) : ITransportRouter
    {
        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct) => transport.SendAsync(request, ct);
    }
}
