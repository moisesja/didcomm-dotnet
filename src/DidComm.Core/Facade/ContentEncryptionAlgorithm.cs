namespace DidComm.Facade;

/// <summary>
/// Public selection of the JOSE content-encryption (<c>enc</c>) algorithm used for the
/// outermost encrypt layer when packing. The library implements the FR-ENC-05/06/07/08
/// algorithm set; FR-ENC-09 forbids GCM/XC20P for authcrypt (1PU) — the facade enforces that
/// at pack time.
/// </summary>
public enum ContentEncryptionAlgorithm
{
    /// <summary>AES-256-CBC with HMAC-SHA-512 (RFC 7518 §5.2.5). Mandatory for authcrypt; valid for anoncrypt.</summary>
    A256CbcHs512,

    /// <summary>AES-256-GCM (RFC 7518 §5.3). Anoncrypt only — refused for authcrypt per FR-ENC-09.</summary>
    A256Gcm,

    /// <summary>XChaCha20-Poly1305. Anoncrypt only — refused for authcrypt per FR-ENC-09.</summary>
    XC20P,
}
