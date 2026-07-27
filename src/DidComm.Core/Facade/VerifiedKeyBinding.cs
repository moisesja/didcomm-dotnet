using DidComm.Resolution;

namespace DidComm.Facade;

/// <summary>
/// Immutable, message-scoped evidence that one verification method — key material AND its
/// identity facts — came from a <em>single</em> DID resolution performed during one
/// <see cref="DidCommClient.UnpackAsync"/> call, and that the JOSE layer used exactly that key.
/// Surfaced on <see cref="UnpackResult"/> (and mirrored to observers) so consumers no longer
/// need process-global correlation state to bind crypto success to controller authority (#56).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this proves.</strong> The key whose RFC 7638 SHA-256 thumbprint is
/// <see cref="PublicKeyThumbprint"/> (a) verified the signature / authenticated the decrypt /
/// was the decrypting recipient key for this specific unpack, and (b) was listed in the
/// resolved document of <see cref="Did"/> under <see cref="Relationship"/> with
/// <see cref="Controller"/> — all read from the same document instance in the same resolution.
/// No second resolution contributed to this record.
/// </para>
/// <para>
/// <strong>What this does not prove.</strong> It does not prove the document is the freshest
/// version the DID method could serve, and it does not prove anything about resolutions
/// performed outside this unpack call. When a sender/signer binding is present but the
/// plaintext carried no <c>from</c>, the binding is key/controller evidence only — the
/// envelope authenticated a key, not a plaintext identity assertion.
/// </para>
/// <para>
/// Instances are created only by the unpack pipeline (internal constructor): a
/// <see cref="UnpackResult"/> assembled by external code cannot manufacture one. Absence of a
/// binding (null) on an authenticated result means the registered
/// <see cref="Resolution.IDidKeyService"/> does not implement
/// <see cref="IDidKeyBindingService"/> — kid strings and flags alone are not same-resolution
/// provenance.
/// </para>
/// </remarks>
public sealed class VerifiedKeyBinding
{
    internal VerifiedKeyBinding(ResolvedKeyBinding resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        Kid = resolved.Kid;
        Did = resolved.Did;
        Controller = resolved.Controller;
        Relationship = resolved.Relationship;
        PublicKeyThumbprint = resolved.PublicKeyThumbprint;
    }

    /// <summary>The verification method id (absolute DID URL) the JOSE layer reported and used.</summary>
    public string Kid { get; }

    /// <summary>The DID subject whose resolved document supplied both the key and the authority evidence.</summary>
    public string Did { get; }

    /// <summary>The verification method's declared <c>controller</c> in that same document, or null when omitted.</summary>
    public string? Controller { get; }

    /// <summary>The verification relationship the method was found under in that same document.</summary>
    public VerificationRelationship Relationship { get; }

    /// <summary>RFC 7638 SHA-256 thumbprint (base64url, no padding) of the exact public key the JOSE layer used.</summary>
    public string PublicKeyThumbprint { get; }
}
