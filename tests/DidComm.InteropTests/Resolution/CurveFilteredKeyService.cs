using DidComm.Jose;
using DidComm.Resolution;

namespace DidComm.InteropTests.Resolution;

/// <summary>
/// Narrows the keyAgreement keys a key service returns to one curve, so a test can make the facade
/// negotiate P-256, P-384 or P-521 against a DID document that also publishes X25519 keys (the
/// facade prefers X25519). Authorization checks pass through unfiltered: this shapes key
/// <em>selection</em>, never trust. Mirrors <c>tools/FixtureGen/CurveFilteredKeyService</c>.
/// </summary>
internal sealed class CurveFilteredKeyService : IDidKeyService
{
    private readonly IDidKeyService _inner;
    private readonly string _keyAgreementCrv;

    public CurveFilteredKeyService(IDidKeyService inner, string keyAgreementCrv)
    {
        _inner = inner;
        _keyAgreementCrv = keyAgreementCrv;
    }

    public async Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
    {
        var keys = await _inner.GetVerificationMethodsAsync(did, relationship, ct).ConfigureAwait(false);
        if (relationship != VerificationRelationship.KeyAgreement)
            return keys;
        return keys.Where(k => string.Equals(k.Crv, _keyAgreementCrv, StringComparison.Ordinal)).ToArray();
    }

    public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
        => _inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

    public void RejectUnsupportedMethod(string did) => _inner.RejectUnsupportedMethod(did);
}
