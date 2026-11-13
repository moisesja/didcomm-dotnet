using System.Text.Json;
using DidComm.Extensions.DependencyInjection;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Method.Peer;
using Xunit;

namespace DidComm.Tests.Resolution;

/// <summary>
/// #95 B3 (design §1.3): <see cref="DidCommServiceBuilder"/> — the entry shapes, every refusal (none
/// quoting the value), the route-length bound counted before any entry is looked at, a round trip
/// through net-did resolution of a did:peer:2 built with it (read back by
/// <see cref="IServiceEndpointResolver"/> as the FR-ROUTE-03 object form), and that the samples'
/// <see cref="PeerIdentityFactory"/>, which now delegates to it, mints exactly the service encoding it
/// minted before.
/// </summary>
public sealed class DidCommServiceBuilderTests
{
    private const string MediatorDid = "did:example:mediator";

    // ── Shapes ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForEndpoint_defaults_to_id_didcomm_and_accept_didcomm_v2_with_no_routing_keys()
    {
        var service = DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example/didcomm")).Build();

        service.Id.Should().Be("#didcomm");
        service.Type.Should().Be("DIDCommMessaging");
        EndpointJson(service).Should().Be("""{"uri":"https://agent.example/didcomm","accept":["didcomm/v2"]}""");
    }

    [Fact]
    public void ForMediator_with_routing_keys_puts_uri_then_routingKeys_then_accept_and_keeps_the_order()
    {
        var service = DidCommServiceBuilder.ForMediator(MediatorDid)
            .WithRoutingKeys("did:example:relay2#key-1", "did:example:relay1")
            .WithAccept("didcomm/v2", "didcomm/aip2;env=rfc587")
            .WithId("did:example:alice#mediated")
            .Build();

        service.Id.Should().Be("did:example:alice#mediated");
        EndpointJson(service).Should().Be(
            """{"uri":"did:example:mediator","routingKeys":["did:example:relay2#key-1","did:example:relay1"],"accept":["didcomm/v2","didcomm/aip2;env=rfc587"]}""");
    }

    [Fact]
    public void WithAccept_with_no_profiles_leaves_accept_out_and_WithRoutingKeys_with_none_clears_them()
    {
        var service = DidCommServiceBuilder.ForEndpoint(new Uri("wss://agent.example/ws"))
            .WithRoutingKeys("did:example:relay#key-1")
            .WithRoutingKeys()
            .WithAccept()
            .Build();

        EndpointJson(service).Should().Be("""{"uri":"wss://agent.example/ws"}""");
    }

    [Theory]
    [InlineData("http://agent.example", "http://agent.example/")]
    [InlineData("HTTPS://Agent.Example:443/a/../didcomm", "https://agent.example/didcomm")]
    [InlineData("ws://127.0.0.1:5000/didcomm", "ws://127.0.0.1:5000/didcomm")]
    [InlineData("wss://agent.example/didcomm?x=1", "wss://agent.example/didcomm?x=1")]
    public void ForEndpoint_stores_the_canonical_absolute_uri(string given, string stored)
    {
        Uri(DidCommServiceBuilder.ForEndpoint(new Uri(given)).Build()).Should().Be(stored);
    }

    [Fact]
    public void Build_returns_independent_entries_and_later_changes_do_not_reach_an_earlier_one()
    {
        var builder = DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example/didcomm"));
        var keys = new[] { "did:example:relay#key-1" };
        builder.WithRoutingKeys(keys);
        var first = builder.Build();

        keys[0] = "did:example:changed#key-1";
        builder.WithAccept();
        var second = builder.Build();

        EndpointJson(first).Should().Be("""{"uri":"https://agent.example/didcomm","routingKeys":["did:example:relay#key-1"],"accept":["didcomm/v2"]}""");
        EndpointJson(second).Should().Be("""{"uri":"https://agent.example/didcomm","routingKeys":["did:example:relay#key-1"]}""",
            "the builder copied the routing keys when they were given");
    }

    [Theory]
    [InlineData("#didcomm")]
    [InlineData("#svc-1._~!$&'()*+,;=:@/?")]
    [InlineData("#a%20b")]
    [InlineData("did:example:123#didcomm")]
    [InlineData("did:web:agent.example#didcomm-1")]
    public void WithId_accepts_a_fragment_or_a_DID_URL_with_a_fragment(string id)
    {
        DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example")).WithId(id).Build().Id.Should().Be(id);
    }

