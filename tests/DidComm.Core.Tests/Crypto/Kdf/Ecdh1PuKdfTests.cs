using DidComm.Crypto.Kdf;
using FluentAssertions;
using NetDid.Core;
using NetDid.Core.Crypto;
using Xunit;
using NetDidConcatKdf = NetDid.Core.Crypto.Kdf.ConcatKdf;
using NetDidCryptoProvider = NetDid.Core.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Crypto.Kdf;

/// <summary>
/// Tests for <see cref="Ecdh1PuKdf"/>. ECDH-1PU's underlying primitives (raw ECDH + Concat KDF)
/// are tested upstream in net-did 1.3.0 against published vectors. The tests here pin the
/// DIDComm-specific composition: Z = Ze ‖ Zs (ephemeral first, then static), and the AEAD
/// authentication tag must be appended to <c>SuppPubInfo</c> after the 4-byte keydatalen.
/// </summary>
/// <remarks>
/// X25519 keys come from RFC 7748 §6.1 so any reviewer can re-derive Ze and Zs independently.
/// The expected output is computed by a differential reference path that bypasses
/// <see cref="Ecdh1PuKdf"/> entirely — we hand-assemble Z and SuppPubInfo and call net-did's
/// public Concat KDF directly. If the two paths agree, the composition wiring is correct.
/// </remarks>
public sealed class Ecdh1PuKdfTests
{
    private readonly ICryptoProvider _netDid = new NetDidCryptoProvider();

    // RFC 7748 §6.1 X25519 test vectors (Alice and Bob).

    private static readonly byte[] AlicePrivate = Hex.Decode(
        "77 07 6d 0a 73 18 a5 7d 3c 16 c1 72 51 b2 66 45 df 4c 2f 87 eb c0 99 2a b1 77 fb f1 c1 ec 1d 3a");

    private static readonly byte[] BobPrivate = Hex.Decode(
        "5d ab 08 7e 62 4a 8a 4b 79 e1 7f 8b 83 80 0e e6 6f 3b b1 29 26 18 b6 fd 1c 2f 8b 27 ff 88 e0 eb");

    private static readonly byte[] BobPublic = Hex.Decode(
        "de 9e db 7d 7b 7d c1 b4 d3 5b 61 c2 ec e4 35 37 3f 83 43 c8 5b 78 67 4d ad fc 7e 14 6f 88 2b 4f");

    // Synthetic but stable: 32 bytes of 0x42 as the deterministic ephemeral key for these tests.
    private static readonly byte[] EphemeralPrivate = Enumerable.Repeat((byte)0x42, 32).ToArray();

    // Synthetic but stable: 16 bytes of 0xAA as a fake AEAD tag.
    private static readonly byte[] AeadTag = Enumerable.Repeat((byte)0xAA, 16).ToArray();

    private static readonly byte[] AlgorithmId = Encoding.UTF8.GetBytes("ECDH-1PU+A256KW");
    private static readonly byte[] Apu = Encoding.UTF8.GetBytes("Alice");
    private static readonly byte[] Apv = Encoding.UTF8.GetBytes("Bob");

    [Fact]
    public void Composition_matches_independent_reference_path()
    {
        // Reference path: do the same work outside Ecdh1PuKdf so we can verify wiring.
        var ze = _netDid.DeriveSharedSecret(KeyType.X25519, EphemeralPrivate, BobPublic);
        var zs = _netDid.DeriveSharedSecret(KeyType.X25519, AlicePrivate, BobPublic);

        var z = new byte[ze.Length + zs.Length];
        Buffer.BlockCopy(ze, 0, z, 0, ze.Length);
        Buffer.BlockCopy(zs, 0, z, ze.Length, zs.Length);

        // SuppPubInfo = BE32(keyDataLen * 8) || aeadTag (1PU binding, draft-madden §2.3).
        const int keyDataLen = 32;
        var suppPubInfo = new byte[4 + AeadTag.Length];
        BinaryPrimitives.WriteUInt32BigEndian(suppPubInfo, keyDataLen * 8U);
        AeadTag.CopyTo(suppPubInfo.AsSpan(4));

        var expected = NetDidConcatKdf.DeriveKey(
            sharedSecret: z,
            algorithmId: AlgorithmId,
            partyUInfo: Apu,
            partyVInfo: Apv,
            suppPubInfo: suppPubInfo,
            suppPrivInfo: ReadOnlySpan<byte>.Empty,
            keyDataLen: keyDataLen);

        var actual = Ecdh1PuKdf.DeriveKey(
            _netDid, KeyType.X25519,
            senderPrivateKey: AlicePrivate,
            ephemeralPrivateKey: EphemeralPrivate,
            recipientPublicKey: BobPublic,
            algorithmId: AlgorithmId,
            apu: Apu, apv: Apv,
            aeadTag: AeadTag,
            keyDataLen: keyDataLen);

        actual.Should().Equal(expected,
            because: "Ecdh1PuKdf must compose Z = Ze ‖ Zs (ephemeral-static order per draft-madden §2.1) and append the AEAD tag to SuppPubInfo after the 4-byte big-endian keydatalen (§2.3).");
        actual.Length.Should().Be(keyDataLen);
    }

