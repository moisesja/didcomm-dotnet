using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Resolution;
using FluentAssertions;
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
        route.RoutingKeyJwks.Should().ContainSingle().Which.Kid.Should().Be("did:example:bob#r-1");
        route.FallbackUris.Should().BeEmpty();
        svc.ResolveCount.Should().Be(0, "mediator lookup must only fire when uri is a DID");
    }

    [Fact]
    public async Task Did_endpoint_resolves_mediator_and_prepends_its_first_key_agreement_kid()
    {
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
        route.RoutingKeyJwks.Select(j => j.Kid).Should().Equal(
            "did:example:mediator1#kx-1", // prepended mediator key (FR-ROUTE-04)
            "did:example:bob#r-1");       // recipient's routing key
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

        route.RoutingKeyJwks.Select(j => j.Kid).Should().Equal(
            "did:example:mediator1#kx-1",   // mediator's first keyAgreement (outermost)
            "did:example:bob#r-1");         // recipient's routing key
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
    public async Task Throws_when_routing_key_string_lacks_fragment()
    {
        var svc = new StubServiceResolver();
        var keys = new StubKeyService();

        var act = () => MediatorEndpointExpander.ExpandAsync(
            new[] { Info("https://r/", routing: new[] { "did:example:bob" }) },
            svc, keys, Recipient);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .Which.Message.Should().Contain("not a DID URL with a fragment");
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

    private static DidCommServiceInfo Info(string uri, IReadOnlyList<string>? routing = null) =>
        new(uri, routing ?? Array.Empty<string>(), Array.Empty<string>());

    private static Jwk Jwk(string kid) => new() { Kty = "OKP", Crv = "X25519", X = "stub", Kid = kid };

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

        public StubKeyService(params (string Did, Jwk[] Keys)[] entries)
        {
            _map = new Dictionary<string, IReadOnlyList<Jwk>>(StringComparer.Ordinal);
            foreach (var (did, keys) in entries) _map[did] = keys;
        }

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(_map.TryGetValue(did, out var k) ? k : Array.Empty<Jwk>());

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(_map.TryGetValue(did, out var k) && k.Any(j => j.Kid == kid));

        public void RejectUnsupportedMethod(string did) { }
    }
}
