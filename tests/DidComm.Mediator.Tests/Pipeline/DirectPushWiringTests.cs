using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// How <c>AddMediator()</c> wires direct push (#110), through the real container and a real generic
/// host: off by default with nothing of it built; on, it needs a host endpoint directory and the
/// HTTP transport (refused at start-up in a generic host, at the first push outside one); it works
/// together with re-wrapping, however the two settings meet (owner decision OD-3 on #110: the
/// recipient's endpoint opens the mediator's forward); and a setting made only through a host
/// <c>Configure&lt;MediatorOptions&gt;</c> takes effect.
/// </summary>
public sealed class DirectPushWiringTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private const string Peer = "did:example:wiring-peer";
    private const string Recipient = "did:example:wiring-recipient";

    // ---- off by default ---------------------------------------------------------------------------

    [Fact]
    public async Task Direct_push_is_off_by_default_and_neither_the_directory_nor_the_sender_is_ever_built()
    {
        new MediatorOptions().DirectPush.Should().BeFalse();
        var counts = new BuildCounts();
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var host = WiringHost.Build(peers, mediator.Did, counts: counts);

        await host.App.StartAsync().WaitAsync(Wait);
        var dispatcher = host.Services.GetRequiredService<PushDispatcher>();
        await host.EnrolAsync(Peer, Recipient);
        await host.ForwardAsync(mediator.Did, Recipient, "fwd-wiring-off");
        dispatcher.Wake(Peer).Should().BeTrue();
        await host.Recorder.WaitForAsync(r => r.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Count >= 1, Wait, "the wake-up was served");
        await dispatcher.WaitForIdleAsync(Wait);
        await host.App.StopAsync().WaitAsync(Wait);

        counts.Directories.Should().Be(0, "nothing resolves the directory while direct push is off");
        counts.Senders.Should().Be(0, "nothing builds the direct-push sender while direct push is off");
        dispatcher.PushesWithoutLiveSession.Should().BeFalse();
        host.Recorder.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag)
            .Should().Equal(new[] { PushOutcomes.SkippedNoLiveSession }, "the forward woke nothing (no live session), and the explicit wake-up found none");
        host.ReadyLine.Should().Contain("direct push off");
    }

    // ---- on, and what it needs ---------------------------------------------------------------------

    [Fact]
    public async Task With_direct_push_on_and_no_directory_a_generic_host_refuses_to_start()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var host = WiringHost.Build(peers, mediator.Did, configure: o => o.DirectPush = true, directory: false);

        var failure = (await host.App.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().Contain(nameof(IRecipientEndpointDirectory)).And.Contain("before AddMediator()").And.NotContain(mediator.Did);
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().BeNull("a mediator that did not start issues no invitation");
    }

    [Fact]
    public async Task With_direct_push_on_and_no_directory_outside_a_host_the_push_workers_are_refused_when_first_built()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        await using var services = WiringHost.Container(peers, mediator.Did, configure: o => o.DirectPush = true, directory: false);

        var sink = () => services.GetRequiredService<IForwardSink>();
        var dispatcher = () => services.GetRequiredService<PushDispatcher>();

        sink.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(nameof(IRecipientEndpointDirectory));
        dispatcher.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(nameof(IRecipientEndpointDirectory));
    }

    [Fact]
    public async Task With_direct_push_on_and_no_http_transport_a_generic_host_refuses_to_start()
    {
        var counts = new BuildCounts();
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var host = WiringHost.Build(peers, mediator.Did, configure: o => o.DirectPush = true, http: false, counts: counts);

        var failure = (await host.App.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().NotContain(mediator.Did);
        counts.Senders.Should().Be(1, "start-up built the sender, and it refused");
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().BeNull();
    }

    [Fact]
    public async Task With_direct_push_on_an_invalid_trusted_proxy_fails_start_up()
    {
        // The host's own HTTP transport refuses it too, when the protocol handlers are built (start-up
        // step 3); the direct-push sender copies the same setting and would refuse it at its own build.
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var host = WiringHost.Build(peers, mediator.Did, configure: o => o.DirectPush = true, http: true, httpOptions: o => o.TrustedProxy = new WebProxy());

        var failure = (await host.App.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().Contain("TrustedProxy").And.NotContain(mediator.Did);
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().BeNull();
    }

    [Fact]
    public async Task With_direct_push_on_and_no_http_transport_outside_a_host_each_direct_push_fails_and_the_worker_goes_on()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var recorderMeters = new TestMeterFactory();
        using var recorder = new MeasurementRecorder(recorderMeters);
        await using var services = WiringHost.Container(peers, mediator.Did, configure: o =>
        {
            o.DirectPush = true;
            o.PushConcurrency = 1;
        }, http: false, meters: recorderMeters);
        var dispatcher = services.GetRequiredService<PushDispatcher>();
        dispatcher.PushesWithoutLiveSession.Should().BeTrue("the sender is built at the first push, not with the workers");
        var mediation = services.GetRequiredService<IMediationStore>();
        await mediation.GrantAsync(new MediationGrant(Peer, new[] { mediator.Did }), CancellationToken.None);
        await mediation.UpdateRecipientsAsync(Peer, new[] { new RecipientUpdate(Recipient, RecipientUpdateAction.Add) }, CancellationToken.None);
        var stored = await services.GetRequiredService<MessagePickupQueue>().DeliverAsync(Recipient, PushFixture.Payload(64), new PickupDeliveryOptions(AllowLive: false));

        for (var wake = 0; wake < 2; wake++)
        {
            dispatcher.Wake(Peer).Should().BeTrue();
            await recorder.WaitForAsync(r => r.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Count >= wake + 1, Wait, "the wake-up was served");
            await dispatcher.WaitForIdleAsync(Wait);
        }

        // The failure paused the recipient's direct pushes: the second wake-up is served (the worker
        // goes on) and skips the direct step.
        recorder.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedCooldown);
        var store = services.GetRequiredService<IMessageStore>();
        var now = services.GetRequiredService<IOptions<DidCommOptions>>().Value.Now();
        (await store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, long.MaxValue, now, new MessageLease("wiring-pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Select(r => r.Id).Should().Equal(new[] { stored.MessageId }, "the message waits for pickup");
    }

    [Fact]
    public async Task Direct_push_set_only_through_a_host_Configure_takes_effect_and_start_up_builds_the_sender_once()
    {
        var counts = new BuildCounts();
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        using var host = WiringHost.Build(peers, mediator.Did, counts: counts, after: s => s.Configure<MediatorOptions>(o => o.DirectPush = true));

        await host.App.StartAsync().WaitAsync(Wait);
        counts.Senders.Should().Be(1, "start-up built the sender, before any push");
        var dispatcher = host.Services.GetRequiredService<PushDispatcher>();
        await host.EnrolAsync(Peer, Recipient);
        await host.ForwardAsync(mediator.Did, Recipient, "fwd-wiring-on");
        await host.Recorder.WaitForAsync(r => r.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Count >= 1, Wait, "the forward woke its grant");
        await dispatcher.WaitForIdleAsync(Wait);
        await host.App.StopAsync().WaitAsync(Wait);

        dispatcher.PushesWithoutLiveSession.Should().BeTrue();
        counts.Senders.Should().Be(1, "start-up built the sender, and the pushes use that one");
        counts.Asked.Should().Contain(Peer, "a due forward for a grant without a live session went to the directory");
        host.Recorder.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag)
            .Should().Equal(new[] { PushOutcomes.SkippedNoEndpoint }, "the directory has no endpoint for it: nothing taken");
        host.ReadyLine.Should().Contain("direct push on");
    }

    // ---- with re-wrapping (owner decision OD-3 on #110: the recipient's endpoint opens it) --------

    [Fact]
    public void DirectPush_with_ReanoncryptToNext_is_accepted_at_AddMediator()
    {
        var services = new ServiceCollection();

        var act = () => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport();
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add("did:example:wiring-mediator");
                o.DirectPush = true;
                o.RewrapMode = RewrapMode.ReanoncryptToNext;
            });
        });

        act.Should().NotThrow();
        services.Should().Contain(d => d.ServiceType == typeof(MediatorMarker));
        var probe = new MediatorOptions { DirectPush = true, RewrapMode = RewrapMode.ReanoncryptToNext };
        probe.CoordinateMediation.RoutingDids.Add("did:example:wiring-mediator");
        probe.Invoking(o => o.Validate()).Should().NotThrow("the settings check no longer refuses the combination");
    }

    public static TheoryData<string> SettingsMeetingAtStartUp => new() { "direct push in the delegate", "re-wrap in the delegate" };

    [Theory]
    [MemberData(nameof(SettingsMeetingAtStartUp))]
    public async Task DirectPush_and_ReanoncryptToNext_meeting_only_in_the_settings_in_effect_start_and_push(string how)
    {
        var counts = new BuildCounts();
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        var inDelegate = how == "direct push in the delegate";
        using var host = WiringHost.Build(
            peers,
            mediator.Did,
            counts: counts,
            configure: o =>
            {
                if (inDelegate)
                    o.DirectPush = true;
                else
                    o.RewrapMode = RewrapMode.ReanoncryptToNext;
            },
            after: s => s.Configure<MediatorOptions>(o =>
            {
                if (inDelegate)
                    o.RewrapMode = RewrapMode.ReanoncryptToNext;
                else
                    o.DirectPush = true;
            }));

        await host.App.StartAsync().WaitAsync(Wait);
        var dispatcher = host.Services.GetRequiredService<PushDispatcher>();
        await host.EnrolAsync(Peer, Recipient);
        await host.ForwardAsync(mediator.Did, Recipient, "fwd-wiring-rewrap");
        await host.Recorder.WaitForAsync(r => r.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Count >= 1, Wait, "the forward woke its grant");
        await dispatcher.WaitForIdleAsync(Wait);
        await host.App.StopAsync().WaitAsync(Wait);

        dispatcher.PushesWithoutLiveSession.Should().BeTrue(how);
        counts.Asked.Should().Contain(Peer, "the direct push went to the directory ({0})", how);
        host.ReadyLine.Should().Contain("direct push on");
        host.Services.GetRequiredService<MediatorInvitationHolder>().PlaintextJson.Should().NotBeNull("the mediator started");
    }

    [Fact]
    public async Task DirectPush_and_ReanoncryptToNext_meeting_in_the_settings_in_effect_outside_a_host_build_the_push_workers()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        await using var services = WiringHost.Container(peers, mediator.Did, configure: o => o.RewrapMode = RewrapMode.ReanoncryptToNext,
            after: s => s.Configure<MediatorOptions>(o => o.DirectPush = true));

        var sink = () => services.GetRequiredService<IForwardSink>();

        sink.Should().NotThrow();
        services.GetRequiredService<PushDispatcher>().PushesWithoutLiveSession.Should().BeTrue();
    }

    // ---- the directory's constructor -----------------------------------------------------------

    public static TheoryData<string> CycleRoutes => new() { "push dispatcher", "forward sink", "IForwardSink" };

    [Theory]
    [MemberData(nameof(CycleRoutes))]
    public async Task A_directory_that_takes_the_push_workers_or_the_sink_in_its_constructor_fails_fast_with_a_circular_dependency_message(string takes)
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync(HostedFixture.HttpsService);
        await using var services = WiringHost.Container(peers, mediator.Did, configure: o => o.DirectPush = true, directory: false,
            before: s => s.AddSingleton<IRecipientEndpointDirectory>(sp =>
            {
                _ = takes switch
                {
                    "push dispatcher" => (object)sp.GetRequiredService<PushDispatcher>(),
                    "forward sink" => sp.GetRequiredService<MediatorForwardSink>(),
                    _ => sp.GetRequiredService<IForwardSink>(),
                };
                return new CountingDirectory(new BuildCounts());
            }));

        var act = () => Task.Run(() => services.GetRequiredService<PushDispatcher>()).WaitAsync(Wait);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("circular dependency").And.Contain(nameof(IRecipientEndpointDirectory));
    }
}

