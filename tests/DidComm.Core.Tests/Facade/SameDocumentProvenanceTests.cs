using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using DpJwkThumbprint = DataProofsDotnet.Jose.JwkThumbprint;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Facade;

/// <summary>
/// End-to-end acceptance tests for issue #56 — same-document key provenance on unpack. The
/// unpack pipeline must never authorize a sender/signer against a different DID-document
/// version than the one that supplied the key the JOSE layer verified/decrypted with, and it
/// must surface the exact evidence (kid / DID / controller / relationship / key thumbprint)
/// captured from that single resolution.
/// </summary>
public sealed class SameDocumentProvenanceTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string AliceAuthKid = "did:example:alice#auth-1";
    private const string AliceKaKid = "did:example:alice#ka-1";
    private const string BobKaKid = "did:example:bob#ka-1";

    // ---------------------------------------------------------------------------------------
    // Acceptance: rotated key under the same kid (signed). The resolver serves document A (KA)
    // at crypto time and document B (same kid, different key KB) afterwards. v1.3.0 verified
    // with KA and then authorized against B — a splice of two documents. Now sender/signer
    // authority is never re-resolved: exactly one resolution, and the surfaced evidence pins
    // the exact key that verified.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Signed_RotatedKidSameName_SecondDocumentNeverConsulted()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var keyB = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var docA = Doc(Alice, Auth(keyA.PublicJwk));
        var docB = Doc(Alice, Auth(keyB.PublicJwk));

        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, docA, docB); // first resolution sees A, any later one would see B
        var recipient = Client(resolver);

        var result = await recipient.UnpackAsync(packed);

        result.Authenticated.Should().BeTrue();
        result.NonRepudiation.Should().BeTrue();
        resolver.CountFor(Alice).Should().Be(1, "sender/signer authority must not be re-resolved after crypto (#56)");
        result.SignerKeyBinding.Should().NotBeNull();
        result.SignerKeyBinding!.PublicKeyThumbprint.Should().Be(
            DpJwkThumbprint.ComputeBase64Url(keyA.PublicJwk),
            "the evidence must pin the exact key that verified the signature, not document B's replacement");
        result.SignerKeyBinding.Did.Should().Be(Alice);
        result.SignerKeyBinding.Relationship.Should().Be(VerificationRelationship.Authentication);
    }

    [Fact]
    public async Task Signed_ResolverAlreadyRotated_StaleEnvelopeFailsClosed()
    {
        // Inverse ordering: the resolver already serves document B (KB) when the envelope —
        // signed with the rotated-out KA — arrives. The one document consulted cannot verify
        // the signature, so the envelope is rejected outright.
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var keyB = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);

        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyB.PublicJwk)));
        var recipient = Client(resolver);

        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<CryptoException>();
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: the equivalent authcrypt A→B race.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Authcrypt_RotatedKidSameName_SecondDocumentNeverConsulted()
    {
        var aliceKaA = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var aliceKaB = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var bobDoc = Doc(Bob, Ka(bobKa.PublicJwk));

        var packed = await PackAuthcrypt(aliceKaA, bobDoc);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Ka(aliceKaA.PublicJwk)), Doc(Alice, Ka(aliceKaB.PublicJwk)));
        resolver.SetSequence(Bob, bobDoc);
        var recipient = Client(resolver, bobKa.PrivateJwk);

        var result = await recipient.UnpackAsync(packed);

        result.Authenticated.Should().BeTrue();
        resolver.CountFor(Alice).Should().Be(1, "authcrypt sender authority must not be re-resolved after decrypt (#56)");
        result.SenderKeyBinding.Should().NotBeNull();
        result.SenderKeyBinding!.PublicKeyThumbprint.Should().Be(DpJwkThumbprint.ComputeBase64Url(aliceKaA.PublicJwk));
        result.SenderKeyBinding.Relationship.Should().Be(VerificationRelationship.KeyAgreement);
        result.RecipientKeyBinding.Should().NotBeNull();
        result.RecipientKeyBinding!.Kid.Should().Be(BobKaKid);
        resolver.CountFor(Bob).Should().Be(1, "recipient evidence is resolved exactly once, after the decrypting kid is known");
    }

    [Fact]
    public async Task Authcrypt_ResolverAlreadyRotated_StaleEnvelopeFailsClosed()
    {
        var aliceKaA = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var aliceKaB = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var bobDoc = Doc(Bob, Ka(bobKa.PublicJwk));

        var packed = await PackAuthcrypt(aliceKaA, bobDoc);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Ka(aliceKaB.PublicJwk)));
        resolver.SetSequence(Bob, bobDoc);
        var recipient = Client(resolver, bobKa.PrivateJwk);

        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<CryptoException>();
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: key/controller splicing across document versions. Document A (which supplied
    // the crypto key) says the key is controlled by eve; document B (which v1.3.0 would have
    // authorized against) says alice. Authorization must run against A's evidence — the splice
    // can no longer produce an authenticated identity.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Authcrypt_ControllerSpliceAcrossVersions_Rejected()
    {
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var bobDoc = Doc(Bob, Ka(bobKa.PublicJwk));

        var packed = await PackAuthcrypt(aliceKa, bobDoc);

        var resolver = new VersionedResolver();
        resolver.SetSequence(
            Alice,
            Doc(Alice, Ka(aliceKa.PublicJwk, controller: "did:example:eve")), // crypto-time document
            Doc(Alice, Ka(aliceKa.PublicJwk)));                               // would-be authorization document
        resolver.SetSequence(Bob, bobDoc);
        var recipient = Client(resolver, bobKa.PrivateJwk);

        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<ConsistencyException>();
        resolver.CountFor(Alice).Should().Be(1, "the favorable second document must never be consulted");
    }

    [Fact]
    public async Task Signed_ControllerSpliceAcrossVersions_Rejected()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);

        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(
            Alice,
            Doc(Alice, Auth(keyA.PublicJwk, controller: "did:example:eve")),
            Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);

        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<ConsistencyException>();
        resolver.CountFor(Alice).Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: duplicate / shadowed verification-method ids fail closed during unpack.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Signed_DuplicateKidInDocument_FailsClosed()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var shadow = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);

        var packed = await PackSigned(keyA);

        var doc = Doc(Alice, Auth(keyA.PublicJwk), Auth(shadow.PublicJwk));
        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, doc);
        var recipient = Client(resolver);

        // The ambiguous binding is rejected at the key-lookup seam; regardless of how the JOSE
        // layer wraps the lookup failure, the envelope must not unpack successfully.
        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<Exception>()
            .Where(e => e is DidResolutionException || e is CryptoException || e is MalformedMessageException);
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: recipient evidence — resolved once, fail-closed against the recipient's own
    // document (kid absent, or controlled by another DID).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Anoncrypt_RecipientKidAbsentFromOwnDocument_Rejected()
    {
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var goodBobDoc = Doc(Bob, Ka(bobKa.PublicJwk));

        var packed = await PackAnoncrypt(goodBobDoc);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Bob, Doc(Bob)); // no keyAgreement entries at unpack time
        var recipient = Client(resolver, bobKa.PrivateJwk);

        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<ConsistencyException>()
            .WithMessage("*FR-CONSIST-06*");
    }

    [Fact]
    public async Task Anoncrypt_RecipientKidControlledByAnotherDid_Rejected()
    {
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var goodBobDoc = Doc(Bob, Ka(bobKa.PublicJwk));

        var packed = await PackAnoncrypt(goodBobDoc);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Bob, Doc(Bob, Ka(bobKa.PublicJwk, controller: "did:example:eve")));
        var recipient = Client(resolver, bobKa.PrivateJwk);

        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<ConsistencyException>();
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: nested compositions preserve per-role bindings without collapsing flags.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AnoncryptOfSigned_SignerAndRecipientBindings_NoSenderBinding()
    {
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var aliceDoc = Doc(Alice, Auth(aliceAuth.PublicJwk));
        var bobDoc = Doc(Bob, Ka(bobKa.PublicJwk));

        var senderResolver = new VersionedResolver();
        senderResolver.SetSequence(Alice, aliceDoc);
        senderResolver.SetSequence(Bob, bobDoc);
        var sender = Client(senderResolver, aliceAuth.PrivateJwk);
        var packed = (await sender.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: new[] { Bob }, SignFrom: Alice))).Message;

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, aliceDoc);
        resolver.SetSequence(Bob, bobDoc);
        var recipient = Client(resolver, bobKa.PrivateJwk);

        var result = await recipient.UnpackAsync(packed);

        result.AnonymousSender.Should().BeTrue();
        result.Authenticated.Should().BeTrue("the inner signature authenticates the signer");
        result.SignerKeyBinding.Should().NotBeNull();
        result.SignerKeyBinding!.Relationship.Should().Be(VerificationRelationship.Authentication);
        result.SenderKeyBinding.Should().BeNull("no authcrypt layer was present");
        result.RecipientKeyBinding.Should().NotBeNull();
    }

    [Fact]
    public async Task ProtectedSender_AnoncryptOfAuthcryptOfSigned_AllBindingsPreserved()
    {
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var aliceDoc = Doc(Alice, Auth(aliceAuth.PublicJwk), Ka(aliceKa.PublicJwk));
        var bobDoc = Doc(Bob, Ka(bobKa.PublicJwk));

        var senderResolver = new VersionedResolver();
        senderResolver.SetSequence(Alice, aliceDoc);
        senderResolver.SetSequence(Bob, bobDoc);
        var sender = Client(senderResolver, aliceAuth.PrivateJwk, aliceKa.PrivateJwk);
        var packed = (await sender.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(
                Recipients: new[] { Bob },
                From: Alice,
                SignFrom: Alice,
                ProtectSender: true))).Message;

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, aliceDoc);
        resolver.SetSequence(Bob, bobDoc);
        var recipient = Client(resolver, bobKa.PrivateJwk);

        var result = await recipient.UnpackAsync(packed);

        result.AnonymousSender.Should().BeTrue("the outermost layer is anoncrypt (protected sender)");
        result.Authenticated.Should().BeTrue();
        result.NonRepudiation.Should().BeTrue();
        result.SenderKeyBinding.Should().NotBeNull();
        result.SenderKeyBinding!.PublicKeyThumbprint.Should().Be(DpJwkThumbprint.ComputeBase64Url(aliceKa.PublicJwk));
        result.SignerKeyBinding.Should().NotBeNull();
        result.SignerKeyBinding!.PublicKeyThumbprint.Should().Be(DpJwkThumbprint.ComputeBase64Url(aliceAuth.PublicJwk));
        result.RecipientKeyBinding.Should().NotBeNull();
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: signed message with no 'from' — key evidence is still surfaced so consumers
    // can distinguish "verified key/controller evidence exists" from "only a kid/flag".
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Signed_NoFromHeader_BindingEvidenceStillSurfaced()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA, NewMessage(from: null));

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);

        var result = await recipient.UnpackAsync(packed);

        result.Message.From.Should().BeNull();
        result.NonRepudiation.Should().BeTrue();
        result.SignerKeyBinding.Should().NotBeNull();
        result.SignerKeyBinding!.Did.Should().Be(Alice);
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: concurrent unpacks retain independent, correct evidence.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ConcurrentUnpacks_SameKid_IndependentBindings()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var doc = Doc(Alice, Auth(keyA.PublicJwk));
        var packed = await PackSigned(keyA);
        var expected = DpJwkThumbprint.ComputeBase64Url(keyA.PublicJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, doc);
        var recipient = Client(resolver);

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => recipient.UnpackAsync(packed)));

        results.Should().OnlyContain(r =>
            r.SignerKeyBinding != null && r.SignerKeyBinding.PublicKeyThumbprint == expected);
    }

    [Fact]
    public async Task SequentialRotation_EachUnpackPinsItsOwnDocumentVersion()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var keyB = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packedA = await PackSigned(keyA);
        var packedB = await PackSigned(keyB);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);
        var resultA = await recipient.UnpackAsync(packedA);

        resolver.SetSequence(Alice, Doc(Alice, Auth(keyB.PublicJwk))); // rotation happens
        var resultB = await recipient.UnpackAsync(packedB);

        resultA.SignerKeyBinding!.PublicKeyThumbprint.Should().Be(DpJwkThumbprint.ComputeBase64Url(keyA.PublicJwk));
        resultB.SignerKeyBinding!.PublicKeyThumbprint.Should().Be(DpJwkThumbprint.ComputeBase64Url(keyB.PublicJwk));
        resultA.SignerKeyBinding.PublicKeyThumbprint.Should().NotBe(resultB.SignerKeyBinding.PublicKeyThumbprint,
            "each operation's evidence is scoped to that operation");
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: legacy custom IDidKeyService (no capability) — source/binary compatible,
    // no strong binding, pre-existing two-resolution checks still enforced.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task LegacyKeyService_NoCapability_NoBindings_UnpackStillSucceeds()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var legacy = new LegacyKeyServiceDecorator(new NetDidKeyService(resolver));
        var recipient = new DidCommClient(new DictionarySecretsLookup(Array.Empty<Jwk>()), legacy, new DidCommOptions());

        var result = await recipient.UnpackAsync(packed);

        result.NonRepudiation.Should().BeTrue();
        result.SignerKeyBinding.Should().BeNull("a key service without IDidKeyBindingService cannot attest same-document provenance");
        result.SenderKeyBinding.Should().BeNull();
        result.RecipientKeyBinding.Should().BeNull();
        resolver.CountFor(Alice).Should().Be(2, "the legacy path keeps its separate authorization resolution");
    }

    [Fact]
    public async Task LegacyKeyService_UnauthorizedKidAtSecondResolution_StillRejected()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)), Doc(Alice)); // kid gone by authorization time
        var legacy = new LegacyKeyServiceDecorator(new NetDidKeyService(resolver));
        var recipient = new DidCommClient(new DictionarySecretsLookup(Array.Empty<Jwk>()), legacy, new DidCommOptions());

        await recipient.Invoking(c => c.UnpackAsync(packed))
            .Should().ThrowAsync<ConsistencyException>();
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: snapshots and observers carry the evidence; synthetic results cannot
    // manufacture it.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Observation_FromRealUnpack_CarriesBindings()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);
        var result = await recipient.UnpackAsync(packed);

        var observation = InboundObservation.FromUnpackResult(result);

        observation.SignerKeyBinding.Should().NotBeNull();
        observation.SignerKeyBinding!.PublicKeyThumbprint.Should().Be(result.SignerKeyBinding!.PublicKeyThumbprint);
    }

    [Fact]
    public void Observation_FromSyntheticResult_CarriesNoBindings()
    {
        // A consumer-assembled UnpackResult over a message the pipeline never verified must not
        // yield strong provenance — flags alone are not evidence.
        var synthetic = new UnpackResult(
            Message: NewMessage(),
            Stack: new[] { Jose.EnvelopeKind.Signed, Jose.EnvelopeKind.Plaintext },
            Encrypted: false,
            Authenticated: true,
            NonRepudiation: true,
            AnonymousSender: false,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: "EdDSA",
            SignerKid: AliceAuthKid,
            SenderKid: null,
            RecipientKid: null,
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null);

        var observation = InboundObservation.FromUnpackResult(synthetic);
        var fallback = InboundMessageSnapshot.CreateFallback(synthetic);

        observation.SignerKeyBinding.Should().BeNull();
        observation.SenderKeyBinding.Should().BeNull();
        observation.RecipientKeyBinding.Should().BeNull();
        fallback.SignerKeyBinding.Should().BeNull();
        fallback.SenderKeyBinding.Should().BeNull();
        fallback.RecipientKeyBinding.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------

    private static Message NewMessage(string? from = Alice)
    {
        var builder = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithTo(Bob)
            .WithBody(JsonNode.Parse("""{"v":1}""")!.AsObject());
        if (from is not null)
            builder = builder.WithFrom(from);
        return builder.Build();
    }

    /// <summary>Pack a signed envelope as alice using <paramref name="signingKey"/> (the sender trusts its own doc).</summary>
    private static async Task<string> PackSigned(TestKeyMaterial signingKey, Message? message = null)
    {
        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(signingKey.PublicJwk)));
        var sender = Client(resolver, signingKey.PrivateJwk);
        return await sender.PackSignedAsync(message ?? NewMessage(), Alice);
    }

    /// <summary>Pack an authcrypt envelope alice → bob with <paramref name="aliceKa"/> as the sender key.</summary>
    private static async Task<string> PackAuthcrypt(TestKeyMaterial aliceKa, DidDocument bobDoc)
    {
        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Ka(aliceKa.PublicJwk)));
        resolver.SetSequence(Bob, bobDoc);
        var sender = Client(resolver, aliceKa.PrivateJwk);
        return (await sender.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;
    }

    /// <summary>Pack an anoncrypt envelope to bob (no sender identity).</summary>
    private static async Task<string> PackAnoncrypt(DidDocument bobDoc)
    {
        var resolver = new VersionedResolver();
        resolver.SetSequence(Bob, bobDoc);
        var sender = Client(resolver);
        return (await sender.PackEncryptedAsync(
            NewMessage(from: null),
            new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;
    }

    private static DidCommClient Client(IDidResolver resolver, params Jwk[] privateJwks)
        => new(new DictionarySecretsLookup(privateJwks), new NetDidKeyService(resolver), new DidCommOptions());

    private sealed record VmSpec(Jwk PublicJwk, VerificationRelationship Relationship, string? Controller);

    private static VmSpec Auth(Jwk publicJwk, string? controller = null)
        => new(publicJwk, VerificationRelationship.Authentication, controller);

    private static VmSpec Ka(Jwk publicJwk, string? controller = null)
        => new(publicJwk, VerificationRelationship.KeyAgreement, controller);

    private static DidDocument Doc(string did, params VmSpec[] methods)
    {
        var vms = new List<VerificationMethod>();
        var auth = new List<VerificationRelationshipEntry>();
        var ka = new List<VerificationRelationshipEntry>();
        foreach (var spec in methods)
        {
            var vm = new VerificationMethod
            {
                Id = spec.PublicJwk.Kid!,
                Type = "JsonWebKey2020",
                Controller = new Did(spec.Controller ?? did),
                PublicKeyJwk = new JsonWebKey
                {
                    Kty = spec.PublicJwk.Kty,
                    Crv = spec.PublicJwk.Crv,
                    X = spec.PublicJwk.X,
                    Y = spec.PublicJwk.Y,
                },
            };
            vms.Add(vm);
            (spec.Relationship == VerificationRelationship.Authentication ? auth : ka)
                .Add(VerificationRelationshipEntry.FromEmbedded(vm));
        }

        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = vms.ToArray(),
            Authentication = auth.ToArray(),
            KeyAgreement = ka.ToArray(),
        };
    }

    /// <summary>
    /// Resolver that serves a per-DID sequence of document versions: each resolution consumes
    /// the next document until one remains, which is then served repeatedly. Counts resolutions
    /// per DID so tests can prove how many times an unpack consulted a document.
    /// </summary>
    private sealed class VersionedResolver : IDidResolver
    {
        private readonly Dictionary<string, Queue<DidDocument>> _sequences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public void SetSequence(string did, params DidDocument[] versions)
        {
            lock (_gate)
            {
                _sequences[did] = new Queue<DidDocument>(versions);
            }
        }

        public int CountFor(string did)
        {
            lock (_gate)
            {
                return _counts.GetValueOrDefault(did);
            }
        }

        public bool CanResolve(string did)
        {
            lock (_gate)
            {
                return _sequences.ContainsKey(did);
            }
        }

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _counts[did] = _counts.GetValueOrDefault(did) + 1;
                if (!_sequences.TryGetValue(did, out var queue) || queue.Count == 0)
                    return Task.FromResult(DidResolutionResult.NotFound(did));

                var doc = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return Task.FromResult(new DidResolutionResult
                {
                    DidDocument = doc,
                    ResolutionMetadata = new DidResolutionMetadata(),
                });
            }
        }
    }

    /// <summary>An IDidKeyService wrapper hiding the binding capability — models pre-1.4.0 custom services.</summary>
    private sealed class LegacyKeyServiceDecorator : IDidKeyService
    {
        private readonly NetDidKeyService _inner;
        public LegacyKeyServiceDecorator(NetDidKeyService inner) => _inner = inner;

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => _inner.GetVerificationMethodsAsync(did, relationship, ct);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => _inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => _inner.RejectUnsupportedMethod(did);
    }
}
