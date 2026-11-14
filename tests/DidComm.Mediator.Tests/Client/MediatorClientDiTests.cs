using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <c>AddMediatorClient()</c> (#96 design §5, L-017): one channel per container that is also the
/// dispatcher's only client correlator, both clients over it, repeated calls composing; a mediator
/// and a recipient in one container resolving without a cycle or a hang; the WebSocket refusal
/// without <c>UseInboundDispatch()</c>; the container's clock; disposal; the non-DI clients; and an
/// answer travelling through the real dispatcher to the waiting request (the correlator claims it,
/// so the dispatcher's "no handler" line drops to Debug, R-3).
/// </summary>
public sealed class MediatorClientDiTests
{
    private static readonly TimeSpan NoHang = TimeSpan.FromSeconds(10);

    [Fact]
    public void One_channel_that_is_the_only_client_correlator_with_both_clients_over_it_and_repeated_calls_compose()
    {
        using var sp = Build(b => b
            .AddMediatorClient(o => o.RequestTimeout = TimeSpan.FromSeconds(5))
            .AddMediatorClient(o => o.MaxPendingRequests = 3)
            .AddMediatorClient());

        var channel = sp.GetRequiredService<MediatorClientChannel>();
        sp.GetRequiredService<MediatorClientChannel>().Should().BeSameAs(channel);
        sp.GetServices<IInboundCorrelator>().OfType<MediatorClientChannel>().Should().ContainSingle().Which.Should().BeSameAs(channel);
        sp.GetRequiredService<MediationClient>().Exchange.Should().BeSameAs(channel);
        sp.GetRequiredService<PickupClient>().Exchange.Should().BeSameAs(channel);
        sp.GetRequiredService<MediationClient>().Should().BeSameAs(sp.GetRequiredService<MediationClient>());
        channel.Options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(5));
        channel.Options.MaxPendingRequests.Should().Be(3);
        channel.Client.Should().BeSameAs(sp.GetRequiredService<DidCommClient>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_mediator_and_a_recipient_in_one_container_resolve_the_dispatcher_without_a_cycle_or_hang(bool clientFirst)
    {
        using var sp = Build(b =>
        {
            if (clientFirst)
                b.AddMediatorClient();
            b.AddMediator(m => m.CoordinateMediation.RoutingDids.Add("did:example:routing"));
            if (!clientFirst)
                b.AddMediatorClient();
            b.UseInboundDispatch();
        });

        var dispatcher = await Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>()).WaitAsync(NoHang);

        dispatcher.Should().NotBeNull();
        var channel = sp.GetRequiredService<MediatorClientChannel>();
        sp.GetServices<IInboundCorrelator>().Should().Contain(c => ReferenceEquals(c, channel));
        (await Task.Run(() => sp.GetRequiredService<MediationClient>()).WaitAsync(NoHang)).Exchange.Should().BeSameAs(channel);
        channel.ReceivesInbound.Should().BeTrue();
    }

    [Fact]
    public async Task Without_UseInboundDispatch_a_WebSocket_route_is_refused_naming_the_fix()
    {
        using var sp = Build(b => b.AddMediatorClient());
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        var act = () => channel.ResolveRouteAsync("did:example:mediator", new Uri("wss://mediator.example/ws"), requireWebSocket: false, default);

        channel.ReceivesInbound.Should().BeFalse();
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("UseWebSocketTransport(o => o.ReceiveInbound = true)").And.Contain("UseInboundDispatch()");
        (await channel.ResolveRouteAsync("did:example:mediator", new Uri("https://mediator.example/"), false, default)).IsHttp.Should().BeTrue("HTTP still works");
    }

    [Fact]
    public async Task With_UseInboundDispatch_a_WebSocket_route_is_admitted()
    {
        using var sp = Build(b => b.UseInboundDispatch().AddMediatorClient());
        var channel = sp.GetRequiredService<MediatorClientChannel>();

        var route = await channel.ResolveRouteAsync("did:example:mediator", new Uri("wss://mediator.example/ws"), requireWebSocket: true, default);

        channel.ReceivesInbound.Should().BeTrue();
        route.IsWebSocket.Should().BeTrue();
    }

    [Fact]
    public void The_containers_TimeProvider_is_the_channels_clock()
    {
        var time = new ManualTimeProvider();
        using var sp = Build(b => b.AddMediatorClient(), services => services.AddSingleton<TimeProvider>(time));

        sp.GetRequiredService<MediatorClientChannel>().Time.Should().BeSameAs(time);
        sp.GetRequiredService<MediatorClientChannel>().Versions.Time.Should().BeSameAs(time);
    }

    [Fact]
    public async Task Disposing_the_container_disposes_the_channel_once_and_fails_its_waiting_requests()
    {
        var transport = new AcceptingTransport("wss");
        var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), services => services.AddSingleton<IDidCommTransport>(transport));
        var parties = await Parties.CreateAsync(sp);
        var channel = sp.GetRequiredService<MediatorClientChannel>();
        _ = sp.GetRequiredService<ProtocolDispatcher>(); // resolves the channel as a correlator too
        var waiting = channel.ExchangeAsync(new MediatorRoute(transport.Endpoint, false, true), parties.StatusRequest(), ClientSpecs.StatusRequest, parties.Mediator.Did, parties.Local.Did, default);
        (await transport.Sent.Task.WaitAsync(NoHang)).Should().NotBeNull();

        await sp.DisposeAsync();

        channel.IsDisposed.Should().BeTrue();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task An_answer_dispatched_by_the_real_dispatcher_completes_the_waiting_request_and_the_no_handler_line_is_Debug()
    {
        var transport = new AcceptingTransport("wss");
        var logs = new ClientLogCapture();
        using var sp = Build(b => b.UseInboundDispatch().AddMediatorClient(), services =>
        {
            services.AddSingleton<IDidCommTransport>(transport);
            services.AddLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        });
        var parties = await Parties.CreateAsync(sp);
        var channel = sp.GetRequiredService<MediatorClientChannel>();
        var request = parties.StatusRequest();
        var exchange = channel.ExchangeAsync(new MediatorRoute(transport.Endpoint, false, true), request, ClientSpecs.StatusRequest, parties.Mediator.Did, parties.Local.Did, default);
        await transport.Sent.Task.WaitAsync(NoHang);
        var client = sp.GetRequiredService<DidCommClient>();
        var answer = MessagePickup.CreateStatus(parties.Mediator.Did, parties.Local.Did, new PickupStatus(4), thid: request.Id);
        var packed = await client.PackEncryptedAsync(answer, new PackEncryptedOptions(Recipients: new[] { parties.Local.Did }, From: parties.Mediator.Did));
        var unpacked = await client.UnpackAsync(packed.Message);

        var outcome = await sp.GetRequiredService<ProtocolDispatcher>().DispatchAsync(unpacked, client, sp.GetRequiredService<IOptions<DidCommOptions>>().Value);

        outcome.Result.Should().Be(DispatchResult.NoHandler, "a recipient-only host has no handler for the answer");
        var snapshot = await exchange.WaitAsync(NoHang);
        snapshot.Id.Should().Be(answer.Id);
        MessagePickup.TryParseStatus(snapshot.DeserializeMessage(), out var status).Should().BeTrue();
        status!.MessageCount.Should().Be(4);
        logs.Entries.Where(e => e.Formatted.Contains("No protocol handler registered", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug, "the correlator claimed it (R-3)");
        transport.Requests.Single().ReturnRoute.Should().Be(Transports.ReturnRoute.ReturnRouteMode.All);
    }

    [Fact]
    public async Task The_non_DI_clients_each_own_a_channel_that_cannot_receive_and_keep_a_copy_of_the_settings()
    {
        using var sp = Build(b => { });
        var client = sp.GetRequiredService<DidCommClient>();
        var options = new MediatorClientOptions { RequestTimeout = TimeSpan.FromSeconds(9) };

        var mediation = new MediationClient(client, options);
        var pickup = new PickupClient(client, options);
        options.RequestTimeout = TimeSpan.FromSeconds(1);

        var mediationChannel = mediation.Exchange.Should().BeOfType<MediatorClientChannel>().Which;
        var pickupChannel = pickup.Exchange.Should().BeOfType<MediatorClientChannel>().Which;
        mediationChannel.Should().NotBeSameAs(pickupChannel);
        mediationChannel.ReceivesInbound.Should().BeFalse();
        mediationChannel.Options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(9));
        mediationChannel.Time.Should().BeSameAs(TimeProvider.System);
        var act = () => pickupChannel.ResolveRouteAsync("did:example:mediator", new Uri("wss://m.example/"), requireWebSocket: true, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ServiceProvider Build(Action<DidCommBuilder> configure, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        var secrets = new InMemorySecretsResolver();
        collection.AddSingleton(secrets);
        services?.Invoke(collection);
        collection.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.Configure(o => o.OutboundEndpointPolicy.ResolveDnsNames = false);
            configure(b);
        });
        return collection.BuildServiceProvider();
    }

    /// <summary>A mediator DID and a local DID whose keys the container's secrets resolver holds.</summary>
    private sealed record Parties(PeerIdentity Mediator, PeerIdentity Local)
    {
        public static async Task<Parties> CreateAsync(IServiceProvider sp)
        {
            var secrets = sp.GetRequiredService<InMemorySecretsResolver>();
            async Task<PeerIdentity> Mint()
            {
                var peer = await PeerIdentityFactory.CreateAsync(sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>());
                foreach (var jwk in peer.Privates)
                    secrets.Add(jwk);
                return peer;
            }
            return new Parties(await Mint(), await Mint());
        }

        public Messages.Message StatusRequest() => MessagePickup.CreateStatusRequest(Local.Did, Mediator.Did);
    }

    /// <summary>A transport for one scheme that accepts every request and records it.</summary>
    private sealed class AcceptingTransport(string scheme) : IDidCommTransport
    {
        public Uri Endpoint { get; } = new($"{scheme}://mediator.example/ws");
        public ConcurrentQueue<TransportRequest> Requests { get; } = new();
        public TaskCompletionSource<TransportRequest> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Scheme => scheme;

        public bool CanHandle(Uri endpoint) => string.Equals(endpoint.Scheme, scheme, StringComparison.OrdinalIgnoreCase);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            Sent.TrySetResult(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: null));
        }
    }
}