// ---- support ---------------------------------------------------------------------------------------

/// <summary>What the counting registrations of a <see cref="WiringHost"/> built.</summary>
internal sealed class BuildCounts
{
    private int _directories;
    private int _senders;

    public int Directories => Volatile.Read(ref _directories);
    public int Senders => Volatile.Read(ref _senders);
    public System.Collections.Concurrent.ConcurrentQueue<string> Asked { get; } = new();

    public void Directory() => Interlocked.Increment(ref _directories);
    public void Sender() => Interlocked.Increment(ref _senders);
}

/// <summary>An endpoint directory with no endpoint for anyone, that records who it was asked for.</summary>
internal sealed class CountingDirectory(BuildCounts counts) : IRecipientEndpointDirectory
{
    public ValueTask<Uri?> FindAsync(string grantPeerDid, CancellationToken ct)
    {
        counts.Asked.Enqueue(grantPeerDid);
        return ValueTask.FromResult<Uri?>(null);
    }
}

/// <summary>
/// A mediator in a real generic host (<see cref="Build"/>) or a bare container (<see cref="Container"/>),
/// with the HTTP transport unless told otherwise, a directory that counts its builds, a sender
/// registration that counts its builds and builds the real one, captured logs and its own meters.
/// </summary>
internal sealed class WiringHost : IDisposable
{
    private WiringHost(IHost app, HandlerLogSink logs, TestMeterFactory meters)
    {
        App = app;
        Logs = logs;
        Meters = meters;
        Recorder = new MeasurementRecorder(meters);
    }

