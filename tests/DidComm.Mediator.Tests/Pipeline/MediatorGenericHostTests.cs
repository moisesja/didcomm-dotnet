using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <c>AddMediator()</c> under a real generic <see cref="IHost"/>: the host finds the lifecycle
/// service, runs the checks before it reports started, builds the whole graph without a cycle, and
/// refuses to start a mediator whose routing DID is unusable or whose Coordinate Mediation 2.0
/// setting disagrees with its registration.
/// </summary>
public sealed class MediatorGenericHostTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static IHost Build(PipelinePeers peers, string routingDid, Action<MediatorOptions>? configure = null, Action<IServiceCollection>? after = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(peers.Secrets);
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(routingDid);
                configure?.Invoke(o);
            });
        });
        after?.Invoke(builder.Services);
        return builder.Build();
    }

    [Fact]
    public async Task The_host_starts_the_mediator_and_stops_it_promptly()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var host = Build(peers, mediator.Did);

        await host.StartAsync().WaitAsync(Wait);

        host.Services.GetServices<IHostedService>().OfType<MediatorHostedService>().Should().ContainSingle()
            .Which.Should().BeAssignableTo<IHostedLifecycleService>();
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().Contain(mediator.Did);
        host.Services.GetRequiredService<IForwardSink>().Should().BeOfType<MediatorForwardSink>();
        host.Services.GetRequiredService<PushDispatcher>().Should().NotBeNull();
        host.Services.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();

        await host.StopAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task The_host_refuses_to_start_a_mediator_whose_routing_did_is_unusable()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService, holdSecrets: false);
        using var host = Build(peers, mediator.Did);

        var failure = (await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().Contain("index 0").And.NotContain(mediator.Did);
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().BeNull();
    }

    [Fact]
    public async Task The_host_refuses_to_start_a_mediator_whose_EnableV2_setting_disagrees_with_its_registration()
    {
        // AddMediator() registered Coordinate Mediation 2.0 (the default); a later host
        // Configure<> switches it off. Start-up builds the handlers and refuses rather than serve
        // (or silently not serve) a protocol the settings say otherwise about (#99, DC-5).
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var host = Build(peers, mediator.Did, after: s => s.Configure<CoordinateMediationOptions>(o => o.EnableV2 = false));

        var failure = (await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().Contain("EnableV2").And.NotContain(mediator.Did);
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().BeNull("a mediator that did not start issues no invitation");
    }

    [Fact]
    public async Task Disposing_the_host_after_a_start_disposes_the_pipeline_cleanly()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        var host = Build(peers, mediator.Did);
        await host.StartAsync().WaitAsync(Wait);
        var dispatcher = host.Services.GetRequiredService<PushDispatcher>();

        await host.StopAsync().WaitAsync(Wait);
        await ((IAsyncDisposable)host).DisposeAsync().AsTask().WaitAsync(Wait);

        dispatcher.Wake("did:example:p").Should().BeFalse("the dispatcher was disposed with the container");
    }
}
