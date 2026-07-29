using DpJwkThumbprint = DataProofsDotnet.Jose.JwkThumbprint;

namespace DidComm.Resolution;

/// <summary>
/// The atomic projection of one verification method out of <em>one</em> resolved DID document:
/// the exact public JWK handed to the JOSE layer <em>together with</em> the identity facts
/// (normalized kid, DID subject, controller, relationship) that authorize it — so the crypto
/// key and the authority evidence can never come from two different document versions (#56).
/// </summary>
/// <remarks>
/// <para>
/// Returned by <see cref="IDidKeyBindingService.ResolveKeyBindingAsync"/>. All members are
/// captured from the same <c>DidDocument</c> instance in a single resolution; implementations
/// MUST NOT assemble a binding from more than one resolution result.
/// </para>
/// <para>
/// <see cref="PublicKeyThumbprint"/> is the RFC 7638 SHA-256 JWK thumbprint (base64url, no
/// padding) of <see cref="PublicJwk"/> — a deterministic fingerprint of the exact key bytes,
/// independent of kid strings, suitable for downstream audit/pinning.
/// </para>
/// </remarks>
public sealed class ResolvedKeyBinding
{
    /// <summary>Create a binding projected from a single resolved DID document.</summary>
    /// <param name="kid">The verification method id, normalized to an absolute DID URL.</param>
    /// <param name="did">The DID subject of <paramref name="kid"/>.</param>
    /// <param name="controller">The verification method's <c>controller</c>, when declared (else null).</param>
    /// <param name="relationship">The verification relationship the method was found under.</param>
    /// <param name="publicJwk">The exact public JWK projected from the method — the one the JOSE layer consumes.</param>
    public ResolvedKeyBinding(
        string kid,
        string did,
        string? controller,
        VerificationRelationship relationship,
        Jwk publicJwk)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);
        ArgumentException.ThrowIfNullOrEmpty(did);
        ArgumentNullException.ThrowIfNull(publicJwk);
        Kid = kid;
        Did = did;
        Controller = controller;
        Relationship = relationship;
        PublicJwk = publicJwk;
        PublicKeyThumbprint = DpJwkThumbprint.ComputeBase64Url(publicJwk);
    }

    /// <summary>The verification method id as an absolute DID URL (relative ids are normalized by the resolver adapter).</summary>
    public string Kid { get; }

    /// <summary>The DID subject of <see cref="Kid"/> — the document the binding was projected from.</summary>
    public string Did { get; }

    /// <summary>The verification method's declared <c>controller</c>, or null when the document omits it.</summary>
    public string? Controller { get; }

    /// <summary>The verification relationship (<c>keyAgreement</c> / <c>authentication</c>) the method was found under.</summary>
    public VerificationRelationship Relationship { get; }

    /// <summary>The exact public JWK projected from the verification method — the key material the JOSE layer used.</summary>
    public Jwk PublicJwk { get; }

    /// <summary>RFC 7638 SHA-256 thumbprint of <see cref="PublicJwk"/>, base64url without padding.</summary>
    public string PublicKeyThumbprint { get; }
}