    public IHost App { get; }
    public IServiceProvider Services => App.Services;
    public HandlerLogSink Logs { get; }
    public TestMeterFactory Meters { get; }
    public MeasurementRecorder Recorder { get; }

    /// <summary>The hosted service's "ready" line.</summary>
    public string ReadyLine => PipelineLogs.Of<MediatorHostedService>(Logs).Single(e => e.Formatted.StartsWith("Mediator ready", StringComparison.Ordinal)).Formatted;

    public static WiringHost Build(
        PipelinePeers peers,
        string routingDid,
        Action<MediatorOptions>? configure = null,
        bool http = true,
        Action<HttpTransportOptions>? httpOptions = null,
        bool directory = true,
        BuildCounts? counts = null,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null)
    {
        var logs = new HandlerLogSink();
        var meters = new TestMeterFactory();
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        Register(builder.Services, peers, routingDid, configure, http, httpOptions, directory, counts ?? new BuildCounts(), logs, meters, before, after);
        return new WiringHost(builder.Build(), logs, meters);
    }

    public static ServiceProvider Container(
        PipelinePeers peers,
        string routingDid,
        Action<MediatorOptions>? configure = null,
        bool http = true,
        bool directory = true,
        BuildCounts? counts = null,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null,
        TestMeterFactory? meters = null)
    {
        var services = new ServiceCollection();
        Register(services, peers, routingDid, configure, http, null, directory, counts ?? new BuildCounts(), new HandlerLogSink(), meters ?? new TestMeterFactory(), before, after);
        return services.BuildServiceProvider();
    }

