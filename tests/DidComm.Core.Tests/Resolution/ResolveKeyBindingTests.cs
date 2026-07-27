using DidComm.Exceptions;
using DidComm.Resolution;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using DpJwkThumbprint = DataProofsDotnet.Jose.JwkThumbprint;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Resolution;

/// <summary>
/// Unit tests for <see cref="NetDidKeyService.ResolveKeyBindingAsync"/> — the atomic
/// same-document key projection behind the #56 provenance fix. Covers the fail-closed matrix
/// the issue's acceptance criteria call out: relative ids, embedded methods, relationship
/// references, duplicate/shadowed ids, missing controllers, cross-DID controllers, and wrong
/// relationships.
/// </summary>
public sealed class ResolveKeyBindingTests
{
    private const string X25519Point = "avH0O2Y4tqLAq8y9zpianr8ajii5m4F_mICrzNlatXs";
    private const string Ed25519Point = "G-boxFB6vOZBu-wXkm-9Lh79I8nf9Z50cILaOgKKGww";

    private static VerificationMethod X25519Vm(string id, string? controller = "did:example:alice")
        => new()
        {
            Id = id,
            Type = "JsonWebKey2020",
            Controller = controller is null ? default : new Did(controller),
            PublicKeyJwk = new JsonWebKey { Kty = "OKP", Crv = "X25519", X = X25519Point },
        };

    private static VerificationMethod Ed25519Vm(string id, string? controller = "did:example:alice")
        => new()
        {
            Id = id,
            Type = "JsonWebKey2020",
            Controller = controller is null ? default : new Did(controller),
            PublicKeyJwk = new JsonWebKey { Kty = "OKP", Crv = "Ed25519", X = Ed25519Point },
        };

    [Fact]
    public async Task EmbeddedMethod_ProjectsBindingWithControllerAndThumbprint()
    {
        var vm = X25519Vm("did:example:alice#ka-1");
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        var binding = await sut.ResolveKeyBindingAsync("did:example:alice#ka-1", VerificationRelationship.KeyAgreement);

        binding.Should().NotBeNull();
        binding!.Kid.Should().Be("did:example:alice#ka-1");
        binding.Did.Should().Be("did:example:alice");
        binding.Controller.Should().Be("did:example:alice");
        binding.Relationship.Should().Be(VerificationRelationship.KeyAgreement);
        binding.PublicJwk.Crv.Should().Be("X25519");
        binding.PublicJwk.X.Should().Be(X25519Point);
        binding.PublicKeyThumbprint.Should().Be(DpJwkThumbprint.ComputeBase64Url(binding.PublicJwk));
    }

    [Fact]
    public async Task FragmentReference_DereferencedAgainstSameDocument()
    {
        var vm = Ed25519Vm("did:example:alice#auth-1");
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            Authentication = new[] { VerificationRelationshipEntry.FromReference("did:example:alice#auth-1") },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        var binding = await sut.ResolveKeyBindingAsync("did:example:alice#auth-1", VerificationRelationship.Authentication);

        binding.Should().NotBeNull();
        binding!.Kid.Should().Be("did:example:alice#auth-1");
        binding.Relationship.Should().Be(VerificationRelationship.Authentication);
    }

    [Fact]
    public async Task RelativeVmId_NormalizedToAbsoluteBeforeMatching()
    {
        // did:peer:2-style relative id ("#key-1") must bind under the absolute kid the
        // envelope layer uses, and the projected JWK's kid must be normalized too.
        var vm = X25519Vm("#key-1");
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        var binding = await sut.ResolveKeyBindingAsync("did:example:alice#key-1", VerificationRelationship.KeyAgreement);

        binding.Should().NotBeNull();
        binding!.Kid.Should().Be("did:example:alice#key-1");
        binding.PublicJwk.Kid.Should().Be("did:example:alice#key-1");
    }

    [Fact]
    public async Task DuplicateMatches_SameKidTwice_Throws()
    {
        // Shadowing: the same normalized kid appears twice under the relationship (reference +
        // embedded with different key material). Taking either silently would leave "which key
        // did crypto use?" ambiguous — must fail closed.
        var canonical = X25519Vm("did:example:alice#ka-1");
        var shadow = new VerificationMethod
        {
            Id = "did:example:alice#ka-1",
            Type = "JsonWebKey2020",
            Controller = new Did("did:example:alice"),
            PublicKeyJwk = new JsonWebKey { Kty = "OKP", Crv = "X25519", X = "hSDwCYkwp1R0i33ctD73Wg2_Og0mOBr066SpjqqbTmo" },
        };
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { canonical },
            KeyAgreement = new[]
            {
                VerificationRelationshipEntry.FromReference("did:example:alice#ka-1"),
                VerificationRelationshipEntry.FromEmbedded(shadow),
            },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        var act = () => sut.ResolveKeyBindingAsync("did:example:alice#ka-1", VerificationRelationship.KeyAgreement);

        (await act.Should().ThrowAsync<DidResolutionException>())
            .WithMessage("*more than one entry*");
    }

