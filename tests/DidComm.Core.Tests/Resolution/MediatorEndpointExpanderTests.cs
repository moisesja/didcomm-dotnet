using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Resolution;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Resolution;

/// <summary>
/// Phase 4 Checkpoint C — covers FR-ROUTE-04 (mediator-as-DID-endpoint expansion). The
/// expander is internal but driven indirectly via this test class because Phase 4's
/// `DidComm.Core.Tests` already references the test project's `InternalsVisibleTo` entry.
/// </summary>
public sealed class MediatorEndpointExpanderTests
{
    private const string Recipient = "did:example:bob";
    private const string Mediator = "did:example:mediator1";

    [Fact]
    public async Task Plain_uri_passthrough_no_mediator_lookup()
    {
        var svc = new StubServiceResolver(); // empty — never consulted
        var keys = new StubKeyService(
            (Recipient, new[] { Jwk("did:example:bob#r-1") }));
        var candidates = new[]
        {
            Info("https://recipient.example/inbox", routing: new[] { "did:example:bob#r-1" }),
        };

        var route = await MediatorEndpointExpander.ExpandAsync(candidates, svc, keys, Recipient);

        route.TransportUri.Should().Be("https://recipient.example/inbox");
        var layer = route.Layers.Should().ContainSingle().Which;
        layer.Did.Should().Be(Recipient);
        layer.Recipients.Should().ContainSingle().Which.Kid.Should().Be("did:example:bob#r-1");
        route.FallbackUris.Should().BeEmpty();
        svc.ResolveCount.Should().Be(0, "mediator lookup must only fire when uri is a DID");
    }

