using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Routing;
using DidComm.Protocols.TrustPing;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

// L-014: a namespace import must not shadow the same-named static class.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <c>AddMediator()</c> through the real container (JC-8, design §6–§7): the registration sequence,
/// the conflict checks, repeated calls, options composition with the host's own
/// <c>Configure&lt;T&gt;</c> calls, the identity, Discover Features, and a graph that resolves.
/// </summary>
public sealed class AddMediatorDiTests
{
    private const string Mediator1 = "did:example:mediator-one";
    private const string Mediator2 = "did:example:mediator-two";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ServiceProvider Build(Action<DidCommBuilder> configure, Action<IServiceCollection>? before = null, Action<IServiceCollection>? after = null, InMemorySecretsResolver? secrets = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets ?? new InMemorySecretsResolver());
            configure(b);
        });
        after?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ServiceCollection Collection(Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        return services;
    }

    // ---- what is registered -----------------------------------------------------------------

    [Fact]
    public async Task Registers_every_role_the_marker_and_an_identity_over_the_configured_routing_dids()
    {
        await using var sp = Build(b => b.AddMediator(o =>
        {
            o.CoordinateMediation.RoutingDids.Add(Mediator1);
            o.CoordinateMediation.RoutingDids.Add(Mediator2);
            o.CoordinateMediation.RoutingDids.Add(Mediator1);
        }));

        sp.GetService<MediatorMarker>().Should().NotBeNull();
        var identity = sp.GetRequiredService<IMediatorIdentity>().Should().BeOfType<MediatorIdentity>().Which;
        identity.RoutingDids.Should().Equal(Mediator1, Mediator2);

        var handlers = sp.GetServices<IProtocolHandler>().ToArray();
        handlers.Should().ContainSingle(h => h is CoordinateMediationHandler);
        handlers.Should().ContainSingle(h => h is MessagePickupHandler);
        handlers.Should().ContainSingle(h => h is ForwardHandler);
        handlers.Should().ContainSingle(h => h is TrustPingHandler, "Trust Ping answers health checks");
        handlers.Should().ContainSingle(h => h is DiscoverFeaturesHandler);
        sp.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<MediationForwardRecipientRegistry>("the grant store is the forward directory");
        sp.GetRequiredService<MessagePickupQueue>().Should().NotBeNull();
    }

    [Fact]
    public async Task The_forward_settings_come_from_the_mediator_options()
    {
        await using var sp = Build(b => b.AddMediator(o =>
        {
            o.CoordinateMediation.RoutingDids.Add(Mediator1);
            o.RewrapMode = RewrapMode.ReanoncryptToNext;
            o.MaxForwardDelay = TimeSpan.FromMinutes(10);
        }));

        var forward = sp.GetRequiredService<ForwardProcessorOptions>();
        forward.Mode.Should().Be(RewrapMode.ReanoncryptToNext);
        forward.MaxDelay.Should().Be(TimeSpan.FromMinutes(10));
        forward.ExtraRecipientRoutingKeys.Should().BeNull();
        sp.GetRequiredService<IRecipientRoutingKeyProvider>().Should().BeOfType<StaticRecipientRoutingKeyProvider>();
    }

    [Fact]
    public async Task Repeated_calls_compose_their_settings_and_register_each_role_once()
    {
        var services = Collection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Mediator1);
                o.MaxForwardDelay = TimeSpan.FromMinutes(10);
            });
            b.AddMediator(o =>
            {
                o.RewrapMode = RewrapMode.ReanoncryptToNext;
                o.Pickup.MaxQueueMessages = 7;
            });
            b.AddMediator();
        });

        services.Count(d => d.ServiceType == typeof(MediatorMarker)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(ForwardProcessorOptions)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IMediatorIdentity)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IRecipientRoutingKeyProvider)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(ForwardMetrics)).Should().Be(1);

        await using var sp = services.BuildServiceProvider();
        var forward = sp.GetRequiredService<ForwardProcessorOptions>();
        forward.Mode.Should().Be(RewrapMode.ReanoncryptToNext);
        forward.MaxDelay.Should().Be(TimeSpan.FromMinutes(10));
        sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value.MaxQueueMessages.Should().Be(7);
        sp.GetRequiredService<IMediatorIdentity>().RoutingDids.Should().Equal(Mediator1);
        sp.GetServices<IProtocolHandler>().GroupBy(h => h.GetType()).Should().OnlyContain(g => g.Count() == 1);
    }

    [Fact]
    public async Task Without_a_configure_delegate_a_host_identity_supplies_the_routing_dids()
    {
        var host = new MediatorIdentity(Mediator2);
        await using var sp = Build(b => b.AddMediator(), before: s => s.AddSingleton<IMediatorIdentity>(host));

        sp.GetRequiredService<IMediatorIdentity>().Should().BeSameAs(host);
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
    }

    // ---- routing DIDs ----------------------------------------------------------------------

    [Fact]
    public void No_routing_did_and_nothing_that_could_supply_one_is_refused_at_registration_and_nothing_is_added()
    {
        var services = Collection();
        var builderCount = 0;

        var act = () => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            builderCount = services.Count;
            b.AddMediator(o => o.Pickup.MaxQueueMessages = 5);
        });

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("routing DID");
        services.Count.Should().Be(builderCount, "a refused call adds nothing");
    }

    [Fact]
    public async Task A_host_configuration_of_the_coordinate_mediation_options_defers_the_check_to_first_use()
    {
        await using var withDid = Build(b => b.AddMediator(), before: s => s.Configure<CoordinateMediationOptions>(o => o.RoutingDids.Add(Mediator1)));
        withDid.GetRequiredService<IMediatorIdentity>().RoutingDids.Should().Equal(Mediator1);

        await using var withoutDid = Build(b => b.AddMediator(), before: s => s.Configure<CoordinateMediationOptions>(o => o.MaxPageSize = 5));
        withoutDid.Invoking(sp => sp.GetRequiredService<IMediatorIdentity>()).Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("at least one routing DID");
    }

    // ---- conflicts ---------------------------------------------------------------------------

    [Fact]
    public void An_earlier_AddRoutingMediator_is_refused_and_nothing_is_added()
    {
        var services = Collection();
        var count = 0;

        var act = () => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddRoutingMediator();
            count = services.Count;
            b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(Mediator1));
        });

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("AddRoutingMediator()");
        services.Count.Should().Be(count);
    }

    [Fact]
    public async Task AddRoutingMediator_after_AddMediator_changes_nothing()
    {
        await using var sp = Build(b =>
        {
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Mediator1);
                o.RewrapMode = RewrapMode.ReanoncryptToNext;
            });
            b.AddRoutingMediator(new ForwardProcessorOptions(RewrapMode.PassThrough));
        });

        sp.GetRequiredService<ForwardProcessorOptions>().Mode.Should().Be(RewrapMode.ReanoncryptToNext);
    }

    [Fact]
    public void Rewrap_together_with_a_host_routing_key_provider_is_refused()
    {
        var services = Collection(s => s.AddSingleton<IRecipientRoutingKeyProvider>(new StaticRecipientRoutingKeyProvider(null)));

        var act = () => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Mediator1);
                o.RewrapMode = RewrapMode.ReanoncryptToNext;
            });
        });

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(MediatorOptions.RewrapMode));
    }

    [Fact]
    public async Task Pass_through_keeps_a_host_routing_key_provider()
    {
        var provider = new StaticRecipientRoutingKeyProvider(null);
        await using var sp = Build(b => b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(Mediator1)),
            before: s => s.AddSingleton<IRecipientRoutingKeyProvider>(provider));

        sp.GetRequiredService<IRecipientRoutingKeyProvider>().Should().BeSameAs(provider);
    }

    [Fact]
    public async Task Rewrap_on_a_repeated_call_without_a_host_provider_is_accepted()
    {
        await using var sp = Build(b =>
        {
            b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(Mediator1));
            b.AddMediator(o => o.RewrapMode = RewrapMode.ReanoncryptToNext);
        });

        sp.GetRequiredService<ForwardProcessorOptions>().Mode.Should().Be(RewrapMode.ReanoncryptToNext);
    }

    public static TheoryData<Action<MediatorOptions>, Type?> InvalidSettings => new()
    {
        { o => o.Replay.MaxEntries = 0, typeof(InvalidOperationException) },
        { o => o.Sessions.MaxSessions = 0, typeof(InvalidOperationException) },
        { o => o.Pickup.MaxQueueMessages = 0, null },
        { o => o.CoordinateMediation.MaxGrants = 0, null },
        { o => o.MaxForwardDelay = TimeSpan.FromSeconds(-1), null },
        { o => o.DirectPushConcurrency = 0, null },
        { o => { o.MaxForwardDelay = TimeSpan.FromDays(20); o.Pickup.MaxMessageAge = TimeSpan.FromDays(10); }, null },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Every_invalid_setting_is_refused_at_registration_as_an_ArgumentException(Action<MediatorOptions> invalid, Type? inner)
    {
        var services = Collection();

        var act = () => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Mediator1);
                invalid(o);
            });
        });

        var refusal = act.Should().Throw<ArgumentException>().Which;
        if (inner is not null)
            refusal.InnerException.Should().BeOfType(inner);
        services.Should().NotContain(d => d.ServiceType == typeof(MediatorMarker));
    }

    // ---- options composition (JC-8) ------------------------------------------------------------

    [Fact]
    public async Task Host_replay_settings_compose_with_the_mediator_delegate_by_registration_order()
    {
        await using var sp = Build(
            b => b.AddMediator(o =>
            {
                o.CoordinateMediation.RoutingDids.Add(Mediator1);
                o.Replay.MaxEntries = 2_000;
                o.Replay.MaxEntriesPerPeer = 20;
            }),
            before: s => s.Configure<ReplayProtectionOptions>(r =>
            {
                r.Window = TimeSpan.FromMinutes(10);
                r.MaxEntries = 1_000;
            }),
            after: s => s.Configure<ReplayProtectionOptions>(r => r.MaxEntriesPerPeer = 30));

        var replay = sp.GetRequiredService<IOptions<ReplayProtectionOptions>>().Value;
        replay.Window.Should().Be(TimeSpan.FromMinutes(10), "an earlier host setting the delegate does not assign survives");
        replay.MaxEntries.Should().Be(2_000, "the delegate's assignment wins over an earlier host setting");
        replay.MaxEntriesPerPeer.Should().Be(30, "a later host setting wins over the delegate");
    }

    [Fact]
    public async Task Every_role_reads_the_delegate_through_its_own_options()
    {
        await using var sp = Build(b => b.AddMediator(o =>
        {
            o.CoordinateMediation.RoutingDids.Add(Mediator1);
            o.CoordinateMediation.MaxPageSize = 17;
            o.Pickup.LeaseTimeout = TimeSpan.FromSeconds(42);
            o.Replay.ClockSkew = TimeSpan.FromSeconds(5);
            o.Sessions.MaxPendingPushes = 3;
            o.PurgeInterval = TimeSpan.FromSeconds(9);
        }));

        sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value.MaxPageSize.Should().Be(17);
        sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value.LeaseTimeout.Should().Be(TimeSpan.FromSeconds(42));
        sp.GetRequiredService<IOptions<ReplayProtectionOptions>>().Value.ClockSkew.Should().Be(TimeSpan.FromSeconds(5));
        sp.GetRequiredService<IOptions<ReturnRouteSessionOptions>>().Value.MaxPendingPushes.Should().Be(3);
        sp.GetRequiredService<IOptions<MediatorOptions>>().Value.PurgeInterval.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task AddDidComm_without_a_host_guard_builds_its_replay_guard_from_the_composed_values()
    {
        await using var sp = Build(b => b.AddMediator(o =>
        {
            o.CoordinateMediation.RoutingDids.Add(Mediator1);
            o.Replay.MaxEntriesPerPeer = 1;
        }));

        var guard = sp.GetRequiredService<IInboundReplayGuard>().Should().BeOfType<InMemoryReplayGuard>().Which;
        var now = DateTimeOffset.UtcNow;
        guard.TryAdmit("did:example:peer", "id-1", now.ToUnixTimeSeconds(), now).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit("did:example:peer", "id-2", now.ToUnixTimeSeconds(), now).Should().Be(ReplayAdmission.Saturated,
            "the guard AddDidComm builds reads MediatorOptions.Replay through IOptions<ReplayProtectionOptions>");
    }

    // ---- Discover Features (FR-MED-01, FR-PICK-01) -------------------------------------------

    [Fact]
    public async Task Discover_features_discloses_the_three_mediator_roles_and_trust_ping()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var alice = await peers.NewAsync();
        await using var sp = Build(b => b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(mediator.Did)), secrets: peers.Secrets);
        var client = sp.GetRequiredService<DidCommClient>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;

        var query = DiscoverFeaturesApi.CreateQuery(alice.Did, mediator.Did,
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "*" });
        var packed = await client.PackEncryptedAsync(query, new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: alice.Did));
        var outcome = await sp.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, options);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(outcome.Reply!);
        foreach (var piuri in new[] { ForwardConstants.ProtocolIdentifier, "https://didcomm.org/coordinate-mediation/3.0", "https://didcomm.org/messagepickup/3.0" })
            disclosures.Should().ContainSingle(d => d.Id == piuri).Which.Roles.Should().Equal("mediator");
        disclosures.Should().ContainSingle(d => d.Id == TrustPing.ProtocolUri);
    }

    // ---- the graph -----------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_graph_resolves_without_a_cycle()
    {
        await using var sp = Build(b => b.AddMediator(o => o.CoordinateMediation.RoutingDids.Add(Mediator1)));

        await Task.Run(() =>
        {
            sp.GetRequiredService<ProtocolDispatcher>();
            sp.GetRequiredService<ForwardHandler>();
            sp.GetRequiredService<IForwardSink>();
            sp.GetRequiredService<DidCommClient>();
        }).WaitAsync(Timeout);
    }
}
