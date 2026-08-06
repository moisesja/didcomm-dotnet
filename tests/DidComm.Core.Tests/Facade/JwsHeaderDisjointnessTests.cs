using System.Text.Json;
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

namespace DidComm.Tests.Facade;

/// <summary>
/// The signed-envelope header shape has to satisfy two independent rules at once.
/// RFC 7515 §7.2: a signature's protected and unprotected header parameter sets MUST be
/// disjoint — nimbus (didcomm-jvm) enforces it. DIDComm v2.1: the signer <c>kid</c> travels in
/// the per-signature <b>unprotected</b> header, with <c>protected</c> holding only
/// <c>typ</c>+<c>alg</c> — the shape of every signed vector in Appendix C and of everything both
/// SICPA reference implementations emit and require.
/// <para>
/// History, because each release satisfied one rule by breaking the other: up to
/// DataProofsDotnet.Jose 1.1.1 the kid appeared in <b>both</b> headers (disjointness violated;
/// didcomm-jvm rejected our envelopes, didcomm-python tolerated them). 1.2.0/1.2.1 fixed that by
/// moving it to <b>protected only</b> (dataproofs-dotnet#17), which is disjoint but not the
/// DIDComm shape — so both reference implementations now reject it. The conformant emission is
/// unprotected-only, tracked as dataproofs-dotnet#25.
/// </para>
/// </summary>
public sealed class JwsHeaderDisjointnessTests
{
    private const string SignerDid = "did:example:alice";
    private const string SignerKid = "did:example:alice#auth-1";

    [Fact]
    public async Task PackSigned_ProtectedAndUnprotectedHeaders_AreDisjoint()
    {
        var packed = await PackSignedAsync();

        using var jws = JsonDocument.Parse(packed);

        // We emit General (a "signatures" array) at every signer count, but the Flattened form
        // hoists those same members to the root and is still legal to receive, so this reads
        // either shape — the disjointness rule applies per signature regardless of serialization.
        var signatures = jws.RootElement.TryGetProperty("signatures", out var array)
            ? array.EnumerateArray().ToList()
            : new List<JsonElement> { jws.RootElement };
        signatures.Should().NotBeEmpty();

        foreach (var signature in signatures)
        {
            var protectedJson = Base64UrlEncoder.Decode(
                signature.GetProperty("protected").GetString());
            using var protectedHeader = JsonDocument.Parse(protectedJson);
            var protectedNames = protectedHeader.RootElement.EnumerateObject()
                .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            var unprotectedNames = signature.TryGetProperty("header", out var unprotectedHeader)
                ? unprotectedHeader.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            unprotectedNames.Intersect(protectedNames).Should().BeEmpty(
                "RFC 7515 §7.2 requires a signature's protected and unprotected header " +
                "parameter sets to be disjoint (dataproofs-dotnet#17)");

            unprotectedNames.Should().Contain("kid",
                "DIDComm v2.1 Appendix C carries the signer kid in the per-signature " +
                "unprotected header, and both SICPA reference implementations require it " +
                "there — didcomm-jvm rejects the envelope outright without it " +
                "(dataproofs-dotnet#25)");
            protectedNames.Should().NotContain("kid");
        }
    }

    private static async Task<string> PackSignedAsync()
    {
        var key = TestKeyMaterial.Generate(KeyType.Ed25519, SignerKid);
        var client = new DidCommClient(
            new DictionarySecretsLookup(new[] { key.PrivateJwk }),
            new NetDidKeyService(new SingleDocResolver(Doc(key.PublicJwk))),
            new DidCommOptions());
        var message = new MessageBuilder()
            .WithType("http://example.com/p/1.0/m")
            .WithFrom(SignerDid)
            .WithTo("did:example:bob")
            .Build();
        return await client.PackSignedAsync(message, SignerDid);
    }

    private static DidDocument Doc(DataProofsDotnet.Jose.Jwk authenticationKey)
    {
        var vm = new VerificationMethod
        {
            Id = authenticationKey.Kid!,
            Type = "JsonWebKey2020",
            Controller = new Did(SignerDid),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = authenticationKey.Kty,
                Crv = authenticationKey.Crv,
                X = authenticationKey.X,
            },
        };
        return new DidDocument
        {
            Id = new Did(SignerDid),
            VerificationMethod = new[] { vm },
            Authentication = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
    }

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
