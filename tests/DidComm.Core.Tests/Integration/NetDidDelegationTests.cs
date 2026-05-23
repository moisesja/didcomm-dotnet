using DidComm.Jose;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;
using DidCommCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Integration;

/// <summary>
/// Confirms the JOSE-shaped DIDComm <see cref="DefaultCryptoProvider"/> correctly delegates
/// sign / verify and raw ECDH to net-did 1.3.0. The actual crypto correctness is net-did's
/// responsibility (covered by its own test suite); we test the mapping table from JOSE
/// algorithm strings to net-did's <see cref="KeyType"/> + <see cref="EcdsaSignatureFormat"/>
/// and confirm the off-curve-rejection security property propagates through our pipeline.
/// </summary>
public sealed class NetDidDelegationTests
{
    private readonly DidCommCryptoProvider _provider = new();
    private readonly DefaultKeyGenerator _keyGen = new();

    // --- Signature delegation ---

    [Theory]
    [InlineData(JoseAlgorithms.EdDSA, KeyType.Ed25519, null)]
    [InlineData(JoseAlgorithms.ES256, KeyType.P256, 64)]
    [InlineData(JoseAlgorithms.ES384, KeyType.P384, 96)]
    [InlineData(JoseAlgorithms.ES512, KeyType.P521, 132)]
    [InlineData(JoseAlgorithms.ES256K, KeyType.Secp256k1, 64)]
    public void Sign_and_Verify_round_trip_for_every_supported_algorithm(string joseAlg, KeyType keyType, int? expectedSignatureLength)
    {
        var keyPair = _keyGen.Generate(keyType);
        var message = "didcomm-dotnet sign/verify round-trip"u8.ToArray();

        var signature = _provider.Sign(joseAlg, keyPair.PrivateKey, message);

        if (expectedSignatureLength is { } expected)
        {
            signature.Length.Should().Be(expected,
                because: $"{joseAlg} on a NIST/secp256k1 curve MUST emit fixed-width IEEE P1363 (R‖S) per RFC 7515 §3.4.");
        }

        _provider.Verify(joseAlg, keyPair.PublicKey, message, signature)
            .Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_signature_from_a_different_key()
    {
        var algos = new[] { JoseAlgorithms.EdDSA, JoseAlgorithms.ES256, JoseAlgorithms.ES512, JoseAlgorithms.ES256K };
        foreach (var alg in algos)
        {
            var keyType = MapJoseAlgToKeyType(alg);
            var signerKp = _keyGen.Generate(keyType);
            var unrelatedKp = _keyGen.Generate(keyType);
            var message = Encoding.UTF8.GetBytes($"checking {alg}");

            var sig = _provider.Sign(alg, signerKp.PrivateKey, message);
            _provider.Verify(alg, unrelatedKp.PublicKey, message, sig)
                .Should().BeFalse(because: $"{alg}: verification with the wrong public key MUST return false.");
        }
    }

    [Fact]
    public void Verify_rejects_tampered_payload()
    {
        var keyPair = _keyGen.Generate(KeyType.Ed25519);
        var message = "original message"u8.ToArray();
        var sig = _provider.Sign(JoseAlgorithms.EdDSA, keyPair.PrivateKey, message);

        var tampered = (byte[])message.Clone();
        tampered[0] ^= 0x01;

        _provider.Verify(JoseAlgorithms.EdDSA, keyPair.PublicKey, tampered, sig)
            .Should().BeFalse();
    }

    [Fact]
    public void Sign_with_unknown_algorithm_throws_NotSupportedException()
    {
        var act = () => _provider.Sign("RS256", new byte[32], new byte[8]);
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*RS256*");
    }

    // --- ECDH delegation ---

    [Theory]
    [InlineData(JoseAlgorithms.CrvX25519, KeyType.X25519, 32)]
    [InlineData(JoseAlgorithms.CrvP256, KeyType.P256, 32)]
    [InlineData(JoseAlgorithms.CrvP384, KeyType.P384, 48)]
    [InlineData(JoseAlgorithms.CrvP521, KeyType.P521, 66)]
    public void DeriveSharedSecret_works_for_every_supported_curve(string joseCrv, KeyType keyType, int expectedSharedSecretLength)
    {
        var alice = _keyGen.Generate(keyType);
        var bob = _keyGen.Generate(keyType);

        // Both sides derive — the two shared secrets MUST be byte-identical (ECDH symmetry).
        var aliceSeesBob = _provider.DeriveSharedSecret(joseCrv, alice.PrivateKey, bob.PublicKey);
        var bobSeesAlice = _provider.DeriveSharedSecret(joseCrv, bob.PrivateKey, alice.PublicKey);

        aliceSeesBob.Length.Should().Be(expectedSharedSecretLength,
            because: $"raw ECDH on {joseCrv} returns the field-size X-coordinate (32 for X25519/P-256, 48 for P-384, 66 for P-521).");
        aliceSeesBob.Should().Equal(bobSeesAlice,
            because: $"{joseCrv} ECDH MUST be commutative — both parties derive the same Z.");
    }

    [Fact]
    public void DeriveSharedSecret_with_unsupported_curve_throws()
    {
        var act = () => _provider.DeriveSharedSecret("Ed25519", new byte[32], new byte[32]);
        act.Should().Throw<NotSupportedException>(
            because: "Ed25519 is a signing curve, not an ECDH curve — JOSE never uses it for key agreement.");
    }

    // --- Off-curve JWK rejection (invalid-curve defense) ---

    [Fact]
    public void Off_curve_EC_jwk_is_rejected_at_the_jwk_boundary()
    {
        // Start from a real P-256 JWK so x/y are well-formed (correct length, valid base64url),
        // then mutate Y to produce an off-curve point that still passes a naive length/x<p check
        // — exactly the invalid-curve-attack threat model (RFC 7518 §6.2.2, Antipa et al. 2003).
        var realKey = _keyGen.Generate(KeyType.P256);
        var realJwk = NetDid.Core.Jwk.JwkConverter.ToPublicJwk(realKey);

        var yBytes = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(realJwk.Y);
        yBytes[^1] ^= 0x01; // flip the low bit of Y → (x, y') with overwhelming probability not on the curve

        var offCurveJwk = new Jwk
        {
            Kty = realJwk.Kty,
            Crv = realJwk.Crv,
            X = realJwk.X,
            Y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(yBytes),
        };

        var act = () => JwkConversion.ExtractPublicKey(offCurveJwk);

        act.Should().Throw<CryptographicException>(
            because: "FR-ENC-03 / RFC 7518 §6.2.2: an off-curve (x, y) MUST be rejected before any downstream code can use it for ECDH. The check is inherited from net-did's EcPointValidator wired into JwkConverter.ExtractPublicKey.");
    }

    [Fact]
    public void Identity_point_jwk_is_rejected()
    {
        // The point at infinity (0, 0) is the additive identity — any ECDH multiplying by it
        // yields infinity again, which the attacker controls. Must be rejected.
        var jwk = new Jwk
        {
            Kty = "EC",
            Crv = "P-256",
            X = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(new byte[32]),
            Y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(new byte[32]),
        };

        var act = () => JwkConversion.ExtractPublicKey(jwk);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Well_formed_jwk_round_trips_to_raw_public_key()
    {
        var keyPair = _keyGen.Generate(KeyType.P256);
        var publicJwk = NetDid.Core.Jwk.JwkConverter.ToPublicJwk(keyPair);

        var didcommJwk = new Jwk
        {
            Kty = publicJwk.Kty,
            Crv = publicJwk.Crv,
            X = publicJwk.X,
            Y = publicJwk.Y,
        };

        var (keyType, _) = JwkConversion.ExtractPublicKey(didcommJwk);
        keyType.Should().Be(KeyType.P256);
    }

    // --- AEAD dispatch (smoke through DefaultCryptoProvider) ---

    [Theory]
    [InlineData(JoseAlgorithms.A256CbcHs512, 64, 16)]
    [InlineData(JoseAlgorithms.A256Gcm, 32, 12)]
    [InlineData(JoseAlgorithms.XC20P, 32, 24)]
    public void AeadEncrypt_AeadDecrypt_round_trip_for_each_enc(string enc, int keyLen, int ivLen)
    {
        var key = new byte[keyLen];
        var iv = new byte[ivLen];
        RandomNumberGenerator.Fill(key);
        RandomNumberGenerator.Fill(iv);
        var aad = "didcomm-dotnet"u8.ToArray();
        var plaintext = "hello, world"u8.ToArray();

        var (ciphertext, tag) = _provider.AeadEncrypt(enc, key, iv, aad, plaintext);
        var recovered = _provider.AeadDecrypt(enc, key, iv, aad, ciphertext, tag);

        recovered.Should().Equal(plaintext);
    }

    // --- Key wrap dispatch ---

    [Fact]
    public void KeyWrap_KeyUnwrap_round_trip_via_A256KW()
    {
        var kek = new byte[32];
        var cek = new byte[32];
        RandomNumberGenerator.Fill(kek);
        RandomNumberGenerator.Fill(cek);

        var wrapped = _provider.KeyWrap(JoseAlgorithms.A256Kw, kek, cek);
        var unwrapped = _provider.KeyUnwrap(JoseAlgorithms.A256Kw, kek, wrapped);

        unwrapped.Should().Equal(cek);
    }

    [Fact]
    public void KeyWrap_with_unsupported_alg_throws()
    {
        var act = () => _provider.KeyWrap("A128KW", new byte[16], new byte[16]);
        act.Should().Throw<NotSupportedException>();
    }

    private static KeyType MapJoseAlgToKeyType(string joseAlg) => joseAlg switch
    {
        JoseAlgorithms.EdDSA => KeyType.Ed25519,
        JoseAlgorithms.ES256 => KeyType.P256,
        JoseAlgorithms.ES384 => KeyType.P384,
        JoseAlgorithms.ES512 => KeyType.P521,
        JoseAlgorithms.ES256K => KeyType.Secp256k1,
        _ => throw new ArgumentException($"Unmapped JOSE alg: {joseAlg}"),
    };
}