    private static void Register(
        IServiceCollection services,
        PipelinePeers peers,
        string routingDid,
        Action<MediatorOptions>? configure,
        bool http,
        Action<HttpTransportOptions>? httpOptions,
        bool directory,
        BuildCounts counts,
        HandlerLogSink logs,
        TestMeterFactory meters,
        Action<IServiceCollection>? before,
        Action<IServiceCollection>? after)
    {
        services.AddSingleton(logs);
        services.AddSingleton(typeof(ILogger<>), typeof(HandlerCapturingLogger<>));
        services.AddSingleton<IMeterFactory>(meters);
        if (directory)
        {
            services.AddSingleton<IRecipientEndpointDirectory>(_ =>
            {
                counts.Directory();
                return new CountingDirectory(counts);
            });
        }
        // Ahead of AddMediator()'s own registration (a TryAdd): counts every build of the real sender.
        services.AddSingleton(sp =>
        {
            counts.Sender();
            return DirectPushSender.Create(sp);
        });
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(peers.Secrets);
            if (http)
                b.UseHttpTransport(httpOptions);
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(routingDid);
                configure?.Invoke(o);
            });
        });
        after?.Invoke(services);
    }

    /// <summary>Enrol <paramref name="peerDid"/> (straight through the store) with <paramref name="recipientDid"/>.</summary>
    public async Task EnrolAsync(string peerDid, string recipientDid)
    {
        var mediation = Services.GetRequiredService<IMediationStore>();
        _ = await mediation.GrantAsync(new MediationGrant(peerDid, Services.GetRequiredService<IMediatorIdentity>().RoutingDids), CancellationToken.None);
        await mediation.UpdateRecipientsAsync(peerDid, new[] { new RecipientUpdate(recipientDid, RecipientUpdateAction.Add) }, CancellationToken.None);
    }

    /// <summary>What the forward pump does with a relayed forward: hand it to the mediator's sink.</summary>
    public Task ForwardAsync(string mediatorDid, string recipientDid, string forwardId)
        => Services.GetRequiredService<IForwardSink>().AcceptAsync(
            new ForwardProcessingResult(recipientDid, PushFixture.Payload(64), null, Delay: null),
            DirectWorld.ForwardContext(mediatorDid, Services.GetRequiredService<IOptions<DidCommOptions>>().Value, forwardId),
            CancellationToken.None);

    public void Dispose()
    {
        Recorder.Dispose();
        App.Dispose();
        Meters.Dispose();
    }
}
