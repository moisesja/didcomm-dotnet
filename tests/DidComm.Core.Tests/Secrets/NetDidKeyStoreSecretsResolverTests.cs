using DidComm.Adapters.NetDid;
using FluentAssertions;
using NetDid.Core;
using NetDid.Core.Crypto;
using NetDid.Core.KeyStore;
using Xunit;

namespace DidComm.Tests.Secrets;

public sealed class NetDidKeyStoreSecretsResolverTests
{
    [Fact]
    public async Task FindAsync_ReturnsPublicJwkForAlias()
    {
        IKeyStore store = new InMemoryKeyStore(new DefaultKeyGenerator(), new DefaultCryptoProvider());
        await store.GenerateAsync("alice-key-1", KeyType.Ed25519);
        var resolver = new NetDidKeyStoreSecretsResolver(store);

        var jwk = await resolver.FindAsync("alice-key-1");

        jwk.Should().NotBeNull();
        jwk!.Kid.Should().Be("alice-key-1");
        jwk.Kty.Should().Be("OKP");
        jwk.Crv.Should().Be("Ed25519");
        jwk.X.Should().NotBeNullOrEmpty();
        jwk.D.Should().BeNull("IKeyStore intentionally does not expose private bytes");
    }

    [Fact]
    public async Task FindAsync_ReturnsNullForUnknownAlias()
    {
        IKeyStore store = new InMemoryKeyStore(new DefaultKeyGenerator(), new DefaultCryptoProvider());
        var resolver = new NetDidKeyStoreSecretsResolver(store);

        var jwk = await resolver.FindAsync("nope");

        jwk.Should().BeNull();
    }

    [Fact]
    public async Task FindPresentAsync_ReturnsHeldSubset()
    {
        IKeyStore store = new InMemoryKeyStore(new DefaultKeyGenerator(), new DefaultCryptoProvider());
        await store.GenerateAsync("a", KeyType.Ed25519);
        await store.GenerateAsync("b", KeyType.X25519);
        var resolver = new NetDidKeyStoreSecretsResolver(store);

        var present = await resolver.FindPresentAsync(new[] { "a", "b", "c" });

        present.Should().BeEquivalentTo(new[] { "a", "b" });
    }
}