    [Fact]
    public async Task Did_endpoint_resolves_mediator_and_prepends_one_layer_to_all_its_key_agreement_keys()
    {
        // FR-ROUTE-04 + FR-ENC-19: the mediator's keyAgreement keys are prepended as ONE forward
        // layer encrypted to all of them, so the mediator can unwrap on any of its devices.
        var svc = new StubServiceResolver(
            (Mediator, new[] { Info("https://mediator.example/inbox") }));
        var keys = new StubKeyService(
            (Mediator, new[] { Jwk("did:example:mediator1#kx-1"), Jwk("did:example:mediator1#kx-2") }),
            (Recipient, new[] { Jwk("did:example:bob#r-1") }));

        var candidates = new[]
        {
            Info(Mediator, routing: new[] { "did:example:bob#r-1" }),
        };

        var route = await MediatorEndpointExpander.ExpandAsync(candidates, svc, keys, Recipient);

        route.TransportUri.Should().Be("https://mediator.example/inbox");
        LayerKids(route).Should().BeEquivalentTo(
            new[]
            {
                (Mediator, new[] { "did:example:mediator1#kx-1", "did:example:mediator1#kx-2" }), // prepended mediator layer
                (Recipient, new[] { "did:example:bob#r-1" }),                                     // recipient's routing key
            },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Mediator_publishing_a_did_as_its_own_uri_throws_FR_ROUTE_04()
    {
        var svc = new StubServiceResolver(
            (Mediator, new[] { Info("did:example:another-mediator") }));
        var keys = new StubKeyService((Mediator, new[] { Jwk("did:example:mediator1#kx-1") }));
        var candidates = new[] { Info(Mediator) };

        var act = () => MediatorEndpointExpander.ExpandAsync(candidates, svc, keys, Recipient);

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Which.Message.Should().Contain("Recursive endpoint resolution is forbidden");
    }

    [Fact]
    public async Task Mediator_keyAgreement_prepends_only_recipient_routing_keys_follow()
    {
        // Per FR-ROUTE-04 / spec §Using a DID as an endpoint: the mediator's *keyAgreement*
        // keys are implicitly prepended, but the mediator's own routingKeys are ignored when
        // it appears merely as an endpoint (they apply only when the mediator is itself the
        // message recipient). The mediator service here advertises routingKeys deliberately to
        // confirm we don't accidentally weave them in.
        var svc = new StubServiceResolver(
            (Mediator, new[] { Info("https://mediator.example/", routing: new[] { "did:example:mediator2#kx-1" }) }));
        var keys = new StubKeyService(
            (Mediator, new[] { Jwk("did:example:mediator1#kx-1") }),
            (Recipient, new[] { Jwk("did:example:bob#r-1") }));

        var candidates = new[] { Info(Mediator, routing: new[] { "did:example:bob#r-1" }) };

        var route = await MediatorEndpointExpander.ExpandAsync(candidates, svc, keys, Recipient);

        LayerKids(route).Should().BeEquivalentTo(
            new[]
            {
                (Mediator, new[] { "did:example:mediator1#kx-1" }), // mediator's keyAgreement (outermost)
                (Recipient, new[] { "did:example:bob#r-1" }),       // recipient's routing key
            },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Preserves_fallback_uris_from_extra_candidates()
    {
        var svc = new StubServiceResolver();
        var keys = new StubKeyService();
        var candidates = new[]
        {
            Info("https://primary/"),
            Info("https://failover-1/"),
            Info("https://failover-2/"),
        };

        var route = await MediatorEndpointExpander.ExpandAsync(candidates, svc, keys, Recipient);

        route.TransportUri.Should().Be("https://primary/");
        route.FallbackUris.Should().Equal("https://failover-1/", "https://failover-2/");
    }

    [Fact]
    public async Task Throws_DidResolutionException_when_no_candidates()
    {
        var act = () => MediatorEndpointExpander.ExpandAsync(
            Array.Empty<DidCommServiceInfo>(),
            new StubServiceResolver(),
            new StubKeyService(),
            Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("no DIDCommMessaging");
    }

    [Fact]
    public async Task Throws_when_mediator_has_no_keyAgreement_keys_to_prepend()
    {
        var svc = new StubServiceResolver(
            (Mediator, new[] { Info("https://mediator/") }));
        var keys = new StubKeyService(); // mediator has zero keys

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info(Mediator) }, svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("no keyAgreement keys to prepend");
    }

    [Fact]
    public async Task Throws_when_routing_key_kid_does_not_appear_in_subject_document()
    {
        var svc = new StubServiceResolver();
        var keys = new StubKeyService(
            (Recipient, new[] { Jwk("did:example:bob#different-kid") }));

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { "did:example:bob#missing" }) },
            svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("not declared in keyAgreement");
    }

    [Fact]
    public async Task Bare_DID_routing_key_wraps_one_layer_to_all_of_that_DIDs_keys()
    {
        // A routingKeys entry that is a bare DID names the mediator itself, as didcomm-python and
        // didcomm-jvm read it: one layer encrypted to all of its keyAgreement keys on one curve.
        var svc = new StubServiceResolver();
        var keys = new StubKeyService(
            (Mediator, new[] { Jwk("did:example:mediator1#kx-1"), Jwk("did:example:mediator1#p-1", "P-256"), Jwk("did:example:mediator1#kx-2") }));

        var route = await MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { Mediator }) },
            svc, keys, Recipient);

