namespace DidComm.Jose.Encryption;

/// <summary>
/// One JWE recipient entry: the recipient key identifier and the AES-KW-wrapped CEK that
/// only the matching private key can unwrap. Each entry corresponds to a single recipient
/// kid; multi-recipient JWEs carry one of these per kid (FR-ENC-16, FR-ENC-19).
/// </summary>
/// <param name="Kid">The recipient key identifier (DID URL with fragment).</param>
/// <param name="EncryptedKey">The CEK wrapped under the per-recipient KEK (AES-KW output).</param>
internal sealed record RecipientWrap(string Kid, byte[] EncryptedKey);
