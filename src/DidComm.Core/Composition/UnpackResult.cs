using DidComm.Jose;
using DidComm.Messages;

namespace DidComm.Composition;

/// <summary>
/// Metadata describing how a packed DIDComm message was unwrapped, mirroring the FR-API-04
/// surface. Phase 2 keeps this <c>internal</c>; the Phase 3 facade will surface a public
/// mirror with the same shape.
/// </summary>
/// <param name="Message">The fully-unwrapped inner plaintext message.</param>
/// <param name="Stack">The envelope kinds encountered, outermost first (e.g. <c>[Encrypted, Signed]</c> for anoncrypt-then-sign).</param>
/// <param name="Encrypted">True when the outermost layer was a JWE.</param>
/// <param name="Authenticated">True when an authcrypt layer was present (sender authenticated via 1PU).</param>
/// <param name="NonRepudiation">True when a JWS layer was present (signed by an authentication key).</param>
/// <param name="AnonymousSender">True when the encrypt layer was anoncrypt (no sender authentication).</param>
/// <param name="ContentEncryption">JOSE <c>enc</c> of the encrypt layer, if any (else null).</param>
/// <param name="KeyWrap">JOSE <c>alg</c> of the encrypt layer, if any (else null).</param>
/// <param name="SignatureAlgorithm">JOSE <c>alg</c> of the JWS layer, if any (else null).</param>
/// <param name="SignerKid">Verified signer key identifier (JWS layer), if any.</param>
/// <param name="SenderKid">Authcrypt sender key identifier (encrypt layer), if any.</param>
/// <param name="RecipientKid">The recipient kid whose private key actually decrypted the envelope.</param>
/// <param name="AllRecipientKids">All recipient kids carried in the encrypt layer.</param>
internal sealed record UnpackResult(
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
    IReadOnlyList<string> AllRecipientKids);
