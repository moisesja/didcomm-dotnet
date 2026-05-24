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
/// Phase 4 Checkpoint D — sender-side forward wrapping (FR-ROUTE-02) against the vendored
/// Appendix A secrets + bob-with-routing / mediator1 / mediator2 / charlie DID documents.
/// Endpoint Example 1 (uri = DID, no recipient routingKeys) exercised via charlie's document;
/// Endpoint Example 2 (recipient routingKeys + mediator-as-endpoint) exercised end-to-end by
/// inspecting the outermost forward layer.
/// </summary>
public sealed class SenderForwardWrappingTests
{
    private static readonly Lazy<SpecActorRegistry> Actors = new(SpecActorRegistry.LoadDefault);

    [Fact]
    public async Task BobWithRouting_wraps_one_forward_layer_to_mediator1_outermost()
    {
        var client = BuildClient();
        var message = NewProposal(to: "did:example:bob");

        var result = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" },
            Forward: true));

        result.ServiceEndpoint.Should().Be("http://example.com/path");
        result.FallbackServiceEndpoints.Should().BeEmpty();
        result.Message.Should().NotBeNullOrEmpty();

        // The outermost layer must be a forward addressed to the recipient's routing key (mediator1).
        var outerForward = UnwrapForwardMessage(result.Message, recipientKid: "did:example:mediator1#key-x25519-1");
        outerForward.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        outerForward.Body!["next"]!.GetValue<string>().Should().Be("did:example:bob");
        outerForward.Attachments.Should().ContainSingle("the forward wraps the inner JWE as a single attachment");
    }

    [Fact]
    public async Task Charlie_uses_mediator2_as_DID_endpoint_with_two_forward_layers()
    {
        var client = BuildClient();
        var message = NewProposal(to: "did:example:charlie");

        var result = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { "did:example:charlie" },
            Forward: true));

        // Mediator2 publishes a plain transport URI (FR-ROUTE-04 mediator-as-endpoint resolved).
        result.ServiceEndpoint.Should().Be("http://example.com/path");

        // Outer wrap: mediator2's prepended keyAgreement (FR-ROUTE-04 prepend).
        var outer = UnwrapForwardMessage(result.Message, recipientKid: "did:example:mediator2#key-x25519-1");
        outer.Type.Should().Be(ForwardConstants.ForwardTypeUri);
        // body.next on the outer wrap is the kid one hop inward — i.e. the next routing key,
        // which is charlie's routingKeys entry: did:example:mediator1#key-x25519-1.
        outer.Body!["next"]!.GetValue<string>().Should().Be("did:example:mediator1#key-x25519-1");

        // Inner wrap: charlie's routingKeys entry (mediator1).
        var innerPacked = outer.Attachments![0].Data.Json!.ToJsonString();
        var inner = UnwrapForwardMessage(innerPacked, recipientKid: "did:example:mediator1#key-x25519-1");
        inner.Body!["next"]!.GetValue<string>().Should().Be("did:example:charlie");
    }

    [Fact]
    public async Task Recipient_without_DIDCommMessaging_service_raises_when_Forward_true()
    {
        // Plain Bob (bob.json — Phase 3 fixture, no service block) has no routing service.
        // Build a client whose service resolver only knows about plain bob.
        var client = BuildClient(useBobWithoutRouting: true);
        var message = NewProposal(to: "did:example:bob");

        var act = async () => await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" },
            Forward: true));

        await act.Should().ThrowAsync<Exception>("either DidResolutionException (no DIDCommMessaging service) per Checkpoint C");
    }

    [Fact]
    public async Task Forward_false_does_not_consult_the_service_resolver()
    {
        var client = BuildClient();
        var message = NewProposal(to: "did:example:bob");

        var result = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" },
            Forward: false));

        result.ServiceEndpoint.Should().BeNull();
        result.Message.Should().NotBeNullOrEmpty();
    }

    private static DidCommClient BuildClient(bool useBobWithoutRouting = false)
    {
        var docs = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
        var bobFile = useBobWithoutRouting ? "bob.json" : "bob-with-routing.json";
        foreach (var fileName in new[] { "alice.json", bobFile, "mediator1.json", "mediator2.json", "charlie.json" })
        {
            var path = Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec", fileName);
            var doc = DidDocumentSerializer.Deserialize(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"DID Document at '{path}' deserialised to null.");
            docs[doc.Id.Value!] = doc;
        }

        var resolver = new FixtureDidResolver(docs);
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        return new DidCommClient(Actors.Value.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
    }

    private static Message NewProposal(string to) => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo(to)
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();

    /// <summary>
    /// Decrypt a single forward layer using the routing key whose <paramref name="recipientKid"/>
    /// we expect to address. Returns the unpacked plaintext forward message.
    /// </summary>
    private static Message UnwrapForwardMessage(string packed, string recipientKid)
    {
        // The mediator owning `recipientKid` would use UnpackAsync to decrypt — short-circuit that
        // here by spinning up a DidCommClient configured for the mediator's DID.
        var client = BuildClient(); // mediator secrets are inside the same registry.
        var unpack = client.UnpackAsync(packed).GetAwaiter().GetResult();
        unpack.RecipientKid.Should().Be(recipientKid);
        return unpack.Message;
    }
}
