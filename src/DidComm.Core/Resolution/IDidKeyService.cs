using DidComm.Exceptions;
using DidComm.Jose;

namespace DidComm.Resolution;

/// <summary>
/// DID resolution + verification-method extraction surface used by the facade (FR-DID-01..05).
/// Implementations adapt an external DID resolver (the default ships <c>NetDidKeyService</c>
/// which wraps NetDid 1.3.0+) into the JWK-shaped form the JOSE composition layer consumes.
/// </summary>
/// <remarks>
/// <para>
/// The facade calls <see cref="RejectUnsupportedMethod"/> at every public entry point on every
/// DID-bearing field — recipient lists, <c>from</c>, <c>signFrom</c>, and the inner plaintext
/// <c>from</c> / <c>to</c> revealed on unpack. This satisfies FR-DID-06 / DD-08 (active
/// <c>did:web</c> rejection) before any envelope work begins.
/// </para>
/// <para>
/// <see cref="GetVerificationMethodsAsync"/> returns public JWKs only; private key material
/// is the responsibility of <see cref="Secrets.ISecretsResolver"/>. The result list MAY be
/// empty (a DID Document with no keys for the requested relationship), in which case the
/// caller raises an appropriate operation-specific error.
/// </para>
/// </remarks>
public interface IDidKeyService
{
    /// <summary>
    /// Resolve <paramref name="did"/> and return the public JWKs declared under
    /// <paramref name="relationship"/>. Fragment references inside the relationship list are
    /// dereferenced against the same document's top-level <c>verificationMethod</c> array.
    /// </summary>
    /// <param name="did">A DID (no fragment) — the subject to resolve.</param>
    /// <param name="relationship">Either <see cref="VerificationRelationship.KeyAgreement"/> or <see cref="VerificationRelationship.Authentication"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="UnsupportedDidMethodException">When <paramref name="did"/> uses a method this library refuses to resolve (e.g. <c>did:web</c>).</exception>
    /// <exception cref="DidResolutionException">When resolution fails or the document contains no extractable keys for the requested relationship.</exception>
    Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default);

    /// <summary>
    /// Authorization check used by the FR-CONSIST-06 resolver-backed consistency rule: returns
    /// <c>true</c> iff <paramref name="kid"/> appears in the resolved document of
    /// <paramref name="did"/> under <paramref name="relationship"/>.
    /// </summary>
    /// <param name="did">The DID asserted to control the key.</param>
    /// <param name="kid">The key identifier whose presence is being checked.</param>
    /// <param name="relationship">Relationship under which the key MUST be authorized.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default);

    /// <summary>
    /// Synchronously fail when <paramref name="did"/> uses a method this library refuses to
    /// resolve. Called by the facade at every API perimeter before any async work begins so
    /// FR-DID-06 / DD-08 are enforced up front.
    /// </summary>
    /// <param name="did">The DID to check.</param>
    /// <exception cref="UnsupportedDidMethodException">When the method is intentionally unsupported (canonically <c>did:web</c>).</exception>
    void RejectUnsupportedMethod(string did);
}
