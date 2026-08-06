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
/// FR-ROT-05 — the exp / nbf claim *shapes* the validator accepts. A present-but-non-numeric
/// exp or nbf MUST be rejected as malformed, never silently dropped: dropping it would fail
/// OPEN — the issuer bounded the token's replay window, and a lenient read erases the bound,
/// turning a freshness-limited rotation JWT into a non-expiring one.
/// </summary>
public sealed class FromPriorFreshnessClaimTests
{
    private const string PriorDid = "did:example:alice";
    private const string PriorKid = "did:example:alice#auth-1";
    private const string NewDid = "did:example:newalice";

    [Theory]
    [InlineData("exp")]
    [InlineData("nbf")]
    public async Task Validator_NonNumericFreshnessClaim_RejectedAsMalformed_NotIgnored(string claim)
    {
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(
            new FromPriorClaims(Sub: NewDid, Iss: PriorDid, Iat: 1700000000), priorKey.PrivateJwk);
        var mutated = WithClaim(jwt, claim, JsonValue.Create("1700000000")); // numeric date as a STRING

        var act = () => FromPriorValidator.ValidateAsync(mutated, NewDid, KeyService(priorKey));

        await act.Should().ThrowAsync<ProtocolException>(
            $"a present-but-wrong-kind '{claim}' must fail closed (RFC 7519 NumericDate), not degrade to a non-expiring token");
    }

    [Theory]
    [InlineData("exp")]
    [InlineData("nbf")]
    public async Task Validator_NonNumericFreshnessClaim_OnTerminationForm_AlsoRejected(string claim)
    {
        // The termination form (FR-ROT-06) carries the same optional freshness bounds.
        var priorKey = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, iat: 1700000000, priorKey.PrivateJwk);
        var mutated = WithClaim(jwt, claim, JsonValue.Create("soon"));

        var act = () => FromPriorValidator.ValidateAsync(mutated, currentSenderDid: null, KeyService(priorKey));

        await act.Should().ThrowAsync<ProtocolException>();
    }

    /// <summary>Re-emit the JWT with <paramref name="claim"/> set to <paramref name="value"/> (signature not re-computed — the malformed-claims rejection fires before verification).</summary>
    private static string WithClaim(string jwt, string claim, JsonNode? value)
    {
        var parts = jwt.Split('.');
        var claims = JsonNode.Parse(Encoding.UTF8.GetString(DidComm.Jose.Base64Url.Decode(parts[1])))!.AsObject();
        claims[claim] = value;
        parts[1] = DidComm.Jose.Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        return string.Join('.', parts);
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
