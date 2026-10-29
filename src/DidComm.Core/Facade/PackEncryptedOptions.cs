namespace DidComm.Facade;

/// <summary>
/// Inputs to <c>DidCommClient.PackEncryptedAsync</c> (FR-API-01). The facade resolves DID
/// strings to JWKs via the registered <see cref="Resolution.IDidKeyService"/> and
/// <see cref="Secrets.ISecretsResolver"/> before invoking the JOSE composition layer.
/// </summary>
/// <param name="Recipients">At least one entry. A DID addresses every one of that DID's <c>keyAgreement</c> keys on the negotiated curve, in document order, so the recipient can decrypt on any device holding any of them (FR-ENC-19); keys without a <c>kid</c>, or whose <c>kid</c> names another DID, are skipped, each kid is used once per envelope, and a bare DID contributes at most 64 automatically selected keys. A DID URL of the form <c>did#fragment</c> pins exactly that key, which must be one of the DID's <c>keyAgreement</c> keys; a pin is taken as given and never counts toward a bare DID's 64, wherever it appears in the list, and its curve then applies to every recipient; a DID and a pin for the same DID together give that DID's keys on the pin's curve. The message's own <c>to</c> is not changed. An entry with a path, query or parameters, or longer than 4096 characters, throws <see cref="ArgumentException"/>.</param>
/// <param name="From">Sender DID for authcrypt (1PU). <c>null</c> selects anoncrypt (anonymous sender).</param>
/// <param name="SignFrom">Signer DID for sign-then-encrypt composition. <c>null</c> packs without an inner JWS.</param>
/// <param name="Enc">Content-encryption algorithm; defaults to A256CBC-HS512 (the authcrypt-safe choice and the FR-ENC-05 floor).</param>
/// <param name="ProtectSender">When <c>true</c> with authcrypt, wraps the authcrypt envelope in an outer anoncrypt to hide <c>skid</c> from mediators (<c>anoncrypt(authcrypt(...))</c>).</param>
/// <param name="Forward">When <c>true</c>, the facade resolves the recipient's <c>DIDCommMessaging</c> service entry, applies reverse-order <c>forward</c> wrapping per FR-ROUTE-02, and surfaces the transport URI on the result. Forwarding supports **single-recipient** packs only: every <see cref="Recipients"/> entry must name the same DID (pins of that DID are fine; <c>body.next</c> is the bare DID), otherwise <c>Forward = true</c> throws <see cref="InvalidOperationException"/>.</param>
/// <example>
/// Every shape — anoncrypt, authcrypt, sign-then-encrypt, protect-sender, explicit enc, multi-recipient — is packed in <c>samples/02-Cookbook</c> (sections E–J).
/// </example>
public sealed record PackEncryptedOptions(
    IReadOnlyList<string> Recipients,
    string? From = null,
    string? SignFrom = null,
    ContentEncryptionAlgorithm Enc = ContentEncryptionAlgorithm.A256CbcHs512,
    bool ProtectSender = false,
    bool Forward = false);
