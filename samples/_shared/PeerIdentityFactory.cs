using DidComm.Jose;
using NetDid.Core;
using NetDid.Core.Crypto;
using NetDid.Core.Model;
using NetDid.Method.Peer;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Samples.Shared;

/// <summary>
/// A peer-DID identity: the resolved DID + the matching private JWKs the application owns.
/// </summary>
/// <param name="Did">The newly-minted <c>did:peer:2</c>.</param>
/// <param name="Privates">Private JWKs for every verification method in the DID Doc. Each <c>Kid</c> is an absolute DID URL.</param>
public sealed record PeerIdentity(string Did, IReadOnlyList<Jwk> Privates);

/// <summary>
/// Mints <c>did:peer:2</c> identities backed by net-did's <see cref="IDidManager"/> and
/// surfaces the private JWKs so the application can load them into its
/// <see cref="DidComm.Secrets.ISecretsResolver"/>.
/// </summary>
public static class PeerIdentityFactory
{
    /// <summary>Mint a fresh <c>did:peer:2</c> with one X25519 keyAgreement key and one Ed25519 authentication key.</summary>
    /// <param name="manager">net-did DID manager (resolved from the DI container after <c>AddDidComm(b =&gt; b.UseNetDidResolver()...)</c>).</param>
    /// <param name="keyGenerator">net-did key generator (resolved from the same container).</param>
    /// <param name="cryptoProvider">net-did crypto provider (resolved from the same container).</param>
    public static async Task<PeerIdentity> CreateAsync(
        IDidManager manager,
        IKeyGenerator keyGenerator,
        ICryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(keyGenerator);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var kxPair = keyGenerator.Generate(KeyType.X25519);
        var authPair = keyGenerator.Generate(KeyType.Ed25519);

        var kxSigner = new KeyPairSigner(kxPair, cryptoProvider);
        var authSigner = new KeyPairSigner(authPair, cryptoProvider);

        var options = new DidPeerCreateOptions
        {
            Numalgo = PeerNumalgo.Two,
            Keys = new[]
            {
                new PeerKeyPurpose(kxSigner, PeerPurpose.KeyAgreement),
                new PeerKeyPurpose(authSigner, PeerPurpose.Authentication),
            },
        };

        var result = await manager.CreateAsync(options).ConfigureAwait(false);
        var didValue = result.Did.Value
            ?? throw new InvalidOperationException("DID manager returned a DID with no Value.");

        var privates = new List<Jwk>(2)
        {
            BuildPrivateJwk(kxPair, didValue, result.DidDocument),
            BuildPrivateJwk(authPair, didValue, result.DidDocument),
        };

        return new PeerIdentity(didValue, privates);
    }

    private static Jwk BuildPrivateJwk(KeyPair keyPair, string did, DidDocument doc)
    {
        var multibase = keyPair.MultibasePublicKey;
        var match = doc.VerificationMethod?.FirstOrDefault(vm =>
                        string.Equals(vm.PublicKeyMultibase, multibase, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"KeyPair (multibase={multibase}) is not represented in the resolved DID Document for {did}.");

        // numalgo 2 emits relative ids like "#key-1"; the envelope layer keys by absolute DID URL.
        var kid = match.Id.StartsWith('#') ? did + match.Id : match.Id;

        var jwk = NetDidJwkConverter.ToPrivateJwk(keyPair);
        return new Jwk
        {
            Kty = jwk.Kty ?? string.Empty,
            Crv = jwk.Crv,
            X = jwk.X,
            Y = jwk.Y,
            D = jwk.D,
            Kid = kid,
        };
    }
}
