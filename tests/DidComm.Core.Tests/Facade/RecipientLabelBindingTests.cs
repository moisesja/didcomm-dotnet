using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
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
    private const string AliasDid = "did:example:bobalias";
    private const string AliasKid = "did:example:bobalias#ka-1";

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameKeyMaterialUnderTwoDids_ResolverOrderIndependent_StillUnpacks(bool reverseFindPresent)
    {
        // One key published by two DIDs (a legitimate alias) means both recipient entries wrap to
        // the SAME KEK, so the parser reports whichever entry comes FIRST in the envelope.
        // ISecretsResolver promises only a subset from FindPresentAsync, not an order, so a keystore
        // that returns the alias first must not make the label check reject a legitimate message —
        // the reader selects in envelope order to stay in step with the parser's scan.
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var aliasPrivate = CloneJwk(bobKa.PrivateJwk, AliasKid);
        var aliasPublic = CloneJwk(bobKa.PublicJwk, AliasKid);
        var resolver = new StaticResolver(
            (Bob, Doc(Bob, bobKa.PublicJwk)),
            (AliasDid, Doc(AliasDid, aliasPublic)));

        var sender = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions());

        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithTo(Bob, AliasDid)
            .WithBody(JsonNode.Parse("""{"v":1}""")!.AsObject())
            .Build();

        var packed = (await sender.PackEncryptedAsync(
            message,
            new PackEncryptedOptions(Recipients: new[] { Bob, AliasDid }))).Message;

        var secrets = new OrderedSecretsLookup(new[] { bobKa.PrivateJwk, aliasPrivate }, reverseFindPresent);
        var bob = new DidCommClient(secrets, new NetDidKeyService(resolver), new DidCommOptions());

        var result = await bob.UnpackAsync(packed);

        result.RecipientKid.Should().Be(BobKid, "the parser reports the first envelope entry the KEK opens");
        result.RecipientKeyBinding!.Kid.Should().Be(BobKid);
    }

    private static Jwk CloneJwk(Jwk source, string kid) => new()
    {
        Kty = source.Kty,
        Crv = source.Crv,
        X = source.X,
        Y = source.Y,
        D = source.D,
        Kid = kid,
        Alg = source.Alg,
        Use = source.Use,
    };

    /// <summary>Secrets resolver whose <c>FindPresentAsync</c> can return hits in a non-envelope order.</summary>
    private sealed class OrderedSecretsLookup : ISecretsResolver
    {
        private readonly Dictionary<string, Jwk> _byKid;
        private readonly bool _reverse;

        public OrderedSecretsLookup(IEnumerable<Jwk> privateJwks, bool reverseFindPresent)
        {
            _byKid = privateJwks.ToDictionary(j => j.Kid!, StringComparer.Ordinal);
            _reverse = reverseFindPresent;
        }

        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => Task.FromResult(_byKid.GetValueOrDefault(kid));

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        {
            var hits = kids.Where(_byKid.ContainsKey).ToList();
            if (_reverse)
                hits.Reverse();
            return Task.FromResult<IReadOnlyList<string>>(hits);
        }
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
