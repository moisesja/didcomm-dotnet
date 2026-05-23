using DidComm.Jose;
using DidComm.TestSupport;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Secrets;

public sealed class InMemorySecretsResolverTests
{
    [Fact]
    public async Task FindAsync_ReturnsAddedJwk()
    {
        var jwk = new Jwk { Kty = "OKP", Crv = "Ed25519", Kid = "did:example:alice#k1", D = "..." };
        var resolver = new InMemorySecretsResolver(new[] { jwk });

        var hit = await resolver.FindAsync("did:example:alice#k1");

        hit.Should().BeSameAs(jwk);
    }

    [Fact]
    public async Task FindAsync_ReturnsNullForUnknownKid()
    {
        var resolver = new InMemorySecretsResolver();

        var miss = await resolver.FindAsync("did:example:alice#missing");

        miss.Should().BeNull();
    }

    [Fact]
    public void Add_WithoutKid_Throws()
    {
        var resolver = new InMemorySecretsResolver();

        Action act = () => resolver.Add(new Jwk { Kty = "OKP", Crv = "Ed25519" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task FindPresentAsync_ReturnsHeldSubset()
    {
        var resolver = new InMemorySecretsResolver(new[]
        {
            new Jwk { Kid = "did:example:alice#a", Kty = "OKP" },
            new Jwk { Kid = "did:example:alice#b", Kty = "OKP" },
        });

        var present = await resolver.FindPresentAsync(new[]
        {
            "did:example:alice#a",
            "did:example:bob#x",
            "did:example:alice#b",
        });

        present.Should().BeEquivalentTo(new[] { "did:example:alice#a", "did:example:alice#b" });
    }
}
