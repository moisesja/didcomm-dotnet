using DidComm.Secrets;
using NetCrypto;
using DpJwkConversion = DataProofsDotnet.Jose.JwkConversion;
using Jwk = DataProofsDotnet.Jose.Jwk;

namespace DidComm.Adapters.NetDid;

/// <summary>
/// Optional <see cref="ISecretsResolver"/> bridge backed by a NetCrypto <see cref="IKeyStore"/>
/// (FR-SEC-04, SHOULD). Apps already holding keys in a NetCrypto key store can avoid duplicating
/// their key material into a second store.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope:</strong> <see cref="IKeyStore"/> exposes <c>SignAsync</c> and
/// <c>DeriveSharedSecretAsync</c> plus public-key surfaces, but never raw private-key bytes — by
/// design, so HSM-backed stores are safe. That means this adapter surfaces only the
/// <em>public</em> JWK shape with no <c>d</c> component. DIDComm signing
/// (<c>EnvelopeWriter.PackSignedAsync</c>, the outer signed envelope, and the <c>from_prior</c>
/// JWT) and decryption still resolve private JWKs through <see cref="FindAsync"/> today, so this
/// adapter ships in "public-resolution only" form. It is therefore <em>not</em> sufficient as the
/// sole <see cref="ISecretsResolver"/> in a fully-functional facade; an opaque-signer / opaque-ECDH
/// path over <see cref="IKeyStore.SignAsync"/> and <see cref="IKeyStore.DeriveSharedSecretAsync"/>
/// is future work. Consumers using HSM-backed stores typically pair it with an extractable-secret
/// store for the keys that must perform crypto.
/// </para>
/// <para>
/// The kid passed to <see cref="FindAsync"/> is treated as the key alias inside the
/// <see cref="IKeyStore"/>. Apps that key by DID URL should ensure their store's aliases match.
/// </para>
/// </remarks>
public sealed class NetDidKeyStoreSecretsResolver : ISecretsResolver
{
    private readonly IKeyStore _keyStore;

    /// <summary>Initialise the adapter.</summary>
    /// <param name="keyStore">The backing NetCrypto key store.</param>
    public NetDidKeyStoreSecretsResolver(IKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        _keyStore = keyStore;
    }

    /// <inheritdoc />
    public async Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);
        var info = await _keyStore.GetInfoAsync(kid, ct).ConfigureAwait(false);
        if (info is null) return null;
        return DpJwkConversion.ToPublicJwk(info.KeyType, info.PublicKey, kid);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kids);
        var aliases = await _keyStore.ListAsync(ct).ConfigureAwait(false);
        var set = new HashSet<string>(aliases, StringComparer.Ordinal);
        return kids.Where(set.Contains).ToArray();
    }
}
