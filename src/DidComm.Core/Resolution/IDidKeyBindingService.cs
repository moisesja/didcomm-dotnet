using DidComm.Exceptions;

namespace DidComm.Resolution;

/// <summary>
/// Optional capability interface for <see cref="IDidKeyService"/> implementations that can
/// project a verification method <em>atomically</em> out of a single DID resolution — the key
/// material and the authority evidence (kid / DID subject / controller / relationship) from the
/// same document instance. The unpack pipeline probes for this capability; when present it
/// captures per-operation bindings instead of re-resolving sender/signer authority after the
/// cryptographic layer succeeded, closing the two-resolution TOCTOU identity gap (#56).
/// </summary>
/// <remarks>
/// <para>
/// Implementations that do not add this interface keep the exact pre-1.4.0 behavior: JOSE key
/// lookup through <see cref="IDidKeyService.GetVerificationMethodsAsync"/> plus the separate
/// resolver-backed <see cref="IDidKeyService.IsKeyAuthorizedAsync"/> authorization check. On
/// that path <c>DidComm.Facade.UnpackResult</c> surfaces no <c>VerifiedKeyBinding</c> —
/// kid strings and flags alone are <em>not</em> same-resolution controller provenance.
/// </para>
/// <para>
/// <strong>Implementers: adding this interface replaces your authorization hook during unpack.</strong>
/// Re-resolving after the cryptographic layer is exactly the gap this closes, so on the capability
/// path the pipeline never calls <see cref="IDidKeyService.IsKeyAuthorizedAsync"/> for the sender,
/// signer, or recipient — it applies the built-in id-subject + <c>controller</c> rule to the
/// captured binding instead. Any extra policy your <c>IsKeyAuthorizedAsync</c> enforced (revocation
/// lists, allowlists, key-purpose or method restrictions) MUST move into
/// <see cref="ResolveKeyBindingAsync"/>: return <c>null</c> for a key your policy rejects, so the
/// rejection happens against the same document the key came from. <c>IsKeyAuthorizedAsync</c> is
/// still used elsewhere (e.g. mediator routing-key checks), so keep it implemented.
/// </para>
/// <para>
/// The capability is discovered by an interface probe on the registered service instance, so a
/// decorator (caching, logging, tenant routing) that wraps a capable service without also
/// implementing this interface silently returns the whole unpack path to the pre-1.4.0 behavior
/// and to null bindings. Forward the capability in decorators.
/// </para>
/// </remarks>
public interface IDidKeyBindingService
{
    /// <summary>
    /// Resolve the DID subject of <paramref name="kid"/> <strong>once</strong> and return the
    /// binding for the verification method whose normalized (absolute) id equals
    /// <paramref name="kid"/> under <paramref name="relationship"/>.
    /// </summary>
    /// <remarks>
    /// Contract:
    /// <list type="bullet">
    /// <item>Exactly one resolution per call; every member of the returned binding MUST come
    /// from that single resolved document.</item>
    /// <item>Returns <c>null</c> when the kid is not a parseable DID URL, is absent under the
    /// relationship, or its key cannot be materialized for the relationship's curve family.</item>
    /// <item>MUST fail (throw <see cref="DidResolutionException"/>) when more than one entry
    /// under the relationship normalizes to the same <paramref name="kid"/> — duplicate or
    /// shadowed ids are rejected instead of silently taking the first match.</item>
    /// <item>This method projects evidence; it does NOT itself decide authorization. The
    /// unpack pipeline enforces the controller rule against the captured binding.</item>
    /// </list>
    /// </remarks>
    /// <param name="kid">Key identifier — a DID URL with fragment.</param>
    /// <param name="relationship">The relationship the method must be listed under.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="DidResolutionException">When resolution fails or the kid matches more than one entry (ambiguous binding).</exception>
    /// <exception cref="UnsupportedDidMethodException">When the kid's DID uses a rejected method (e.g. <c>did:web</c>).</exception>
    Task<ResolvedKeyBinding?> ResolveKeyBindingAsync(
        string kid,
        VerificationRelationship relationship,
        CancellationToken ct = default);
}
