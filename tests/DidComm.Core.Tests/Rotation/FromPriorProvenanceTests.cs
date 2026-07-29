using DidComm.Exceptions;
using DidComm.Protocols.Rotation;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Rotation;

/// <summary>
/// <c>from_prior</c> rotation validation used to authorize the signer kid in one DID resolution
/// and then fetch the verifying key in a second — the same TOCTOU as #56, with a worse payoff: a
/// forged accepted rotation hands the attacker whatever relationship the prior DID held. These
/// tests pin the single-resolution behavior (FR-CONSIST-07 applied to FR-ROT-01).
/// </summary>
public sealed class FromPriorProvenanceTests
{
    private const string PriorDid = "did:example:alice";
    private const string PriorKid = "did:example:alice#auth-1";
    private const string NewDid = "did:example:newalice";

    private static FromPriorClaims Claims() => new(Sub: NewDid, Iss: PriorDid, Iat: 1700000000);

    [Fact]
    public async Task RotationSignedByGenuinePriorKey_Accepted_WithOneResolution()
    {
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(Claims(), priorKey.PrivateJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence(PriorDid, Doc(PriorDid, priorKey.PublicJwk));
        var keyService = new NetDidKeyService(resolver);

        var validated = await FromPriorValidator.ValidateAsync(jwt, NewDid, keyService);

        validated.Iss.Should().Be(PriorDid);
        resolver.CountFor(PriorDid).Should().Be(1,
            "authority and verifying key must come from one resolution (FR-CONSIST-07)");
    }

    [Fact]
    public async Task RotationSignedByReplacementKeyUnderSameKid_Rejected()
    {
        // The attack the two-resolution shape allowed: resolution A (authorization) sees the
        // victim's genuine key under the kid; resolution B (verify) sees a replacement key under
        // the SAME kid, which is what actually signed the rotation JWT. Pre-fix this produced an
        // accepted rotation did:example:alice → did:example:newalice that alice never signed.
        var victimKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var replacementKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(Claims(), replacementKey.PrivateJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence(
            PriorDid,
            Doc(PriorDid, victimKey.PublicJwk),      // authorization-time document
            Doc(PriorDid, replacementKey.PublicJwk)); // verify-time document (pre-fix)
        var keyService = new NetDidKeyService(resolver);

        var act = () => FromPriorValidator.ValidateAsync(jwt, NewDid, keyService);

        await act.Should().ThrowAsync<ConsistencyException>();
        resolver.CountFor(PriorDid).Should().Be(1, "the favorable second document must never be consulted");
    }

    [Fact]
    public async Task RotationSignerControlledByAnotherDid_Rejected()
    {
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(Claims(), priorKey.PrivateJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence(PriorDid, Doc(PriorDid, priorKey.PublicJwk, controller: "did:example:eve"));
        var keyService = new NetDidKeyService(resolver);

        var act = () => FromPriorValidator.ValidateAsync(jwt, NewDid, keyService);

        await act.Should().ThrowAsync<ConsistencyException>().WithMessage("*FR-ROT-01*");
    }

    [Fact]
    public async Task RotationSignerAbsentFromPriorDocument_Rejected()
    {
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(Claims(), priorKey.PrivateJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence(PriorDid, Doc(PriorDid)); // no authentication methods
        var keyService = new NetDidKeyService(resolver);

        var act = () => FromPriorValidator.ValidateAsync(jwt, NewDid, keyService);

        await act.Should().ThrowAsync<ConsistencyException>().WithMessage("*FR-ROT-01*");
    }

    [Fact]
    public async Task RotationSignerKidNamingAnotherDid_RejectedWithoutResolvingIt()
    {
        // A kid under a DID other than 'iss' can never authorize this rotation. Rejecting before
        // resolution also means an attacker-chosen kid cannot steer us into resolving arbitrary DIDs.
        var evilKey = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:eve#auth-1");
        var jwt = await FromPriorBuilder.BuildAsync(Claims(), evilKey.PrivateJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence("did:example:eve", Doc("did:example:eve", evilKey.PublicJwk));
        var keyService = new NetDidKeyService(resolver);

        var act = () => FromPriorValidator.ValidateAsync(jwt, NewDid, keyService);

        await act.Should().ThrowAsync<ConsistencyException>().WithMessage("*FR-ROT-01*");
        resolver.CountFor("did:example:eve").Should().Be(0);
    }

    [Theory]
    [InlineData("did:example:alice?versionId=9")]
    [InlineData("did:example:alice/path")]
    public async Task DecoratedIssDidUrl_Rejected(string decoratedIss)
    {
        // Authorization compares DID subjects, so 'did:x' and 'did:x?v=1' would authorize
        // identically while staying distinct strings — and 'iss' is what an application keys its
        // rotation replay state on. The prior DID's key holder must not be able to mint unlimited
        // equivalent-but-distinct 'iss' values that each carry a valid signature.
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(
            new FromPriorClaims(Sub: NewDid, Iss: decoratedIss, Iat: 1700000000), priorKey.PrivateJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence(PriorDid, Doc(PriorDid, priorKey.PublicJwk));
        var keyService = new NetDidKeyService(resolver);

        var act = () => FromPriorValidator.ValidateAsync(jwt, NewDid, keyService);

        await act.Should().ThrowAsync<ConsistencyException>().WithMessage("*bare DID*");
    }

    [Fact]
    public async Task LegacyKeyService_KeepsTwoResolutionBehavior()
    {
        // Custom services without the binding capability are unchanged (source/binary compatible).
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(Claims(), priorKey.PrivateJwk);

        var resolver = new VersionedResolver();
        resolver.SetSequence(PriorDid, Doc(PriorDid, priorKey.PublicJwk));
        var legacy = new LegacyKeyService(new NetDidKeyService(resolver));

        var validated = await FromPriorValidator.ValidateAsync(jwt, NewDid, legacy);

        validated.Iss.Should().Be(PriorDid);
        resolver.CountFor(PriorDid).Should().Be(2);
    }

    private static DidDocument Doc(string did, Jwk? authenticationKey = null, string? controller = null)
    {
        if (authenticationKey is null)
        {
            return new DidDocument
            {
                Id = new Did(did),
                VerificationMethod = Array.Empty<VerificationMethod>(),
                Authentication = Array.Empty<VerificationRelationshipEntry>(),
            };
        }

        var vm = new VerificationMethod
        {
            Id = authenticationKey.Kid!,
            Type = "JsonWebKey2020",
            Controller = new Did(controller ?? did),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = authenticationKey.Kty,
                Crv = authenticationKey.Crv,
                X = authenticationKey.X,
            },
        };
        return new DidDocument
        {
            Id = new Did(did),
            VerificationMethod = new[] { vm },
            Authentication = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
    }

    /// <summary>Serves a per-DID sequence of document versions and counts resolutions.</summary>
    private sealed class VersionedResolver : IDidResolver
    {
        private readonly Dictionary<string, Queue<DidDocument>> _sequences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void SetSequence(string did, params DidDocument[] versions)
            => _sequences[did] = new Queue<DidDocument>(versions);

        public int CountFor(string did) => _counts.GetValueOrDefault(did);

        public bool CanResolve(string did) => _sequences.ContainsKey(did);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
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

    /// <summary>Hides the binding capability — models a pre-1.4.0 custom key service.</summary>
    private sealed class LegacyKeyService : IDidKeyService
    {
        private readonly NetDidKeyService _inner;
        public LegacyKeyService(NetDidKeyService inner) => _inner = inner;

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => _inner.GetVerificationMethodsAsync(did, relationship, ct);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => _inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => _inner.RejectUnsupportedMethod(did);
    }
}
