using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Facade;

/// <summary>
/// The recipient kid a JWE reports is the <em>label</em> of whichever recipient entry the derived
/// KEK opened, and those labels are attacker-authored: <c>apv</c> commits only to the sorted kid
/// list, so swapping two entries' labels leaves the envelope cryptographically intact. Without a
/// check, an envelope whose real key-wrap is labelled with another DID's kid decrypts with our key
/// while reporting someone else's — which the new <c>RecipientKeyBinding</c> would then describe as
/// the key that decrypted. These tests pin the fail-closed label check (#56 review finding).
/// </summary>
public sealed class RecipientLabelBindingTests
{
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";
    private const string BobKid = "did:example:bob#ka-1";
    private const string CarolKid = "did:example:carol#ka-1";

    [Fact]
    public async Task SwappedRecipientLabels_DecryptedEntryMislabelled_Rejected()
    {
        var (packed, bobKa, resolver) = await PackToBothAndSwapLabels();

        var bob = new DidCommClient(
            new DictionarySecretsLookup(new[] { bobKa.PrivateJwk }),
            new NetDidKeyService(resolver),
            new DidCommOptions());

        await bob.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<ConsistencyException>()
            .WithMessage("*labelled with a kid other than the one whose key agreement opened it*");
    }

    [Fact]
    public async Task UnswappedMultiRecipientEnvelope_StillUnpacks_BindingNamesOurOwnKey()
    {
        // Control for the test above: the identical two-recipient envelope, labels untouched,
        // must still unpack and bind to the key that actually decrypted.
        var (packed, bobKa, resolver) = await PackToBoth();

        var bob = new DidCommClient(
            new DictionarySecretsLookup(new[] { bobKa.PrivateJwk }),
            new NetDidKeyService(resolver),
            new DidCommOptions());

        var result = await bob.UnpackAsync(packed);

        result.RecipientKid.Should().Be(BobKid);
        result.RecipientKeyBinding.Should().NotBeNull();
        result.RecipientKeyBinding!.Kid.Should().Be(BobKid);
        result.RecipientKeyBinding.Did.Should().Be(Bob);
        result.RecipientKeyBinding.AuthorizedForDid.Should().Be(Bob);
    }

    private static async Task<(string Packed, TestKeyMaterial BobKa, IDidResolver Resolver)> PackToBoth()
    {
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carolKa = TestKeyMaterial.Generate(KeyType.X25519, CarolKid);
        var resolver = new StaticResolver(
            (Bob, Doc(Bob, bobKa.PublicJwk)),
            (Carol, Doc(Carol, carolKa.PublicJwk)));

        var sender = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions());

        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithTo(Bob, Carol)
            .WithBody(JsonNode.Parse("""{"v":1}""")!.AsObject())
            .Build();

        var packed = (await sender.PackEncryptedAsync(
            message,
            new PackEncryptedOptions(Recipients: new[] { Bob, Carol }))).Message;

        return (packed, bobKa, resolver);
    }

    private static async Task<(string Packed, TestKeyMaterial BobKa, IDidResolver Resolver)> PackToBothAndSwapLabels()
    {
        var (packed, bobKa, resolver) = await PackToBoth();

        // Swap the two recipients' kid labels, leaving every encrypted_key in place. The protected
        // header (and therefore apv, computed over the SORTED kid list) is untouched and still
        // valid, so the envelope remains cryptographically well-formed.
        var jwe = JsonNode.Parse(packed)!.AsObject();
        var recipients = jwe["recipients"]!.AsArray();
        recipients.Should().HaveCount(2);
        var first = recipients[0]!["header"]!["kid"]!.GetValue<string>();
        var second = recipients[1]!["header"]!["kid"]!.GetValue<string>();
        recipients[0]!["header"]!["kid"] = second;
        recipients[1]!["header"]!["kid"] = first;

        return (jwe.ToJsonString(), bobKa, resolver);
    }

    private static DidDocument Doc(string did, Jwk keyAgreementKey)
    {
        var vm = new VerificationMethod
        {
            Id = keyAgreementKey.Kid!,
            Type = "JsonWebKey2020",
            Controller = new Did(did),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = keyAgreementKey.Kty,
                Crv = keyAgreementKey.Crv,
                X = keyAgreementKey.X,
            },
        };
        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
    }

    private sealed class StaticResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _docs;

        public StaticResolver(params (string Did, DidDocument Doc)[] entries)
            => _docs = entries.ToDictionary(e => e.Did, e => e.Doc, StringComparer.Ordinal);

        public bool CanResolve(string did) => _docs.ContainsKey(did);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(_docs.TryGetValue(did, out var doc)
                ? new DidResolutionResult { DidDocument = doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did));
    }
}
