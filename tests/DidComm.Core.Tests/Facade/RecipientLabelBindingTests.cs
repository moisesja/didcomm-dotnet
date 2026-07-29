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
            .Should().ThrowAsync<CryptoException>();
    }

    [Fact]
    public async Task SwappedRecipientLabels_HeldAndUnheldFailIdentically_NoCustodyOracle()
    {
        // The rejection must not tell a peer whether we hold a recipient key: a holder reaches the
        // label check (their key opened an entry) while a non-holder fails in the decoy path. Both
        // must surface the SAME exception type and message, or the category itself is the oracle
        // that the constant-work decrypt path exists to close.
        var (packed, bobKa, resolver) = await PackToBothAndSwapLabels();

        var holder = new DidCommClient(
            new DictionarySecretsLookup(new[] { bobKa.PrivateJwk }),
            new NetDidKeyService(resolver),
            new DidCommOptions());
        var stranger = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions());

        var holderFailure = (await holder.Invoking(c => c.UnpackAsync(packed)).Should().ThrowAsync<CryptoException>()).Which;
        var strangerFailure = (await stranger.Invoking(c => c.UnpackAsync(packed)).Should().ThrowAsync<CryptoException>()).Which;

        holderFailure.GetType().Should().Be(strangerFailure.GetType());
        holderFailure.Message.Should().Be(strangerFailure.Message);
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

    [Fact]
    public async Task OurOwnKidRotatedToNewMaterial_MessageStillUnpacks_ButNoRecipientProvenance()
    {
        // Our document rotates kid K from KA to KB while we still hold KA. A message encrypted to KA
        // decrypts fine and must keep working (rotation grace) — but the key the DOCUMENT now
        // publishes for K is not the key that decrypted, so attesting KB's thumbprint would be a
        // false statement about which key opened this envelope.
        var oldKey = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var newKey = TestKeyMaterial.Generate(KeyType.X25519, BobKid);

        var senderResolver = new StaticResolver((Bob, Doc(Bob, oldKey.PublicJwk)));
        var sender = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(senderResolver),
            new DidCommOptions());

        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithTo(Bob)
            .WithBody(JsonNode.Parse("""{"v":1}""")!.AsObject())
            .Build();
        var packed = (await sender.PackEncryptedAsync(
            message, new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        // Post-rotation document: same kid, replacement key material.
        var rotatedResolver = new StaticResolver((Bob, Doc(Bob, newKey.PublicJwk)));
        var bob = new DidCommClient(
            new DictionarySecretsLookup(new[] { oldKey.PrivateJwk }),
            new NetDidKeyService(rotatedResolver),
            new DidCommOptions());

        var result = await bob.UnpackAsync(packed);

        result.RecipientKid.Should().Be(BobKid);
        result.RecipientKeyBinding.Should().BeNull(
            "the resolved key is not the key that decrypted, so no recipient provenance may be claimed");
    }

    [Fact]
    public async Task OpaqueResolverWithoutPublicIdentity_YieldsNoRecipientProvenance()
    {
        // A custom resolver that exposes no public material for a held kid cannot prove the resolved
        // key is the one that decrypted, so it gets no recipient binding rather than an unproven one.
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var resolver = new StaticResolver((Bob, Doc(Bob, bobKa.PublicJwk)));

        var sender = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions());
        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithTo(Bob)
            .WithBody(JsonNode.Parse("""{"v":1}""")!.AsObject())
            .Build();
        var packed = (await sender.PackEncryptedAsync(
            message, new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        var bob = new DidCommClient(
            new PublicIdentityHidingSecrets(bobKa.PrivateJwk),
            new NetDidKeyService(resolver),
            new DidCommOptions());

        var result = await bob.UnpackAsync(packed);

        result.RecipientKid.Should().Be(BobKid);
        result.RecipientKeyBinding.Should().BeNull();
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

    /// <summary>
    /// Holds a key for decryption but exposes no public identity — models an opaque resolver whose
    /// backing store answers "held" and derives, without surfacing key material at all.
    /// </summary>
    private sealed class PublicIdentityHidingSecrets : ISecretsResolver, IOpaqueKeyResolver
    {
        private readonly Jwk _privateJwk;
        public PublicIdentityHidingSecrets(Jwk privateJwk) => _privateJwk = privateJwk;

        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => Task.FromResult<Jwk?>(null);

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                kids.Where(k => string.Equals(k, _privateJwk.Kid, StringComparison.Ordinal)).ToArray());

        public Task<NetCrypto.ISigner?> ResolveSignerAsync(string kid, CancellationToken ct = default)
            => Task.FromResult<NetCrypto.ISigner?>(null);

        public Task<DataProofsDotnet.Jose.Encryption.IEcdhKey?> ResolveKeyAgreementAsync(string kid, CancellationToken ct = default)
        {
            if (!string.Equals(kid, _privateJwk.Kid, StringComparison.Ordinal))
                return Task.FromResult<DataProofsDotnet.Jose.Encryption.IEcdhKey?>(null);
            var scalar = DataProofsDotnet.Jose.Base64Url.Decode(_privateJwk.D!);
            return Task.FromResult<DataProofsDotnet.Jose.Encryption.IEcdhKey?>(
                new DataProofsDotnet.Jose.Encryption.RawEcdhKey(_privateJwk.Crv!, scalar, new DataProofsDotnet.Jose.JoseCryptoProvider()));
        }
    }

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
