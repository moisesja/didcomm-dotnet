using DidComm.Jose;
using DidComm.Messages;

namespace DidComm.Composition;

/// <summary>
/// Parameters for <see cref="EnvelopeWriter.PackSignedAsync"/>. Phase 2 keeps this internal; the
/// Phase 3 public facade will wrap it with resolver-driven key fetching.
/// </summary>
/// <param name="Message">Plaintext message to sign.</param>
/// <param name="SignerPrivateJwks">One or more signer private JWKs.</param>
/// <param name="RequireInnerToHeader">When <c>true</c>, refuse to build if the message has no <c>to</c> (FR-SIG-06; used when this signed envelope will be nested inside an encrypt layer).</param>
internal sealed record PackSignedParameters(
    Message Message,
    IReadOnlyList<Jwk> SignerPrivateJwks,
    bool RequireInnerToHeader = false);

/// <summary>Parameters for <see cref="EnvelopeWriter.PackEncryptedAsync"/>.</summary>
/// <param name="Message">Plaintext message to encrypt.</param>
/// <param name="Recipients">Recipient public JWKs, all sharing the same curve (FR-ENC-04, FR-ENC-11).</param>
/// <param name="ContentEncryption">JOSE <c>enc</c> algorithm.</param>
/// <param name="SenderPrivateJwk">Sender's static private JWK; presence selects authcrypt. <c>null</c> ⇒ anoncrypt.</param>
/// <param name="Skid">Sender key identifier (required when <see cref="SenderPrivateJwk"/> is non-null).</param>
/// <param name="SignerPrivateJwks">Signer private JWKs for sign-then-encrypt composition; <c>null</c> ⇒ pack the plaintext directly.</param>
/// <param name="ProtectSender">When <c>true</c> with authcrypt, wraps the authcrypt envelope in an additional anoncrypt layer to hide <c>skid</c> from mediators (FR-API-01 / anoncrypt(authcrypt)).</param>
internal sealed record PackEncryptedParameters(
    Message Message,
    IReadOnlyList<Jwk> Recipients,
    string ContentEncryption,
    Jwk? SenderPrivateJwk = null,
    string? Skid = null,
    IReadOnlyList<Jwk>? SignerPrivateJwks = null,
    bool ProtectSender = false);