    // ── Refusals (never quoting the value) ────────────────────────────────────────────────

    [Theory]
    [InlineData("ftp://secret-host.example/x")]
    [InlineData("file:///secret/path")]
    [InlineData("mailto:secret@example.com")]
    [InlineData("urn:secret:thing")]
    [InlineData("https://secret:pass@agent.example/didcomm")]
    [InlineData("https://secret@agent.example/didcomm")]
    public void ForEndpoint_refuses_other_schemes_and_user_information_without_quoting(string endpoint)
    {
        var act = () => DidCommServiceBuilder.ForEndpoint(new Uri(endpoint));

        act.Should().Throw<ArgumentException>().WithParameterName("endpoint")
            .Which.Message.Should().NotContain("secret");
    }

    [Fact]
    public void ForEndpoint_refuses_a_relative_uri_and_null()
    {
        FluentActions.Invoking(() => DidCommServiceBuilder.ForEndpoint(new Uri("/secret/inbox", UriKind.Relative)))
            .Should().Throw<ArgumentException>().WithParameterName("endpoint").Which.Message.Should().NotContain("secret");
        FluentActions.Invoking(() => DidCommServiceBuilder.ForEndpoint(null!)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("secret-not-a-did")]
    [InlineData("did:example:secret#key-1")]
    [InlineData("did:example:secret/path")]
    [InlineData("did:example:secret?service=x")]
    [InlineData(" did:example:secret")]
    public void ForMediator_refuses_anything_but_a_bare_DID_without_quoting(string mediator)
    {
        var act = () => DidCommServiceBuilder.ForMediator(mediator);

        act.Should().Throw<ArgumentException>().WithParameterName("mediatorDid")
            .Which.Message.Should().NotContain("secret");
    }

    [Fact]
    public void ForMediator_refuses_a_DID_over_2048_characters_and_null()
    {
        var atBound = "did:example:" + new string('a', 2048 - "did:example:".Length);
        DidCommServiceBuilder.ForMediator(atBound).Build().Should().NotBeNull("2048 characters is within the bound");

        FluentActions.Invoking(() => DidCommServiceBuilder.ForMediator(atBound + "a"))
            .Should().Throw<ArgumentException>().WithParameterName("mediatorDid").Which.Message.Should().NotContain("aaaa");
        FluentActions.Invoking(() => DidCommServiceBuilder.ForMediator(null!)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret")]
    [InlineData("#")]
    [InlineData("did:example:secret#")]
    [InlineData("#secret value")]
    [InlineData("#secret#twice")]
    [InlineData("#secret%2")]
    [InlineData("not-a-did#secret")]
    public void WithId_refuses_anything_but_a_fragment_or_a_DID_URL_with_one_without_quoting(string id)
    {
        var act = () => DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example")).WithId(id);

        act.Should().Throw<ArgumentException>().WithParameterName("id")
            .Which.Message.Should().NotContain("secret");
    }

    [Theory]
    [InlineData("")]
    [InlineData("#secret-relative")]
    [InlineData("secret-not-a-did")]
    [InlineData("did:example:secret#")]
    [InlineData("did:example:secret/path#key-1")]
    [InlineData("did:example:secret?q=1#key-1")]
    [InlineData("did:example:secret#key 1")]
    public void WithRoutingKeys_refuses_anything_but_a_DID_or_a_DID_with_a_fragment_without_quoting(string key)
    {
        var act = () => DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example")).WithRoutingKeys("did:example:ok#key-1", key);

        act.Should().Throw<ArgumentException>().WithParameterName("routingKeys")
            .Which.Message.Should().NotContain("secret").And.Contain("1", "the refusal names the entry's position");
    }

    [Fact]
    public void WithRoutingKeys_refuses_a_key_over_the_identifier_limits_and_null_entries()
    {
        var builder = DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example"));
        var longDidPart = "did:example:" + new string('s', 2048) + "#key-1";
        var longFragment = "did:example:m#" + new string('s', 4096);

        FluentActions.Invoking(() => builder.WithRoutingKeys(longDidPart)).Should().Throw<ArgumentException>().Which.Message.Should().NotContain("sss");
        FluentActions.Invoking(() => builder.WithRoutingKeys(longFragment)).Should().Throw<ArgumentException>().Which.Message.Should().NotContain("sss");
        FluentActions.Invoking(() => builder.WithRoutingKeys(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => builder.WithRoutingKeys("did:example:m", null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_refused_call_changes_nothing()
    {
        var builder = DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example/didcomm")).WithRoutingKeys("did:example:relay#key-1");

        FluentActions.Invoking(() => builder.WithRoutingKeys("did:example:new#key-1", "bad")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.WithAccept("didcomm/v2", " ")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.WithId("bad")).Should().Throw<ArgumentException>();

        var service = builder.Build();
        service.Id.Should().Be("#didcomm");
        EndpointJson(service).Should().Be("""{"uri":"https://agent.example/didcomm","routingKeys":["did:example:relay#key-1"],"accept":["didcomm/v2"]}""");
    }

    [Fact]
    public void A_route_of_eight_forward_layers_is_the_longest_accepted_counting_the_mediators_layer()
    {
        var eight = Keys(8);
        var seven = Keys(7);

        DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example")).WithRoutingKeys(eight).Build().Should().NotBeNull();
        DidCommServiceBuilder.ForMediator(MediatorDid).WithRoutingKeys(seven).Build().Should().NotBeNull();

        FluentActions.Invoking(() => DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example")).WithRoutingKeys(Keys(9)))
            .Should().Throw<ArgumentException>().Which.Message.Should().Contain("9 forward layers").And.Contain("8");
        FluentActions.Invoking(() => DidCommServiceBuilder.ForMediator(MediatorDid).WithRoutingKeys(eight))
            .Should().Throw<ArgumentException>().Which.Message.Should().Contain("9 forward layers");
    }

    [Fact]
    public void The_route_length_is_refused_from_the_count_before_any_entry_is_looked_at()
    {
        // L-055: poison beyond the limit. The ninth entry is null; the refusal is still the count.
        var keys = Keys(8).Append(null!).ToArray();

        FluentActions.Invoking(() => DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example")).WithRoutingKeys(keys))
            .Should().Throw<ArgumentException>().Which.Message.Should().Contain("forward layers");
    }

    [Fact]
    public void WithAccept_refuses_null_and_empty_profiles()
    {
        var builder = DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example"));

        FluentActions.Invoking(() => builder.WithAccept(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => builder.WithAccept("didcomm/v2", null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => builder.WithAccept("")).Should().Throw<ArgumentException>().WithParameterName("profiles");
        FluentActions.Invoking(() => builder.WithAccept("didcomm/v2", "  ")).Should().Throw<ArgumentException>().WithParameterName("profiles");
    }

    [Fact]
    public void WithId_refuses_null()
    {
        FluentActions.Invoking(() => DidCommServiceBuilder.ForEndpoint(new Uri("https://agent.example")).WithId(null!))
            .Should().Throw<ArgumentNullException>();
    }

    // ── Round trip through net-did resolution ─────────────────────────────────────────────

    [Fact]
    public async Task A_did_peer_2_minted_with_a_mediated_entry_resolves_to_the_same_FR_ROUTE_03_object_form()
    {
        await using var sp = Container();
        var mediator = await MintAsync(sp, service: null);
        var relay = await MintAsync(sp, service: null);
        var relayKid = relay.Privates.First(k => k.Crv == "X25519").Kid!;
        var service = DidCommServiceBuilder.ForMediator(mediator.Did).WithRoutingKeys(relayKid).Build();

        var bob = await MintAsync(sp, service);
        var resolved = await sp.GetRequiredService<IServiceEndpointResolver>().ResolveAsync(bob.Did);

        var entry = resolved.Should().ContainSingle().Which;
        entry.Uri.Should().Be(mediator.Did);
        entry.RoutingKeys.Should().Equal(relayKid);
        entry.Accept.Should().Equal("didcomm/v2");
    }

    [Fact]
    public async Task A_did_peer_2_minted_with_a_direct_entry_and_no_accept_resolves_to_the_uri_alone()
    {
        await using var sp = Container();
        var service = DidCommServiceBuilder.ForEndpoint(new Uri("wss://agent.example/didcomm")).WithAccept().Build();

        var alice = await MintAsync(sp, service);
        var resolved = await sp.GetRequiredService<IServiceEndpointResolver>().ResolveAsync(alice.Did);

        var entry = resolved.Should().ContainSingle().Which;
        entry.Uri.Should().Be("wss://agent.example/didcomm");
        entry.RoutingKeys.Should().BeEmpty();
        entry.Accept.Should().BeEmpty();
    }

    // ── PeerIdentityFactory delegates without changing a byte ─────────────────────────────

    public static TheoryData<string, string[]?, string[]?> SampleSpecs => new()
    {
        { "https://bob.example/didcomm", null, new[] { "didcomm/v2" } },                                   // samples/10
        { "http://127.0.0.1:5123/inbox", new[] { "did:example:mediator#key-x25519-1" }, new[] { "didcomm/v2" } }, // samples/04
        { "https://mediator.example/pppppppppppppppp", null, null },                                     // the pickup sizing test
        { "https://agent.example/didcomm", new[] { "did:example:r1#k", "did:example:r2" }, null },
        { "did:example:mediator", new[] { "did:example:r1#k" }, new[] { "didcomm/v2", "didcomm/aip2;env=rfc19" } },
        { "did:example:mediator", null, null },
    };

    [Theory]
    [MemberData(nameof(SampleSpecs))]
    public async Task PeerIdentityFactory_mints_the_same_service_encoding_as_before_the_builder(string endpoint, string[]? routingKeys, string[]? accept)
    {
        await using var sp = Container();
        var spec = new DidCommServiceSpec(endpoint, routingKeys, accept);

        var viaFactory = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>(), spec);
        var reference = await MintAsync(sp, LegacyService(spec));

        // did:peer:2 encodes the service into the DID string ('.S' element): the keys differ between
        // the two DIDs, the service element must not.
        ServiceElements(viaFactory.Did).Should().ContainSingle()
            .Which.Should().Be(ServiceElements(reference.Did).Single());
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static string[] Keys(int count) => Enumerable.Range(1, count).Select(i => $"did:example:relay{i}#key-1").ToArray();

    private static string EndpointJson(Service service)
    {
        service.ServiceEndpoint.Should().NotBeNull();
        service.ServiceEndpoint!.IsMap.Should().BeTrue("the object form of FR-ROUTE-03");
        return JsonSerializer.Serialize(service.ServiceEndpoint.Map);
    }

    private static string Uri(Service service) => service.ServiceEndpoint!.Map!["uri"].GetString()!;

    private static ServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        return services.BuildServiceProvider();
    }

    private static Task<PeerIdentity> MintAsync(IServiceProvider sp, Service? service)
        => service is null
            ? PeerIdentityFactory.CreateAsync(sp.GetRequiredService<IDidManager>(), sp.GetRequiredService<IKeyGenerator>(), sp.GetRequiredService<ICryptoProvider>())
            : MintWithAsync(sp, service);

    private static async Task<PeerIdentity> MintWithAsync(IServiceProvider sp, Service service)
    {
        var keys = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();
        var result = await sp.GetRequiredService<IDidManager>().CreateAsync(new DidPeerCreateOptions
        {
            Numalgo = PeerNumalgo.Two,
            Keys = new[]
            {
                new PeerKeyPurpose(new KeyPairSigner(keys.Generate(KeyType.X25519), crypto), PeerPurpose.KeyAgreement),
                new PeerKeyPurpose(new KeyPairSigner(keys.Generate(KeyType.Ed25519), crypto), PeerPurpose.Authentication),
            },
            Services = new[] { service },
        });
        return new PeerIdentity(result.Did.Value!, Array.Empty<Jwk>());
    }

    private static IEnumerable<string> ServiceElements(string did) => did.Split('.').Where(e => e.StartsWith('S'));

    /// <summary>The service PeerIdentityFactory built before it delegated to the builder, verbatim.</summary>
    private static Service LegacyService(DidCommServiceSpec spec)
    {
        var endpoint = new Dictionary<string, JsonElement>
        {
            ["uri"] = JsonSerializer.SerializeToElement(spec.EndpointUriOrDid),
        };
        if (spec.RoutingKeys is { Count: > 0 })
            endpoint["routingKeys"] = JsonSerializer.SerializeToElement(spec.RoutingKeys);
        if (spec.Accept is { Count: > 0 })
            endpoint["accept"] = JsonSerializer.SerializeToElement(spec.Accept);

        return new Service
        {
            Id = "#didcomm",
            Type = "DIDCommMessaging",
            ServiceEndpoint = ServiceEndpointValue.FromMap(endpoint),
        };
    }
}
