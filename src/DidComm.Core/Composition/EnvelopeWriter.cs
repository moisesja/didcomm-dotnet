using System.Text.Json;
using DidComm.Exceptions;
using DidComm.Jose.Signing;
using DidComm.Json;
using DidComm.Messages;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using DpSig = DataProofsDotnet.Jose.Signing;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Composition;

/// <summary>
/// High-level pack orchestrator: composes the legal envelope shapes documented in FR-ENV-02 on
/// top of the DataProofsDotnet.Jose JWS/JWE builders. Takes explicit key material — no resolver
/// lookups (the facade resolves DIDs and supplies the recipient/sender/signer JWKs).
/// </summary>
/// <remarks>
/// FR-ENV-05: when both signing and encrypting, **sign first then encrypt**. The signed JWS bytes
/// become the AEAD plaintext for the JWE. The DIDComm media-type <c>typ</c> values are stamped on
/// every envelope here (FR-ENV-07); DataProofsDotnet.Jose treats <c>typ</c> as an opaque header.
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
    /// <param name="ct">Cancellation token.</param>
    public static Task<string> PackSignedAsync(PackSignedParameters parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return SignAsync(parameters.Message, parameters.SignerPrivateJwks, parameters.RequireInnerToHeader, ct);
    }

    /// <summary>
    /// Pack an encrypted DIDComm message. Routes to anoncrypt or authcrypt based on the presence
    /// of <see cref="PackEncryptedParameters.SenderPrivateJwk"/>; nests a JWS inside when
    /// <see cref="PackEncryptedParameters.SignerPrivateJwks"/> is provided; optionally wraps the
    /// entire result in an extra anoncrypt layer to protect the <c>skid</c>
    /// (<see cref="PackEncryptedParameters.ProtectSender"/> = anoncrypt(authcrypt(...))).
    /// </summary>
    /// <param name="parameters">Encrypt parameters.</param>
    /// <param name="cryptoProvider">JOSE crypto provider (NetCrypto-backed).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> PackEncryptedAsync(
        PackEncryptedParameters parameters,
        JoseCryptoProvider cryptoProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(cryptoProvider);

        var innerBytes = await BuildInnerBytesAsync(parameters, ct).ConfigureAwait(false);

        var encryptedJson = parameters.SenderPrivateJwk is not null
            ? PackAuthcrypt(parameters, innerBytes, cryptoProvider)
            : DpEnc.JweBuilder.BuildEcdhEsA256Kw(
                innerBytes, parameters.Recipients, parameters.ContentEncryption, cryptoProvider, MediaTypes.Encrypted);

        if (!parameters.ProtectSender)
            return encryptedJson;

        // anoncrypt(authcrypt(plaintext)) — wrap the authcrypt envelope in an outer anoncrypt to
        // hide the sender from mediators. The outer anoncrypt addresses the same recipients
        // (FR-ENC-11 same-curve invariant holds by construction — same recipient list).
        return DpEnc.JweBuilder.BuildEcdhEsA256Kw(
            Encoding.UTF8.GetBytes(encryptedJson),
            parameters.Recipients,
            parameters.ContentEncryption,
            cryptoProvider,
            MediaTypes.Encrypted);
    }

    private static async Task<byte[]> BuildInnerBytesAsync(PackEncryptedParameters parameters, CancellationToken ct)
    {
        if (parameters.SignerPrivateJwks is { Count: > 0 } signers)
        {
            // FR-SIG-06: the inner signed JWM MUST carry 'to'.
            var signedJson = await SignAsync(parameters.Message, signers, requireInnerToHeader: true, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(signedJson);
        }

        parameters.Message.Typ ??= MediaTypes.Plaintext;
        parameters.Message.Validate();
        var node = JsonSerializer.SerializeToNode(parameters.Message, DidCommJson.Default);
        return DeterministicJsonWriter.WriteUtf8(node);
    }

    private static async Task<string> SignAsync(
        Message message,
        IReadOnlyList<Jwk>? signerPrivateJwks,
        bool requireInnerToHeader,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (signerPrivateJwks is null || signerPrivateJwks.Count == 0)
            throw new ArgumentException("At least one signer key is required.", nameof(signerPrivateJwks));

        message.Validate();
        if (requireInnerToHeader && (message.To is null || message.To.Count == 0))
            throw new MalformedMessageException(
                "Sign-then-encrypt requires the inner signed JWM to carry a 'to' header (FR-SIG-06).");

        // The JWS payload is the deterministic canonical bytes of the inner JWM (FR-SIG-04),
        // produced here so DataProofs signs exactly the bytes DIDComm canonicalizes.
        var node = JsonSerializer.SerializeToNode(message, DidCommJson.Default);
        var payloadBytes = DeterministicJsonWriter.WriteUtf8(node);

        var signers = new List<DpSig.JwsSigner>(signerPrivateJwks.Count);
        foreach (var jwk in signerPrivateJwks)
            signers.Add(JwsSignerFactory.FromPrivateJwk(jwk));

        return await DpSig.JwsBuilder
            .BuildJsonAsync(payloadBytes, signers, MediaTypes.Signed, detachedPayload: false, ct)
            .ConfigureAwait(false);
    }

    private static string PackAuthcrypt(PackEncryptedParameters parameters, byte[] innerBytes, JoseCryptoProvider cryptoProvider)
    {
        if (string.IsNullOrEmpty(parameters.Skid))
            throw new ArgumentException("Authcrypt requires a non-empty 'Skid'.", nameof(parameters));
        return DpEnc.JweBuilder.BuildEcdh1PuA256Kw(
            innerBytes,
            parameters.Recipients,
            parameters.SenderPrivateJwk!,
            parameters.Skid,
            parameters.ContentEncryption,
            cryptoProvider,
            MediaTypes.Encrypted);
    }
}
