using DidComm.Crypto.Aead;
using DidComm.Crypto.KeyAgreement;
using DidComm.Crypto.KeyWrap;
using DidComm.Jose;
using NetDid.Core;
using NetDid.Core.Crypto;
using NetDidDefaultCryptoProvider = NetDid.Core.Crypto.DefaultCryptoProvider;

namespace DidComm.Crypto;

/// <summary>
/// Default <see cref="ICryptoProvider"/>. Sign/verify and raw ECDH delegate to
/// <see cref="NetDid.Core.ICryptoProvider"/>; AEAD, key wrap, and (in later phases) the
/// JOSE-1PU KDF wrapper are owned locally because they are JOSE-specific compositions.
/// </summary>
/// <remarks>
/// Thread-safe — the underlying primitives are stateless and the AEAD / key-wrap helpers
/// allocate fresh per call (NFR-03). A single <see cref="DefaultCryptoProvider"/> is a safe
/// singleton across the entire pack/unpack pipeline.
/// </remarks>
internal sealed class DefaultCryptoProvider : ICryptoProvider
{
    private readonly NetDid.Core.ICryptoProvider _netDid;

    private readonly IAead _a256CbcHs512 = new AesCbcHmacSha512();
    private readonly IAead _a256Gcm = new AesGcmAead();
    private readonly IAead _xc20p = new XChaCha20Poly1305Aead();

    public DefaultCryptoProvider()
        : this(new NetDidDefaultCryptoProvider()) { }

    public DefaultCryptoProvider(NetDid.Core.ICryptoProvider netDidCryptoProvider)
    {
        _netDid = netDidCryptoProvider ?? throw new ArgumentNullException(nameof(netDidCryptoProvider));
    }

    public byte[] Sign(string joseAlg, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> data)
    {
        var (keyType, format) = MapSigningAlgorithm(joseAlg);
        return _netDid.Sign(keyType, privateKey, data, format);
    }

    public bool Verify(string joseAlg, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        var (keyType, format) = MapSigningAlgorithm(joseAlg);
        return _netDid.Verify(keyType, publicKey, data, signature, format);
    }

    public byte[] DeriveSharedSecret(string crv, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        var keyType = KeyTypeMapper.FromCurveForKeyAgreement(crv);
        return _netDid.DeriveSharedSecret(keyType, privateKey, publicKey);
    }

    public (byte[] Ciphertext, byte[] Tag) AeadEncrypt(
        string enc,
        ReadOnlySpan<byte> cek,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext)
        => GetAead(enc).Encrypt(cek, iv, aad, plaintext);

    public byte[] AeadDecrypt(
        string enc,
        ReadOnlySpan<byte> cek,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
        => GetAead(enc).Decrypt(cek, iv, aad, ciphertext, tag);

    public byte[] KeyWrap(string alg, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> cek)
    {
        if (alg != JoseAlgorithms.A256Kw)
            throw new NotSupportedException($"Key-wrap algorithm '{alg}' is not supported. Phase 0 supports A256KW only.");
        return AesKeyWrap.Wrap(kek, cek);
    }

    public byte[] KeyUnwrap(string alg, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped)
    {
        if (alg != JoseAlgorithms.A256Kw)
            throw new NotSupportedException($"Key-wrap algorithm '{alg}' is not supported. Phase 0 supports A256KW only.");
        return AesKeyWrap.Unwrap(kek, wrapped);
    }

    public void Fill(Span<byte> destination)
        => RandomNumberGenerator.Fill(destination);

    /// <summary>Exposes the underlying net-did provider so the 1PU KDF wrapper can reuse it.</summary>
    internal NetDid.Core.ICryptoProvider NetDidProvider => _netDid;

    private IAead GetAead(string enc) => enc switch
    {
        JoseAlgorithms.A256CbcHs512 => _a256CbcHs512,
        JoseAlgorithms.A256Gcm => _a256Gcm,
        JoseAlgorithms.XC20P => _xc20p,
        _ => throw new NotSupportedException($"Content-encryption algorithm '{enc}' is not supported."),
    };

    private static (KeyType KeyType, EcdsaSignatureFormat Format) MapSigningAlgorithm(string joseAlg) => joseAlg switch
    {
        JoseAlgorithms.EdDSA => (KeyType.Ed25519, EcdsaSignatureFormat.Der), // format ignored for Ed25519
        JoseAlgorithms.ES256 => (KeyType.P256, EcdsaSignatureFormat.IeeeP1363),
        JoseAlgorithms.ES384 => (KeyType.P384, EcdsaSignatureFormat.IeeeP1363),
        JoseAlgorithms.ES512 => (KeyType.P521, EcdsaSignatureFormat.IeeeP1363),
        JoseAlgorithms.ES256K => (KeyType.Secp256k1, EcdsaSignatureFormat.IeeeP1363), // ignored; net-did secp256k1 already returns R‖S
        _ => throw new NotSupportedException($"Signing algorithm '{joseAlg}' is not supported."),
    };
}
