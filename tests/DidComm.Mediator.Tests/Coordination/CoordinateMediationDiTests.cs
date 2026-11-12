using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Routing;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

// L-014: a namespace import must not shadow the same-named static class.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// <c>AddCoordinateMediation()</c> through the real container (FR-MED-01, JC-8): what it
/// registers, idempotence, host overrides, the ordering rule against <c>AddRoutingMediator()</c>,
/// fail-fast option validation, the L-017 cycle check, Discover Features disclosure, and the grant
/// store acting as the forward recipient directory (FR-ROUTE-11).
/// </summary>
public sealed class CoordinateMediationDiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ServiceProvider Build(Action<DidCommBuilder> configure, Action<IServiceCollection>? before = null, InMemorySecretsResolver? secrets = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets ?? new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            configure(b);
        });
        return services.BuildServiceProvider();
    }

    // ---- registrations --------------------------------------------------------------------

    [Fact]
    public void AddCoordinateMediation_registers_the_handler_and_its_defaults()
    {
        using var sp = Build(b => b.AddCoordinateMediation());

        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        foreach (var type in new[]
                 {
                     CoordinateMediation.MediateRequestType, CoordinateMediation.MediateGrantType, CoordinateMediation.MediateDenyType,
                     CoordinateMediation.RecipientUpdateType, CoordinateMediation.RecipientUpdateResponseType,
                     CoordinateMediation.RecipientQueryType, CoordinateMediation.RecipientType,
                 })
        {
            registry.TryResolve(type, out var resolved).Should().BeTrue(type);
            resolved.Should().BeSameAs(sp.GetRequiredService<CoordinateMediationHandler>(), "the registry entry and the typed singleton are one instance");
        }
        registry.TryResolve("https://didcomm.org/coordinate-mediation/2.0/keylist-update", out _).Should().BeFalse("2.0 is a separate handler (#99)");

        sp.GetRequiredService<CoordinateMediationHandler>().Roles.Should().Equal("mediator");
        sp.GetRequiredService<IMediationStore>().Should().BeOfType<InMemoryMediationStore>();
        sp.GetRequiredService<IMediationPolicy>().Should().BeOfType<DefaultMediationPolicy>();
        sp.GetRequiredService<IMediatorIdentity>().RoutingDids.Should().BeEmpty();
        sp.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<MediationForwardRecipientRegistry>();
        sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value.MaxPageSize.Should().Be(100);
    }

    [Fact]
    public void An_ordinary_agent_registers_none_of_it()
    {
        using var sp = Build(_ => { });

        sp.GetService<CoordinateMediationHandler>().Should().BeNull();
        sp.GetService<IMediationStore>().Should().BeNull();
        sp.GetService<IMediationPolicy>().Should().BeNull();
        sp.GetService<IMediatorIdentity>().Should().BeNull();
        sp.GetService<IForwardRecipientRegistry>().Should().BeNull();
        sp.GetRequiredService<ProtocolHandlerRegistry>().TryResolve(CoordinateMediation.MediateRequestType, out _).Should().BeFalse();
    }

    [Fact]
    public void Calling_it_twice_registers_one_of_each_and_composes_the_settings()
    {
        Action<CoordinateMediationOptions> addRouting = o => o.RoutingDids.Add("did:example:routing");
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation(o => o.MaxPageSize = 7);
            b.AddCoordinateMediation();
            b.AddCoordinateMediation(o => o.MaxGrants = 3);
            b.AddCoordinateMediation(addRouting);
            b.AddCoordinateMediation(addRouting); // the same delegate twice, as a host plus AddMediator() might
        });

        sp.GetServices<IProtocolHandler>().OfType<CoordinateMediationHandler>().Should().HaveCount(1);
        sp.GetServices<CoordinateMediationHandler>().Should().HaveCount(1);
        sp.GetServices<IMediationStore>().Should().HaveCount(1);
        sp.GetServices<IMediationPolicy>().Should().HaveCount(1);
        sp.GetServices<IMediatorIdentity>().Should().HaveCount(1);
        sp.GetServices<IForwardRecipientRegistry>().Should().HaveCount(1);

        var options = sp.GetRequiredService<IOptions<CoordinateMediationOptions>>().Value;
        options.MaxPageSize.Should().Be(7);
        options.MaxGrants.Should().Be(3);
        sp.GetRequiredService<IMediatorIdentity>().RoutingDids.Should().Equal(new[] { "did:example:routing" }, "the same DID configured twice is granted once");
    }

    [Fact]
    public void Host_registrations_made_before_win()
    {
        var store = new InMemoryMediationStore();
        var policy = new DiDenyAllPolicy();
        var identity = new DiIdentity("did:example:host-routing");
        using var sp = Build(
            b => b.AddCoordinateMediation(o => o.RoutingDids.Add("did:example:ignored")),
            before: s =>
            {
                s.AddSingleton<IMediationStore>(store);
                s.AddSingleton<IMediationPolicy>(policy);
                s.AddSingleton<IMediatorIdentity>(identity);
            });

        sp.GetRequiredService<IMediationStore>().Should().BeSameAs(store);
        sp.GetRequiredService<IMediationPolicy>().Should().BeSameAs(policy);
        sp.GetRequiredService<IMediatorIdentity>().Should().BeSameAs(identity);
        sp.GetServices<IMediationStore>().Should().ContainSingle();
        sp.GetServices<IMediationPolicy>().Should().ContainSingle();
        sp.GetServices<IMediatorIdentity>().Should().ContainSingle();
        sp.GetRequiredService<CoordinateMediationHandler>().Should().NotBeNull("the handler builds against the host's collaborators");
    }

    [Fact]
    public async Task Bounds_configured_at_registration_reach_the_default_store()
    {
        await using var host = MediatorTestHost.Create(o => o.MaxGrants = 1);
        var mediator = await host.NewPeerAsync();
        var bob = await host.NewPeerAsync();
        var carol = await host.NewPeerAsync();

        var first = await host.SendAuthcryptAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);
        var second = await host.SendAuthcryptAsync(CoordinateMediation.CreateMediateRequest(carol.Did, mediator.Did), mediator.Did);

        CoordinateMediation.TryParseGrant(first.Reply!, out _).Should().BeTrue();
        CoordinateMediation.TryParseDeny(second.Reply!).Should().BeTrue();
        ((InMemoryMediationStore)host.Store).GrantCount.Should().Be(1);
    }

    // ---- ordering against AddRoutingMediator (JC-8) ---------------------------------------

    [Fact]
    public void Before_AddRoutingMediator_the_store_is_the_forward_recipient_directory()
    {
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddRoutingMediator();
        });

        sp.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<MediationForwardRecipientRegistry>();
        sp.GetServices<IForwardRecipientRegistry>().Should().ContainSingle("AddRoutingMediator's default is a TryAdd and stays out");
        sp.GetRequiredService<ForwardHandler>().Should().NotBeNull();
    }

    [Fact]
    public void After_AddRoutingMediator_it_fails_fast_and_says_what_to_do()
    {
        var act = () => Build(b =>
        {
            b.AddRoutingMediator();
            b.AddCoordinateMediation();
        });

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().Contain("AddCoordinateMediation() before AddRoutingMediator()").And.Contain("UseAsForwardRecipientRegistry");
    }

    public static TheoryData<string> HostRegistryShapes => new() { "type", "instance", "factory" };

    [Theory]
    [MemberData(nameof(HostRegistryShapes))]
    public void A_host_directory_registered_first_fails_fast_unless_the_host_opts_out(string shape)
    {
        void RegisterHostDirectory(IServiceCollection services)
        {
            switch (shape)
            {
                case "type":
                    services.AddSingleton<IForwardRecipientRegistry, InMemoryForwardRecipientRegistry>();
                    break;
                case "instance":
                    services.AddSingleton<IForwardRecipientRegistry>(new InMemoryForwardRecipientRegistry());
                    break;
                default:
                    services.AddSingleton<IForwardRecipientRegistry>(_ => new InMemoryForwardRecipientRegistry());
                    break;
            }
        }

        var defaulted = () => Build(b => b.AddCoordinateMediation(), before: RegisterHostDirectory);
        defaulted.Should().Throw<InvalidOperationException>();

        using var sp = Build(b => b.AddCoordinateMediation(o => o.UseAsForwardRecipientRegistry = false), before: RegisterHostDirectory);
        sp.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<InMemoryForwardRecipientRegistry>("the host's directory is kept");
        sp.GetServices<IForwardRecipientRegistry>().Should().ContainSingle();
        sp.GetRequiredService<CoordinateMediationHandler>().Should().NotBeNull();
    }

    [Fact]
    public void Opting_out_registers_no_directory_and_AddRoutingMediator_supplies_its_own()
    {
        using var alone = Build(b => b.AddCoordinateMediation(o => o.UseAsForwardRecipientRegistry = false));
        alone.GetService<IForwardRecipientRegistry>().Should().BeNull();

        using var both = Build(b =>
        {
            b.AddRoutingMediator();
            b.AddCoordinateMediation(o => o.UseAsForwardRecipientRegistry = false);
        });
        both.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<InMemoryForwardRecipientRegistry>();
    }

    [Fact]
    public void A_directory_registered_after_the_call_is_caught_when_the_handler_is_first_built()
    {
        // Registration-time checks cannot see the future. The handler's factory looks again.
        using var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.Services.AddSingleton<IForwardRecipientRegistry>(new InMemoryForwardRecipientRegistry());
        });

        var act = () => sp.GetRequiredService<ProtocolDispatcher>();
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("registered after AddCoordinateMediation()");
        FluentActions.Invoking(() => sp.GetRequiredService<CoordinateMediationHandler>()).Should().Throw<InvalidOperationException>();

        // Opted out on every call: the host's later directory is simply the directory.
        using var optedOut = Build(b =>
        {
            b.AddCoordinateMediation(o => o.UseAsForwardRecipientRegistry = false);
            b.Services.AddSingleton<IForwardRecipientRegistry>(new InMemoryForwardRecipientRegistry());
        });
        optedOut.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
        optedOut.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<InMemoryForwardRecipientRegistry>();

        // A host that removed the store-backed directory on purpose is not second-guessed.
        using var removed = Build(b =>
        {
            b.AddCoordinateMediation();
            var ours = b.Services.Single(d => d.ServiceType == typeof(IForwardRecipientRegistry));
            b.Services.Remove(ours);
            b.Services.AddSingleton<IForwardRecipientRegistry>(new InMemoryForwardRecipientRegistry());
        });
        removed.GetRequiredService<ProtocolDispatcher>().Should().NotBeNull();
    }

    [Fact]
    public void A_keyed_directory_registration_is_not_mistaken_for_the_directory()
    {
        using var sp = Build(
            b => b.AddCoordinateMediation(),
            before: s => s.AddKeyedSingleton<IForwardRecipientRegistry>("tenant-a", new InMemoryForwardRecipientRegistry()));

        sp.GetRequiredService<IForwardRecipientRegistry>().Should().BeOfType<MediationForwardRecipientRegistry>();
    }

    // ---- option validation at registration ------------------------------------------------

    public static TheoryData<string, Action<CoordinateMediationOptions>> BadSettings => new()
    {
        { "MaxPageSize 0", o => o.MaxPageSize = 0 },
        { "MaxPageSize negative", o => o.MaxPageSize = -1 },
        { "MaxUpdatesPerMessage 0", o => o.MaxUpdatesPerMessage = 0 },
        { "MaxGrants 0", o => o.MaxGrants = 0 },
        { "MaxRecipientsPerGrant 0", o => o.MaxRecipientsPerGrant = 0 },
        { "MaxRecipients 0", o => o.MaxRecipients = 0 },
        { "routing DID empty", o => o.RoutingDids.Add("") },
        { "routing DID null", o => o.RoutingDids.Add(null!) },
        { "routing DID URL", o => o.RoutingDids.Add("did:example:SENTINEL#key-1") },
        { "routing DID with a service query", o => o.RoutingDids.Add("did:example:SENTINEL?service=didcomm") },
        { "routing DID not a DID", o => o.RoutingDids.Add("https://SENTINEL.example/didcomm") },
        { "routing DID over 2048", o => o.RoutingDids.Add("did:example:SENTINEL" + new string('a', 2049)) },
    };

    [Theory]
    [MemberData(nameof(BadSettings))]
    public void Bad_settings_fail_at_registration_without_quoting_a_did(string what, Action<CoordinateMediationOptions> configure)
    {
        var act = () => Build(b => b.AddCoordinateMediation(configure));

        var ex = act.Should().Throw<ArgumentException>(what).Which;
        ex.Message.Should().NotContain("SENTINEL");

        // The same check guards the options object itself and the store built from it.
        var options = new CoordinateMediationOptions();
        configure(options);
        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Settings_that_only_become_invalid_when_composed_fail_when_the_handler_is_built()
    {
        // Each delegate passes on its own scratch instance; a later PostConfigure breaks the result.
        using var sp = Build(
            b => b.AddCoordinateMediation(o => o.MaxPageSize = 5),
            before: s => s.PostConfigure<CoordinateMediationOptions>(o => o.MaxPageSize = 0));

        var act = () => sp.GetRequiredService<CoordinateMediationHandler>();
        act.Should().Throw<ArgumentException>();
    }

    // ---- L-017: dependency cycles ---------------------------------------------------------

    [Fact]
    public async Task The_provider_builds_resolves_and_disposes_without_hanging()
    {
        var sp = Build(b =>
        {
            b.AddCoordinateMediation();
            b.AddRoutingMediator();
        });

        var (dispatcher, handler, forward) = await Task.Run(() =>
            (sp.GetRequiredService<ProtocolDispatcher>(), sp.GetRequiredService<CoordinateMediationHandler>(), sp.GetRequiredService<ForwardHandler>()))
            .WaitAsync(Timeout);
        dispatcher.Should().NotBeNull();
        handler.Should().NotBeNull();
        forward.Should().NotBeNull();

        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_store_that_takes_the_dispatcher_in_its_constructor_fails_fast_instead_of_hanging()
    {
        await using var sp = Build(b => b.AddCoordinateMediation(), before: s => s.AddSingleton<IMediationStore, DiDispatcherTakingStore>());

        var act = () => Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Timeout);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("circular dependency");
    }

    [Fact]
    public async Task A_store_that_resolves_the_dispatcher_lazily_is_fine()
    {
        await using var sp = Build(b => b.AddCoordinateMediation(), before: s => s.AddSingleton<IMediationStore, DiLazyStore>());

        var dispatcher = await Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Timeout);

        ((DiLazyStore)sp.GetRequiredService<IMediationStore>()).Dispatcher.Should().BeSameAs(dispatcher);
    }

    // ---- Discover Features (FR-MED-01) ----------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discover_features_discloses_coordinate_mediation_3_0_with_roles_mediator_only_for_a_mediator(bool mediatorRole)
    {
        var secrets = new InMemorySecretsResolver();
        await using var sp = Build(b =>
        {
            if (mediatorRole)
                b.AddCoordinateMediation();
        }, secrets: secrets);
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
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default));
        var entries = wire.RootElement.GetProperty("body").GetProperty("disclosures").EnumerateArray().ToList();

        if (mediatorRole)
        {
            var disclosed = disclosures.Should().ContainSingle().Which;
            disclosed.Id.Should().Be("https://didcomm.org/coordinate-mediation/3.0");
            disclosed.Roles.Should().Equal("mediator");
            var entry = entries.Should().ContainSingle().Which;
            entry.GetProperty("feature-type").GetString().Should().Be("protocol");
            entry.GetProperty("id").GetString().Should().Be("https://didcomm.org/coordinate-mediation/3.0");
            entry.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("mediator");
        }
        else
        {
            disclosures.Should().BeEmpty("an ordinary agent does not advertise the mediator role");
            entries.Should().BeEmpty();
        }
    }

    // ---- the grant store as the forward recipient directory (FR-ROUTE-11) ------------------

    [Fact]
    public async Task A_forward_is_relayed_exactly_when_its_next_is_a_registered_recipient_did()
    {
        var sink = new DiCapturingSink();
        await using var host = MediatorTestHost.Create(before: s => s.AddSingleton<IForwardSink>(sink), after: b => b.AddRoutingMediator());
        var forwardHandler = host.Services.GetRequiredService<ForwardHandler>();
        var mediator = await host.NewPeerAsync();
        var alice = await host.NewPeerAsync();
        var bob = await host.NewPeerAsync();
        var bobRecipient = await host.NewPeerAsync();
        var unregistered = await host.NewPeerAsync();

        async Task<int> ForwardAsync(string next, string innerRecipient)
        {
            var before = sink.Results.Count;
            var inner = new MessageBuilder().WithType("https://example.com/protocols/note/1.0/note").WithFrom(alice.Did).WithTo(innerRecipient).Build();
            var innerPacked = (await host.Client.PackEncryptedAsync(inner, new PackEncryptedOptions(Recipients: new[] { innerRecipient }, From: alice.Did))).Message;
            var forward = ForwardMessage.Create(mediator.Did, next, new[] { innerPacked });
            var outcome = await host.SendAnoncryptAsync(forward, mediator.Did);
            outcome.Result.Should().Be(DispatchResult.NoReply, "a mediator sends nothing back toward the sender");
            await forwardHandler.FlushAsync(Timeout);
            return sink.Results.Count - before;
        }

        // Nothing enrolled yet: dropped.
        (await ForwardAsync(bobRecipient.Did, bobRecipient.Did)).Should().Be(0);

        // Bob enrols and registers a DID through real messages.
        await host.SendAuthcryptAsync(CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did), mediator.Did);
        await host.SendAuthcryptAsync(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(bobRecipient.Did, RecipientUpdateAction.Add) }), mediator.Did);

        (await ForwardAsync(bobRecipient.Did, bobRecipient.Did)).Should().Be(1);
        sink.Results.Last().NextHop.Should().Be(bobRecipient.Did);

        // `next` naming one of the recipient's keys: the handler reduces it to the DID subject.
        (await ForwardAsync(bobRecipient.Did + "#key-1", bobRecipient.Did)).Should().Be(1);

        // Enrolled is not registered: Bob's own DID was never added; nor was a stranger's.
        (await ForwardAsync(bob.Did, bob.Did)).Should().Be(0);
        (await ForwardAsync(unregistered.Did, unregistered.Did)).Should().Be(0);

        // Removed → dropped again.
        await host.SendAuthcryptAsync(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(bobRecipient.Did, RecipientUpdateAction.Remove) }), mediator.Did);
        (await ForwardAsync(bobRecipient.Did, bobRecipient.Did)).Should().Be(0);

        // Re-added, then the grant is revoked by the host → dropped.
        await host.SendAuthcryptAsync(CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { new RecipientUpdate(bobRecipient.Did, RecipientUpdateAction.Add) }), mediator.Did);
        (await ForwardAsync(bobRecipient.Did, bobRecipient.Did)).Should().Be(1);
        (await host.Store.RevokeAsync(bob.Did, default)).Should().BeTrue();
        (await ForwardAsync(bobRecipient.Did, bobRecipient.Did)).Should().Be(0);
    }

    [Fact]
    public async Task The_directory_adapter_makes_one_store_lookup_per_question_sync_or_async()
    {
        var store = new DiLookupStore();
        var registry = new MediationForwardRecipientRegistry(store);

        store.Answer = _ => new ValueTask<MediationGrant?>(new MediationGrant("did:example:bob", new[] { "did:example:m" }));
        (await registry.IsRegisteredAsync("did:example:r", default)).Should().BeTrue();
        store.Answer = _ => new ValueTask<MediationGrant?>((MediationGrant?)null);
        (await registry.IsRegisteredAsync("did:example:r", default)).Should().BeFalse();

        // A store that answers asynchronously (a database).
        store.Answer = async _ =>
        {
            await Task.Yield();
            return new MediationGrant("did:example:bob", new[] { "did:example:m" });
        };
        (await registry.IsRegisteredAsync("did:example:r", default)).Should().BeTrue();
        store.Answer = async _ =>
        {
            await Task.Yield();
            return null;
        };
        (await registry.IsRegisteredAsync("did:example:r", default)).Should().BeFalse();

        store.Lookups.Should().Equal("did:example:r", "did:example:r", "did:example:r", "did:example:r");

        // A faulting store surfaces to the forward handler, which drops the forward.
        store.Answer = _ => throw new InvalidOperationException("down");
        await FluentActions.Awaiting(async () => await registry.IsRegisteredAsync("did:example:r", default)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(async () => await registry.IsRegisteredAsync(null!, default)).Should().ThrowAsync<ArgumentNullException>();
        FluentActions.Invoking(() => new MediationForwardRecipientRegistry(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddCoordinateMediation_refuses_a_null_builder()
    {
        var act = () => CoordinateMediationBuilderExtensions.AddCoordinateMediation(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- helpers --------------------------------------------------------------------------

    private sealed class DiDenyAllPolicy : IMediationPolicy
    {
        public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct) => new(MediationDecision.Deny);

        public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct) => new(false);
    }

    private sealed class DiIdentity(params string[] routingDids) : IMediatorIdentity
    {
        public IReadOnlyList<string> RoutingDids { get; } = routingDids;
    }

    private sealed class DiCapturingSink : IForwardSink
    {
        private readonly ConcurrentQueue<ForwardProcessingResult> _results = new();

        public IReadOnlyCollection<ForwardProcessingResult> Results => _results;

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            _results.Enqueue(result);
            return Task.CompletedTask;
        }
    }

    private class DiDelegatingStore : IMediationStore
    {
        private readonly InMemoryMediationStore _inner = new();

        public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct) => _inner.GetGrantAsync(peerDid, ct);

        public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => _inner.GrantAsync(grant, ct);

        public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => _inner.RevokeAsync(peerDid, ct);

        public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
            => _inner.UpdateRecipientsAsync(peerDid, updates, ct);

        public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);

        public virtual ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct) => _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }

    /// <summary>The L-017 mistake: a collaborator of a handler that needs the dispatcher at construction.</summary>
    private sealed class DiDispatcherTakingStore(ProtocolDispatcher dispatcher) : DiDelegatingStore
    {
        public ProtocolDispatcher Dispatcher { get; } = dispatcher;
    }

    /// <summary>The documented way: resolve it when first needed.</summary>
    private sealed class DiLazyStore(IServiceProvider services) : DiDelegatingStore
    {
        public ProtocolDispatcher Dispatcher => services.GetRequiredService<ProtocolDispatcher>();
    }

    private sealed class DiLookupStore : DiDelegatingStore
    {
        public Func<string, ValueTask<MediationGrant?>> Answer { get; set; } = _ => new ValueTask<MediationGrant?>((MediationGrant?)null);

        public List<string> Lookups { get; } = new();

        public override ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
        {
            Lookups.Add(recipientDid);
            return Answer(recipientDid);
        }
    }
}

/// <summary>Mints a did:peer identity in a container that was not built by <see cref="MediatorTestHost"/>.</summary>
internal static class DiPeers
{
    public static async Task<DidComm.Samples.Shared.PeerIdentity> NewAsync(IServiceProvider sp, InMemorySecretsResolver secrets)
    {
        var peer = await DidComm.Samples.Shared.PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<NetDid.Core.IDidManager>(),
            sp.GetRequiredService<NetCrypto.IKeyGenerator>(),
            sp.GetRequiredService<NetCrypto.ICryptoProvider>());
        foreach (var jwk in peer.Privates)
            secrets.Add(jwk);
        return peer;
    }
}
