using DidComm.Jose;
using DidComm.Secrets;
using NetDid.Core;
using NetDidJwkConverter = NetDid.Core.Jwk.JwkConverter;

namespace DidComm.Adapters.NetDid;

/// <summary>
/// Optional <see cref="ISecretsResolver"/> bridge backed by an <see cref="IKeyStore"/>
/// (FR-SEC-04, SHOULD). Apps already holding net-did keys can avoid duplicating their key
/// material into a second store.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope:</strong> <see cref="IKeyStore"/> exposes <c>SignAsync</c> + public-key /
/// multibase surfaces, but never raw private-key bytes — by design, so HSM-backed stores are
/// safe. That means this adapter surfaces only the <em>public</em> JWK shape with no <c>d</c>
/// component. DIDComm signing (the JWS path inside <c>EnvelopeWriter.PackSigned</c>, the
/// outer signed envelope, and the <c>from_prior</c> JWT) needs access to the private bytes
/// today; until net-did exposes an <c>IEcdhProvider</c> / opaque-signer surface that the
/// DIDComm crypto layer can drive without raw bytes, this adapter ships in
/// "public-resolution only" form. It is therefore <em>not</em> sufficient as the sole
/// <see cref="ISecretsResolver"/> in a fully-functional facade; consumers using HSM-backed
/// stores typically pair it with an extractable-secret store for the small set of keys that
/// must perform crypto.
/// </para>
/// <para>
/// The kid passed to <see cref="FindAsync"/> is treated as the key alias inside the
/// <see cref="IKeyStore"/>. Apps that key by DID URL should ensure their store's aliases
/// match.
/// </para>
/// </remarks>
public sealed class NetDidKeyStoreSecretsResolver : ISecretsResolver
{
    private readonly IKeyStore _keyStore;

    /// <summary>Initialise the adapter.</summary>
    /// <param name="keyStore">The backing net-did key store.</param>
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
        var jwk = NetDidJwkConverter.ToPublicJwk(info.KeyType, info.PublicKey);
        return new Jwk
        {
            Kty = jwk.Kty,
            Crv = jwk.Crv,
            X = jwk.X,
            Y = jwk.Y,
            Kid = kid,
        };
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
