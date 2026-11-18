using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

// L-014: a namespace import must not shadow the same-named static class.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// How <c>AddCoordinateMediation()</c> registers the Coordinate Mediation 2.0 mediator role
/// through the real container (FR-MED-10, design call DC-5): on by default and resolved to one
/// singleton with the <c>mediator</c> role; off with <c>EnableV2 = false</c> (nothing resolves,
/// nothing is disclosed, a 2.0 request finds no handler); repeated calls; settings that disagree
/// with what was registered fail when the handlers are first built, naming the setting and no DID;
/// and both versions sharing the host's store, policy and identity.
/// </summary>
public sealed class CoordinateMediation20DiTests
{
    private const string Sentinel = "did:example:SENTINEL-routing";

    private static readonly string[] TwoZeroTypes =
    {
        CoordinateMediation20.MediateRequestType, CoordinateMediation20.MediateGrantType, CoordinateMediation20.MediateDenyType,
        CoordinateMediation20.KeylistUpdateType, CoordinateMediation20.KeylistUpdateResponseType,
        CoordinateMediation20.KeylistQueryType, CoordinateMediation20.KeylistType,
    };

    private static readonly string[] ThreeZeroTypes =
    {
        CoordinateMediation.MediateRequestType, CoordinateMediation.MediateGrantType, CoordinateMediation.MediateDenyType,
        CoordinateMediation.RecipientUpdateType, CoordinateMediation.RecipientUpdateResponseType,
        CoordinateMediation.RecipientQueryType, CoordinateMediation.RecipientType,
    };

