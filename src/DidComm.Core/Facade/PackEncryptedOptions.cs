namespace DidComm.Facade;

/// <summary>
/// Inputs to <c>DidCommClient.PackEncryptedAsync</c> (FR-API-01). The facade resolves DID
/// strings to JWKs via the registered <see cref="Resolution.IDidKeyService"/> and
/// <see cref="Secrets.ISecretsResolver"/> before invoking the JOSE composition layer.
/// </summary>
/// <param name="Recipients">Recipient DIDs (no fragment). At least one entry required.</param>
/// <param name="From">Sender DID for authcrypt (1PU). <c>null</c> selects anoncrypt (anonymous sender).</param>
/// <param name="SignFrom">Signer DID for sign-then-encrypt composition. <c>null</c> packs without an inner JWS.</param>
/// <param name="Enc">Content-encryption algorithm; defaults to A256CBC-HS512 (the authcrypt-safe choice and the FR-ENC-05 floor).</param>
/// <param name="ProtectSender">When <c>true</c> with authcrypt, wraps the authcrypt envelope in an outer anoncrypt to hide <c>skid</c> from mediators (<c>anoncrypt(authcrypt(...))</c>).</param>
public sealed record PackEncryptedOptions(
    IReadOnlyList<string> Recipients,
    string? From = null,
    string? SignFrom = null,
    ContentEncryptionAlgorithm Enc = ContentEncryptionAlgorithm.A256CbcHs512,
    bool ProtectSender = false);
