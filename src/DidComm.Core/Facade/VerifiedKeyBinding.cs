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
/// performed outside this unpack call. When <see cref="AuthorizedForDid"/> is <c>null</c> the
/// controller rule never ran (the plaintext carried no <c>from</c>), so the binding is key
/// evidence only — the envelope authenticated a key, not an identity assertion. Each binding is
/// internally same-document, but two bindings on one result (e.g. sender and signer, resolved
/// under different verification relationships) are separate resolutions and may observe
/// different versions of the same DID document — compare <see cref="Did"/>, not document state.
/// </para>
/// <para>
/// <strong>Only meaningful on a result returned by <see cref="DidCommClient.UnpackAsync"/>.</strong>
/// The properties cannot be set by external code, but a record <c>with</c>-expression copies them:
/// <c>result with { Message = somethingElse }</c> carries the evidence onto a message the pipeline
/// never verified. Read bindings off the result the facade handed you.
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
public sealed class VerifiedKeyBinding : IEquatable<VerifiedKeyBinding>
{
    internal VerifiedKeyBinding(ResolvedKeyBinding resolved, string? authorizedForDid)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        Kid = resolved.Kid;
        Did = resolved.Did;
        Controller = resolved.Controller;
        Relationship = resolved.Relationship;
        PublicKeyThumbprint = resolved.PublicKeyThumbprint;
        AuthorizedForDid = authorizedForDid;
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

    /// <summary>
    /// The asserted identity this key was <em>authorized against</em> (the plaintext <c>from</c> for
    /// sender/signer bindings; the recipient kid's own DID subject for recipient bindings), or
    /// <c>null</c> when no such check ran — which happens for a sender/signer key on a plaintext
    /// carrying no <c>from</c>. When null, <see cref="Controller"/> is self-declared evidence from
    /// the key holder's own document that was never compared against a claimed identity: the
    /// envelope authenticated a key, not an identity assertion. Treat a null value as "do not use
    /// this binding for authorization".
    /// </summary>
    public string? AuthorizedForDid { get; }

    /// <summary>Structural equality over every surfaced field.</summary>
    /// <param name="other">The binding to compare with.</param>
    public bool Equals(VerifiedKeyBinding? other)
        => other is not null
           && string.Equals(Kid, other.Kid, StringComparison.Ordinal)
           && string.Equals(Did, other.Did, StringComparison.Ordinal)
           && string.Equals(Controller, other.Controller, StringComparison.Ordinal)
           && Relationship == other.Relationship
           && string.Equals(PublicKeyThumbprint, other.PublicKeyThumbprint, StringComparison.Ordinal)
           && string.Equals(AuthorizedForDid, other.AuthorizedForDid, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as VerifiedKeyBinding);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Kid, Did, Controller, Relationship, PublicKeyThumbprint, AuthorizedForDid);
}
