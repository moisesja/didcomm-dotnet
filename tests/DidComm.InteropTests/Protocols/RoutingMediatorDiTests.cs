using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// #90 test matrix I1..I5 — <c>AddRoutingMediator()</c> through the real DI container and the
/// real <see cref="ProtocolDispatcher"/> (no HTTP): registrations and their defaults, idempotence,
/// host overrides, the L-017 build/resolve/dispose hang check, Discover Features disclosure with
/// <c>roles: ["mediator"]</c>, and an end-to-end anoncrypt forward from Alice to the sink with
/// did:peer identities.
/// </summary>
public sealed class RoutingMediatorDiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void AddRoutingMediator_registers_ForwardHandler_processor_and_defaults()
    {
        using var sp = BuildProvider(mediator: true);

        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        registry.TryResolve(ForwardConstants.ForwardTypeUri, out var handler).Should().BeTrue("the forward MTURI dispatches to the mediator handler");
        handler.Should().BeOfType<ForwardHandler>();
        handler.Should().BeSameAs(sp.GetRequiredService<ForwardHandler>(), "the registry entry and the typed singleton are one instance");
        ((ForwardHandler)handler!).ProtocolUri.Should().Be(ForwardConstants.ProtocolIdentifier);
        ((ForwardHandler)handler).Roles.Should().Equal("mediator");

        sp.GetRequiredService<IForwardSink>().Should().BeOfType<NullForwardSink>("the default sink drops");
        sp.GetRequiredService<IForwardRecipientRegistry>()
            .Should().BeSameAs(sp.GetRequiredService<InMemoryForwardRecipientRegistry>(), "the interface and the concrete registry are one singleton");
        sp.GetRequiredService<InMemoryForwardRecipientRegistry>().Count.Should().Be(0, "the registry starts empty — nothing is relayed until the host registers recipients");
        sp.GetRequiredService<IRecipientRoutingKeyProvider>().Should().BeOfType<StaticRecipientRoutingKeyProvider>();
        sp.GetRequiredService<ForwardProcessor>().Should().NotBeNull();
        sp.GetRequiredService<ForwardProcessorOptions>().Mode.Should().Be(RewrapMode.PassThrough);
    }

    [Fact]
    public void AddRoutingMediator_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddRoutingMediator();
            b.AddRoutingMediator(); // intentional double-register
        });
        using var sp = services.BuildServiceProvider();

        sp.GetServices<IProtocolHandler>().OfType<ForwardHandler>().Should().HaveCount(1);
        sp.GetServices<ForwardHandler>().Should().HaveCount(1);
        sp.GetServices<IForwardSink>().Should().HaveCount(1);
        sp.GetServices<IForwardRecipientRegistry>().Should().HaveCount(1);
        sp.GetServices<ForwardProcessor>().Should().HaveCount(1);
        sp.GetServices<ForwardProcessorOptions>().Should().HaveCount(1);
    }

    [Fact]
    public void A_host_registered_sink_wins_over_the_NullForwardSink_default()
    {
        var hostSink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddSingleton<IForwardSink>(hostSink); // BEFORE AddDidComm / AddRoutingMediator
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddRoutingMediator();
        });
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IForwardSink>().Should().BeSameAs(hostSink);
        sp.GetServices<IForwardSink>().Should().ContainSingle().Which.Should().BeSameAs(hostSink, "TryAdd must not append the default beside the host's sink");
        sp.GetRequiredService<ForwardHandler>().Should().NotBeNull("the handler builds against the host's sink");
    }

    [Fact]
    public void AddBuiltInProtocols_alone_registers_no_routing_handler()
    {
        using var sp = BuildProvider(mediator: false);

        sp.GetRequiredService<ProtocolHandlerRegistry>().TryResolve(ForwardConstants.ForwardTypeUri, out _)
            .Should().BeFalse("an ordinary agent is not a mediator");
        sp.GetServices<IProtocolHandler>().OfType<ForwardHandler>().Should().BeEmpty();
        sp.GetService<ForwardHandler>().Should().BeNull();
        sp.GetService<IForwardSink>().Should().BeNull();
        sp.GetService<IForwardRecipientRegistry>().Should().BeNull();
        sp.GetService<ForwardProcessor>().Should().BeNull();
    }

    [Fact]
    public async Task Provider_builds_resolves_the_dispatcher_and_disposes_without_hang()
    {
        // L-017: the registry factory walks IEnumerable<IProtocolHandler>, which now constructs
        // ForwardHandler → ForwardProcessor → DidCommClient; a cycle back into the registry would
        // hang on first resolution, so the resolution itself is under the same 10 s budget.
        var sp = BuildProvider(mediator: true);
        var (dispatcher, handler) = await Task.Run(() =>
            (sp.GetRequiredService<ProtocolDispatcher>(), sp.GetRequiredService<ForwardHandler>()))
            .WaitAsync(Timeout);
        dispatcher.Should().NotBeNull();
        handler.Should().NotBeNull();

        await sp.DisposeAsync().AsTask().WaitAsync(Timeout);

        await handler.ShutdownCompletion.WaitAsync(Timeout);
        handler.ShutdownCompletion.IsCompletedSuccessfully.Should().BeTrue("the container disposes the singleton handler and its pump stops within the grace");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discover_features_discloses_routing_2_0_with_roles_mediator_only_with_AddRoutingMediator(bool mediator)
    {
        var secrets = new InMemorySecretsResolver();
        using var sp = BuildProvider(mediator, secrets);
        var (alice, bob) = await TwoPeersAsync(sp, secrets);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;

        var query = DiscoverFeaturesApi.CreateQuery(alice.Did, bob.Did,
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/routing/*" },
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/trust-ping/*" });
        var packed = await client.PackEncryptedAsync(query, new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did));
        var unpacked = await client.UnpackAsync(packed.Message);

        var outcome = await dispatcher.DispatchAsync(unpacked, client, options);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.Type.Should().Be(DiscoverFeaturesApi.DiscloseType);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(outcome.Reply);

        var routing = disclosures.Where(d => d.Id == ForwardConstants.ProtocolIdentifier).ToList();
        if (mediator)
        {
            routing.Should().ContainSingle("exactly one routing/2.0 entry").Which.Roles.Should().Equal("mediator");
        }
        else
        {
            routing.Should().BeEmpty("without AddRoutingMediator the agent does not advertise the mediator role");
            disclosures.Should().NotContain(d => d.Id.StartsWith("https://didcomm.org/routing/", StringComparison.Ordinal));
        }

        var trustPing = disclosures.Should().ContainSingle(d => d.Id == TrustPingApi.ProtocolUri).Which;
        trustPing.Roles.Should().BeNull("handlers without IProtocolHandlerRoles disclose no roles");

        // On the wire: "roles" is omitted for role-less handlers and carries ["mediator"] for routing.
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default));
        var entries = wire.RootElement.GetProperty("body").GetProperty("disclosures").EnumerateArray().ToList();
        var trustPingWire = entries.Single(e => e.GetProperty("id").GetString() == TrustPingApi.ProtocolUri);
        trustPingWire.TryGetProperty("roles", out _).Should().BeFalse("a null Roles must not serialize as \"roles\":null");
        if (mediator)
        {
            var routingWire = entries.Single(e => e.GetProperty("id").GetString() == ForwardConstants.ProtocolIdentifier);
            routingWire.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("mediator");
            routingWire.GetProperty("feature-type").GetString().Should().Be(DiscoverFeaturesApi.FeatureTypeProtocol);
        }
        else
        {
            entries.Should().NotContain(e => e.GetProperty("id").GetString() == ForwardConstants.ProtocolIdentifier);
        }
    }

    [Fact]
    public async Task Anoncrypt_forward_for_a_registered_recipient_reaches_the_sink_with_the_inner_payload()
    {
        var sink = new CapturingSink();
        var secrets = new InMemorySecretsResolver();
        using var sp = BuildProvider(mediator: true, secrets, sink);
        var (alice, bob) = await TwoPeersAsync(sp, secrets);
        var mediator = await NewPeerAsync(sp, secrets);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var handler = sp.GetRequiredService<ForwardHandler>();
        sp.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(bob.Did);

        // Alice: authcrypt the real message for Bob, then anoncrypt a forward for the mediator.
        var proposal = NewProposal(alice.Did, bob.Did);
        var inner = (await client.PackEncryptedAsync(proposal, new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var forward = ForwardMessage.Create(mediator.Did, bob.Did, new[] { inner });
        var packedForward = await client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediator.Did }));

        // Mediator: unpack + dispatch through the registry.
        var unpacked = await client.UnpackAsync(packedForward.Message);
        unpacked.Encrypted.Should().BeTrue();
        unpacked.RecipientKid.Should().StartWith(mediator.Did + "#");
        var outcome = await dispatcher.DispatchAsync(unpacked, client, options);

        outcome.Result.Should().Be(DispatchResult.NoReply, "a mediator sends nothing back toward the sender (FR-ROUTE-07)");
        outcome.Reply.Should().BeNull();
        outcome.Handler.Should().BeSameAs(handler);

        await handler.FlushAsync(Timeout);
        var result = sink.Results.Should().ContainSingle().Which;
        result.NextHop.Should().Be(bob.Did);
        result.ExpiresTime.Should().BeNull();
        result.Delay.Should().BeNull();
        result.NotBefore.Should().BeNull();
        // Pass-through relays the inner JWE as-is: the attachment's data.json is re-emitted from its
        // JsonNode, which keeps every member and value (key order is not significant in a JWE).
        JsonNode.DeepEquals(JsonNode.Parse(Encoding.UTF8.GetString(result.OnwardPacked)), JsonNode.Parse(inner))
            .Should().BeTrue("pass-through mode relays the inner payload unchanged");
        sink.Contexts.Single().Received.Should().BeSameAs(unpacked, "the sink sees the mediator's own unpack");

        // Bob: the relayed bytes are Alice's authenticated message.
        var bobUnpack = await client.UnpackAsync(Encoding.UTF8.GetString(result.OnwardPacked));
        bobUnpack.Message.From.Should().Be(alice.Did);
        bobUnpack.Message.Type.Should().Be(proposal.Type);
        bobUnpack.Authenticated.Should().BeTrue();
    }

    [Fact]
    public async Task Expired_forward_is_dropped_by_the_handler_at_relay_time_even_when_unpack_accepted_it()
    {
        // FR-ROUTE-09 at the handler, independent of the unpack-time FR-API-05 check: the mediator's
        // unpack clock is pinned before the expiry so UnpackAsync accepts the forward; the dispatch
        // then runs on a clock past the expiry and the handler must drop before the sink.
        var t0 = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var sink = new CapturingSink();
        var secrets = new InMemorySecretsResolver();
        using var sp = BuildProvider(mediator: true, secrets, sink, configure: o => o.Clock = () => t0);
        var (alice, bob) = await TwoPeersAsync(sp, secrets);
        var mediator = await NewPeerAsync(sp, secrets);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var handler = sp.GetRequiredService<ForwardHandler>();
        sp.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(bob.Did);

        var inner = (await client.PackEncryptedAsync(NewProposal(alice.Did, bob.Did), new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var forward = ForwardMessage.Create(mediator.Did, bob.Did, new[] { inner }, expiresTimeEpochSeconds: t0.AddSeconds(60).ToUnixTimeSeconds());
        var packedForward = await client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediator.Did }));
        var unpacked = await client.UnpackAsync(packedForward.Message); // clock = t0 < expiry: accepted

        var late = new DidCommOptions { Clock = () => t0.AddSeconds(120) };
        var dropped = await dispatcher.DispatchAsync(unpacked, client, late);
        dropped.Result.Should().Be(DispatchResult.NoReply, "the drop is silent — no problem report (FR-ROUTE-11)");
        await handler.FlushAsync(Timeout);
        sink.Results.Should().BeEmpty("an expired forward never reaches the sink (FR-ROUTE-09)");

        // Control: the same forward relays on a clock within the skew.
        var withinSkew = new DidCommOptions { Clock = () => t0.AddSeconds(61), ExpiresClockSkew = TimeSpan.FromSeconds(5) };
        (await dispatcher.DispatchAsync(unpacked, client, withinSkew)).Result.Should().Be(DispatchResult.NoReply);
        await handler.FlushAsync(Timeout);
        sink.Results.Should().ContainSingle().Which.ExpiresTime.Should().Be(forward.ExpiresTime);
    }

    [Fact]
    public async Task Forward_addressed_to_someone_else_but_decrypted_with_a_held_key_still_relays()
    {
        // The FR-ROUTE-11 posture is kid-based: what matters is that the envelope was decrypted
        // with a key this mediator holds, not what the plaintext `to` says. (Over the wire the
        // unpack-time FR-CONSIST-02 check already refuses a `to` that omits the decrypting DID,
        // so this reaches the handler through a synthetic UnpackResult — see
        // MediatorEndpointRelayTests for the no-`to` variant on real HTTP.)
        var sink = new CapturingSink();
        var secrets = new InMemorySecretsResolver();
        using var sp = BuildProvider(mediator: true, secrets, sink);
        var (alice, bob) = await TwoPeersAsync(sp, secrets);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var handler = sp.GetRequiredService<ForwardHandler>();
        sp.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(bob.Did);

        var inner = (await client.PackEncryptedAsync(NewProposal(alice.Did, bob.Did), new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var forward = ForwardMessage.Create("did:example:someone-else", bob.Did, new[] { inner });
        forward.To.Should().Equal("did:example:someone-else");
        const string mediatorKid = "did:example:mediator#key-x25519-1";
        var decryptedByMediator = new UnpackResult(
            Message: forward,
            Stack: new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
            Encrypted: true,
            Authenticated: false,
            NonRepudiation: false,
            AnonymousSender: true,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: null,
            SignerKid: null,
            SenderKid: null,
            RecipientKid: mediatorKid,
            AllRecipientKids: new[] { mediatorKid },
            FromPrior: null);

        var outcome = await dispatcher.DispatchAsync(decryptedByMediator, client, options);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        await handler.FlushAsync(Timeout);
        var result = sink.Results.Should().ContainSingle().Which;
        result.NextHop.Should().Be(bob.Did);
        JsonNode.DeepEquals(JsonNode.Parse(Encoding.UTF8.GetString(result.OnwardPacked)), JsonNode.Parse(inner)).Should().BeTrue();
    }

    [Fact]
    public async Task Plaintext_forward_through_the_dispatcher_is_dropped_before_the_sink()
    {
        var sink = new CapturingSink();
        var secrets = new InMemorySecretsResolver();
        using var sp = BuildProvider(mediator: true, secrets, sink);
        var (alice, bob) = await TwoPeersAsync(sp, secrets);
        var mediator = await NewPeerAsync(sp, secrets);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var handler = sp.GetRequiredService<ForwardHandler>();
        sp.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(bob.Did);

        var inner = (await client.PackEncryptedAsync(NewProposal(alice.Did, bob.Did), new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var plain = await client.PackPlaintextAsync(ForwardMessage.Create(mediator.Did, bob.Did, new[] { inner }));
        var unpacked = await client.UnpackAsync(plain);
        unpacked.Encrypted.Should().BeFalse();

        var outcome = await dispatcher.DispatchAsync(unpacked, client, options);

        outcome.Result.Should().Be(DispatchResult.NoReply, "the handler is found and drops silently");
        outcome.Handler.Should().BeSameAs(handler);
        await handler.FlushAsync(Timeout);
        sink.Results.Should().BeEmpty("a plaintext forward is never relayed (FR-ROUTE-11)");
    }

    // ---- L-017 cycle guard (red-team probe 6) ---------------------------------------------

    [Fact]
    public async Task A_sink_that_resolves_the_dispatcher_from_IServiceProvider_at_call_time_relays_end_to_end()
    {
        // The documented DI rule: a sink that needs the dispatcher injects IServiceProvider and
        // resolves it inside AcceptAsync — after the registry has been realized — so no cycle.
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddSingleton<IForwardSink, LazyDispatcherSink>(); // BEFORE AddDidComm / AddRoutingMediator
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
            b.AddRoutingMediator();
        });
        using var sp = services.BuildServiceProvider();
        var (alice, bob) = await TwoPeersAsync(sp, secrets);
        var mediator = await NewPeerAsync(sp, secrets);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = await Task.Run(() => sp.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Timeout);
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var handler = sp.GetRequiredService<ForwardHandler>();
        var sink = (LazyDispatcherSink)sp.GetRequiredService<IForwardSink>();
        sp.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(bob.Did);

        var inner = (await client.PackEncryptedAsync(NewProposal(alice.Did, bob.Did), new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var packed = (await client.PackEncryptedAsync(ForwardMessage.Create(mediator.Did, bob.Did, new[] { inner }), new PackEncryptedOptions(Recipients: new[] { mediator.Did }))).Message;
        var outcome = await dispatcher.DispatchAsync(await client.UnpackAsync(packed), client, options);

        outcome.Result.Should().Be(DispatchResult.NoReply);
        await handler.FlushAsync(Timeout);
        sink.Resolved.Should().ContainSingle().Which.Should().BeSameAs(dispatcher, "the sink saw the one dispatcher singleton");
    }

    [Theory]
    [InlineData(typeof(RegistryTakingSink), nameof(ProtocolHandlerRegistry))]
    [InlineData(typeof(DispatcherTakingSink), nameof(ProtocolDispatcher))]
    [InlineData(typeof(HandlersTakingSink), "IEnumerable`1")]
    [InlineData(typeof(ForwardHandlerTakingSink), nameof(ForwardHandler))]
    public void AddRoutingMediator_refuses_a_sink_whose_constructor_closes_the_registry_cycle(Type sinkType, string parameterTypeName)
    {
        // Without the guard this registration builds fine and the first resolution of the
        // dispatcher parks the resolving thread forever (observed on net10.0: the sink's
        // constructor is never reached; the container blocks inside its own singleton realization).
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IForwardSink), sinkType);

        var act = () => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddRoutingMediator();
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(sinkType.FullName!).And.Contain(parameterTypeName).And.Contain("IServiceProvider");
    }

    [Fact]
    public void AddRoutingMediator_refuses_a_recipient_registry_or_routing_key_provider_that_takes_the_dispatcher()
    {
        foreach (var (service, implementation) in new[]
        {
            (typeof(IForwardRecipientRegistry), typeof(DispatcherTakingRecipientRegistry)),
            (typeof(IRecipientRoutingKeyProvider), typeof(DispatcherTakingRoutingKeyProvider)),
        })
        {
            var services = new ServiceCollection();
            services.AddSingleton(service, implementation);

            var act = () => services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(new InMemorySecretsResolver());
                b.AddBuiltInProtocols();
                b.AddRoutingMediator();
            });

            act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(implementation.FullName!).And.Contain(service.Name);
        }
    }

    [Fact]
    public void AddRoutingMediator_accepts_a_factory_registered_sink_it_cannot_inspect_and_an_instance()
    {
        // The guard covers implementation types only; a factory or an instance registers as before.
        var services = new ServiceCollection();
        services.AddSingleton<IForwardSink>(_ => new CapturingSink());
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddRoutingMediator();
        });
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IForwardSink>().Should().BeOfType<CapturingSink>();
    }

    // ---- helpers -------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(
        bool mediator,
        InMemorySecretsResolver? secrets = null,
        IForwardSink? hostSink = null,
        Action<DidCommOptions>? configure = null)
    {
        var services = new ServiceCollection();
        if (hostSink is not null)
            services.AddSingleton(hostSink);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets ?? new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            if (mediator)
                b.AddRoutingMediator();
            if (configure is not null)
                b.Configure(configure);
        });
        return services.BuildServiceProvider();
    }

    private static async Task<(PeerIdentity Alice, PeerIdentity Bob)> TwoPeersAsync(IServiceProvider sp, InMemorySecretsResolver secrets)
        => (await NewPeerAsync(sp, secrets), await NewPeerAsync(sp, secrets));

    private static async Task<PeerIdentity> NewPeerAsync(IServiceProvider sp, InMemorySecretsResolver secrets)
    {
        var peer = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(),
            sp.GetRequiredService<IKeyGenerator>(),
            sp.GetRequiredService<ICryptoProvider>());
        foreach (var jwk in peer.Privates) secrets.Add(jwk);
        return peer;
    }

    private static Message NewProposal(string from, string to) => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom(from)
        .WithTo(to)
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();

    private sealed class CapturingSink : IForwardSink
    {
        private readonly ConcurrentQueue<ForwardProcessingResult> _results = new();
        private readonly ConcurrentQueue<ProtocolContext> _contexts = new();

        public IReadOnlyCollection<ForwardProcessingResult> Results => _results;
        public IReadOnlyCollection<ProtocolContext> Contexts => _contexts;

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            _results.Enqueue(result);
            _contexts.Enqueue(context);
            return Task.CompletedTask;
        }
    }

    private sealed class LazyDispatcherSink : IForwardSink
    {
        private readonly IServiceProvider _services;
        public LazyDispatcherSink(IServiceProvider services) => _services = services;
        public ConcurrentQueue<ProtocolDispatcher> Resolved { get; } = new();

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            Resolved.Enqueue(_services.GetRequiredService<ProtocolDispatcher>());
            return Task.CompletedTask;
        }
    }

    private sealed class RegistryTakingSink : IForwardSink
    {
        public RegistryTakingSink(ProtocolHandlerRegistry registry) { }
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DispatcherTakingSink : IForwardSink
    {
        public DispatcherTakingSink(ProtocolDispatcher dispatcher) { }
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class HandlersTakingSink : IForwardSink
    {
        public HandlersTakingSink(IEnumerable<IProtocolHandler> handlers) { }
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ForwardHandlerTakingSink : IForwardSink
    {
        public ForwardHandlerTakingSink(ForwardHandler handler) { }
        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DispatcherTakingRecipientRegistry : IForwardRecipientRegistry
    {
        public DispatcherTakingRecipientRegistry(ProtocolDispatcher dispatcher) { }
        public ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct) => ValueTask.FromResult(false);
    }

    private sealed class DispatcherTakingRoutingKeyProvider : IRecipientRoutingKeyProvider
    {
        public DispatcherTakingRoutingKeyProvider(ProtocolDispatcher dispatcher) { }
        public ValueTask<IReadOnlyList<Jwk>> GetAsync(string recipientDid, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());
    }
}
