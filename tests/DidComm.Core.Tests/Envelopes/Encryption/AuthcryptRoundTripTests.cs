using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Encryption;
using FluentAssertions;
using NetDid.Core.Crypto;
using Xunit;
using DidCommDefaultCryptoProvider = DidComm.Crypto.DefaultCryptoProvider;

namespace DidComm.Tests.Envelopes.Encryption;

public sealed class AuthcryptRoundTripTests
{
    private static readonly DidCommDefaultCryptoProvider _crypto = new();

    [Theory]
    [InlineData(KeyType.X25519)]
    [InlineData(KeyType.P256)]
    [InlineData(KeyType.P384)]
    [InlineData(KeyType.P521)]
    public void Pack_then_unpack_round_trips_on_every_curve(KeyType keyType)
    {
        var alice = TestKeyMaterial.Generate(keyType, "did:example:alice#enc-1");
        var bob = TestKeyMaterial.Generate(keyType, "did:example:bob#enc-1");
        var plaintext = Encoding.UTF8.GetBytes("{\"id\":\"m1\",\"type\":\"https://didcomm.org/empty/1.0/empty\"}");

        var packed = JweBuilder.PackAuthcrypt(
            plaintext,
            new[] { bob.PublicJwk },
            alice.PrivateJwk,
            alice.PublicJwk.Kid!,
            "A256CBC-HS512",
            _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        var result = JweParser.Parse(packed, secrets, senderLookup, _crypto);

        result.IsAuthenticated.Should().BeTrue();
        result.Algorithm.Should().Be("ECDH-1PU+A256KW");
        result.ContentEncryption.Should().Be("A256CBC-HS512");
        result.SenderKid.Should().Be(alice.PublicJwk.Kid);
        result.RecipientKid.Should().Be(bob.PublicJwk.Kid);
        Encoding.UTF8.GetString(result.Plaintext).Should().Be(Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Authcrypt_refuses_a256gcm_per_fr_enc_09()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#enc");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#enc");

        Action act = () => JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256GCM", _crypto);

        act.Should().Throw<ArgumentException>().WithMessage("*FR-ENC-09*");
    }

    [Fact]
    public void Authcrypt_refuses_mismatched_sender_and_recipient_curves()
    {
        var aliceX = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bobP = TestKeyMaterial.Generate(KeyType.P256, "did:example:bob#p");

        Action act = () => JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bobP.PublicJwk },
            aliceX.PrivateJwk, aliceX.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unpack_without_sender_lookup_throws_for_authcrypt()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");

        var packed = JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });

