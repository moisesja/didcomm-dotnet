using DidComm.Facade;
using DidComm.Resolution;
using FluentAssertions;
using NetDid.Core.Model;
using NetDid.Core.Serialization;
using Xunit;

namespace DidComm.InteropTests.Resolution;

/// <summary>
/// Phase 4 Checkpoint B — drives <see cref="NetDidServiceEndpointResolver"/> against the
/// vendored Appendix B fixtures (<c>bob-with-routing</c>, <c>mediator1</c>, <c>mediator2</c>,
/// <c>charlie</c>) using the in-memory <see cref="FixtureDidResolver"/>. Confirms the parser
/// + adapter behave end-to-end on real spec documents, not just the synthetic cases in
/// <c>ServiceEndpointParserTests</c>.
/// </summary>
public sealed class NetDidServiceEndpointResolverTests
{
    [Fact]
    public async Task BobWithRouting_returns_a_single_DIDCommMessaging_entry_with_routing_keys()
    {
        var sut = BuildSut();

        var services = await sut.ResolveAsync("did:example:bob");

        services.Should().ContainSingle();
        services[0].Uri.Should().Be("http://example.com/path");
        services[0].RoutingKeys.Should().ContainSingle().Which.Should().Be("did:example:mediator1#key-x25519-1");
        services[0].Accept.Should().Contain(new[] { "didcomm/v2", "didcomm/aip2;env=rfc587" });
    }

    [Fact]
    public async Task Mediator2_publishes_its_own_DIDCommMessaging_endpoint()
    {
        var sut = BuildSut();

        var services = await sut.ResolveAsync("did:example:mediator2");

        services.Should().ContainSingle();
        services[0].Uri.Should().Be("http://example.com/path");
        services[0].RoutingKeys.Should().ContainSingle().Which.Should().Be("did:example:mediator1#key-x25519-1");
    }

    [Fact]
    public async Task Mediator1_has_no_service_block_so_the_resolver_returns_empty()
    {
        var sut = BuildSut();

        var services = await sut.ResolveAsync("did:example:mediator1");

        services.Should().BeEmpty();
    }

    [Fact]
    public async Task Charlie_carries_a_mediator_as_DID_endpoint_for_FR_ROUTE_04()
    {
        var sut = BuildSut();

        var services = await sut.ResolveAsync("did:example:charlie");

        services.Should().ContainSingle();
        // FR-ROUTE-04: the URI may itself be a DID — Checkpoint C expands this further.
        services[0].Uri.Should().Be("did:example:mediator2");
        services[0].RoutingKeys.Should().ContainSingle().Which.Should().Be("did:example:mediator1#key-x25519-1");
    }

    private static IServiceEndpointResolver BuildSut()
    {
        // Construct the in-memory resolver from explicit files because the spec/ directory
        // contains BOTH bob.json (Phase 3, no service) and bob-with-routing.json — they
        // share the did:example:bob subject and a directory scan would collide on key.
        var docs = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
        foreach (var fileName in new[] { "bob-with-routing.json", "mediator1.json", "mediator2.json", "charlie.json" })
        {
            var path = Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec", fileName);
            var doc = DidDocumentSerializer.Deserialize(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"DID Document at '{path}' deserialised to null.");
            docs[doc.Id.Value!] = doc;
        }

        var resolver = new FixtureDidResolver(docs);
        var keyService = new NetDidKeyService(resolver);
        var options = new DidCommOptions();
        return new NetDidServiceEndpointResolver(resolver, keyService, options);
    }
}
