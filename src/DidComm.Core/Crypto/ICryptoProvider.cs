namespace DidComm.Crypto;

/// <summary>
/// JOSE-shaped cryptographic surface used by DIDComm envelope code. Methods dispatch by JOSE
/// algorithm identifier strings (<c>"EdDSA"</c>, <c>"ES256"</c>, <c>"ECDH-1PU+A256KW"</c>, etc.)
/// rather than enum types, matching the way JWE / JWS protected headers carry the values.
/// </summary>
/// <remarks>
/// <para>
/// The implementation delegates curve-level primitives (sign, verify, raw ECDH) to
/// <c>NetDid.Core.ICryptoProvider</c> and owns the JOSE-specific composition layer
/// (<see cref="Aead"/>, <see cref="KeyWrap"/>, <see cref="Kdf"/>).
/// </para>
/// <para>
/// Internal in Phase 0 — the surface lifts to public in Phase 2 once the envelope layer
/// validates it against the DIDComm v2.1 spec vectors.
/// </para>
/// </remarks>
internal interface ICryptoProvider
{
    /// <summary>Sign <paramref name="data"/> with the algorithm named by <paramref name="joseAlg"/>.</summary>
    /// <param name="joseAlg">One of <c>"EdDSA"</c>, <c>"ES256"</c>, <c>"ES384"</c>, <c>"ES512"</c>, <c>"ES256K"</c>.</param>
    /// <param name="privateKey">Raw private key bytes for the algorithm.</param>
    /// <param name="data">Payload to sign.</param>
    /// <returns>The signature in the wire format DIDComm uses (Ed25519 raw 64-byte; ECDSA fixed-width R‖S per RFC 7515 §3.4).</returns>
    byte[] Sign(string joseAlg, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> data);

    /// <summary>Verify a signature produced by <see cref="Sign"/>.</summary>
    bool Verify(string joseAlg, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);

    /// <summary>
    /// Compute the raw ECDH shared secret <c>Z</c> on the curve named by <paramref name="crv"/>.
    /// Returns the unprocessed shared secret (no KDF applied) — callers wrap it in
    /// <see cref="Kdf.Ecdh1PuKdf"/> or call net-did's <c>ConcatKdf</c> directly for anoncrypt.
    /// </summary>
    /// <param name="crv">JWK <c>crv</c> value — one of <c>"X25519"</c>, <c>"P-256"</c>, <c>"P-384"</c>, <c>"P-521"</c>.</param>
    /// <param name="privateKey">Raw private key bytes for the local party.</param>
    /// <param name="publicKey">Raw or SEC1-encoded public key bytes for the remote party.</param>
    byte[] DeriveSharedSecret(string crv, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey);

    /// <summary>Authenticated encryption with associated data.</summary>
    /// <param name="enc">JWE <c>enc</c> value — one of <c>"A256CBC-HS512"</c>, <c>"A256GCM"</c>, <c>"XC20P"</c>.</param>
    /// <param name="cek">Content-encryption key matching the algorithm's expected length.</param>
    /// <param name="iv">Initialization vector / nonce matching the algorithm's expected length.</param>
    /// <param name="aad">Associated data covered by the authentication tag but not encrypted.</param>
    /// <param name="plaintext">Data to encrypt.</param>
    (byte[] Ciphertext, byte[] Tag) AeadEncrypt(
        string enc,
        ReadOnlySpan<byte> cek,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext);

    /// <summary>Authenticated decryption.</summary>
    /// <param name="enc">JWE <c>enc</c> value used to produce <paramref name="ciphertext"/>.</param>
    /// <param name="cek">Content-encryption key.</param>
    /// <param name="iv">IV / nonce the ciphertext was produced with.</param>
    /// <param name="aad">Associated data the tag was computed over.</param>
    /// <param name="ciphertext">Ciphertext bytes.</param>
    /// <param name="tag">Authentication tag produced at encryption time.</param>
    byte[] AeadDecrypt(
        string enc,
        ReadOnlySpan<byte> cek,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag);

    /// <summary>Wrap <paramref name="cek"/> under <paramref name="kek"/>.</summary>
    /// <param name="alg">Key-wrap algorithm — Phase 0 supports only <c>"A256KW"</c> (RFC 3394).</param>
    /// <param name="kek">Key-encryption key.</param>
    /// <param name="cek">Content-encryption key to wrap.</param>
    byte[] KeyWrap(string alg, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> cek);

    /// <summary>Unwrap a CEK previously wrapped by <see cref="KeyWrap"/>.</summary>
    /// <param name="alg">Key-wrap algorithm used to produce <paramref name="wrapped"/>.</param>
    /// <param name="kek">Key-encryption key.</param>
    /// <param name="wrapped">Wrapped CEK as returned by <see cref="KeyWrap"/>.</param>
    byte[] KeyUnwrap(string alg, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped);

    /// <summary>Cryptographically secure RNG suitable for IVs and ephemeral keys (FR-ENC-08).</summary>
    void Fill(Span<byte> destination);
}
