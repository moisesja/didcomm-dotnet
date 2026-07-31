using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Json;
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
    private const string Carol = "did:example:carol";
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
        result.SignerKeyBinding.AuthorizedForDid.Should().BeNull(
            "with no plaintext 'from' there was no asserted identity to authorize the key against — " +
            "the controller value is self-declared evidence, not an identity proof");
    }

    [Fact]
    public async Task Signed_WithFromHeader_BindingRecordsWhatItWasAuthorizedAgainst()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);

        var result = await recipient.UnpackAsync(packed);

        result.SignerKeyBinding!.AuthorizedForDid.Should().Be(Alice);
    }

    [Fact]
    public async Task DecoratedFromDidUrl_RejectedAtThePerimeter()
    {
        // Guards the assumption behind AuthorizedForDid holding a bare DID: the facade refuses a
        // 'from' that is not a bare DID on both pack and unpack, so a decorated identity never
        // reaches the binding in the first place. (The binding stores the compared DID subject
        // regardless, so the property is a bare DID by construction on every path.)
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithFrom(Alice + "?versionId=3")
            .WithTo(Bob)
            .WithBody(JsonNode.Parse("""{"v":1}""")!.AsObject())
            .Build();

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var client = Client(resolver, keyA.PrivateJwk);

        await client.Invoking(c => c.PackSignedAsync(message, Alice))
            .Should().ThrowAsync<DidResolutionException>();
    }

    [Fact]
    public async Task Bindings_CompareStructurally_SoResultEqualityIsPreserved()
    {
        // VerifiedKeyBinding is a class, not a record: without value equality two results from
        // unpacking the same bytes twice would compare unequal, silently changing UnpackResult's
        // record equality for consumers upgrading from 1.3.0.
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);

        var first = await recipient.UnpackAsync(packed);
        var second = await recipient.UnpackAsync(packed);

        first.SignerKeyBinding.Should().Be(second.SignerKeyBinding);
        first.SignerKeyBinding!.GetHashCode().Should().Be(second.SignerKeyBinding!.GetHashCode());
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance: concurrent unpacks retain independent, correct evidence.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ConcurrentUnpacks_SameKid_IndependentBindings()
    {
        // Forced overlap: the resolver holds every caller inside ResolveAsync until all of them have
        // arrived, so the unpacks are genuinely in flight together. (Awaiting a list of already-
        // synchronously-completed tasks would prove nothing about concurrency.)
        const int concurrency = 12;
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);
        var expected = DpJwkThumbprint.ComputeBase64Url(keyA.PublicJwk);

        var resolver = new RendezvousResolver(Doc(Alice, Auth(keyA.PublicJwk)), concurrency);
        var recipient = Client(resolver);

        var results = await Task.WhenAll(Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => recipient.UnpackAsync(packed))));

        resolver.MaxObservedConcurrency.Should().Be(concurrency, "the unpacks must actually have overlapped");
        results.Should().OnlyContain(r =>
            r.SignerKeyBinding != null && r.SignerKeyBinding.PublicKeyThumbprint == expected);
    }

    [Fact]
    public async Task ConcurrentUnpacks_DifferentKeysSameKid_NeitherSeesTheOthersEvidence()
    {
        // Two envelopes signed by DIFFERENT keys under the SAME kid, unpacked concurrently against a
        // resolver that alternates documents. Whichever pairing each operation gets, the invariant is
        // absolute: a result that verified may only ever report the key that verified IT. A shared or
        // leaked binding context would show up here as a result carrying the other envelope's key.
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var keyB = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packedA = await PackSigned(keyA);
        var packedB = await PackSigned(keyB);
        var thumbA = DpJwkThumbprint.ComputeBase64Url(keyA.PublicJwk);
        var thumbB = DpJwkThumbprint.ComputeBase64Url(keyB.PublicJwk);

        // Documents are handed out in arrival order, and the test parks the first unpack inside the
        // resolver before starting the second — so the pairing is deterministic (A↔KA, B↔KB) while
        // both operations are genuinely in flight at the same time. If the two operations shared
        // captured evidence, one would report the other's key or fail to verify.
        var resolver = new ArrivalOrderedResolver(Doc(Alice, Auth(keyA.PublicJwk)), Doc(Alice, Auth(keyB.PublicJwk)));
        var recipient = Client(resolver);

        var first = Task.Run(() => recipient.UnpackAsync(packedA));
        await resolver.WaitForArrivalsAsync(1);
        var second = Task.Run(() => recipient.UnpackAsync(packedB));
        await resolver.WaitForArrivalsAsync(2);
        resolver.Release();

        var resultA = await first;
        var resultB = await second;

        resolver.MaxObservedConcurrency.Should().Be(2, "both unpacks must have been in flight together");
        resultA.SignerKeyBinding!.PublicKeyThumbprint.Should().Be(thumbA);
        resultB.SignerKeyBinding!.PublicKeyThumbprint.Should().Be(thumbB);
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
    public async Task Observation_FromMutatedVerifiedMessage_NeutralizesAllTrustMetadata()
    {
        // Message is mutable and the verified snapshot is keyed by object identity, so an in-place
        // edit keeps that identity: without a content check, a caller could rewrite a verified
        // message and hand an observer Alice's binding attached to content Alice never signed.
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);
        var result = await recipient.UnpackAsync(packed);

        InboundObservation.FromUnpackResult(result).SignerKeyBinding.Should().NotBeNull();

        // Poison every caller-controlled trust member, then mutate the verified message in place:
        // same object identity, different content. Divergence must fail closed rather than turn
        // this verified result into the synthetic compatibility path.
        var poisoned = result with
        {
            Encrypted = true,
            Authenticated = true,
            NonRepudiation = true,
            AnonymousSender = true,
            SenderKid = "did:example:mallory#ka",
            SignerKid = "did:example:mallory#sign",
            RecipientAddressing = RecipientAddressing.Addressed,
        };
        poisoned.Message.Body = JsonNode.Parse("""{"content":"attacker-substituted"}""")!.AsObject();

        var afterMutation = InboundObservation.FromUnpackResult(poisoned);

        afterMutation.Encrypted.Should().BeFalse();
        afterMutation.Authenticated.Should().BeFalse();
        afterMutation.NonRepudiation.Should().BeFalse();
        afterMutation.AnonymousSender.Should().BeFalse();
        afterMutation.SenderKid.Should().BeNull();
        afterMutation.SignerKid.Should().BeNull();
        afterMutation.SignerKeyBinding.Should().BeNull("the evidence does not cover mutated content");
        afterMutation.SenderKeyBinding.Should().BeNull();
        afterMutation.RecipientKeyBinding.Should().BeNull();
        afterMutation.RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
    }

    [Fact]
    public void Observation_FromDivergedSnapshot_NeutralizesEveryNonDefaultMetadataMember()
    {
        // Register an intentionally all-nondefault structural sentinel so every neutralization
        // assertion below proves a real transition. The tuple is deliberately denser than a legal
        // envelope combination; direct registration keeps the regression focused on the snapshot
        // boundary rather than JOSE composition rules.
        var message = NewMessage();
        var senderKey = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var signerKey = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var recipientKey = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var senderBinding = new VerifiedKeyBinding(
            new ResolvedKeyBinding(
                AliceKaKid,
                Alice,
                Alice,
                VerificationRelationship.KeyAgreement,
                senderKey.PublicJwk),
            Alice);
        var signerBinding = new VerifiedKeyBinding(
            new ResolvedKeyBinding(
                AliceAuthKid,
                Alice,
                Alice,
                VerificationRelationship.Authentication,
                signerKey.PublicJwk),
            Alice);
        var recipientBinding = new VerifiedKeyBinding(
            new ResolvedKeyBinding(
                BobKaKid,
                Bob,
                Bob,
                VerificationRelationship.KeyAgreement,
                recipientKey.PublicJwk),
            Bob);

        InboundMessageSnapshot.RegisterVerified(
            message,
            JsonSerializer.Serialize(message, DidCommJson.Default),
            encrypted: true,
            authenticated: true,
            nonRepudiation: true,
            anonymousSender: true,
            senderKid: AliceKaKid,
            signerKid: AliceAuthKid,
            recipientKid: BobKaKid,
            recipientAddressing: RecipientAddressing.Addressed,
            senderKeyBinding: senderBinding,
            signerKeyBinding: signerBinding,
            recipientKeyBinding: recipientBinding);

        var received = new UnpackResult(
            Message: message,
            Stack: new[]
            {
                Jose.EnvelopeKind.Encrypted,
                Jose.EnvelopeKind.Signed,
                Jose.EnvelopeKind.Plaintext,
            },
            Encrypted: true,
            Authenticated: true,
            NonRepudiation: true,
            AnonymousSender: true,
            ContentEncryption: "A256CBC-HS512",
            KeyWrap: "ECDH-1PU+A256KW",
            SignatureAlgorithm: "EdDSA",
            SignerKid: AliceAuthKid,
            SenderKid: AliceKaKid,
            RecipientKid: BobKaKid,
            AllRecipientKids: new[] { BobKaKid },
            FromPrior: null)
        {
            SenderKeyBinding = senderBinding,
            SignerKeyBinding = signerBinding,
            RecipientKeyBinding = recipientBinding,
            RecipientAddressing = RecipientAddressing.Addressed,
        };

        message.Body = JsonNode.Parse("""{"content":"diverged"}""")!.AsObject();

        var observation = InboundObservation.FromUnpackResult(received);

        observation.Encrypted.Should().BeFalse();
        observation.Authenticated.Should().BeFalse();
        observation.NonRepudiation.Should().BeFalse();
        observation.AnonymousSender.Should().BeFalse();
        observation.SenderKid.Should().BeNull();
        observation.SignerKid.Should().BeNull();
        observation.SenderKeyBinding.Should().BeNull();
        observation.SignerKeyBinding.Should().BeNull();
        observation.RecipientKeyBinding.Should().BeNull();
        observation.RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
    }

    [Fact]
    public void Observation_FromSyntheticResult_PreservesOnlyPositionalTrustMetadata()
    {
        // A consumer-assembled UnpackResult over a message the pipeline never verified must not
        // yield strong provenance — flags alone are not evidence.
        var synthetic = new UnpackResult(
            Message: NewMessage(),
            Stack: new[] { Jose.EnvelopeKind.Signed, Jose.EnvelopeKind.Plaintext },
            Encrypted: true,
            Authenticated: true,
            NonRepudiation: true,
            AnonymousSender: true,
            ContentEncryption: null,
            KeyWrap: null,
            SignatureAlgorithm: "EdDSA",
            SignerKid: AliceAuthKid,
            SenderKid: AliceKaKid,
            RecipientKid: null,
            AllRecipientKids: Array.Empty<string>(),
            FromPrior: null)
        {
            RecipientAddressing = RecipientAddressing.Addressed,
        };

        var observation = InboundObservation.FromUnpackResult(synthetic);
        var fallback = InboundMessageSnapshot.CreateFallback(synthetic);

        observation.Encrypted.Should().BeTrue();
        observation.Authenticated.Should().BeTrue();
        observation.NonRepudiation.Should().BeTrue();
        observation.AnonymousSender.Should().BeTrue();
        observation.SenderKid.Should().Be(AliceKaKid);
        observation.SignerKid.Should().Be(AliceAuthKid);
        observation.SignerKeyBinding.Should().BeNull();
        observation.SenderKeyBinding.Should().BeNull();
        observation.RecipientKeyBinding.Should().BeNull();
        observation.RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
        fallback.SignerKeyBinding.Should().BeNull();
        fallback.SenderKeyBinding.Should().BeNull();
        fallback.RecipientKeyBinding.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // Acceptance (#61): the FR-CONSIST-04 addressing outcome rides the same snapshot backstop
    // as the bindings — mirrored onto observations and reset when verified content diverges.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Observation_FromRealUnpack_CarriesRecipientAddressing()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        // Carol unpacks a message addressed to bob only — the FR-CONSIST-04 warning case.
        var recipient = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions { OwnIdentifiers = new[] { Carol } });
        var result = await recipient.UnpackAsync(packed);

        result.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);
        InboundObservation.FromUnpackResult(result).RecipientAddressing.Should().Be(
            RecipientAddressing.NotAddressed,
            "observer-only applications must see the addressing warning (#61)");
    }

    [Fact]
    public async Task Observation_FromMutatedVerifiedMessage_ResetsRecipientAddressingToNotEvaluated()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions { OwnIdentifiers = new[] { Carol } });
        var result = await recipient.UnpackAsync(packed);

        // Load-bearing precondition: the value is non-default before mutation, so the reset below
        // is a real transition and not the unconfigured default.
        InboundObservation.FromUnpackResult(result).RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);

        // Mutate the verified message in place — same object identity, different content.
        result.Message.Body = JsonNode.Parse("""{"content":"attacker-substituted"}""")!.AsObject();

        InboundObservation.FromUnpackResult(result).RecipientAddressing.Should().Be(
            RecipientAddressing.NotEvaluated,
            "an addressing outcome does not cover content the unpack never evaluated");
    }

    [Fact]
    public async Task Observation_WithClone_KeepsTheValueOfTheMessageItWasBuiltFrom()
    {
        // Documented limitation, pinned deliberately: InboundObservation is a record, so a
        // consumer's with-clone copies RecipientAddressing — like every member — onto whatever
        // Message the clone carries. The library's pairing guarantee covers observations it
        // constructs; a clone's value describes the message the observation was BUILT from, and
        // consumers must not read a transplanted observation's value as describing its current
        // Message. If this test starts failing, the record shape changed — update the scope
        // language in the PRD FR-CONSIST-04 row and the CHANGELOG alongside.
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions { OwnIdentifiers = new[] { Carol } });
        var result = await recipient.UnpackAsync(packed);

        var observation = InboundObservation.FromUnpackResult(result);
        observation.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);

        var messageAddressedToCarol = NewMessage();
        messageAddressedToCarol.To = new[] { Carol };
        var transplanted = observation with { Message = messageAddressedToCarol };

        transplanted.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed,
            "a record clone preserves the stored value even though the transplanted Message " +
            "would recompute as Addressed for Carol");
    }

    [Fact]
    public async Task Observation_DirectMessageMutation_KeepsTheValueOfTheMessageItWasBuiltFrom()
    {
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = new DidCommClient(
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            new NetDidKeyService(resolver),
            new DidCommOptions { OwnIdentifiers = new[] { Carol } });
        var result = await recipient.UnpackAsync(packed);

        var observation = InboundObservation.FromUnpackResult(result);
        observation.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);

        observation.Message.To = new[] { Carol };

        observation.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed,
            "direct mutation does not recompute the value; it still describes the built-from message");
    }

    [Fact]
    public void RecipientAddressing_ParticipatesInObservationValueEquality()
    {
        // Upgrader-visible semantics (see CHANGELOG): the record's synthesized Equals/GetHashCode
        // now cover RecipientAddressing, so a library observation carrying a real outcome no
        // longer equals a consumer-constructed observation that agrees on all seven positional
        // members but holds the default NotEvaluated.
        var msg = NewMessage();
        var baseline = new InboundObservation(msg, Encrypted: false, Authenticated: false,
            NonRepudiation: false, AnonymousSender: false, SenderKid: null, SignerKid: null);
        var carrying = baseline with { RecipientAddressing = RecipientAddressing.NotAddressed };

        carrying.Should().NotBe(baseline);
        (baseline with { RecipientAddressing = RecipientAddressing.NotAddressed }).Should().Be(carrying,
            "equality remains structural over the full member set");
    }

    [Fact]
    public async Task Observation_FromForgedFlagsOnVerifiedAuthcryptResult_ReadsTrustMetadataFromSnapshot()
    {
        // Authcrypt counterpart of the signed-path test below: together they pin all six
        // trust-metadata members. This one covers Encrypted, AnonymousSender, and SenderKid,
        // plus the positive recipient-binding carry-through in the covering state.
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var bobKa = TestKeyMaterial.Generate(KeyType.X25519, BobKaKid);
        var bobDoc = Doc(Bob, Ka(bobKa.PublicJwk));
        var packed = await PackAuthcrypt(aliceKa, bobDoc);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Ka(aliceKa.PublicJwk)));
        resolver.SetSequence(Bob, bobDoc);
        var recipient = Client(resolver, bobKa.PrivateJwk);
        var result = await recipient.UnpackAsync(packed);

        var forged = result with
        {
            Encrypted = false,
            AnonymousSender = true,
            SenderKid = "did:example:mallory#ka",
            SignerKid = "did:example:mallory#sign",
        };

        var observation = InboundObservation.FromUnpackResult(forged);

        observation.Encrypted.Should().BeTrue("the unpack decrypted this content; a clone cannot unsay it");
        observation.AnonymousSender.Should().BeFalse("authcrypt named its sender; a clone cannot anonymize it");
        observation.SenderKid.Should().Be(AliceKaKid, "a clone cannot reattribute the authenticated sender key");
        observation.SignerKid.Should().BeNull("a caller claim cannot replace a verified null signer key");
        observation.RecipientKeyBinding.Should().NotBeNull(
            "a covering snapshot carries the recipient evidence onto the observation");
        observation.RecipientKeyBinding!.PublicKeyThumbprint.Should().Be(result.RecipientKeyBinding!.PublicKeyThumbprint);
    }

    [Fact]
    public async Task Observation_FromForgedFlagsOnVerifiedSignedResult_ReadsTrustMetadataFromSnapshot()
    {
        // UnpackResult is a record, so a with-clone can rewrite the flags an application is told
        // to interpret the addressing outcome (and the bindings) with. When the verified snapshot
        // still covers the content, the observation's trust metadata must come from the snapshot,
        // not the caller's result — a rewritten flag or kid must not survive.
        var keyA = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var packed = await PackSigned(keyA);

        var resolver = new VersionedResolver();
        resolver.SetSequence(Alice, Doc(Alice, Auth(keyA.PublicJwk)));
        var recipient = Client(resolver);
        var result = await recipient.UnpackAsync(packed);

        var forged = result with
        {
            Authenticated = false,
            NonRepudiation = false,
            SenderKid = "did:example:mallory#ka",
            SignerKid = "did:example:mallory#k",
        };

        var observation = InboundObservation.FromUnpackResult(forged);

        observation.Authenticated.Should().BeTrue("the unpack authenticated this content; a clone cannot unsay it");
        observation.NonRepudiation.Should().BeTrue();
        observation.SenderKid.Should().BeNull("a caller claim cannot replace a verified null sender key");
        observation.SignerKid.Should().Be(AliceAuthKid, "a clone cannot reattribute the verified signature");
        observation.SignerKeyBinding.Should().NotBeNull();
    }

    [Fact]
    public void Observation_FromSyntheticResult_CannotLaunderRecipientAddressing()
    {
        // A consumer-assembled result claiming Addressed must not propagate the claim: the value
        // is sourced only from the verified-at-unpack snapshot, which a synthetic result lacks.
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
            FromPrior: null)
        {
            RecipientAddressing = RecipientAddressing.Addressed,
        };

        InboundObservation.FromUnpackResult(synthetic).RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
        InboundMessageSnapshot.CreateFallback(synthetic).RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
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

    /// <summary>
    /// Serves one document, but holds every caller inside <c>ResolveAsync</c> until
    /// <paramref name="participants"/> callers have arrived — forcing the unpacks to genuinely
    /// overlap instead of completing one after another on synchronously-completed tasks.
    /// </summary>
    private sealed class RendezvousResolver : IDidResolver
    {
        private readonly DidDocument _document;
        private readonly int _participants;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _inFlight;

        public RendezvousResolver(DidDocument document, int participants)
        {
            _document = document;
            _participants = participants;
        }

        public int MaxObservedConcurrency { get; private set; }

        public bool CanResolve(string did) => true;

        public async Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            var current = Interlocked.Increment(ref _inFlight);
            lock (_gate)
            {
                if (current > MaxObservedConcurrency)
                    MaxObservedConcurrency = current;
            }

            if (Interlocked.Increment(ref _arrived) >= _participants)
                _gate.TrySetResult();
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

            Interlocked.Decrement(ref _inFlight);
            return Answer(did);
        }

        private DidResolutionResult Answer(string did)
            => new() { DidDocument = _document, ResolutionMetadata = new DidResolutionMetadata() };
    }

    /// <summary>
    /// Hands out documents in arrival order and parks every caller until <see cref="Release"/>, so a
    /// test can drive a deterministic pairing (first arrival ↔ first document) while keeping the
    /// operations concurrently in flight.
    /// </summary>
    private sealed class ArrivalOrderedResolver : IDidResolver
    {
        private readonly DidDocument[] _documents;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private int _arrived;
        private int _inFlight;

        public ArrivalOrderedResolver(params DidDocument[] documents) => _documents = documents;

        public int MaxObservedConcurrency { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task WaitForArrivalsAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Volatile.Read(ref _arrived) < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"only {Volatile.Read(ref _arrived)} of {count} resolutions arrived");
                await Task.Delay(5);
            }
        }

        public bool CanResolve(string did) => true;

        public async Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            int index;
            lock (_gate)
            {
                index = _arrived;
                _inFlight++;
                if (_inFlight > MaxObservedConcurrency)
                    MaxObservedConcurrency = _inFlight;
            }
            Interlocked.Increment(ref _arrived);

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

            lock (_gate)
            {
                _inFlight--;
            }

            return new DidResolutionResult
            {
                DidDocument = _documents[Math.Min(index, _documents.Length - 1)],
                ResolutionMetadata = new DidResolutionMetadata(),
            };
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