    [Fact]
    public async Task WrongRelationship_ReturnsNull()
    {
        var vm = Ed25519Vm("did:example:alice#auth-1");
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            Authentication = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        (await sut.ResolveKeyBindingAsync("did:example:alice#auth-1", VerificationRelationship.KeyAgreement))
            .Should().BeNull();
    }

    [Fact]
    public async Task UnusableCurveForRelationship_ReturnsNull()
    {
        // Ed25519 listed under keyAgreement: the JOSE layer could never use it there, so no binding.
        var vm = Ed25519Vm("did:example:alice#ed");
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        (await sut.ResolveKeyBindingAsync("did:example:alice#ed", VerificationRelationship.KeyAgreement))
            .Should().BeNull();
    }

    [Fact]
    public async Task MissingController_CapturedAsNull()
    {
        var vm = X25519Vm("did:example:alice#ka-1", controller: null);
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        var binding = await sut.ResolveKeyBindingAsync("did:example:alice#ka-1", VerificationRelationship.KeyAgreement);

        binding.Should().NotBeNull();
        binding!.Controller.Should().BeNull();
    }

    [Fact]
    public async Task CrossDidController_EvidenceCapturedVerbatim()
    {
        // The projection captures evidence; it does not authorize. A key controlled by eve is
        // still projected — the unpack pipeline is what rejects it against an asserted 'from'.
        var vm = X25519Vm("did:example:alice#ka-1", controller: "did:example:eve");
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver(("did:example:alice", doc)));

        var binding = await sut.ResolveKeyBindingAsync("did:example:alice#ka-1", VerificationRelationship.KeyAgreement);

        binding.Should().NotBeNull();
        binding!.Controller.Should().Be("did:example:eve");
    }

    [Fact]
    public async Task NotADidUrl_ReturnsNull()
    {
        var sut = new NetDidKeyService(new StubResolver());

        (await sut.ResolveKeyBindingAsync("not-a-did-url", VerificationRelationship.KeyAgreement))
            .Should().BeNull();
    }

    [Fact]
    public async Task UnresolvableDid_ThrowsDidResolutionException()
    {
        var sut = new NetDidKeyService(new StubResolver());

        var act = () => sut.ResolveKeyBindingAsync("did:example:ghost#k", VerificationRelationship.KeyAgreement);

        await act.Should().ThrowAsync<DidResolutionException>();
    }

    [Fact]
    public async Task DidWebKid_RejectedBeforeResolution()
    {
        var resolver = new StubResolver();
        var sut = new NetDidKeyService(resolver);

        var act = () => sut.ResolveKeyBindingAsync("did:web:example.com#k", VerificationRelationship.KeyAgreement);

        await act.Should().ThrowAsync<UnsupportedDidMethodException>();
        resolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Projection_UsesExactlyOneResolution()
    {
        var vm = X25519Vm("did:example:alice#ka-1");
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var resolver = new StubResolver(("did:example:alice", doc));
        var sut = new NetDidKeyService(resolver);

        await sut.ResolveKeyBindingAsync("did:example:alice#ka-1", VerificationRelationship.KeyAgreement);

        resolver.ResolveCallCount.Should().Be(1);
    }

    /// <summary>Hand-rolled resolver returning pre-canned documents, counting invocations.</summary>
    private sealed class StubResolver : IDidResolver
    {
        private readonly Dictionary<string, DidDocument> _docs;
        public int ResolveCallCount { get; private set; }

        public StubResolver(params (string Did, DidDocument Doc)[] entries)
        {
            _docs = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
            foreach (var (did, doc) in entries)
                _docs[did] = doc;
        }

        public bool CanResolve(string did) => _docs.ContainsKey(did);

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            ResolveCallCount++;
            if (_docs.TryGetValue(did, out var doc))
            {
                return Task.FromResult(new DidResolutionResult
                {
                    DidDocument = doc,
                    ResolutionMetadata = new DidResolutionMetadata(),
                });
            }
            return Task.FromResult(DidResolutionResult.NotFound(did));
        }
    }
}
