using DidComm.Messages;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using DpSig = DataProofsDotnet.Jose.Signing;

namespace DidComm.Composition;

/// <summary>
/// Parameters for <see cref="EnvelopeWriter.PackSignedAsync"/>. The facade resolves each signer DID
/// to an opaque-or-extractable <see cref="DpSig.JwsSigner"/> handle (FR-SEC-06) before calling in, so
/// the envelope layer holds no private key material.
/// </summary>
/// <param name="Message">Plaintext message to sign.</param>
/// <param name="Signers">One or more JWS signer handles (keystore-backed or built from a private JWK).</param>
/// <param name="RequireInnerToHeader">When <c>true</c>, refuse to build if the message has no <c>to</c> (FR-SIG-06; used when this signed envelope will be nested inside an encrypt layer).</param>
internal sealed record PackSignedParameters(
    Message Message,
    IReadOnlyList<DpSig.JwsSigner> Signers,
    bool RequireInnerToHeader = false);

/// <summary>Parameters for <see cref="EnvelopeWriter.PackEncryptedAsync"/>.</summary>
/// <param name="Message">Plaintext message to encrypt.</param>
/// <param name="Recipients">Recipient public JWKs, all sharing the same curve (FR-ENC-04, FR-ENC-11).</param>
/// <param name="ContentEncryption">JOSE <c>enc</c> algorithm.</param>
/// <param name="SenderKey">Sender's static ECDH key-agreement handle; presence selects authcrypt. <c>null</c> ⇒ anoncrypt. Opaque (keystore) or extractable, resolved by the facade (FR-SEC-06).</param>
/// <param name="Skid">Sender key identifier (required when <see cref="SenderKey"/> is non-null).</param>
/// <param name="Signers">Signer handles for sign-then-encrypt composition; <c>null</c> ⇒ pack the plaintext directly.</param>
/// <param name="ProtectSender">When <c>true</c> with authcrypt, wraps the authcrypt envelope in an additional anoncrypt layer to hide <c>skid</c> from mediators (FR-API-01 / anoncrypt(authcrypt)).</param>
internal sealed record PackEncryptedParameters(
    Message Message,
    IReadOnlyList<Jwk> Recipients,
    string ContentEncryption,
    DpEnc.IEcdhKey? SenderKey = null,
    string? Skid = null,
    IReadOnlyList<DpSig.JwsSigner>? Signers = null,
    bool ProtectSender = false);
