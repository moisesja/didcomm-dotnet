using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Resolution;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Resolution;

public sealed class IDidKeyServiceContractTests
{
    [Fact]
    public void RejectUnsupportedMethod_ThrowsForDidWeb()
    {
        var fake = new FakeKeyService();

        var act = () => fake.RejectUnsupportedMethod("did:web:example.com");

        act.Should().Throw<UnsupportedDidMethodException>()
            .Which.Method.Should().Be("web");
    }

    [Fact]
    public void RejectUnsupportedMethod_AllowsSupportedMethods()
    {
        var fake = new FakeKeyService();

        fake.Invoking(s => s.RejectUnsupportedMethod("did:key:z6Mkx")).Should().NotThrow();
        fake.Invoking(s => s.RejectUnsupportedMethod("did:peer:0z6Mkx")).Should().NotThrow();
    }

    [Fact]
    public async Task GetVerificationMethodsAsync_ReturnsConfiguredKeys()
    {
        var jwk = new Jwk { Kty = "OKP", Crv = "X25519", Kid = "did:example:alice#kx" };
        var fake = new FakeKeyService(("did:example:alice", VerificationRelationship.KeyAgreement, jwk));

        var keys = await fake.GetVerificationMethodsAsync("did:example:alice", VerificationRelationship.KeyAgreement);

        keys.Should().ContainSingle().Which.Should().BeSameAs(jwk);
    }

    [Fact]
    public async Task IsKeyAuthorizedAsync_ReflectsConfiguredMembership()
    {
        var jwk = new Jwk { Kty = "OKP", Crv = "Ed25519", Kid = "did:example:alice#key-1" };
        var fake = new FakeKeyService(("did:example:alice", VerificationRelationship.Authentication, jwk));

        (await fake.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#key-1", VerificationRelationship.Authentication))
            .Should().BeTrue();
        (await fake.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#missing", VerificationRelationship.Authentication))
            .Should().BeFalse();
        (await fake.IsKeyAuthorizedAsync("did:example:alice", "did:example:alice#key-1", VerificationRelationship.KeyAgreement))
            .Should().BeFalse();
    }

    /// <summary>Hand-rolled test fake exercising the <see cref="IDidKeyService"/> shape.</summary>
    private sealed class FakeKeyService : IDidKeyService
    {
        private readonly Dictionary<(string Did, VerificationRelationship Rel), List<Jwk>> _keys = new();

        public FakeKeyService(params (string Did, VerificationRelationship Rel, Jwk Key)[] entries)
        {
            foreach (var (did, rel, key) in entries)
            {
                if (!_keys.TryGetValue((did, rel), out var list))
                    _keys[(did, rel)] = list = new List<Jwk>();
                list.Add(key);
            }
        }

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            IReadOnlyList<Jwk> result = _keys.TryGetValue((did, relationship), out var list)
                ? list
                : Array.Empty<Jwk>();
            return Task.FromResult(result);
        }

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
        {
            var present = _keys.TryGetValue((did, relationship), out var list)
                && list.Any(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));
            return Task.FromResult(present);
        }

        public void RejectUnsupportedMethod(string did)
        {
            if (did.StartsWith("did:web:", StringComparison.Ordinal))
                throw new UnsupportedDidMethodException("web", did, "did:web is rejected per DD-08");
        }
    }
}
