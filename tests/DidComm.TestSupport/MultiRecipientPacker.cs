using DidComm.Composition;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Secrets;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.TestSupport;

/// <summary>
/// Test-only bridge to the internal <see cref="EnvelopeWriter"/> for packing an authcrypt JWE
/// addressed to <em>multiple recipient kids</em> at once (e.g. all three of Bob's Appendix-A
/// X25519 keys, mirroring the spec C.3 multi-recipient vectors). The public facade encrypts to
/// one keyAgreement key per recipient DID, so fixture generation (FR-IX-06) needs this seam to
/// publish a multi-recipient vector without exposing envelope internals as public API.
/// </summary>
public static class MultiRecipientPacker
{
    /// <summary>
    /// Pack <paramref name="message"/> as an authcrypt (ECDH-1PU+A256KW / A256CBC-HS512)
    /// envelope with one JWE recipient entry per <paramref name="recipientPublicKeys"/> JWK.
    /// All recipient keys MUST share one curve with <paramref name="senderPublicKey"/>.
    /// </summary>
    /// <param name="message">Plaintext message to encrypt.</param>
    /// <param name="recipientPublicKeys">Recipient public JWKs (same curve, <c>Kid</c> set).</param>
    /// <param name="senderPublicKey">The sender's public keyAgreement JWK; its <c>Kid</c> becomes the JWE <c>skid</c>.</param>
    /// <param name="secrets">Resolver holding the sender's private key for the 1PU derivation.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> PackAuthcryptAsync(
        Message message,
        IReadOnlyList<Jwk> recipientPublicKeys,
        Jwk senderPublicKey,
        ISecretsResolver secrets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipientPublicKeys);
        ArgumentNullException.ThrowIfNull(senderPublicKey);
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrEmpty(senderPublicKey.Kid))
            throw new ArgumentException("Sender JWK 'kid' is required (it becomes the JWE skid).", nameof(senderPublicKey));

        var crypto = new JoseCryptoProvider();
        var keyOps = new KeyOperationResolver(secrets, secrets as IOpaqueKeyResolver, crypto);
        var senderKey = await keyOps.ResolveKeyAgreementAsync(senderPublicKey.Kid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Secrets resolver holds no key for sender kid '{senderPublicKey.Kid}'.");

        var parameters = new PackEncryptedParameters(
            Message: message,
            Recipients: recipientPublicKeys,
            ContentEncryption: JoseAlgorithms.A256CbcHs512,
            SenderKey: senderKey,
            Skid: senderPublicKey.Kid);

        return await EnvelopeWriter.PackEncryptedAsync(parameters, crypto, ct).ConfigureAwait(false);
    }
}
