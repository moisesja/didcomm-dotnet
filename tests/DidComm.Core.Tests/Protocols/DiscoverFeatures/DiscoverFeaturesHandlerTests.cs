using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.Empty;
using DidComm.Protocols.TrustPing;
using DidComm.Threading;
using FluentAssertions;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.Tests.Protocols.DiscoverFeatures;

public sealed class DiscoverFeaturesHandlerTests
{
    private static ProtocolContext Ctx(Message m, DidCommOptions? options = null, string? recipientKid = null)
    {
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            recipientKid is not null, false, false, false, null, null, null, null, null, recipientKid,
            Array.Empty<string>(), null);
        return new ProtocolContext(unpacked, new DidComm.Threading.ThreadState(m.Thid ?? m.Id), Client: null, options ?? new DidCommOptions(), new InMemoryThreadStateStore());
    }

    private static (ProtocolHandlerRegistry registry, DiscoverFeaturesHandler handler) BuildHandler(params IFeatureProvider[] providers)
    {
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new TrustPingHandler());
        registry.Register(new EmptyHandler());
        var resolved = providers.Length > 0 ? providers : new IFeatureProvider[]
        {
            new ProtocolFeatureProvider(new SingletonRegistryProvider(registry)),
            new MaxReceiveBytesConstraintProvider(),
        };
        return (registry, new DiscoverFeaturesHandler(resolved));
    }

    /// <summary>Tiny <see cref="IServiceProvider"/> that hands out a pre-built <see cref="ProtocolHandlerRegistry"/>.</summary>
    private sealed class SingletonRegistryProvider : IServiceProvider
    {
        private readonly ProtocolHandlerRegistry _registry;
        public SingletonRegistryProvider(ProtocolHandlerRegistry registry) => _registry = registry;
        public object? GetService(Type serviceType)
            => serviceType == typeof(ProtocolHandlerRegistry) ? _registry : null;
    }

    [Fact]
    public async Task Star_wildcard_returns_every_registered_protocol()
    {
        var (registry, handler) = BuildHandler();
        registry.Register(new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>()));
        var query = DiscoverFeaturesApi.CreateQuery("did:peer:alice", "did:peer:bob",
            new FeatureQuery { FeatureType = "protocol", Match = "*" });

        var reply = await handler.HandleAsync(query, Ctx(query), CancellationToken.None);
        reply.Should().NotBeNull();
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(reply!);
        disclosures.Select(d => d.Id).Should().Contain(new[]
        {
            TrustPingApi.ProtocolUri,
            EmptyProtocol.ProtocolUri,
            DiscoverFeaturesApi.ProtocolUri,
        });
    }

    [Fact]
    public async Task Prefix_match_filters_to_didcomm_org_protocols_only()
    {
        var (_, handler) = BuildHandler();
        var query = DiscoverFeaturesApi.CreateQuery("did:peer:alice", "did:peer:bob",
            new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/trust-ping/*" });

        var reply = await handler.HandleAsync(query, Ctx(query), CancellationToken.None);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(reply!);
        disclosures.Should().HaveCount(1);
        disclosures[0].Id.Should().Be(TrustPingApi.ProtocolUri);
    }

    [Fact]
    public async Task Constraint_query_reflects_DidCommOptions_MaxReceiveBytes()
    {
        var (_, handler) = BuildHandler();
        var query = DiscoverFeaturesApi.CreateQuery("did:peer:alice", "did:peer:bob",
            new FeatureQuery { FeatureType = "constraint", Match = DiscoverFeaturesApi.ConstraintMaxReceiveBytes });

        var customOptions = new DidCommOptions { MaxReceiveBytes = 4_194_304 };
        var reply = await handler.HandleAsync(query, Ctx(query, customOptions), CancellationToken.None);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(reply!);
        disclosures.Should().HaveCount(1);
        disclosures[0].FeatureType.Should().Be("constraint");
        disclosures[0].Id.Should().Be(DiscoverFeaturesApi.ConstraintMaxReceiveBytes);
        disclosures[0].Value.Should().Be(4_194_304);
    }

    [Fact]
    public async Task Unrecognized_feature_type_is_ignored_and_yields_empty_disclosures()
    {
        // FR-PROTO-05: an empty disclosures array is meaningful, not an error.
        var (_, handler) = BuildHandler();
        var query = DiscoverFeaturesApi.CreateQuery("did:peer:alice", "did:peer:bob",
            new FeatureQuery { FeatureType = "frobnicator", Match = "*" });

        var reply = await handler.HandleAsync(query, Ctx(query), CancellationToken.None);
        reply.Should().NotBeNull();
        DiscoverFeaturesApi.ReadDisclosures(reply!).Should().BeEmpty();
        // Reply still threads correctly and is addressed back to the querier.
        reply!.Thid.Should().Be(query.Id);
        reply.From.Should().Be("did:peer:bob");
        reply.To.Should().Equal("did:peer:alice");
    }

    [Fact]
    public async Task Mixed_recognized_and_unrecognized_types_disclose_only_the_recognized()
    {
        var (_, handler) = BuildHandler();
        var query = DiscoverFeaturesApi.CreateQuery("did:peer:alice", "did:peer:bob",
            new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/empty/*" },
            new FeatureQuery { FeatureType = "frobnicator", Match = "*" });

        var reply = await handler.HandleAsync(query, Ctx(query), CancellationToken.None);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(reply!);
        disclosures.Should().HaveCount(1);
        disclosures[0].Id.Should().Be(EmptyProtocol.ProtocolUri);
    }

    [Fact]
    public async Task FeatureType_match_is_case_insensitive()
    {
        var (_, handler) = BuildHandler();
        var query = DiscoverFeaturesApi.CreateQuery("did:peer:alice", "did:peer:bob",
            new FeatureQuery { FeatureType = "Protocol", Match = "https://didcomm.org/empty/*" });

        var reply = await handler.HandleAsync(query, Ctx(query), CancellationToken.None);
        var disclosures = DiscoverFeaturesApi.ReadDisclosures(reply!);
        disclosures.Should().HaveCount(1);
    }

    [Fact]
    public async Task Disclose_typed_inbound_is_terminal_no_reply()
    {
        var (_, handler) = BuildHandler();
        var disclose = DiscoverFeaturesApi.CreateDisclose("did:peer:bob", "did:peer:alice", "thid",
            new FeatureDisclosure { FeatureType = "protocol", Id = TrustPingApi.ProtocolUri });
        var reply = await handler.HandleAsync(disclose, Ctx(disclose), CancellationToken.None);
        reply.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_query_lacks_from_or_to()
    {
        var (_, handler) = BuildHandler();
        var anonymous = new MessageBuilder().WithType(DiscoverFeaturesApi.QueriesType).Build();
        var reply = await handler.HandleAsync(anonymous, Ctx(anonymous), CancellationToken.None);
        reply.Should().BeNull();
    }

    [Fact]
    public async Task Reply_from_prefers_actual_decrypting_DID_over_first_plaintext_recipient()
    {
        var (_, handler) = BuildHandler();
        var query = new MessageBuilder()
            .WithType(DiscoverFeaturesApi.QueriesType)
            .WithFrom("did:example:alice")
            .WithTo("did:example:other-tenant", "did:example:bob")
            .WithBody(new System.Text.Json.Nodes.JsonObject
            {
                ["queries"] = new System.Text.Json.Nodes.JsonArray(),
            })
            .Build();

        var reply = await handler.HandleAsync(
            query,
            Ctx(query, recipientKid: "did:example:bob#key-agreement-1"),
            CancellationToken.None);

        reply.Should().NotBeNull();
        reply!.From.Should().Be("did:example:bob");
        reply.To.Should().Equal("did:example:alice");
    }

    [Fact]
    public void ProtocolUri_matches_spec()
    {
        new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>()).ProtocolUri
            .Should().Be("https://didcomm.org/discover-features/2.0");
    }
}
