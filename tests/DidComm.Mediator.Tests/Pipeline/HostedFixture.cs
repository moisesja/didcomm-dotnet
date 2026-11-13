using System.Diagnostics.Metrics;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// A real container with <c>AddMediator()</c>, a real did:peer routing DID (its service built by
/// hand with <see cref="DidCommServiceSpec"/>), a manual clock for both the facade and the hosted
/// service, a meter factory of its own and captured logs. The hosted service is driven directly
/// through its lifecycle methods.
/// </summary>
internal sealed class HostedFixture : IAsyncDisposable
{
    public static readonly DidCommServiceSpec HttpsService = new("https://mediator-host.example/didcomm", Accept: new[] { "didcomm/v2" });

    private HostedFixture(ServiceProvider services, PipelinePeers peers, PeerIdentity? mediator, ManualTimeProvider time, TestMeterFactory meters, HandlerLogSink logs)
    {
        Services = services;
        Peers = peers;
        Mediator = mediator;
        Time = time;
        Meters = meters;
        Recorder = new MeasurementRecorder(meters);
        Logs = logs;
    }

    public ServiceProvider Services { get; }
    public PipelinePeers Peers { get; }
    public PeerIdentity? Mediator { get; }
    public ManualTimeProvider Time { get; }
    public TestMeterFactory Meters { get; }
    public MeasurementRecorder Recorder { get; }
    public HandlerLogSink Logs { get; }

    public MediatorHostedService Service => Services.GetServices<IHostedService>().OfType<MediatorHostedService>().Single();
    public MediatorInvitationHolder Invitation => Services.GetRequiredService<MediatorInvitationHolder>();
    public MessagePickupQueue Queue => Services.GetRequiredService<MessagePickupQueue>();
    public IMediationStore Mediation => Services.GetRequiredService<IMediationStore>();
    public IReadOnlyList<HandlerLogEntry> ServiceLines => PipelineLogs.Of<MediatorHostedService>(Logs);

    /// <param name="service">The routing DID's single DIDComm service; <c>null</c> for none.</param>
    /// <param name="holdSecrets">Whether the container holds the routing DID's private keys.</param>
    /// <param name="configure">More mediator settings (the routing DID is already added unless <paramref name="routingDid"/> says otherwise).</param>
    /// <param name="didComm">More facade settings; the clock is already the manual clock.</param>
    /// <param name="before">Registrations made before <c>AddDidComm</c>.</param>
    /// <param name="after">Registrations made after <c>AddDidComm</c>.</param>
    /// <param name="routingDid">Overrides the routing DID put into the settings; empty string for none.</param>
    /// <param name="mintMediator">Whether to mint a did:peer for the mediator at all.</param>
    public static async Task<HostedFixture> CreateAsync(
        DidCommServiceSpec? service = null,
        bool holdSecrets = true,
        Action<MediatorOptions>? configure = null,
        Action<DidCommOptions>? didComm = null,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null,
        string? routingDid = null,
        bool mintMediator = true,
        bool noService = false)
    {
        var peers = new PipelinePeers();
        var mediator = mintMediator ? await peers.NewAsync(noService ? null : service ?? HttpsService, holdSecrets) : null;
        var did = routingDid ?? mediator?.Did ?? throw new InvalidOperationException("A routing DID is needed.");
        var time = new ManualTimeProvider();
        var meters = new TestMeterFactory();
        var logs = new HandlerLogSink();

        var collection = new ServiceCollection();
        collection.AddSingleton(logs);
        collection.AddSingleton(typeof(ILogger<>), typeof(HandlerCapturingLogger<>));
        collection.AddSingleton<TimeProvider>(time);
        collection.AddSingleton<IMeterFactory>(meters);
        before?.Invoke(collection);
        collection.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(peers.Secrets);
            b.Configure(o =>
            {
                o.Clock = time.GetUtcNow;
                didComm?.Invoke(o);
            });
            b.AddMediator(o =>
            {
                if (did.Length > 0)
                    o.CoordinateMediation.RoutingDids.Add(did);
                o.PurgeInterval = TimeSpan.FromMinutes(1);
                configure?.Invoke(o);
            });
        });
        after?.Invoke(collection);
        return new HostedFixture(collection.BuildServiceProvider(), peers, mediator, time, meters, logs);
    }

    /// <summary>Run the start-up checks.</summary>
    public Task StartingAsync() => Service.StartingAsync(CancellationToken.None);

    /// <summary>Run the whole start sequence a host runs.</summary>
    public async Task StartAsync()
    {
        var service = Service;
        await service.StartingAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StartedAsync(CancellationToken.None);
    }

    /// <summary>Run the whole stop sequence a host runs.</summary>
    public async Task StopAsync()
    {
        var service = Service;
        await service.StoppingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StoppedAsync(CancellationToken.None);
    }

    /// <summary>Enrol <paramref name="peerDid"/> (straight through the store) and register <paramref name="recipientDids"/>.</summary>
    public async Task EnrolAsync(string peerDid, params string[] recipientDids)
    {
        _ = await Mediation.GrantAsync(new MediationGrant(peerDid, Services.GetRequiredService<IMediatorIdentity>().RoutingDids), CancellationToken.None)
            ?? throw new InvalidOperationException("The mediation store is full.");
        if (recipientDids.Length > 0)
            await Mediation.UpdateRecipientsAsync(peerDid, recipientDids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray(), CancellationToken.None);
    }

    /// <summary>Every DID a log line must never carry.</summary>
    public string[] Dids => Mediator is null ? Array.Empty<string>() : new[] { Mediator.Did };

    public async ValueTask DisposeAsync()
    {
        Recorder.Dispose();
        await Services.DisposeAsync();
        Meters.Dispose();
        await Peers.DisposeAsync();
    }
}
