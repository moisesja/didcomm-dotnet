using DidComm.Exceptions;
using DidComm.Resolution;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
// NetDid 2.0 introduced its own Model.VerificationRelationship; the DIDComm key-service API speaks
// the DidComm.Resolution enum, so bind the unqualified name to that one.
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Resolution;

public sealed class NetDidKeyServiceTests
{
    [Fact]
    public void RejectUnsupportedMethod_ThrowsForDidWeb()
    {
        var sut = new NetDidKeyService(new StubResolver());

        var act = () => sut.RejectUnsupportedMethod("did:web:example.com");

        act.Should().Throw<UnsupportedDidMethodException>()
            .Which.Method.Should().Be("web");
    }

    [Fact]
    public void RejectUnsupportedMethod_DoesNotThrowForKey()
    {
        var sut = new NetDidKeyService(new StubResolver());

        sut.Invoking(s => s.RejectUnsupportedMethod("did:example:alice"))
            .Should().NotThrow();
    }

    [Fact]
    public void RejectUnsupportedMethod_ThrowsForGarbageInput()
    {
        var sut = new NetDidKeyService(new StubResolver());

        sut.Invoking(s => s.RejectUnsupportedMethod("not-a-did"))
            .Should().Throw<DidResolutionException>();
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_DidWeb_RejectedBeforeResolution()
    {
        var resolver = new StubResolver();
        var sut = new NetDidKeyService(resolver);

        var act = async () => await sut.GetVerificationMethodsAsync(
            "did:web:example.com", VerificationRelationship.KeyAgreement);

        await act.Should().ThrowAsync<UnsupportedDidMethodException>();
        resolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_NoDocument_ThrowsDidResolutionException()
    {
        var resolver = new StubResolver(); // returns NotFound for every DID
        var sut = new NetDidKeyService(resolver);

        var act = async () => await sut.GetVerificationMethodsAsync(
            "did:example:unknown", VerificationRelationship.KeyAgreement);

        await act.Should().ThrowAsync<DidResolutionException>();
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_ResolvesEmbeddedJwk()
    {
        var vm = new VerificationMethod
        {
            Id = "did:example:alice#kx",
            Type = "JsonWebKey2020",
            Controller = new Did("did:example:alice"),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = "OKP",
                Crv = "X25519",
                X = "avH0O2Y4tqLAq8y9zpianr8ajii5m4F_mICrzNlatXs",
            },
        };
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver((doc.Id.Value!, doc)));

        var keys = await sut.GetVerificationMethodsAsync("did:example:alice", VerificationRelationship.KeyAgreement);

        keys.Should().ContainSingle();
        keys[0].Kid.Should().Be("did:example:alice#kx");
        keys[0].Crv.Should().Be("X25519");
        keys[0].X.Should().Be("avH0O2Y4tqLAq8y9zpianr8ajii5m4F_mICrzNlatXs");
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_DereferencesFragmentReference()
    {
        var vm = new VerificationMethod
        {
            Id = "did:example:alice#key-1",
            Type = "JsonWebKey2020",
            Controller = new Did("did:example:alice"),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = "OKP",
                Crv = "Ed25519",
                X = "G-boxFB6vOZBu-wXkm-9Lh79I8nf9Z50cILaOgKKGww",
            },
        };
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            Authentication = new[] { VerificationRelationshipEntry.FromReference("did:example:alice#key-1") },
        };
        var sut = new NetDidKeyService(new StubResolver((doc.Id.Value!, doc)));

        var keys = await sut.GetVerificationMethodsAsync("did:example:alice", VerificationRelationship.Authentication);

        keys.Should().ContainSingle().Which.Kid.Should().Be("did:example:alice#key-1");
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_UnresolvableFragment_Throws()
    {
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = Array.Empty<VerificationMethod>(),
            KeyAgreement = new[] { VerificationRelationshipEntry.FromReference("did:example:alice#ghost") },
        };
        var sut = new NetDidKeyService(new StubResolver((doc.Id.Value!, doc)));

        var act = async () => await sut.GetVerificationMethodsAsync("did:example:alice", VerificationRelationship.KeyAgreement);

        await act.Should().ThrowAsync<DidResolutionException>()
            .Where(e => e.Message.Contains("did:example:alice#ghost"));
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_FiltersUnsupportedCurveForKeyAgreement()
    {
        // Ed25519 is for signing, not key agreement — must be filtered out when requested under KeyAgreement.
        var vm = new VerificationMethod
        {
            Id = "did:example:alice#ed",
            Type = "JsonWebKey2020",
            Controller = new Did("did:example:alice"),
            PublicKeyJwk = new JsonWebKey
            {
                Kty = "OKP",
                Crv = "Ed25519",
                X = "G-boxFB6vOZBu-wXkm-9Lh79I8nf9Z50cILaOgKKGww",
            },
        };
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver((doc.Id.Value!, doc)));

        var keys = await sut.GetVerificationMethodsAsync("did:example:alice", VerificationRelationship.KeyAgreement);

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_MultikeyMethod_DecodedToJwk()
    {
        // z6LS… is a valid X25519 Multikey (multicodec 0xEC + 32 raw bytes encoded base58btc with "z" prefix).
        var vm = new VerificationMethod
        {
            Id = "did:example:alice#mb",
            Type = "Multikey",
            Controller = new Did("did:example:alice"),
            PublicKeyMultibase = "z6LSp2h4y6EeqhKpLUGeKCaWTcH2PHPWbXo4paERM5QLsstT",
        };
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver((doc.Id.Value!, doc)));

        var keys = await sut.GetVerificationMethodsAsync("did:example:alice", VerificationRelationship.KeyAgreement);

        keys.Should().ContainSingle().Which.Crv.Should().Be("X25519");
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_MalformedMultibase_SkippedGracefully()
    {
        var vm = new VerificationMethod
        {
            Id = "did:example:alice#bad",
            Type = "Multikey",
            Controller = new Did("did:example:alice"),
            PublicKeyMultibase = "zNotARealMultibaseValue!!",
        };
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            KeyAgreement = new[] { VerificationRelationshipEntry.FromEmbedded(vm) },
        };
        var sut = new NetDidKeyService(new StubResolver((doc.Id.Value!, doc)));

        var keys = await sut.GetVerificationMethodsAsync("did:example:alice", VerificationRelationship.KeyAgreement);

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task IsKeyAuthorizedAsync_TrueWhenKidPresent()
    {
        var vm = new VerificationMethod
        {
            Id = "did:example:alice#key-1",
            Type = "JsonWebKey2020",
            Controller = new Did("did:example:alice"),
            PublicKeyJwk = new JsonWebKey { Kty = "OKP", Crv = "Ed25519", X = "G-boxFB6vOZBu-wXkm-9Lh79I8nf9Z50cILaOgKKGww" },
        };
        var doc = new DidDocument
        {
            Id = new Did("did:example:alice"),
            VerificationMethod = new[] { vm },
            Authentication = new[] { VerificationRelationshipEntry.FromReference(vm.Id) },
        };
        var sut = new NetDidKeyService(new StubResolver((doc.Id.Value!, doc)));

        (await sut.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#key-1", VerificationRelationship.Authentication))
            .Should().BeTrue();
        (await sut.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#nope", VerificationRelationship.Authentication))
            .Should().BeFalse();
        (await sut.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#key-1", VerificationRelationship.KeyAgreement))
            .Should().BeFalse();
    }

    /// <summary>Hand-rolled resolver that returns pre-canned documents and counts invocations.</summary>
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