    [Fact]
    public void Same_inputs_produce_same_output_deterministically()
    {
        var first = Derive(AeadTag, keyDataLen: 32);
        var second = Derive(AeadTag, keyDataLen: 32);

        first.Should().Equal(second,
            because: "X25519 + Concat KDF are deterministic for fixed inputs — no randomness anywhere.");
    }

    [Fact]
    public void Different_aead_tag_produces_different_output()
    {
        var alternateTag = (byte[])AeadTag.Clone();
        alternateTag[0] ^= 0x01;

        var withOriginalTag = Derive(AeadTag, keyDataLen: 32);
        var withAlternateTag = Derive(alternateTag, keyDataLen: 32);

        withOriginalTag.Should().NotEqual(withAlternateTag,
            because: "FR-ENC-15: the AEAD tag is bound into the KDF via SuppPubInfo. Different tags MUST produce different wrapping keys.");
    }

    [Fact]
    public void Empty_tag_path_still_derives_a_key()
    {
        // Empty-tag path corresponds to non-1PU (anoncrypt / ECDH-ES) callers reusing this helper.
        // The output differs from the with-tag path but the derivation itself must succeed.
        var withEmptyTag = Ecdh1PuKdf.DeriveKey(
            _netDid, KeyType.X25519,
            senderPrivateKey: AlicePrivate,
            ephemeralPrivateKey: EphemeralPrivate,
            recipientPublicKey: BobPublic,
            algorithmId: AlgorithmId,
            apu: Apu, apv: Apv,
            aeadTag: ReadOnlySpan<byte>.Empty,
            keyDataLen: 32);

        withEmptyTag.Length.Should().Be(32);
        withEmptyTag.Should().NotEqual(new byte[32],
            because: "the derived key MUST NOT be all zeros (a stuck KDF would produce that).");
    }

    [Fact]
    public void Different_apu_produces_different_output()
    {
        var apuA = Encoding.UTF8.GetBytes("Alice");
        var apuB = Encoding.UTF8.GetBytes("Carol");

        var keyA = Derive(AeadTag, keyDataLen: 32, apu: apuA);
        var keyB = Derive(AeadTag, keyDataLen: 32, apu: apuB);

        keyA.Should().NotEqual(keyB,
            because: "apu is length-prefixed and concatenated into the Concat KDF OtherInfo — any change MUST diversify output.");
    }

    [Fact]
    public void Larger_keyDataLen_exercises_concat_kdf_counter_loop()
    {
        var key64 = Derive(AeadTag, keyDataLen: 64);
        key64.Length.Should().Be(64);

        var key32 = Derive(AeadTag, keyDataLen: 32);

        // The 64-byte output's first 32 bytes are computed from counter=1; bytes 32..63 from
        // counter=2. So key64[0..32] != key32 — different counter input changes the digest.
        key64.Take(32).Should().NotEqual(key32,
            because: "Concat KDF prepends the counter to every SHA-256 block, so the first block of a multi-block output is NOT the same as a single-block derivation.");
    }

    [Fact]
    public void Different_curve_dispatch_works_for_p256()
    {
        // Generate fresh P-256 keypairs so we exercise the NIST-curve dispatch path through
        // Ecdh1PuKdf — no hard-coded vectors needed because the test is composition-validating
        // (round-trip determinism is checked in another test).
        var keyGen = new DefaultKeyGenerator();
        var p256Sender = keyGen.Generate(KeyType.P256);
        var p256Ephemeral = keyGen.Generate(KeyType.P256);
        var p256Recipient = keyGen.Generate(KeyType.P256);

        var derived = Ecdh1PuKdf.DeriveKey(
            _netDid, KeyType.P256,
            senderPrivateKey: p256Sender.PrivateKey,
            ephemeralPrivateKey: p256Ephemeral.PrivateKey,
            recipientPublicKey: p256Recipient.PublicKey,
            algorithmId: AlgorithmId,
            apu: Apu, apv: Apv,
            aeadTag: AeadTag,
            keyDataLen: 32);

        derived.Length.Should().Be(32);
    }

    private byte[] Derive(byte[] aeadTag, int keyDataLen, byte[]? apu = null)
    {
        return Ecdh1PuKdf.DeriveKey(
            _netDid, KeyType.X25519,
            senderPrivateKey: AlicePrivate,
            ephemeralPrivateKey: EphemeralPrivate,
            recipientPublicKey: BobPublic,
            algorithmId: AlgorithmId,
            apu: apu ?? Apu, apv: Apv,
            aeadTag: aeadTag,
            keyDataLen: keyDataLen);
    }
}