    private static ServiceCollection Services(Action<DidCommBuilder> configure, Action<IServiceCollection>? after = null, InMemorySecretsResolver? secrets = null)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets ?? new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            configure(b);
        });
        after?.Invoke(services);
        return services;
    }

    private static ServiceProvider Build(Action<DidCommBuilder> configure, Action<IServiceCollection>? after = null, InMemorySecretsResolver? secrets = null)
        => Services(configure, after, secrets).BuildServiceProvider();

    // ---- on by default ------------------------------------------------------------------------

    [Fact]
    public void By_default_the_seven_2_0_types_resolve_to_the_typed_2_0_singleton_with_roles_mediator()
    {
        using var sp = Build(b => b.AddCoordinateMediation());

        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        var handler = sp.GetRequiredService<CoordinateMediation20Handler>();
        foreach (var type in TwoZeroTypes)
        {
            registry.TryResolve(type, out var resolved).Should().BeTrue(type);
            resolved.Should().BeSameAs(handler, "the registry entry and the typed singleton are one instance");
        }

        handler.Roles.Should().Equal(CoordinateMediation.MediatorRole);
        handler.ProtocolUri.Should().Be(MediatorServiceProtocols.CoordinateMediation20);
        handler.Should().NotBeSameAs(sp.GetRequiredService<CoordinateMediationHandler>());
        sp.GetServices<IProtocolHandler>().OfType<CoordinateMediation20Handler>().Should().ContainSingle().Which.Should().BeSameAs(handler);
        sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value.EnableV2.Should().BeTrue("2.0 is on unless switched off");
    }

    // ---- off ----------------------------------------------------------------------------------

    [Fact]
    public async Task With_EnableV2_false_no_2_0_handler_resolves_and_discover_features_discloses_only_3_0()
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b => b.AddCoordinateMediation(o => o.EnableV2 = false), secrets: secrets);

        sp.GetService<CoordinateMediation20Handler>().Should().BeNull();
        sp.GetServices<IProtocolHandler>().OfType<CoordinateMediation20Handler>().Should().BeEmpty();
        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        foreach (var type in TwoZeroTypes)
            registry.TryResolve(type, out _).Should().BeFalse(type);
        foreach (var type in ThreeZeroTypes)
            registry.TryResolve(type, out _).Should().BeTrue("3.0 is unaffected: " + type);

        (await DisclosedCoordinateMediationAsync(sp, secrets)).Should().BeEquivalentTo(new[] { MediatorServiceProtocols.CoordinateMediation30 });
    }

    [Fact]
    public async Task By_default_discover_features_discloses_both_versions_with_roles_mediator()
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b => b.AddCoordinateMediation(), secrets: secrets);

        (await DisclosedCoordinateMediationAsync(sp, secrets)).Should().BeEquivalentTo(
            new[] { MediatorServiceProtocols.CoordinateMediation30, MediatorServiceProtocols.CoordinateMediation20 });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_authcrypt_2_0_mediate_request_is_granted_only_while_2_0_is_enabled(bool enableV2)
    {
        await using var host = MediatorTestHost.Create(o => o.EnableV2 = enableV2);
        var mediator = await host.NewPeerAsync();
        var bob = await host.NewPeerAsync();

        var outcome = await host.SendAuthcryptAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);

        if (enableV2)
        {
            outcome.Result.Should().Be(DispatchResult.ReplyProduced);
            CoordinateMediation20.TryParseGrant(outcome.Reply!, out _).Should().BeTrue();
            (await host.Store.GetGrantAsync(bob.Did, default)).Should().NotBeNull();
        }
        else
        {
            outcome.Result.Should().Be(DispatchResult.NoHandler, "with 2.0 off nothing claims the protocol");
            outcome.Reply.Should().BeNull();
            (await host.Store.GetGrantAsync(bob.Did, default)).Should().BeNull("no grant was created");
        }
    }

    // ---- repeated calls -----------------------------------------------------------------------

    [Fact]
    public void Two_calls_that_leave_2_0_on_register_one_2_0_handler()
    {
        var services = Services(b =>
        {
            b.AddCoordinateMediation();
            b.AddCoordinateMediation(o => o.EnableV2 = true);
        });
        services.Count(d => d.ServiceType == typeof(CoordinateMediation20Handler)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        sp.GetServices<CoordinateMediation20Handler>().Should().ContainSingle();
        sp.GetServices<IProtocolHandler>().OfType<CoordinateMediation20Handler>().Should().ContainSingle();
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
    }

    [Fact]
    public void Two_calls_that_switch_2_0_off_register_none_and_the_dispatcher_builds()
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation(o => o.EnableV2 = false);
            b.AddCoordinateMediation(o => o.EnableV2 = false);
        });

        sp.GetService<CoordinateMediation20Handler>().Should().BeNull();
        sp.GetServices<IProtocolHandler>().OfType<CoordinateMediation20Handler>().Should().BeEmpty();
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
        sp.GetRequiredService<CoordinateMediationHandler>().Should().NotBeNull();
    }

    [Fact]
    public void AddMediator_with_EnableV2_false_registers_no_2_0_handler_and_the_dispatcher_builds()
    {
        using var sp = Build(b => b.AddMediator(m =>
        {
            m.CoordinateMediation.RoutingDids.Add("did:example:routing");
            m.CoordinateMediation.EnableV2 = false;
        }));

        sp.GetService<CoordinateMediation20Handler>().Should().BeNull();
        sp.GetServices<IProtocolHandler>().OfType<CoordinateMediation20Handler>().Should().BeEmpty();
        sp.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
        sp.GetRequiredService<ProtocolHandlerRegistry>().TryResolve(CoordinateMediation20.KeylistUpdateType, out _).Should().BeFalse();
    }

    // ---- settings that disagree with what was registered --------------------------------------

    public static TheoryData<string> Disagreements => new()
    {
        "registered on, then Configure sets false",
        "registered off, then Configure sets true",
        "a default call, then a call that sets false",
        "a call that sets false, then AddMediator without EnableV2",
    };

    private static ServiceProvider BuildDisagreement(string what) => what switch
    {
        "registered on, then Configure sets false" => Build(
            b => b.AddCoordinateMediation(o => o.RoutingDids.Add(Sentinel)),
            after: s => s.Configure<CoordinateMediationOptions>(o => o.EnableV2 = false)),
        "registered off, then Configure sets true" => Build(
            b => b.AddCoordinateMediation(o =>
            {
                o.RoutingDids.Add(Sentinel);
                o.EnableV2 = false;
            }),
            after: s => s.Configure<CoordinateMediationOptions>(o => o.EnableV2 = true)),
        "a default call, then a call that sets false" => Build(b =>
        {
            b.AddCoordinateMediation(o => o.RoutingDids.Add(Sentinel));
            b.AddCoordinateMediation(o => o.EnableV2 = false);
        }),
        "a call that sets false, then AddMediator without EnableV2" => Build(b =>
        {
            b.AddCoordinateMediation(o => o.EnableV2 = false);
            b.AddMediator(m => m.CoordinateMediation.RoutingDids.Add(Sentinel));
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    [Theory]
    [MemberData(nameof(Disagreements))]
    public void Settings_that_disagree_with_the_registration_fail_when_either_handler_is_first_built_naming_EnableV2_and_no_did(string what)
    {
        using var sp = BuildDisagreement(what);

        // The dispatcher builds every handler; each handler's factory checks on its own.
        var resolutions = new List<(string Name, Func<object?> Resolve)>
        {
            ("dispatcher", () => sp.GetRequiredService<ProtocolDispatcher>()),
            ("3.0 handler", () => sp.GetRequiredService<CoordinateMediationHandler>()),
        };
        if (sp.GetService<IServiceProviderIsService>()!.IsService(typeof(CoordinateMediation20Handler)))
            resolutions.Add(("2.0 handler", () => sp.GetRequiredService<CoordinateMediation20Handler>()));

        foreach (var (name, resolve) in resolutions)
        {
            var refusal = FluentActions.Invoking(resolve).Should().Throw<InvalidOperationException>($"{what}: {name}").Which;
            refusal.Message.Should().Contain("EnableV2", name).And.NotContain("SENTINEL", name).And.NotContain("did:", name);
        }
    }

    [Fact]
    public void The_registered_off_disagreement_has_no_2_0_handler_to_resolve_and_the_registered_on_one_has()
    {
        // Pins which handlers the theory above exercises: both factories are covered.
        using var on = BuildDisagreement("registered on, then Configure sets false");
        using var off = BuildDisagreement("registered off, then Configure sets true");

        on.GetRequiredService<IServiceProviderIsService>().IsService(typeof(CoordinateMediation20Handler)).Should().BeTrue();
        off.GetRequiredService<IServiceProviderIsService>().IsService(typeof(CoordinateMediation20Handler)).Should().BeFalse();
    }

    // ---- one set of collaborators for both versions -------------------------------------------

    [Fact]
    public async Task Both_handlers_are_handed_the_hosts_store_policy_and_identity()
    {
        var store = new DiV2CountingStore();
        var policy = new DiV2RecordingPolicy();
        var identity = new DiV2Identity("did:example:routing-one", "did:example:routing-two");
        await using var host = MediatorTestHost.Create(before: s =>
        {
            s.AddSingleton<IMediationStore>(store);
            s.AddSingleton<IMediationPolicy>(policy);
            s.AddSingleton<IMediatorIdentity>(identity);
        });
        var mediator = await host.NewPeerAsync();
        var bob = await host.NewPeerAsync();
        var carol = await host.NewPeerAsync();

        // 2.0: Bob enrols and registers a DID.
        var grant20 = await host.SendAuthcryptAsync(CoordinateMediation20.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);
        CoordinateMediation20.TryParseGrant(grant20.Reply!, out var routing20).Should().BeTrue();
        routing20.Should().Equal(new[] { "did:example:routing-one" }, "the 2.0 grant carries the host identity's first routing DID");
        await host.SendAuthcryptAsync(CoordinateMediation20.CreateKeylistUpdate(bob.Did, mediator.Did,
            new[] { new RecipientUpdate("did:example:bob-key", RecipientUpdateAction.Add) }), mediator.Did);

        store.Calls.Should().Contain(new[] { nameof(IMediationStore.GrantAsync), nameof(IMediationStore.UpdateRecipientsAsync) }, "the 2.0 handler writes to the host's store");
        policy.MediateRequests.Should().Equal(new[] { bob.Did }, "the 2.0 handler asks the host's policy");
        policy.Registrations.Should().Equal(new[] { (bob.Did, "did:example:bob-key") });
        var readsAfter20 = identity.Reads;
        readsAfter20.Should().BePositive("the 2.0 handler reads the host's identity");

        // 3.0: Carol enrols through the same collaborators; the identity is read again.
        var grant30 = await host.SendAuthcryptAsync(CoordinateMediation.CreateMediateRequest(carol.Did, mediator.Did), mediator.Did);
        CoordinateMediation.TryParseGrant(grant30.Reply!, out var routing30).Should().BeTrue();
        routing30.Should().Equal("did:example:routing-one", "did:example:routing-two");
        policy.MediateRequests.Should().Equal(bob.Did, carol.Did);
        identity.Reads.Should().BeGreaterThan(readsAfter20);
        store.Calls.Count(c => c == nameof(IMediationStore.GrantAsync)).Should().Be(2, "one grant through each version, both in the host's store");

        // Each version reads what the other wrote, for the same peer.
        var page = await host.SendAuthcryptAsync(CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did), mediator.Did);
        CoordinateMediation.TryParseRecipient(page.Reply!, out var dids, out _).Should().BeTrue();
        dids.Should().Equal("did:example:bob-key");
        store.Calls.Should().Contain(nameof(IMediationStore.ListRecipientsAsync));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<string>> DisclosedCoordinateMediationAsync(IServiceProvider sp, InMemorySecretsResolver secrets)
    {
        var alice = await DiPeers.NewAsync(sp, secrets);
        var mediator = await DiPeers.NewAsync(sp, secrets);
        var client = sp.GetRequiredService<DidCommClient>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;

        var query = DiscoverFeaturesApi.CreateQuery(alice.Did, mediator.Did,
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/coordinate-mediation/*" });
        var packed = await client.PackEncryptedAsync(query, new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: alice.Did));
        var outcome = await sp.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, options);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(outcome.Reply!);
        disclosures.Should().OnlyContain(d => d.Roles != null && d.Roles.SequenceEqual(new[] { CoordinateMediation.MediatorRole }), "every Coordinate Mediation version is disclosed with roles [\"mediator\"]");
        return disclosures.Select(d => d.Id).ToArray();
    }

    private sealed class DiV2CountingStore : IMediationStore
    {
        private readonly InMemoryMediationStore _inner = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
        {
            Calls.Enqueue(nameof(GetGrantAsync));
            return _inner.GetGrantAsync(peerDid, ct);
        }

        public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct)
        {
            Calls.Enqueue(nameof(GrantAsync));
            return _inner.GrantAsync(grant, ct);
        }

        public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct)
        {
            Calls.Enqueue(nameof(RevokeAsync));
            return _inner.RevokeAsync(peerDid, ct);
        }

        public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
        {
            Calls.Enqueue(nameof(UpdateRecipientsAsync));
            return _inner.UpdateRecipientsAsync(peerDid, updates, ct);
        }

        public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct)
        {
            Calls.Enqueue(nameof(ListRecipientsAsync));
            return _inner.ListRecipientsAsync(peerDid, offset, limit, ct);
        }

        public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
        {
            Calls.Enqueue(nameof(FindGrantByRecipientDidAsync));
            return _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
        }
    }

    private sealed class DiV2RecordingPolicy : IMediationPolicy
    {
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly ConcurrentQueue<(string, string)> _registrations = new();

        public IReadOnlyCollection<string> MediateRequests => _requests;

        public IReadOnlyCollection<(string, string)> Registrations => _registrations;

        public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
        {
            _requests.Enqueue(peerDid);
            return new ValueTask<MediationDecision>(MediationDecision.Grant);
        }

        public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
        {
            _registrations.Enqueue((peerDid, recipientDid));
            return new ValueTask<bool>(true);
        }
    }

    private sealed class DiV2Identity(params string[] routingDids) : IMediatorIdentity
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public IReadOnlyList<string> RoutingDids
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return routingDids;
            }
        }
    }
}
