using DidComm.Jose;
using DidComm.Secrets;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Secrets;

public sealed class ISecretsResolverContractTests
{
    [Fact]
    public async Task FindAsync_ReturnsNull_WhenKidUnknown()
    {
        var resolver = new DictionarySecretsResolver();
        var result = await resolver.FindAsync("did:example:alice#missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task FindAsync_ReturnsJwk_WhenKidHeld()
    {
        const string kid = "did:example:alice#key-1";
        var jwk = new Jwk { Kty = "OKP", Crv = "Ed25519", Kid = kid, X = "x", D = "d" };
        var resolver = new DictionarySecretsResolver { { kid, jwk } };

        var result = await resolver.FindAsync(kid);
        result.Should().BeSameAs(jwk);
    }

    [Fact]
    public async Task FindPresentAsync_ReturnsOnlyHeldSubset()
    {
        var resolver = new DictionarySecretsResolver
        {
            { "did:example:alice#a", new Jwk { Kid = "did:example:alice#a" } },
            { "did:example:alice#b", new Jwk { Kid = "did:example:alice#b" } },
        };

        var present = await resolver.FindPresentAsync(
            new[] { "did:example:alice#a", "did:example:bob#x", "did:example:alice#b" });

        present.Should().BeEquivalentTo(new[] { "did:example:alice#a", "did:example:alice#b" });
    }

    /// <summary>Dictionary-backed test fake exercising the <see cref="ISecretsResolver"/> shape.</summary>
    private sealed class DictionarySecretsResolver : Dictionary<string, Jwk>, ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => Task.FromResult(TryGetValue(kid, out var jwk) ? jwk : null);

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        {
            IReadOnlyList<string> hits = kids.Where(ContainsKey).ToList();
            return Task.FromResult(hits);
        }
    }
}
