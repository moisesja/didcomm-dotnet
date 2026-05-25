using DidComm.Facade;

namespace DidComm.Transports;

/// <summary>
/// Inputs to <see cref="Facade.DidCommClient.SendAsync"/>. Mirrors
/// <see cref="PackEncryptedOptions"/> (the pack stage runs first, with
/// <c>Forward = true</c> so the facade resolves a transport URI). For senders that already know
/// the recipient's endpoint and want to skip DID resolution / forward wrapping, set
/// <see cref="ServiceEndpointOverride"/>.
/// </summary>
/// <param name="Recipients">Recipient DIDs (no fragment). At least one entry required.</param>
/// <param name="From">Sender DID for authcrypt (1PU). <c>null</c> selects anoncrypt (anonymous sender).</param>
/// <param name="SignFrom">Signer DID for sign-then-encrypt composition. <c>null</c> packs without an inner JWS.</param>
/// <param name="Enc">Content-encryption algorithm; defaults to A256CBC-HS512.</param>
/// <param name="ProtectSender">When <c>true</c> with authcrypt, wraps the authcrypt envelope in an outer anoncrypt.</param>
/// <param name="ServiceEndpointOverride">Optional explicit endpoint URI. When set, the facade skips forward wrapping and sends the inner envelope directly. When <c>null</c>, the facade packs with <c>Forward = true</c> and uses <c>PackEncryptedResult.ServiceEndpoint</c>.</param>
public sealed record SendOptions(
    IReadOnlyList<string> Recipients,
    string? From = null,
    string? SignFrom = null,
    ContentEncryptionAlgorithm Enc = ContentEncryptionAlgorithm.A256CbcHs512,
    bool ProtectSender = false,
    Uri? ServiceEndpointOverride = null);