        LayerKids(route).Should().BeEquivalentTo(
            new[] { (Mediator, new[] { "did:example:mediator1#kx-1", "did:example:mediator1#kx-2" }) },
            o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("did:example:mediator1/path")]
    [InlineData("did:example:mediator1?service=x")]
    [InlineData("did:example:mediator1;p=1")]
    [InlineData("https://not-a-did.example/")]
    public async Task Throws_when_a_routing_key_without_fragment_is_not_a_bare_DID(string reference)
    {
        var keys = new StubKeyService((Mediator, new[] { Jwk("did:example:mediator1#kx-1") }));

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { reference }) },
            new StubServiceResolver(), keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("neither a DID nor a DID URL with a fragment");
    }

    [Fact]
    public async Task Throws_when_a_bare_DID_routing_key_has_no_keyAgreement_keys()
    {
        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { Mediator }) },
            new StubServiceResolver(), new StubKeyService(), Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("routing key DID has no keyAgreement keys");
    }

    [Fact]
    public async Task Mediator_layer_uses_the_preferred_curve_skips_kidless_keys_and_dedupes()
    {
        var svc = new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") }));
        var kidless = Jwk("unused");
        kidless.Kid = null;
        var keys = new StubKeyService(
            (Mediator, new[]
            {
                Jwk("did:example:mediator1#p-1", "P-256"),
                kidless,
                Jwk("did:example:mediator1#kx-1"),
                Jwk("did:example:mediator1#kx-1"),
                Jwk("did:example:mediator1#kx-2"),
            }));

        var route = await MediatorEndpointExpander.ExpandAsync(new[] { Info(Mediator) }, svc, keys, Recipient);

        LayerKids(route).Should().BeEquivalentTo(
            new[] { (Mediator, new[] { "did:example:mediator1#kx-1", "did:example:mediator1#kx-2" }) },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Mediator_layer_is_capped_at_64_keys_in_document_order()
    {
        var svc = new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") }));
        var mediatorKeys = Enumerable.Range(1, 65).Select(i => Jwk($"did:example:mediator1#kx-{i}")).ToArray();
        var keys = new StubKeyService((Mediator, mediatorKeys));

        var route = await MediatorEndpointExpander.ExpandAsync(new[] { Info(Mediator) }, svc, keys, Recipient);

        route.Layers.Should().ContainSingle().Which.Recipients.Should().Equal(mediatorKeys.Take(64));
    }

    [Fact]
    public async Task Throws_when_the_mediators_keys_all_lack_a_kid()
    {
        var svc = new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") }));
        var kidless = Jwk("unused");
        kidless.Kid = null;
        var keys = new StubKeyService((Mediator, new[] { kidless }));

        var act = () => MediatorEndpointExpander.ExpandAsync(new[] { Info(Mediator) }, svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("has no keyAgreement key with a key id");
    }

    [Fact]
    public async Task Throws_when_the_mediator_has_no_key_on_a_supported_curve()
    {
        var svc = new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") }));
        var keys = new StubKeyService((Mediator, new[] { Jwk("did:example:mediator1#k1", "secp256k1") }));

        var act = () => MediatorEndpointExpander.ExpandAsync(new[] { Info(Mediator) }, svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("supported curve");
    }

    [Fact]
    public async Task Throws_when_mediator_did_resolves_to_zero_service_entries()
    {
        var svc = new StubServiceResolver(); // mediator returns empty list
        var keys = new StubKeyService((Mediator, new[] { Jwk("did:example:mediator1#kx-1") }));

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info(Mediator) }, svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("no DIDCommMessaging service");
    }

    [Fact]
    public async Task Throws_when_routing_key_is_a_relative_fragment_reference()
    {
        var svc = new StubServiceResolver();
        var keys = new StubKeyService();

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { "#key-1" }) },
            svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("relative");
    }

    [Fact]
    public async Task Drops_did_valued_fallback_uris_keeping_only_transport_urls()
    {
        var svc = new StubServiceResolver();
        var keys = new StubKeyService();
        var candidates = new[]
        {
            Info("https://primary/"),
            Info("did:example:mediator2"), // DID-as-endpoint fallback — not a usable transport URI
            Info("https://failover/"),
        };

        var route = await MediatorEndpointExpander.ExpandAsync(candidates, svc, keys, Recipient);

        route.TransportUri.Should().Be("https://primary/");
        route.FallbackUris.Should().Equal("https://failover/");
    }

    [Fact]
    public async Task Mediator_and_bare_DID_routing_key_layers_leave_out_keys_whose_kid_names_another_DID()
    {
        // The mediator's document lists Bob's kid and another mediator's kid next to its own keys.
        // A forward layer addressed to the mediator takes only the keys its own DID names.
        var svc = new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") }));
        var keys = new StubKeyService(
            (Mediator, new[]
            {
                Jwk("did:example:bob#r-1"),
                Jwk("did:example:mediator1x#kx-1"),
                Jwk("did:example:mediator1#kx-1"),
                Jwk("did:example:mediator2#kx-1"),
            }));

        var asEndpoint = await MediatorEndpointExpander.ExpandAsync(new[] { Info(Mediator) }, svc, keys, Recipient);
        var asRoutingKey = await MediatorEndpointExpander.ExpandAsync(new[] { Info("https://r/", routing: new[] { Mediator }) }, svc, keys, Recipient);

        LayerKids(asEndpoint).Should().BeEquivalentTo(new[] { (Mediator, new[] { "did:example:mediator1#kx-1" }) }, o => o.WithStrictOrdering());
        LayerKids(asRoutingKey).Should().BeEquivalentTo(new[] { (Mediator, new[] { "did:example:mediator1#kx-1" }) }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_mediator_whose_keys_all_name_another_DID_is_refused()
    {
        var svc = new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") }));
        var keys = new StubKeyService((Mediator, new[] { Jwk("did:example:bob#r-1") }));

        var act = () => MediatorEndpointExpander.ExpandAsync(new[] { Info(Mediator) }, svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("has no keyAgreement key with a key id of this DID");
    }

    // ---- the forward-layer cap and per-expansion resolution ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eight_forward_layers_are_expanded(bool mediatorAsEndpoint)
    {
        var routing = Hops(mediatorAsEndpoint ? 7 : 8);
        var (svc, keys) = HopWorld(routing);

        var route = await MediatorEndpointExpander.ExpandAsync(
            new[] { Info(mediatorAsEndpoint ? Mediator : "https://r/", routing) }, svc, keys, Recipient);

        MediatorEndpointExpander.MaxForwardLayers.Should().Be(8);
        route.Layers.Should().HaveCount(8);
    }

    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 8)]
    [InlineData(false, 1000)]
    public async Task More_than_eight_forward_layers_are_refused_before_any_routing_key_or_mediator_is_resolved(bool mediatorAsEndpoint, int routingKeyCount)
    {
        var routing = Hops(routingKeyCount);
        var (svc, keys) = HopWorld(routing);

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info(mediatorAsEndpoint ? Mediator : "https://r/", routing) }, svc, keys, Recipient);

        var thrown = (await act.Should().ThrowAsync<DidResolutionException>()).Which;
        var layers = routingKeyCount + (mediatorAsEndpoint ? 1 : 0);
        thrown.Did.Should().Be(Recipient);
        thrown.Message.Should().Contain($"needs {layers} forward layers").And.Contain("more than the maximum of 8");
        thrown.Message.Should().NotContain("did:example:hop", "the refusal names counts, not the peer's references");
        keys.Requested.Should().BeEmpty();
        svc.ResolveCount.Should().Be(0);
    }

    [Fact]
    public async Task A_DID_named_by_several_layers_is_resolved_once_per_expansion()
    {
        var svc = new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") }));
        var keys = new StubKeyService(
            (Mediator, new[] { Jwk("did:example:mediator1#kx-1"), Jwk("did:example:mediator1#kx-2") }),
            (Recipient, new[] { Jwk("did:example:bob#r-1") }));
        var routing = new[] { Mediator, "did:example:mediator1#kx-2", Mediator, "did:example:bob#r-1", "did:example:bob#r-1" };

        var route = await MediatorEndpointExpander.ExpandAsync(new[] { Info(Mediator, routing) }, svc, keys, Recipient);

        route.Layers.Should().HaveCount(6);
        keys.Requested.Should().Equal(Mediator, Recipient);
    }

    // ---- references over the FR-API-09 identifier limits ----

    [Theory]
    [InlineData("did:example:m?", 200_000)]
    [InlineData("did:example:m#", 200_000)]
    [InlineData("#", 200_000)]
    [InlineData("did:example:m", 200_000)]
    [InlineData("did:example:", 4097)]
    public async Task An_over_long_routing_key_reference_is_refused_without_quoting_it(string prefix, int length)
    {
        var reference = prefix + new string('q', length - prefix.Length);
        var keys = new StubKeyService();
        var svc = new StubServiceResolver();

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { "did:example:bob#r-1", reference }) }, svc, keys, Recipient);

        var thrown = (await act.Should().ThrowAsync<DidResolutionException>()).Which;
        thrown.Did.Should().Be(Recipient);
        thrown.Message.Should().Contain("routing key reference").And.Contain($"{length} characters long").And.Contain("FR-API-09");
        thrown.Message.Length.Should().BeLessThan(400);
        thrown.Message.Should().NotContain(new string('q', 64));
        keys.Requested.Should().BeEmpty("the limits are checked before any routing key is resolved");
    }

    [Theory]
    [InlineData("#k")]
    [InlineData("")]
    public async Task A_routing_key_whose_DID_part_is_over_the_DID_limit_is_refused_without_quoting_it(string suffix)
    {
        var reference = "did:example:" + new string('q', 3000) + suffix;
        var keys = new StubKeyService();

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { reference }) }, new StubServiceResolver(), keys, Recipient);

        var thrown = (await act.Should().ThrowAsync<DidResolutionException>()).Which;
        thrown.Message.Should().Contain("has a DID part longer than the maximum of 2048 characters");
        thrown.Message.Should().NotContain(new string('q', 64));
        keys.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task An_over_long_mediator_DID_endpoint_is_refused_without_quoting_it()
    {
        var mediator = "did:example:" + new string('q', 200_000);
        var svc = new StubServiceResolver();
        var keys = new StubKeyService();

        var act = () => MediatorEndpointExpander.ExpandAsync(new[] { Info(mediator) }, svc, keys, Recipient);

        var thrown = (await act.Should().ThrowAsync<DidResolutionException>()).Which;
        thrown.Message.Should().Contain("mediator DID named as its endpoint").And.Contain("FR-API-09");
        thrown.Message.Should().NotContain(new string('q', 64));
        svc.ResolveCount.Should().Be(0);
        keys.Requested.Should().BeEmpty();
    }

    // n distinct bare-DID routing keys, did:example:hop0 .. hop{n-1}.
    private static string[] Hops(int n) => Enumerable.Range(0, n).Select(i => $"did:example:hop{i}").ToArray();

    // A mediator and every hop in routing, each with one key.
    private static (StubServiceResolver Services, StubKeyService Keys) HopWorld(IEnumerable<string> routing)
        => (new StubServiceResolver((Mediator, new[] { Info("https://mediator.example/") })),
            new StubKeyService(routing.Append(Mediator).Distinct().Select(d => (d, new[] { Jwk(d + "#k") })).ToArray()));

    private static DidCommServiceInfo Info(string uri, IReadOnlyList<string>? routing = null) =>
        new(uri, routing ?? Array.Empty<string>(), Array.Empty<string>());

    private static Jwk Jwk(string kid, string crv = "X25519") =>
        new() { Kty = crv == "X25519" ? "OKP" : "EC", Crv = crv, X = "stub", Kid = kid };

    private static (string Did, string[] Kids)[] LayerKids(ExpandedRoute route) =>
        route.Layers.Select(l => (l.Did, l.Recipients.Select(k => k.Kid!).ToArray())).ToArray();

    private sealed class StubServiceResolver : IServiceEndpointResolver
    {
        private readonly Dictionary<string, IReadOnlyList<DidCommServiceInfo>> _map;
        public int ResolveCount { get; private set; }

        public StubServiceResolver(params (string Did, IReadOnlyList<DidCommServiceInfo> Services)[] entries)
        {
            _map = new Dictionary<string, IReadOnlyList<DidCommServiceInfo>>(StringComparer.Ordinal);
            foreach (var (did, services) in entries) _map[did] = services;
        }

        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
        {
            ResolveCount++;
            return Task.FromResult(_map.TryGetValue(did, out var s) ? s : Array.Empty<DidCommServiceInfo>());
        }
    }

    private sealed class StubKeyService : IDidKeyService
    {
        private readonly Dictionary<string, IReadOnlyList<Jwk>> _map;
        private readonly List<string> _requested = new();

        public StubKeyService(params (string Did, Jwk[] Keys)[] entries)
        {
            _map = new Dictionary<string, IReadOnlyList<Jwk>>(StringComparer.Ordinal);
            foreach (var (did, keys) in entries) _map[did] = keys;
        }

        // Every DID asked for, in call order.
        public IReadOnlyList<string> Requested => _requested;

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            _requested.Add(did);
            return Task.FromResult(_map.TryGetValue(did, out var k) ? k : Array.Empty<Jwk>());
        }

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(_map.TryGetValue(did, out var k) && k.Any(j => j.Kid == kid));

        public void RejectUnsupportedMethod(string did) { }
    }
}
