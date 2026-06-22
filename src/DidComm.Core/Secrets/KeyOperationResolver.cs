using DataProofsDotnet.Jose;
using DataProofsDotnet.Jose.Encryption;
using DataProofsDotnet.Jose.Signing;
using DidComm.Jose.Signing;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Secrets;

/// <summary>
/// Turns a kid into a DataProofsDotnet.Jose <em>operation handle</em> — a <see cref="JwsSigner"/> for
/// signing or an <see cref="IEcdhKey"/> for ECDH key agreement — preferring the opaque
/// (non-extractable) path when the registered <see cref="ISecretsResolver"/> also implements
/// <see cref="IOpaqueKeyResolver"/>, and otherwise building the handle from an extractable private
/// <see cref="Jwk"/>. It is the one place the facade / envelope layer resolves "something that can
/// sign or derive" for a kid (FR-SEC-06): on the opaque side no private scalar is ever materialized
/// here; on the extractable side the behavior reproduces the pre-1.1.0 path byte-for-byte.
/// </summary>
internal sealed class KeyOperationResolver
{
    private readonly ISecretsResolver _secrets;
    private readonly IOpaqueKeyResolver? _opaque;
    private readonly JoseCryptoProvider _crypto;

    public KeyOperationResolver(ISecretsResolver secrets, IOpaqueKeyResolver? opaque, JoseCryptoProvider crypto)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(crypto);
        _secrets = secrets;
        _opaque = opaque;
        _crypto = crypto;
    }

    /// <summary>Whether an opaque key resolver is wired in (selects the constant-work receive path).</summary>
    public bool HasOpaqueResolver => _opaque is not null;

    /// <summary>Which of <paramref name="kids"/> the agent holds a usable private key for. Held-ness is
    /// answered by the resolver (a keystore answers from its alias list), so opaque keys count without
    /// exposing material.</summary>
    public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        => _secrets.FindPresentAsync(kids, ct);

    /// <summary>
    /// Build a JWS signer for <paramref name="kid"/> — opaque if the resolver holds it that way, else
    /// from the extractable private JWK — or <c>null</c> when no signing key is held.
    /// </summary>
    public async Task<JwsSigner?> ResolveSignerAsync(string kid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);

        if (_opaque is not null)
        {
            var signer = await _opaque.ResolveSignerAsync(kid, ct).ConfigureAwait(false);
            if (signer is not null)
                return new JwsSigner(signer, kid);
        }

        var jwk = await _secrets.FindAsync(kid, ct).ConfigureAwait(false);
        return jwk is null ? null : JwsSignerFactory.FromPrivateJwk(jwk);
    }

    /// <summary>
    /// Build an ECDH key-agreement handle for <paramref name="kid"/> — opaque if held that way, else a
    /// <see cref="RawEcdhKey"/> over the extractable private scalar — or <c>null</c> when no
    /// key-agreement key is held. The curve is discovered from the resolver's JWK for the kid (a
    /// keystore surfaces a public JWK that still carries <c>crv</c>).
    /// </summary>
    public async Task<IEcdhKey?> ResolveKeyAgreementAsync(string kid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);

        // Opaque first — the handle is self-describing (carries its own crv), so the held opaque path
        // needs no extra crv pre-fetch (one backing lookup, not two). Only when the kid is NOT held
        // opaquely do we consult the extractable resolver and build a RawEcdhKey from the private scalar.
        if (_opaque is not null)
        {
            var handle = await _opaque.ResolveKeyAgreementAsync(kid, ct).ConfigureAwait(false);
            if (handle is not null)
                return handle;
        }

        var jwk = await _secrets.FindAsync(kid, ct).ConfigureAwait(false);
        if (jwk is null || string.IsNullOrEmpty(jwk.D) || string.IsNullOrEmpty(jwk.Crv))
            return null;

        var priv = Base64Url.Decode(jwk.D);
        try
        {
            // RawEcdhKey copies the scalar internally, so the decoded buffer can be wiped immediately.
            return new RawEcdhKey(jwk.Crv, priv, _crypto);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv);
        }
    }
}
