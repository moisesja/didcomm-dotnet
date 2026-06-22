using DidComm.Secrets;
using NetCrypto;
using DpJwkConversion = DataProofsDotnet.Jose.JwkConversion;
using IEcdhKey = DataProofsDotnet.Jose.Encryption.IEcdhKey;
using Jwk = DataProofsDotnet.Jose.Jwk;

namespace DidComm.Adapters.NetDid;

/// <summary>
/// <see cref="ISecretsResolver"/> bridge backed by a NetCrypto <see cref="IKeyStore"/> (FR-SEC-04)
/// that also implements <see cref="IOpaqueKeyResolver"/> for non-extractable custody (FR-SEC-06). Apps
/// holding keys in a NetCrypto key store can adopt the DIDComm facade with a single resolver — keys
/// never have to be duplicated into a second store, and on the opaque path the private scalar never
/// leaves the keystore boundary (HSM / cloud KMS / OS keychain / MPC).
/// </summary>
/// <remarks>
/// <para>
/// <strong>How it works.</strong> <see cref="IKeyStore"/> exposes <c>SignAsync</c> /
/// <c>CreateSignerAsync</c> and <c>DeriveSharedSecretAsync</c> plus public-key surfaces, but never raw
/// private-key bytes. So <see cref="FindAsync"/> surfaces only the <em>public</em> JWK shape (no
/// <c>d</c>) — enough for the facade to select keys and resolve curves — while the actual secret
/// operations run through the <see cref="IOpaqueKeyResolver"/> handles: signing through
/// <see cref="IKeyStore.CreateSignerAsync"/> (an <c>ISigner</c> that signs inside the store) and ECDH
/// through a <see cref="KeyStoreEcdhKey"/> over <see cref="IKeyStore.DeriveSharedSecretAsync"/>. The
/// facade prefers these handles whenever the registered resolver implements
/// <see cref="IOpaqueKeyResolver"/>, so this adapter is a <strong>sufficient sole resolver</strong>
/// for an HSM-backed agent — it can authcrypt / anoncrypt / sign on send and unpack on receive with
/// no private key bytes leaving the store.
/// </para>
/// <para>
/// <strong>kid → alias.</strong> By default a DID-URL <c>kid</c> is used verbatim as the
/// <see cref="IKeyStore"/> alias (apps that key by DID URL should ensure their store's aliases match).
/// Stores that key differently can pass a <c>kidToAlias</c> mapping to the constructor.
/// </para>
/// </remarks>
public sealed class NetDidKeyStoreSecretsResolver : ISecretsResolver, IOpaqueKeyResolver
{
    private readonly IKeyStore _keyStore;
    private readonly Func<string, string> _kidToAlias;

    /// <summary>Initialise the adapter.</summary>
    /// <param name="keyStore">The backing NetCrypto key store.</param>
    /// <param name="kidToAlias">Optional map from a DID-URL <c>kid</c> to its store alias. Defaults to identity (the kid IS the alias).</param>
    public NetDidKeyStoreSecretsResolver(IKeyStore keyStore, Func<string, string>? kidToAlias = null)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        _keyStore = keyStore;
        _kidToAlias = kidToAlias ?? (static kid => kid);
    }

    /// <inheritdoc />
    public async Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);
        var info = await _keyStore.GetInfoAsync(_kidToAlias(kid), ct).ConfigureAwait(false);
        if (info is null) return null;
        return DpJwkConversion.ToPublicJwk(info.KeyType, info.PublicKey, kid);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kids);
        var aliases = await _keyStore.ListAsync(ct).ConfigureAwait(false);
        var set = new HashSet<string>(aliases, StringComparer.Ordinal);
        return kids.Where(kid => set.Contains(_kidToAlias(kid))).ToArray();
    }

    /// <inheritdoc />
    public async Task<ISigner?> ResolveSignerAsync(string kid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);
        var alias = _kidToAlias(kid);
        // Confirm the key is held before creating the signer, so an unheld kid returns null (the
        // facade then falls through to any extractable resolver) rather than throwing.
        var info = await _keyStore.GetInfoAsync(alias, ct).ConfigureAwait(false);
        if (info is null) return null;
        return await _keyStore.CreateSignerAsync(alias, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEcdhKey?> ResolveKeyAgreementAsync(string kid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);
        var alias = _kidToAlias(kid);
        // One backing lookup confirms held-ness AND yields the key's curve (no caller-supplied crv,
        // no redundant second GetInfo): a held opaque receive is a single keystore round-trip.
        var info = await _keyStore.GetInfoAsync(alias, ct).ConfigureAwait(false);
        if (info is null) return null;
        var crv = DpJwkConversion.ToPublicJwk(info.KeyType, info.PublicKey, kid).Crv;
        if (string.IsNullOrEmpty(crv)) return null;
        return new KeyStoreEcdhKey(_keyStore, alias, crv);
    }
}
