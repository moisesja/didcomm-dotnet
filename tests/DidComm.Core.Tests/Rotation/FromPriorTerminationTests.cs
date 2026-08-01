using System.Text.Json.Nodes;
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

namespace DidComm.Tests.Rotation;

/// <summary>
/// FR-ROT-06 — relationship termination: a from_prior JWT that omits <c>sub</c>, delivered on a
/// message without <c>from</c>. These tests pin the builder wire shape (no <c>sub</c> member at
/// all), the validator's acceptance of the termination form, and the two invalid presence
/// combinations (termination JWT + present <c>from</c>; rotation JWT + absent <c>from</c>).
/// </summary>
public sealed class FromPriorTerminationTests
{
    private const string PriorDid = "did:example:alice";
    private const string PriorKid = "did:example:alice#auth-1";

    [Fact]
    public async Task Builder_TerminationJwt_OmitsSubEntirely()
    {
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);

        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, iat: 1700000000, priorKey.PrivateJwk);

        var payload = JsonNode.Parse(Encoding.UTF8.GetString(DidComm.Jose.Base64Url.Decode(jwt.Split('.')[1])))!.AsObject();
        payload.ContainsKey("sub").Should().BeFalse("FR-ROT-06 terminations OMIT sub, they do not null it");
        payload["iss"]!.GetValue<string>().Should().Be(PriorDid);
        payload["iat"]!.GetValue<long>().Should().Be(1700000000);
    }

    [Fact]
    public async Task Validator_TerminationJwt_OnFromlessMessage_ParsesAndSurfacesTermination()
    {
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, iat: 1700000000, priorKey.PrivateJwk);

        var validated = await FromPriorValidator.ValidateAsync(
            jwt, currentSenderDid: null, KeyService(priorKey));

        validated.IsTermination.Should().BeTrue();
        validated.Sub.Should().BeNull();
        validated.Iss.Should().Be(PriorDid);
        validated.Iat.Should().Be(1700000000);
    }

    [Fact]
    public async Task Validator_TerminationJwt_OnMessageWithFrom_Rejected()
    {
        // Absent sub + present from is contradictory: "no successor identity" alongside a named
        // sender. FR-ROT-06 requires the termination to be sent WITHOUT from.
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, iat: 1700000000, priorKey.PrivateJwk);

        var act = () => FromPriorValidator.ValidateAsync(jwt, "did:example:newalice", KeyService(priorKey));

        await act.Should().ThrowAsync<ConsistencyException>().WithMessage("*FR-ROT-06*");
    }

    [Fact]
    public async Task Validator_RotationJwt_OnFromlessMessage_Rejected()
    {
        // The converse presence violation: a rotation-shaped JWT (sub present) has nothing to bind
        // sub to when the message names no sender (FR-ROT-02).
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var rotation = new FromPriorClaims(Sub: "did:example:newalice", Iss: PriorDid, Iat: 1700000000);
        var jwt = await FromPriorBuilder.BuildAsync(rotation, priorKey.PrivateJwk);

        var act = () => FromPriorValidator.ValidateAsync(jwt, currentSenderDid: null, KeyService(priorKey));

        await act.Should().ThrowAsync<ConsistencyException>().WithMessage("*FR-ROT-02*");
    }

    [Fact]
    public async Task Validator_ExplicitJsonNullSub_RejectedAsMalformed()
    {
        // "sub": null is neither a rotation nor the omit-sub termination wire shape.
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, iat: 1700000000, priorKey.PrivateJwk);
        var parts = jwt.Split('.');
        var claims = JsonNode.Parse(Encoding.UTF8.GetString(DidComm.Jose.Base64Url.Decode(parts[1])))!.AsObject();
        claims["sub"] = null;
        parts[1] = DidComm.Jose.Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var malformed = string.Join('.', parts);

        var act = () => FromPriorValidator.ValidateAsync(malformed, currentSenderDid: null, KeyService(priorKey));

        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public async Task Validator_TerminationJwt_StillRequiresAuthorizedSigner()
    {
        // FR-ROT-06 keeps every FR-ROT-01 requirement except sub: iss, an authorized kid, and a
        // valid signature. A signer absent from the prior DID's authentication list is rejected.
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var strangerKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, iat: 1700000000, strangerKey.PrivateJwk);

        var act = () => FromPriorValidator.ValidateAsync(jwt, currentSenderDid: null, KeyService(priorKey));

        await act.Should().ThrowAsync<ConsistencyException>().WithMessage("*FR-ROT-01*");
    }

    [Fact]
    public async Task Validator_EmptyCurrentSenderDid_IsACallerBugNotTermination()
    {
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, iat: 1700000000, priorKey.PrivateJwk);

        var act = () => FromPriorValidator.ValidateAsync(jwt, currentSenderDid: "", KeyService(priorKey));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Claims_ForTermination_SetsIsTermination()
    {
        var claims = FromPriorClaims.ForTermination(PriorDid, 1700000000, Exp: 1700003600);

        claims.IsTermination.Should().BeTrue();
        claims.Sub.Should().BeNull();
        claims.Exp.Should().Be(1700003600);

        new FromPriorClaims("did:example:new", PriorDid, 1700000000)
            .IsTermination.Should().BeFalse();
    }

    private static NetDidKeyService KeyService(TestKeyMaterial priorAuthKey)
        => new(new SingleDocResolver(Doc(PriorDid, priorAuthKey.PublicJwk)));

    private static DidDocument Doc(string did, Jwk authenticationKey)
    {
        var vm = new VerificationMethod
        {
            Id = authenticationKey.Kid!,
            Type = "JsonWebKey2020",
            Controller = new Did(did),
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

    /// <summary>Resolves exactly one pinned DID document.</summary>
    private sealed class SingleDocResolver : IDidResolver
    {
        private readonly DidDocument _doc;
        public SingleDocResolver(DidDocument doc) => _doc = doc;

        public bool CanResolve(string did) => string.Equals(did, _doc.Id.Value, StringComparison.Ordinal);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(CanResolve(did)
                ? new DidResolutionResult { DidDocument = _doc, ResolutionMetadata = new DidResolutionMetadata() }
                : DidResolutionResult.NotFound(did));
    }
}
