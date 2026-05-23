namespace DidComm.Jose;

/// <summary>
/// JOSE algorithm identifier string constants used by DIDComm v2.1 envelopes.
/// </summary>
/// <remarks>
/// These are the exact spelling that appears in JWE/JWS <c>alg</c>, <c>enc</c>, and <c>crv</c>
/// header values per RFC 7518, RFC 8037, and <c>draft-madden-jose-ecdh-1pu-04</c>. The DIDComm
/// envelope code dispatches by these strings; they are never user-typed and never reformatted.
/// </remarks>
internal static class JoseAlgorithms
{
    // --- Signing algorithms (JWA Section 3) ---

    /// <summary>Edwards-curve Digital Signature Algorithm with Ed25519 (RFC 8037).</summary>
    public const string EdDSA = "EdDSA";

    /// <summary>ECDSA using P-256 and SHA-256 (RFC 7518 §3.4).</summary>
    public const string ES256 = "ES256";

    /// <summary>ECDSA using P-384 and SHA-384 (RFC 7518 §3.4).</summary>
    public const string ES384 = "ES384";

    /// <summary>ECDSA using P-521 and SHA-512 (RFC 7518 §3.4). Note the JWA name is ES512, not ES521.</summary>
    public const string ES512 = "ES512";

    /// <summary>ECDSA using secp256k1 and SHA-256 (RFC 8812 §3.2).</summary>
    public const string ES256K = "ES256K";

    // --- Key-agreement / key-management algorithms (JWA Section 4 + ECDH-1PU draft) ---

    /// <summary>ECDH ephemeral-static using Concat KDF, then A256KW to wrap the CEK (RFC 7518 §4.6). Anoncrypt.</summary>
    public const string EcdhEsA256Kw = "ECDH-ES+A256KW";

    /// <summary>ECDH one-pass unified model (1PU) using Concat KDF and A256KW (draft-madden-jose-ecdh-1pu-04). Authcrypt.</summary>
    public const string Ecdh1PuA256Kw = "ECDH-1PU+A256KW";

    /// <summary>AES Key Wrap with 256-bit key (RFC 3394 / RFC 7518 §4.4).</summary>
    public const string A256Kw = "A256KW";

    // --- Content-encryption algorithms (JWA Section 5) ---

    /// <summary>AES-256-CBC with HMAC-SHA-512, truncated 256-bit tag (RFC 7518 §5.2.5).</summary>
    public const string A256CbcHs512 = "A256CBC-HS512";

    /// <summary>AES-256-GCM (RFC 7518 §5.3). 12-byte IV, 16-byte tag.</summary>
    public const string A256Gcm = "A256GCM";

    /// <summary>XChaCha20-Poly1305 (libsodium / draft-irtf-cfrg-xchacha-03). 24-byte IV, 16-byte tag.</summary>
    public const string XC20P = "XC20P";

    // --- Curve names (JWK \"crv\" values per RFC 7518 §6 and RFC 8037 §2) ---

    /// <summary>Curve25519 used for X25519 key agreement (RFC 8037 §2).</summary>
    public const string CrvX25519 = "X25519";

    /// <summary>Edwards-curve Curve25519 used for Ed25519 signing (RFC 8037 §2).</summary>
    public const string CrvEd25519 = "Ed25519";

    /// <summary>NIST P-256 (RFC 7518 §6.2.1.1).</summary>
    public const string CrvP256 = "P-256";

    /// <summary>NIST P-384 (RFC 7518 §6.2.1.1).</summary>
    public const string CrvP384 = "P-384";

    /// <summary>NIST P-521 (RFC 7518 §6.2.1.1).</summary>
    public const string CrvP521 = "P-521";

    /// <summary>SECP256k1 (RFC 8812 §3.1).</summary>
    public const string CrvSecp256k1 = "secp256k1";
}
