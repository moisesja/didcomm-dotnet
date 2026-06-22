using DidComm.Adapters.NetDid;
using DidComm.Protocols.Rotation;
using DataProofsDotnet.Jose.Encryption;
using FluentAssertions;
using NetCrypto;
using Xunit;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;
using JwkConversion = DataProofsDotnet.Jose.JwkConversion;
using JwsSigner = DataProofsDotnet.Jose.Signing.JwsSigner;

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

    [Fact]
    public async Task ResolveSignerAsync_ReturnsNullForUnknownKid()
    {
        IKeyStore store = new InMemoryKeyStore(new DefaultKeyGenerator(), new DefaultCryptoProvider());
        var resolver = new NetDidKeyStoreSecretsResolver(store);

        (await resolver.ResolveSignerAsync("nope")).Should().BeNull();
        (await resolver.ResolveKeyAgreementAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task ResolveKeyAgreementAsync_derives_the_same_Z_as_the_in_process_path()
    {
        // KAT-style equivalence (FR-SEC-06): the opaque keystore ECDH and the extractable RawEcdhKey
        // over the SAME private key must produce a byte-identical shared secret — so an envelope packed
        // for either path decrypts on the other. (We hold an in-process copy of the scalar ONLY to build
        // the comparison baseline; the keystore handle never exposes it.)
        var keyGen = new DefaultKeyGenerator();
        var crypto = new DefaultCryptoProvider();
        var local = keyGen.Generate(KeyType.X25519);
        var peer = keyGen.Generate(KeyType.X25519);

        var store = new InMemoryKeyStore(keyGen, crypto);
        await store.ImportAsync("did:example:alice#x", local);
        var resolver = new NetDidKeyStoreSecretsResolver(store);

        var opaque = await resolver.ResolveKeyAgreementAsync("did:example:alice#x");
        opaque.Should().NotBeNull();
        opaque!.Crv.Should().Be("X25519");

        var inProcess = new RawEcdhKey("X25519", local.PrivateKey, new JoseCryptoProvider());

        var zOpaque = await opaque.DeriveAsync(peer.PublicKey);
        var zInProcess = await inProcess.DeriveAsync(peer.PublicKey);

        zOpaque.Should().Equal(zInProcess);
    }

    [Fact]
    public async Task FromPrior_opaque_eddsa_signer_matches_the_extractable_jwt()
    {
        // EdDSA is deterministic, so signing the same claims with the same Ed25519 key opaquely
        // (keystore ISigner) and extractably (private JWK) MUST yield byte-identical from_prior JWTs —
        // proving the opaque signing overload is a drop-in, and that the rotation JWT can be minted
        // without the prior DID's signing scalar leaving custody (FR-SEC-06 / issue #45 table).
        var keyGen = new DefaultKeyGenerator();
        var pair = keyGen.Generate(KeyType.Ed25519);
        const string kid = "did:example:alice#auth";
        var claims = new FromPriorClaims(Sub: "did:example:alice", Iss: "did:example:alice-prior", Iat: 1_700_000_000);

        var privJwk = JwkConversion.ToPrivateJwk(pair, kid);
        var extractableJwt = await FromPriorBuilder.BuildAsync(claims, privJwk);

        var store = new InMemoryKeyStore(keyGen, new DefaultCryptoProvider());
        await store.ImportAsync(kid, pair);
        var resolver = new NetDidKeyStoreSecretsResolver(store);
        var signer = await resolver.ResolveSignerAsync(kid);
        signer.Should().NotBeNull();

        var opaqueJwt = await FromPriorBuilder.BuildAsync(claims, new JwsSigner(signer!, kid));

        opaqueJwt.Should().Be(extractableJwt);
    }
}