        Action act = () => JweParser.Parse(packed, secrets, senderLookup: null, _crypto);
        act.Should().Throw<CryptoException>().WithMessage("*sender*");
    }

    [Fact]
    public void Tampered_tag_breaks_authcrypt_unwrap_via_kek_mismatch()
    {
        // FR-ENC-15 ordering: the tag is bound into the KEK derivation via SuppPubInfo.
        // Tampering with the tag therefore breaks BOTH the AEAD integrity check AND the KEK,
        // so unwrap fails before AEAD decrypt is reached. This is the load-bearing guarantee.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);

        // Flip a byte in the tag (the JSON property after "tag":")
        var idx = packed.IndexOf("\"tag\":\"", StringComparison.Ordinal) + "\"tag\":\"".Length;
        var bumped = packed[idx] == 'A' ? 'B' : 'A';
        var tampered = string.Concat(packed.AsSpan(0, idx), bumped.ToString(), packed.AsSpan(idx + 1));

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        Action act = () => JweParser.Parse(tampered, secrets, senderLookup, _crypto);
        act.Should().Throw<CryptoException>();
    }

    [Fact]
    public void Unpack_rejects_authcrypt_whose_enc_is_not_cbc_hmac_per_fr_enc_09()
    {
        // FR-ENC-09 on receive: the send side forbids non-CBC-HMAC enc for authcrypt; the parser must
        // mirror it. A malicious sender that hand-crafts an ECDH-1PU envelope with enc=A256GCM (a
        // supported AEAD, but not for authenticated encryption) must be rejected before any KDF.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#enc");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#enc");
        var packed = JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);
        var tampered = RewriteProtectedEnc(packed, "A256GCM");

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        Action act = () => JweParser.Parse(tampered, secrets, senderLookup, _crypto);
        act.Should().Throw<CryptoException>().WithMessage("*A256CBC-HS512*");
    }

    [Fact]
    public void Unpack_rejects_unsupported_content_encryption()
    {
        // An 'enc' outside the DIDComm-permitted set must be rejected cleanly (CryptoException), not
        // surface later as an uncaught NotSupportedException from the AEAD dispatch.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#enc");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#enc");
        var packed = JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);
        var tampered = RewriteProtectedEnc(packed, "A128GCM");

        var secrets = new DictionarySecretsLookup(new[] { bob.PrivateJwk });
        var senderLookup = new DictionarySenderKeyLookup(new[] { alice.PublicJwk });

        Action act = () => JweParser.Parse(tampered, secrets, senderLookup, _crypto);
        act.Should().Throw<CryptoException>().WithMessage("*Unsupported JWE 'enc'*");
    }

    [Fact]
    public void Unpack_rejects_authcrypt_with_apu_not_matching_skid()
    {
        // FR-ENC-14: apu MUST equal base64url(utf8(skid)). A self-consistent envelope that names a
        // different sender in apu than in skid must be rejected (the two indicators must agree). The
        // binding check runs before the AEAD, so the broken AAD never masks it.
        var (packed, secrets, senderLookup) = BuildAuthcrypt();
        var tampered = MutateProtectedHeader(packed, h => h["apu"] = Base64Url.EncodeUtf8("did:example:evil#enc"));

        Action act = () => JweParser.Parse(tampered, secrets, senderLookup, _crypto);
        act.Should().Throw<CryptoException>().WithMessage("*apu*does not match*");
    }

    [Fact]
    public void Unpack_rejects_jwe_with_critical_extension()
    {
        // RFC 7516 §4.1.13: a 'crit' header naming an extension we don't understand must be rejected.
        var (packed, secrets, senderLookup) = BuildAuthcrypt();
        var tampered = MutateProtectedHeader(packed, h => h["crit"] = new JsonArray("did:example:unknown-ext"));

        Action act = () => JweParser.Parse(tampered, secrets, senderLookup, _crypto);
        act.Should().Throw<MalformedMessageException>().WithMessage("*crit*");
    }

    [Fact]
    public void Unpack_rejects_jwe_missing_required_member()
    {
        // A missing required member (here 'iv') must surface as MalformedMessageException, not a raw
        // KeyNotFoundException at the parse boundary.
        var (packed, secrets, senderLookup) = BuildAuthcrypt();
        var broken = RemoveRootMember(packed, "iv");

        Action act = () => JweParser.Parse(broken, secrets, senderLookup, _crypto);
        act.Should().Throw<MalformedMessageException>().WithMessage("*iv*");
    }

    private static (string packed, DictionarySecretsLookup secrets, DictionarySenderKeyLookup senderLookup) BuildAuthcrypt()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#enc");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#enc");
        var packed = JweBuilder.PackAuthcrypt(
            Encoding.UTF8.GetBytes("{}"),
            new[] { bob.PublicJwk },
            alice.PrivateJwk, alice.PublicJwk.Kid!,
            "A256CBC-HS512", _crypto);
        return (packed, new DictionarySecretsLookup(new[] { bob.PrivateJwk }), new DictionarySenderKeyLookup(new[] { alice.PublicJwk }));
    }

    // Rewrite the protected-header 'enc' and re-encode. The parser's enc allow-list runs before any
    // AEAD/AAD work, so the (now-broken) AAD never matters for these negative tests.
    private static string RewriteProtectedEnc(string packed, string newEnc) =>
        MutateProtectedHeader(packed, h => h["enc"] = newEnc);

    private static string MutateProtectedHeader(string packed, Action<JsonObject> mutate)
    {
        using var doc = JsonDocument.Parse(packed);
        var protectedB64u = doc.RootElement.GetProperty("protected").GetString()!;
        var header = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.Decode(protectedB64u)))!.AsObject();
        mutate(header);
        var rewritten = Base64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));
        return packed.Replace("\"" + protectedB64u + "\"", "\"" + rewritten + "\"", StringComparison.Ordinal);
    }

    private static string RemoveRootMember(string packed, string name)
    {
        var root = JsonNode.Parse(packed)!.AsObject();
        root.Remove(name);
        return root.ToJsonString();
    }
}
