using DataProofsDotnet.Jose.Encryption;
using NetCrypto;

namespace DidComm.Adapters.NetDid;

/// <summary>
/// An <see cref="IEcdhKey"/> that performs ECDH key agreement <em>inside</em> a NetCrypto
/// <see cref="IKeyStore"/> — the private scalar never leaves the store, so HSM / KMS / keychain
/// custody holds (FR-SEC-06). Wraps a <c>(keystore, alias, crv)</c> triple; <see cref="DeriveAsync"/>
/// delegates to <see cref="IKeyStore.DeriveSharedSecretAsync"/> and returns the raw, unhashed shared
/// secret <c>Z</c> that the JOSE layer feeds into the Concat-KDF (everything after <c>Z</c> is
/// public-data math).
/// </summary>
internal sealed class KeyStoreEcdhKey : IEcdhKey
{
    private readonly IKeyStore _keyStore;
    private readonly string _alias;

    /// <summary>Wrap a key-agreement key held under <paramref name="alias"/> in <paramref name="keyStore"/>.</summary>
    /// <param name="keyStore">The backing NetCrypto key store.</param>
    /// <param name="alias">The store alias of the local key-agreement key.</param>
    /// <param name="crv">The JWK <c>crv</c> the key agrees on (X25519 / P-256 / P-384 / P-521).</param>
    public KeyStoreEcdhKey(IKeyStore keyStore, string alias, string crv)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentException.ThrowIfNullOrEmpty(crv);
        _keyStore = keyStore;
        _alias = alias;
        Crv = crv;
    }

    /// <inheritdoc />
    public string Crv { get; }

    /// <inheritdoc />
    public ValueTask<byte[]> DeriveAsync(ReadOnlyMemory<byte> peerPublicKey, CancellationToken ct = default)
        // The peer key is already in the curve's canonical encoding the JOSE layer assembled from the
        // JWE epk / sender pub — the same encoding NetCrypto's DeriveSharedSecret expects — so it
        // flows straight through. The store returns the raw Z (no KDF).
        => new(_keyStore.DeriveSharedSecretAsync(_alias, peerPublicKey, ct));
}
