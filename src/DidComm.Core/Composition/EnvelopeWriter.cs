using System.Text.Json;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Encryption;
using DidComm.Jose.Signing;
using DidComm.Json;
using DidComm.Messages;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// High-level pack orchestrator: composes <see cref="JwsBuilder"/> and <see cref="JweBuilder"/>
/// into the legal envelope shapes documented in FR-ENV-02. Takes explicit key material — no
/// resolver lookups in Phase 2.
/// </summary>
/// <remarks>
/// FR-ENV-05: when both signing and encrypting, **sign first then encrypt**. The signed JWS
/// bytes become the AEAD plaintext for the JWE.
/// </remarks>
internal static class EnvelopeWriter
{
    /// <summary>Pack a plaintext DIDComm message (FR-API-02 plaintext path).</summary>
    /// <param name="message">The message to serialize.</param>
    public static string PackPlaintext(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Typ ??= MediaTypes.Plaintext;
        message.Validate();
        return JsonSerializer.Serialize(message, DidCommJson.Default);
    }

    /// <summary>Pack a signed DIDComm message (FR-API-02 signed path).</summary>
    /// <param name="parameters">Signing parameters.</param>
    /// <param name="cryptoProvider">Crypto provider.</param>
    public static string PackSigned(PackSignedParameters parameters, DidCommDefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        return JwsBuilder.Build(parameters.Message, parameters.SignerPrivateJwks, cryptoProvider, parameters.RequireInnerToHeader);
    }

    /// <summary>
    /// Pack an encrypted DIDComm message. Routes to anoncrypt or authcrypt based on the
    /// presence of <see cref="PackEncryptedParameters.SenderPrivateJwk"/>; nests a JWS inside
    /// when <see cref="PackEncryptedParameters.SignerPrivateJwks"/> is provided; optionally
    /// wraps the entire result in an extra anoncrypt layer to protect the <c>skid</c>
    /// (<see cref="PackEncryptedParameters.ProtectSender"/> = anoncrypt(authcrypt(...))).
    /// </summary>
    /// <param name="parameters">Encrypt parameters.</param>
    /// <param name="cryptoProvider">Crypto provider.</param>
    public static string PackEncrypted(PackEncryptedParameters parameters, DidCommDefaultCryptoProvider cryptoProvider)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var innerBytes = BuildInnerBytes(parameters, cryptoProvider);

        var encryptedJson = parameters.SenderPrivateJwk is not null
            ? PackAuthcrypt(parameters, innerBytes, cryptoProvider)
            : JweBuilder.PackAnoncrypt(innerBytes, parameters.Recipients, parameters.ContentEncryption, cryptoProvider);

        if (!parameters.ProtectSender)
            return encryptedJson;

        // anoncrypt(authcrypt(plaintext)) — wrap the authcrypt envelope in an outer anoncrypt
        // to hide the sender from mediators. The outer anoncrypt addresses the same recipients
        // (matching the FR-ENC-11 same-curve invariant by construction since they came from
        // the same recipient list).
        return JweBuilder.PackAnoncrypt(
            Encoding.UTF8.GetBytes(encryptedJson),
            parameters.Recipients,
            parameters.ContentEncryption,
            cryptoProvider);
    }

    private static byte[] BuildInnerBytes(PackEncryptedParameters parameters, DidCommDefaultCryptoProvider cryptoProvider)
    {
        if (parameters.SignerPrivateJwks is { Count: > 0 } signers)
        {
            // FR-SIG-06: the inner signed JWM MUST carry 'to'.
            var signedJson = JwsBuilder.Build(parameters.Message, signers, cryptoProvider, requireInnerToHeader: true);
            return Encoding.UTF8.GetBytes(signedJson);
        }

        parameters.Message.Typ ??= MediaTypes.Plaintext;
        parameters.Message.Validate();
        var node = JsonSerializer.SerializeToNode(parameters.Message, DidCommJson.Default);
        return DeterministicJsonWriter.WriteUtf8(node);
    }

    private static string PackAuthcrypt(PackEncryptedParameters parameters, byte[] innerBytes, DidCommDefaultCryptoProvider cryptoProvider)
    {
        if (string.IsNullOrEmpty(parameters.Skid))
            throw new ArgumentException("Authcrypt requires a non-empty 'Skid'.", nameof(parameters));
        return JweBuilder.PackAuthcrypt(
            innerBytes,
            parameters.Recipients,
            parameters.SenderPrivateJwk!,
            parameters.Skid,
            parameters.ContentEncryption,
            cryptoProvider);
    }
}
