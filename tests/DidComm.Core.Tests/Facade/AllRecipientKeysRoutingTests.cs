using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using AwesomeAssertions;
using NetCrypto;
using Xunit;
using StubKeyService = DidComm.Tests.Facade.AllRecipientKeysTests.StubKeyService;
using StubServiceResolver = DidComm.Tests.Facade.AllRecipientKeysTests.StubServiceResolver;

namespace DidComm.Tests.Facade;

/// <summary>
/// FR-ENC-19 on the routing path (FR-ROUTE-02/04/06): a forward layer addressed to a mediator named
/// by its DID is encrypted to all of that mediator's keyAgreement keys on one curve, whether the DID
/// is the recipient's service <c>uri</c> or a bare-DID <c>routingKeys</c> entry, and the mediator's
/// rewrap to the next hop makes the same selection. Each test unwraps with a mediator device that
/// holds only one of its keys.
/// </summary>
public sealed class AllRecipientKeysRoutingTests
{
    private const string Bob = "did:example:bob";
    private const string Mediator = "did:example:mediator";
    private const string MediatorUri = "https://mediator.example/inbox";

    [Fact]
    public async Task Mediator_named_as_the_endpoint_gets_one_layer_to_all_its_keys_and_unwraps_on_either_device()
    {
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(Mediator) }),
            (Mediator, new[] { Info(MediatorUri) }));

        var result = await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true));

        result.ServiceEndpoint.Should().Be(MediatorUri);
        RecipientKids(result.Message).Should().Equal(Kid(Mediator, "m1"), Kid(Mediator, "m2"));
        foreach (var device in world.MediatorKeys)
            await world.ShouldRelayToBob(result.Message, device);
    }

    [Fact]
    public async Task Bare_DID_routing_key_gets_one_layer_to_all_of_that_mediators_keys()
    {
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(MediatorUri, routing: Mediator) }));

        var result = await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true));

        result.ServiceEndpoint.Should().Be(MediatorUri);
        RecipientKids(result.Message).Should().Equal(Kid(Mediator, "m1"), Kid(Mediator, "m2"));
        await world.ShouldRelayToBob(result.Message, world.MediatorKeys[1]);
    }

    [Fact]
    public async Task DID_URL_routing_key_still_gets_a_layer_for_that_one_key()
    {
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(MediatorUri, routing: Kid(Mediator, "m2")) }));

        var result = await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true));

        RecipientKids(result.Message).Should().Equal(Kid(Mediator, "m2"));
        await world.ShouldRelayToBob(result.Message, world.MediatorKeys[1]);
    }

    [Fact]
    public async Task A_pinned_recipient_behind_a_mediator_gets_an_inner_envelope_for_that_key_and_a_bare_next()
    {
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(MediatorUri, routing: Mediator) }));

        var result = await world.Sender(services).PackEncryptedAsync(
            Ping(), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "x2") }, Forward: true));

        var onward = await world.Processor(world.MediatorKeys[0]).ProcessAsync(result.Message);
        onward.NextHop.Should().Be(Bob, "body.next is the bare DID even when the sender pinned one of its keys");
        RecipientKids(Encoding.UTF8.GetString(onward.OnwardPacked)).Should().Equal(Kid(Bob, "x2"));
    }

    [Fact]
    public async Task Rewrap_to_the_next_hop_addresses_its_keys_through_the_shared_selector()
    {
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(MediatorUri, routing: Mediator) }));
        var packed = (await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true))).Message;

        // The mediator's view of Bob's keys: a duplicate, a key with no kid, and a P-256 key next to
        // his X25519 keys. The rewrap takes each X25519 kid once and nothing else.
        var kidless = TestKeyMaterial.Generate(KeyType.X25519, "unused").PublicJwk;
        kidless.Kid = null;
        var p256 = TestKeyMaterial.Generate(KeyType.P256, Kid(Bob, "p1")).PublicJwk;
        var bobView = new StubKeyService((Bob, new[]
        {
            p256, world.BobKeys[0].PublicJwk, kidless, world.BobKeys[0].PublicJwk, world.BobKeys[1].PublicJwk,
        }));

        var onward = await world.Processor(world.MediatorKeys[0], RewrapMode.ReanoncryptToNext, bobView).ProcessAsync(packed);

        var rewrapped = Encoding.UTF8.GetString(onward.OnwardPacked);
        RecipientKids(rewrapped).Should().Equal(Kid(Bob, "x1"), Kid(Bob, "x2"));
        (await world.Receiver(world.BobKeys[1]).UnpackAsync(rewrapped)).RecipientKid.Should().Be(Kid(Bob, "x2"));
    }

    [Fact]
    public async Task Rewrap_to_a_next_hop_with_no_key_on_a_supported_curve_is_refused()
    {
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(MediatorUri, routing: Mediator) }));
        var packed = (await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true))).Message;
        var bobView = new StubKeyService((Bob, new[] { new Jwk { Kty = "EC", Crv = "secp256k1", X = "stub", Y = "stub", Kid = Kid(Bob, "k1") } }));

        var act = () => world.Processor(world.MediatorKeys[0], RewrapMode.ReanoncryptToNext, bobView).ProcessAsync(packed);

        (await act.Should().ThrowAsync<DidResolutionException>()).Which.Message.Should().Contain("supported curve");
    }

    // ---- keys a document lists under another DID's kid ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_mediator_layer_leaves_out_keys_its_document_lists_under_another_DIDs_kid(bool mediatorAsEndpoint)
    {
        // The mediator's document lists Bob's real x1 first. A forward layer is for the mediator
        // only, so Bob's kid is not in it, and the mediator still unwraps on either device.
        var world = new World(mediatorAlsoLists: b => new[] { b[0].PublicJwk });
        var services = mediatorAsEndpoint
            ? new StubServiceResolver((Bob, new[] { Info(Mediator) }), (Mediator, new[] { Info(MediatorUri) }))
            : new StubServiceResolver((Bob, new[] { Info(MediatorUri, routing: Mediator) }));

        var result = await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true));

        RecipientKids(result.Message).Should().Equal(Kid(Mediator, "m1"), Kid(Mediator, "m2"));
        foreach (var device in world.MediatorKeys)
            await world.ShouldRelayToBob(result.Message, device);
    }

    [Fact]
    public async Task Rewrap_leaves_out_keys_the_next_hops_document_lists_under_another_DIDs_kid()
    {
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(MediatorUri, routing: Mediator) }));
        var packed = (await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true))).Message;

        // The mediator's view of Bob's document lists the mediator's own m1 first, under its kid.
        var bobView = new StubKeyService((Bob, new[]
        {
            world.MediatorKeys[0].PublicJwk, world.BobKeys[0].PublicJwk, world.BobKeys[1].PublicJwk,
        }));

        var onward = await world.Processor(world.MediatorKeys[0], RewrapMode.ReanoncryptToNext, bobView).ProcessAsync(packed);

        var rewrapped = Encoding.UTF8.GetString(onward.OnwardPacked);
        RecipientKids(rewrapped).Should().Equal(Kid(Bob, "x1"), Kid(Bob, "x2"));
        (await world.Receiver(world.BobKeys[1]).UnpackAsync(rewrapped)).RecipientKid.Should().Be(Kid(Bob, "x2"));
    }

    // ---- the forward-layer cap ----

    [Fact]
    public async Task Eight_forward_layers_pack_and_every_hop_relays_to_the_next()
    {
        // The mediator named as Bob's endpoint plus seven bare-DID routing keys: eight layers.
        var hops = Enumerable.Range(0, 7)
            .Select(i => TestKeyMaterial.Generate(KeyType.X25519, Kid($"did:example:hop{i}", "k")))
            .ToArray();
        var hopDids = Enumerable.Range(0, 7).Select(i => $"did:example:hop{i}").ToArray();
        var world = new World(extraDocs: hops.Select((k, i) => LengthCap.DocWithKeys(hopDids[i], Array.Empty<Jwk>(), new[] { k.PublicJwk })));
        var services = new StubServiceResolver(
            (Bob, new[] { Info(Mediator, routing: hopDids) }),
            (Mediator, new[] { Info(MediatorUri) }));

        var result = await world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true));

        result.ServiceEndpoint.Should().Be(MediatorUri);
        var current = result.Message;
        var devices = new[] { world.MediatorKeys[0] }.Concat(hops).ToArray();
        var nextHops = hopDids.Append(Bob).ToArray();
        for (var i = 0; i < devices.Length; i++)
        {
            var onward = await world.Processor(devices[i]).ProcessAsync(current);
            onward.NextHop.Should().Be(nextHops[i]);
            current = Encoding.UTF8.GetString(onward.OnwardPacked);
        }
        (await world.Receiver(world.BobKeys[2]).UnpackAsync(current)).RecipientKid.Should().Be(Kid(Bob, "x3"));
    }

    [Fact]
    public async Task Nine_forward_layers_are_refused_without_resolving_any_routing_key_or_mediator()
    {
        var hopDids = Enumerable.Range(0, 8).Select(i => $"did:example:hop{i}").ToArray();
        var world = new World();
        var services = new StubServiceResolver(
            (Bob, new[] { Info(Mediator, routing: hopDids) }),
            (Mediator, new[] { Info(MediatorUri) }));

        var act = () => world.Sender(services).PackEncryptedAsync(Ping(), new PackEncryptedOptions(Recipients: new[] { Bob }, Forward: true));

        var thrown = (await act.Should().ThrowAsync<DidResolutionException>()).Which;
        thrown.Message.Should().Contain("needs 9 forward layers").And.NotContain("did:example:hop");
        world.ResolvedDids.Should().OnlyContain(d => d == Bob, "only the recipient's own keys were resolved, for the inner envelope");
        services.Resolved.Should().Equal(Bob);
    }

    private static string Kid(string did, string fragment) => did + "#" + fragment;

    private static DidCommServiceInfo Info(string uri, params string[] routing) =>
        new(uri, routing, Array.Empty<string>());

    private static Message Ping() => new MessageBuilder()
        .WithType("https://example.com/protocols/test/1.0/ping")
        .WithTo(Bob)
        .WithBody(new JsonObject { ["v"] = 1 })
        .Build();

    private static string[] RecipientKids(string packed)
        => JsonNode.Parse(packed)!["recipients"]!.AsArray()
            .Select(r => r!["header"]!["kid"]!.GetValue<string>())
            .ToArray();

    // Bob with three X25519 keys and a mediator with two, published through one resolver.
    private sealed class World
    {
        public TestKeyMaterial[] BobKeys { get; } =
            new[] { "x1", "x2", "x3" }.Select(f => TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, f))).ToArray();

        public TestKeyMaterial[] MediatorKeys { get; } =
            new[] { "m1", "m2" }.Select(f => TestKeyMaterial.Generate(KeyType.X25519, Kid(Mediator, f))).ToArray();

        private readonly SpyDidResolver _resolver;

        // mediatorAlsoLists: keys the mediator's document lists ahead of its own, given Bob's keys.
        // extraDocs: more DID documents published through the same resolver.
        public World(Func<TestKeyMaterial[], Jwk[]>? mediatorAlsoLists = null, IEnumerable<NetDid.Core.Model.DidDocument>? extraDocs = null)
        {
            var mediatorDoc = (mediatorAlsoLists?.Invoke(BobKeys) ?? Array.Empty<Jwk>())
                .Concat(MediatorKeys.Select(k => k.PublicJwk))
                .ToArray();
            _resolver = new SpyDidResolver(
                new[]
                {
                    LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), BobKeys.Select(k => k.PublicJwk).ToArray()),
                    LengthCap.DocWithKeys(Mediator, Array.Empty<Jwk>(), mediatorDoc),
                }.Concat(extraDocs ?? Array.Empty<NetDid.Core.Model.DidDocument>()).ToArray());
        }

        // Every DID the shared resolver was asked for.
        public IReadOnlyCollection<string> ResolvedDids => _resolver.Requested;

        public DidCommClient Sender(IServiceEndpointResolver services)
            => new(new SpySecretsResolver(), new NetDidKeyService(_resolver), services, new DidCommOptions());

        public DidCommClient Receiver(params TestKeyMaterial[] held)
            => new(new SpySecretsResolver(held.Select(k => k.PrivateJwk).ToArray()), new NetDidKeyService(_resolver), new DidCommOptions());

        public ForwardProcessor Processor(TestKeyMaterial mediatorDevice, RewrapMode mode = RewrapMode.PassThrough, IDidKeyService? keyService = null)
            => new(Receiver(mediatorDevice), keyService ?? new NetDidKeyService(_resolver), new ForwardProcessorOptions(Mode: mode));

        // A mediator device holding one key unwraps the forward; the onward envelope addresses all
        // three of Bob's keys, and a Bob device holding only the third decrypts it.
        public async Task ShouldRelayToBob(string packed, TestKeyMaterial mediatorDevice)
        {
            var onward = await Processor(mediatorDevice).ProcessAsync(packed);
            onward.NextHop.Should().Be(Bob);
            var inner = Encoding.UTF8.GetString(onward.OnwardPacked);
            RecipientKids(inner).Should().Equal(Kid(Bob, "x1"), Kid(Bob, "x2"), Kid(Bob, "x3"));
            (await Receiver(BobKeys[2]).UnpackAsync(inner)).RecipientKid.Should().Be(Kid(Bob, "x3"));
        }
    }
}
