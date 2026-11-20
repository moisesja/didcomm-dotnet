using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Resolution;
using AwesomeAssertions;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Core.Serialization;
using Xunit;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Resolution;

/// <summary>
/// FR-CONSIST-09: a resolver that answers a request for Bob's DID with a document whose <c>id</c>
/// is another subject. Both net-did adapters refuse it — the key service (it will not attribute
/// another subject's keys to Bob) and the service-endpoint resolver (it will not hand another
/// subject's endpoint to routing as Bob's) — with the same check and the same text. Two forms
/// (AGENTS.md §4):
/// - tamper: Bob's genuine document with only its <c>id</c> changed to Mallory's DID;
/// - self-consistent forgery: Mallory's own, internally consistent document (id, keys and service
///   all Mallory's) returned for Bob's DID.
/// </summary>
public sealed class ResolvedDocumentIdentityTests
{
    private const string Bob = "did:example:bob";
    private const string Mallory = "did:example:mallory";
    private const string BobUri = "https://bob.example/didcomm";
    private const string MalloryUri = "https://mallory.example/collect";

    private const string ForeignDocumentText =
        "DID resolution failed for 'did:example:bob': resolver returned a document whose id is 'did:example:mallory'; refusing to attribute another subject's document to 'did:example:bob'.";

