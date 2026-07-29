using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Rotation;

namespace DidComm.Facade;

/// <summary>
/// Public unpack outcome (FR-API-04). Mirrors the internal
/// <see cref="Composition.UnpackResult"/> record field-for-field plus the validated
/// <see cref="FromPrior"/> rotation claims so the consumer can pattern-match on every
/// envelope property the library knows about.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Trust boundary:</strong> <see cref="Message"/>'s <c>from</c> is only cryptographically
/// proven when <see cref="Authenticated"/> (authcrypt sender) or <see cref="NonRepudiation"/>
/// (a verified signature) is <c>true</c>. On a plaintext or anoncrypt envelope
/// (<see cref="AnonymousSender"/>) the <c>from</c> field is attacker-settable and MUST NOT be
/// trusted for authorization, attribution, or reply routing. Check the authentication flags before
/// acting on the sender identity.
/// </para>
/// <para>
/// <strong>Key provenance (#56):</strong> <see cref="Authenticated"/> and the kid strings prove a
/// key <em>named</em> by that kid satisfied the cryptography — by themselves they do NOT prove the
/// key material and the controller authority came from the same resolved DID document. That
/// stronger, same-resolution guarantee is exactly what <see cref="SenderKeyBinding"/>,
/// <see cref="SignerKeyBinding"/>, and <see cref="RecipientKeyBinding"/> carry when the registered
/// key service implements <see cref="Resolution.IDidKeyBindingService"/> (the built-in
/// <c>NetDidKeyService</c> does). When those properties are null on an authenticated result, only
/// the weaker kid/flag statements hold.
/// </para>
/// </remarks>
/// <param name="Message">The fully-unwrapped inner plaintext message. NOTE: <c>Message.From</c> is trustworthy only when <see cref="Authenticated"/> or <see cref="NonRepudiation"/> is <c>true</c> (see remarks).</param>
/// <param name="Stack">The envelope kinds encountered, outermost first.</param>
/// <param name="Encrypted">True when the outermost layer was a JWE.</param>
/// <param name="Authenticated">True when an authcrypt layer was present (sender authenticated via 1PU).</param>
/// <param name="NonRepudiation">True when a JWS layer was present (signed by an authentication key).</param>
/// <param name="AnonymousSender">True when the outermost encrypt layer was anoncrypt (no sender authentication).</param>
/// <param name="ContentEncryption">JOSE <c>enc</c> of the encrypt layer, if any (else <c>null</c>).</param>
/// <param name="KeyWrap">JOSE <c>alg</c> of the encrypt layer, if any (else <c>null</c>).</param>
/// <param name="SignatureAlgorithm">JOSE <c>alg</c> of the JWS layer, if any (else <c>null</c>).</param>
/// <param name="SignerKid">Verified signer key identifier (JWS layer), if any.</param>
/// <param name="SenderKid">Authcrypt sender key identifier (encrypt layer), if any.</param>
/// <param name="RecipientKid">The recipient kid whose private key actually decrypted the envelope.</param>
/// <param name="AllRecipientKids">All recipient kids carried in the encrypt layer.</param>
/// <param name="FromPrior">Validated <c>from_prior</c> rotation claims, when present (FR-ROT-04).</param>
public sealed record UnpackResult(
    Message Message,
    IReadOnlyList<EnvelopeKind> Stack,
    bool Encrypted,
    bool Authenticated,
    bool NonRepudiation,
    bool AnonymousSender,
    string? ContentEncryption,
    string? KeyWrap,
    string? SignatureAlgorithm,
    string? SignerKid,
    string? SenderKid,
    string? RecipientKid,
    IReadOnlyList<string> AllRecipientKids,
    FromPriorClaims? FromPrior)
{
    /// <summary>
    /// Same-document evidence for the authcrypt sender key (<see cref="SenderKid"/>): the exact
    /// key that authenticated the decrypt and the controller/relationship facts authorizing it,
    /// captured from ONE DID resolution during this unpack (#56). Null when no authcrypt layer
    /// was present, or when the registered <see cref="Resolution.IDidKeyService"/> does not
    /// implement <see cref="Resolution.IDidKeyBindingService"/> — in that legacy case
    /// <see cref="SenderKid"/>/<see cref="Authenticated"/> alone are NOT same-resolution
    /// controller provenance. Settable only by the unpack pipeline.
    /// </summary>
    public VerifiedKeyBinding? SenderKeyBinding { get; internal init; }

    /// <summary>
    /// Same-document evidence for the verified JWS signer key (<see cref="SignerKid"/>),
    /// captured from ONE DID resolution during this unpack (#56). Null when no signed layer was
    /// present or on the legacy key-service path (see <see cref="SenderKeyBinding"/> remarks).
    /// </summary>
    public VerifiedKeyBinding? SignerKeyBinding { get; internal init; }

    /// <summary>
    /// Same-document evidence for the recipient key that actually decrypted the envelope
    /// (<see cref="RecipientKid"/>), resolved exactly once after the decrypting kid was known
    /// (#56). Null when the envelope was not encrypted or on the legacy key-service path.
    /// </summary>
    public VerifiedKeyBinding? RecipientKeyBinding { get; internal init; }
}
