using DidComm.Jose;
using DidComm.Resolution;

namespace FixtureGen;

/// <summary>
/// Pack-time curve selector: wraps an <see cref="IDidKeyService"/> and narrows the JWKs
/// returned for a relationship to a single curve. The facade always picks the
/// highest-preference common curve (X25519 first) and the first held authentication key, so
/// publishing a P-384 anoncrypt vector or an ES256K signature requires the key service to
/// only surface that curve. Authorization checks pass through unfiltered — this shapes key
/// <em>selection</em>, never trust.
/// </summary>
internal sealed class CurveFilteredKeyService : IDidKeyService
{
    private readonly IDidKeyService _inner;
    private readonly string? _keyAgreementCrv;
    private readonly string? _authenticationCrv;

    /// <param name="inner">The unfiltered key service.</param>
    /// <param name="keyAgreementCrv">Only surface keyAgreement JWKs on this curve (<c>null</c> = no filter).</param>
    /// <param name="authenticationCrv">Only surface authentication JWKs on this curve (<c>null</c> = no filter).</param>
    public CurveFilteredKeyService(IDidKeyService inner, string? keyAgreementCrv = null, string? authenticationCrv = null)
    {
        _inner = inner;
        _keyAgreementCrv = keyAgreementCrv;
        _authenticationCrv = authenticationCrv;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
    {
        var keys = await _inner.GetVerificationMethodsAsync(did, relationship, ct).ConfigureAwait(false);
        var crv = relationship == VerificationRelationship.KeyAgreement ? _keyAgreementCrv : _authenticationCrv;
        if (crv is null)
            return keys;
        return keys.Where(k => string.Equals(k.Crv, crv, StringComparison.Ordinal)).ToArray();
    }

    /// <inheritdoc />
    public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
        => _inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

    /// <inheritdoc />
    public void RejectUnsupportedMethod(string did) => _inner.RejectUnsupportedMethod(did);
}