    public static TheoryData<string> Forms => new() { "tamper", "forgery" };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task The_service_endpoint_resolver_refuses_a_document_whose_id_is_another_subject(string form)
    {
        var resolver = ResolverAnswering(form);
        var sut = new NetDidServiceEndpointResolver(resolver, new NetDidKeyService(resolver), new DidCommOptions());

        var thrown = await Record.ExceptionAsync(() => sut.ResolveAsync(Bob));

        thrown.Should().BeOfType<DidResolutionException>(
            "a document whose id is '{0}' does not describe '{1}', so its services are not routed to as Bob's", Mallory, Bob)
            .Which.Message.Should().Be(ForeignDocumentText);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task The_key_service_refuses_the_same_answer_for_every_relationship(string form)
    {
        var sut = new NetDidKeyService(ResolverAnswering(form));

        var keyAgreement = await Record.ExceptionAsync(() => sut.GetVerificationMethodsAsync(Bob, VerificationRelationship.KeyAgreement));
        var authentication = await Record.ExceptionAsync(() => sut.GetVerificationMethodsAsync(Bob, VerificationRelationship.Authentication));
        var binding = await Record.ExceptionAsync(() => sut.ResolveKeyBindingAsync(Bob + "#ka-1", VerificationRelationship.KeyAgreement));

        keyAgreement.Should().BeOfType<DidResolutionException>().Which.Message.Should().Be(ForeignDocumentText);
        authentication.Should().BeOfType<DidResolutionException>().Which.Message.Should().Be(ForeignDocumentText);
        binding.Should().BeOfType<DidResolutionException>().Which.Message.Should().Be(ForeignDocumentText);
    }

    /// <summary>
    /// The refusal comes before any service is read: a foreign document whose service list cannot
    /// even be read is refused for its <c>id</c>, not failed by the list.
    /// </summary>
    [Fact]
    public async Task The_service_endpoint_resolver_refuses_a_foreign_document_before_reading_its_services()
    {
        var poisoned = new DidDocument { Id = new Did(Mallory), Service = new UnreadableServices() };
        var resolver = new MapResolver((Bob, poisoned));
        var sut = new NetDidServiceEndpointResolver(resolver, new NetDidKeyService(resolver), new DidCommOptions());

        var thrown = await Record.ExceptionAsync(() => sut.ResolveAsync(Bob));

        thrown.Should().BeOfType<DidResolutionException>().Which.Message.Should().Be(ForeignDocumentText);
    }

    [Fact]
    public async Task A_genuine_document_still_resolves_to_its_own_endpoint_and_keys()
    {
        var resolver = new MapResolver((Bob, BobDocument(Bob)));
        var keys = new NetDidKeyService(resolver);
        var sut = new NetDidServiceEndpointResolver(resolver, keys, new DidCommOptions());

        var services = await sut.ResolveAsync(Bob);
        var keyAgreement = await keys.GetVerificationMethodsAsync(Bob, VerificationRelationship.KeyAgreement);

        services.Should().ContainSingle().Which.Uri.Should().Be(BobUri);
        keyAgreement.Should().ContainSingle();
    }

    /// <summary>
    /// The two adapters answer a resolver that returned no document with the same text as before:
    /// the resolver's own error when it gave one, "resolver returned no document" otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_adapters_refuse_an_answer_without_a_document_with_the_existing_text(bool withError)
    {
        var answer = withError
            ? DidResolutionResult.NotFound(Bob)
            : new DidResolutionResult { DidDocument = null, ResolutionMetadata = new DidResolutionMetadata() };
        var expected = withError
            ? $"DID resolution failed for '{Bob}': {answer.ResolutionMetadata!.Error}."
            : $"DID resolution failed for '{Bob}': resolver returned no document.";
        var resolver = new FixedResolver(answer);
        var keys = new NetDidKeyService(resolver);
        var sut = new NetDidServiceEndpointResolver(resolver, keys, new DidCommOptions());

        var fromServices = await Record.ExceptionAsync(() => sut.ResolveAsync(Bob));
        var fromKeys = await Record.ExceptionAsync(() => keys.GetVerificationMethodsAsync(Bob, VerificationRelationship.KeyAgreement));

        if (withError)
            answer.ResolutionMetadata!.Error.Should().NotBeNullOrEmpty("the not-found answer names its error");
        fromServices.Should().BeOfType<DidResolutionException>().Which.Message.Should().Be(expected);
        fromKeys.Should().BeOfType<DidResolutionException>().Which.Message.Should().Be(expected);
    }

    private static MapResolver ResolverAnswering(string form) => form switch
    {
        // Bob's genuine document, one field changed after the fact: the id.
        "tamper" => new MapResolver((Bob, BobDocument(Mallory))),
        // Mallory's own consistent document, served for Bob's DID.
        "forgery" => new MapResolver((Bob, MalloryDocument())),
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    private static DidDocument BobDocument(string id) => Document(id, Bob, BobUri);

    private static DidDocument MalloryDocument() => Document(Mallory, Mallory, MalloryUri);

    /// <summary>A minimal document: one X25519 keyAgreement and one Ed25519 authentication key owned by
    /// <paramref name="keyOwner"/>, one DIDCommMessaging service at <paramref name="uri"/>.</summary>
    private static DidDocument Document(string id, string keyOwner, string uri)
    {
        var json = $$"""
        {
          "id": "{{id}}",
          "verificationMethod": [
            { "id": "{{keyOwner}}#ka-1", "type": "JsonWebKey2020", "controller": "{{keyOwner}}",
              "publicKeyJwk": { "kty": "OKP", "crv": "X25519", "x": "BIiFcQEn3dfvB2pjlhOQQour6jXy9d5s2FKEJNTOJik" } },
            { "id": "{{keyOwner}}#auth-1", "type": "JsonWebKey2020", "controller": "{{keyOwner}}",
              "publicKeyJwk": { "kty": "OKP", "crv": "Ed25519", "x": "G-boxFB6vOZBu-wXkm-9Lh79I8nf9Z50cILaOgKKGww" } }
          ],
          "keyAgreement": [ "{{keyOwner}}#ka-1" ],
          "authentication": [ "{{keyOwner}}#auth-1" ],
          "service": [
            { "id": "{{id}}#didcomm", "type": "DIDCommMessaging",
              "serviceEndpoint": { "uri": "{{uri}}", "accept": [ "didcomm/v2" ] } }
          ]
        }
        """;
        return DidDocumentSerializer.Deserialize(json)
            ?? throw new InvalidOperationException("fixture document deserialised to null");
    }

    private sealed class MapResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _docs = new(StringComparer.Ordinal);

        public MapResolver(params (string Did, DidDocument Doc)[] entries)
        {
            foreach (var (did, doc) in entries)
                _docs[did] = doc;
        }

        public bool CanResolve(string did) => _docs.ContainsKey(did);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(_docs.TryGetValue(did, out var doc)
                ? new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did));
    }

    private sealed class FixedResolver : IDidResolver
    {
        private readonly DidResolutionResult _answer;

        public FixedResolver(DidResolutionResult answer) => _answer = answer;

        public bool CanResolve(string did) => true;

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(_answer);
    }

    /// <summary>A service list that fails every read, standing in for services that must not be read.</summary>
    private sealed class UnreadableServices : IReadOnlyList<Service>
    {
        public Service this[int index] => throw new InvalidOperationException("a service was read");

        public int Count => throw new InvalidOperationException("a service was read");

        public IEnumerator<Service> GetEnumerator() => throw new InvalidOperationException("a service was read");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
