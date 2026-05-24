using System.Text;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using FluentAssertions;
using NetDid.Core.Model;
using NetDid.Core.Serialization;
using Xunit;

namespace DidComm.InteropTests.Routing;

/// <summary>
/// Phase 4 Checkpoint F — end-to-end FR-ROUTE-02/05 round-trip: Alice authcrypts a message
/// for Bob with <c>Forward = true</c>; mediator1 receives, unwraps via
/// <see cref="ForwardProcessor"/>, and the bytes it emits are unpackable by Bob into the
/// original plaintext. Uses real Appendix A keys + the Phase 4 vendored DID Documents.
/// </summary>
public sealed class AliceMediatorBobRoundTripTests
{
    [Fact]
    public async Task Alice_to_Bob_via_mediator1_routes_round_trips_through_processor()
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadRoutingResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());

        // Alice packs an authcrypt-for-Bob with Forward=true. The facade resolves bob's
        // service entry (uri = http://example.com/path, routingKeys = [mediator1#kx-1]) and
        // wraps a single forward layer for mediator1.
        var aliceClient = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
        var originalMessage = NewProposal();
        var packResult = await aliceClient.PackEncryptedAsync(originalMessage, new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            Forward: true));

        packResult.ServiceEndpoint.Should().Be("http://example.com/path");

        // mediator1 receives. Build a DidCommClient configured with the same secrets (the
        // shared registry holds the mediator1 X25519 private key). Run the ForwardProcessor.
        var mediatorClient = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
        var processor = new ForwardProcessor(mediatorClient, keyService, new ForwardProcessorOptions());
        var processed = await processor.ProcessAsync(packResult.Message);

        processed.NextHop.Should().Be("did:example:bob");

        // Bob receives the onward bytes (a JWE addressed to bob#key-x25519-1) and unpacks it
        // back to the original plaintext, with authcrypt metadata preserved across the route.
        var bobClient = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
        var bobUnpack = await bobClient.UnpackAsync(Encoding.UTF8.GetString(processed.OnwardPacked));

        bobUnpack.Message.From.Should().Be("did:example:alice");
        bobUnpack.Message.To.Should().Contain("did:example:bob");
        bobUnpack.Message.Type.Should().Be(originalMessage.Type);
        bobUnpack.Authenticated.Should().BeTrue("Alice's authcrypt sender remains visible to Bob after the mediator strips the forward layer");
        bobUnpack.SenderKid.Should().StartWith("did:example:alice#");
    }

    [Fact]
    public async Task Forward_true_against_a_recipient_with_no_service_block_raises_DidResolutionException()
    {
        var actors = SpecActorRegistry.LoadDefault();
        // bob.json (Phase 3) carries no service entries; the resolver returns empty.
        var resolver = LoadPlainBobResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        var client = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());

        var act = async () => await client.PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            Forward: true));

        await act.Should().ThrowAsync<DidComm.Exceptions.DidResolutionException>();
    }

    private static FixtureDidResolver LoadRoutingResolver() =>
        LoadResolverFromFiles("alice.json", "bob-with-routing.json", "mediator1.json", "mediator2.json", "charlie.json");

    private static FixtureDidResolver LoadPlainBobResolver() =>
        LoadResolverFromFiles("alice.json", "bob.json");

    private static FixtureDidResolver LoadResolverFromFiles(params string[] fileNames)
    {
        var docs = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec", fileName);
            var doc = DidDocumentSerializer.Deserialize(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"DID Document at '{path}' deserialised to null.");
            docs[doc.Id.Value!] = doc;
        }
        return new FixtureDidResolver(docs);
    }

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();
}
