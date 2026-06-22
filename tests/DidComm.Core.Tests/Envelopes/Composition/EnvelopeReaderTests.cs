using System.Text;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Jose;
using DidComm.Jose.Signing;
using DidComm.Messages;
using DidComm.Secrets;
using FluentAssertions;
using NetCrypto;
using Xunit;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using DpSig = DataProofsDotnet.Jose.Signing;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Envelopes.Composition;

public sealed class EnvelopeReaderTests
{
    private static readonly JoseCryptoProvider _crypto = new();

    [Fact]
    public void Plaintext_round_trips_through_writer_and_reader()
    {
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = EnvelopeWriter.PackPlaintext(msg);
        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: null,
            _crypto);

        unpacked.Encrypted.Should().BeFalse();
        unpacked.Authenticated.Should().BeFalse();
        unpacked.NonRepudiation.Should().BeFalse();
        unpacked.Stack.Should().Equal(EnvelopeKind.Plaintext);
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public async Task Signed_round_trips_with_metadata()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackSignedAsync(
            new PackSignedParameters(msg, new[] { signer.PrivateJwk.ToJwsSigner() }));

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: kid => kid == signer.PublicJwk.Kid ? signer.PublicJwk : null,
            _crypto);

        unpacked.NonRepudiation.Should().BeTrue();
        unpacked.SignatureAlgorithm.Should().Be("EdDSA");
        unpacked.SignerKid.Should().Be(signer.PublicJwk.Kid);
        unpacked.Stack.Should().Equal(EnvelopeKind.Signed, EnvelopeKind.Plaintext);
    }

    [Fact]
    public async Task Anoncrypt_round_trips_with_metadata()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(msg, new[] { bob.PublicJwk }, "A256GCM"),
            _crypto);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null,
            signerLookup: null,
            _crypto);

        unpacked.Encrypted.Should().BeTrue();
        unpacked.AnonymousSender.Should().BeTrue();
        unpacked.Authenticated.Should().BeFalse();
        unpacked.ContentEncryption.Should().Be("A256GCM");
        unpacked.KeyWrap.Should().Be("ECDH-ES+A256KW");
        unpacked.RecipientKid.Should().Be(bob.PublicJwk.Kid);
        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Plaintext);
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public async Task Authcrypt_round_trips_with_metadata()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid),
            _crypto);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null,
            _crypto);

        unpacked.Encrypted.Should().BeTrue();
        unpacked.Authenticated.Should().BeTrue();
        unpacked.AnonymousSender.Should().BeFalse();
        unpacked.NonRepudiation.Should().BeFalse();
        unpacked.SenderKid.Should().Be(alice.PublicJwk.Kid);
        unpacked.KeyWrap.Should().Be("ECDH-1PU+A256KW");
        unpacked.ContentEncryption.Should().Be("A256CBC-HS512");
    }

    [Fact]
    public async Task Anoncrypt_then_sign_unwraps_to_inner_plaintext()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256GCM",
                Signers: new[] { signer.PrivateJwk.ToJwsSigner() }),
            _crypto);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null,
            signerLookup: _ => signer.PublicJwk,
            _crypto);

        unpacked.Encrypted.Should().BeTrue();
        unpacked.NonRepudiation.Should().BeTrue();
        unpacked.AnonymousSender.Should().BeTrue();
        unpacked.SignerKid.Should().Be(signer.PublicJwk.Kid);
        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Signed, EnvelopeKind.Plaintext);
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public async Task Anoncrypt_authcrypt_protect_sender_unwraps_recursively()
    {
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid,
                ProtectSender: true),
            _crypto);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null,
            _crypto);

        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Encrypted, EnvelopeKind.Plaintext);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.Message.Id.Should().Be(msg.Id);
    }

    [Fact]
    public async Task Consistency_check_blocks_recipient_kid_not_in_to_header()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithTo("did:example:carol") // Bob isn't in the 'to' list
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(msg, new[] { bob.PublicJwk }, "A256GCM"),
            _crypto);

        Action act = () => EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null,
            signerLookup: null,
            _crypto);

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-02*");
    }

    [Fact]
    public async Task Consistency_check_blocks_authcrypt_skid_not_matching_plaintext_from()
    {
        // FR-CONSIST-01: a self-consistent forged envelope — cryptographically valid authcrypt
        // from Alice, but the inner plaintext claims it came from Carol. The skid authenticates
        // Alice, so the mismatched 'from' MUST be rejected.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:carol")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid),
            _crypto);

        Action act = () => EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null,
            _crypto);

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-01*");
    }

    [Fact]
    public async Task Consistency_check_blocks_authcrypt_sign_inner_signer_not_matching_skid()
    {
        // FR-CONSIST-05: authcrypt(sign(...)) where the inner signer (Carol) differs from the
        // authcrypt sender (Alice). The inner signature verifies and its 'from' agrees with the
        // signer (so FR-CONSIST-03 passes), but the signer/sender disagreement MUST be rejected.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var carol = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:carol#k");
        var msg = new MessageBuilder()
            .WithType("https://didcomm.org/empty/1.0/empty")
            .WithFrom("did:example:carol")
            .WithTo("did:example:bob")
            .Build();

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                msg, new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto),
                Skid: alice.PublicJwk.Kid,
                Signers: new[] { carol.PrivateJwk.ToJwsSigner() }),
            _crypto);

        Action act = () => EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: kid => kid == carol.PublicJwk.Kid ? carol.PublicJwk : null,
            _crypto);

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-05*");
    }

    // ---- Issue #17: only the legal FR-ENV-02 compositions (+ authcrypt(sign) on receive, FR-ENV-03)
    //      may unpack. Illegal layer orderings, built by hand-wrapping a packed envelope, MUST be
    //      rejected as malformed structure BEFORE any consistency/content processing. ----

    private static string AnoncryptWrap(string inner, Jwk recipientPublicJwk) =>
        DpEnc.JweBuilder.BuildEcdhEsA256Kw(
            Encoding.UTF8.GetBytes(inner), new[] { recipientPublicJwk }, "A256GCM", _crypto, MediaTypes.Encrypted);

    private static string AuthcryptWrap(string inner, Jwk recipientPublicJwk, Jwk senderPrivateJwk, string skid) =>
        DpEnc.JweBuilder.BuildEcdh1PuA256Kw(
            Encoding.UTF8.GetBytes(inner), new[] { recipientPublicJwk }, senderPrivateJwk, skid, "A256CBC-HS512", _crypto, MediaTypes.Encrypted);

    private static Task<string> SignWrapAsync(string inner, Jwk signerPrivateJwk)
    {
        var signers = new List<DpSig.JwsSigner> { JwsSignerFactory.FromPrivateJwk(signerPrivateJwk) };
        return DpSig.JwsBuilder.BuildJsonAsync(Encoding.UTF8.GetBytes(inner), signers, MediaTypes.Signed, detachedPayload: false);
    }

    private static Message EmptyMessage() => new MessageBuilder()
        .WithType("https://didcomm.org/empty/1.0/empty")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .Build();

    [Fact]
    public async Task Anoncrypt_of_anoncrypt_is_rejected_as_illegal_composition()
    {
        // [AnonEncrypt, AnonEncrypt, Plaintext] — only anoncrypt(authcrypt) is legal; the inner
        // encrypt MUST be authenticated. This is the case the EnvelopeKind stack alone can't tell
        // apart from the legal anoncrypt(authcrypt) — the auth flag is what distinguishes them.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var inner = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);
        var outer = AnoncryptWrap(inner, bob.PublicJwk);

        Action act = () => EnvelopeReaderTestRunner.Unpack(outer,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }), senderLookup: null, signerLookup: null, _crypto);

        act.Should().Throw<MalformedMessageException>().WithMessage("*Illegal*composition*");
    }

    [Fact]
    public async Task Authcrypt_of_authcrypt_is_rejected_as_illegal_composition()
    {
        // [AuthEncrypt, AuthEncrypt, Plaintext] — double authcrypt is not a legal shape.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var inner = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto), Skid: alice.PublicJwk.Kid), _crypto);
        var outer = AuthcryptWrap(inner, bob.PublicJwk, alice.PrivateJwk, alice.PublicJwk.Kid!);

        Action act = () => EnvelopeReaderTestRunner.Unpack(outer,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null, _crypto);

        act.Should().Throw<MalformedMessageException>().WithMessage("*Illegal*composition*");
    }

    [Fact]
    public async Task Sign_outside_encrypt_is_rejected_as_illegal_composition()
    {
        // [Sign, AnonEncrypt, Plaintext] — DIDComm signs INSIDE encrypt (FR-ENV-05). A JWS wrapping a
        // JWE signs opaque ciphertext, defeating FR-SIG-06's anti-surreptitious-forwarding intent.
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var inner = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);
        var outer = await SignWrapAsync(inner, signer.PrivateJwk);

        Action act = () => EnvelopeReaderTestRunner.Unpack(outer,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: null,
            signerLookup: _ => signer.PublicJwk, _crypto);

        act.Should().Throw<MalformedMessageException>().WithMessage("*Illegal*composition*");
    }

    [Fact]
    public async Task Signed_of_signed_is_rejected_as_illegal_composition()
    {
        // [Sign, Sign, Plaintext] — double signature is not a legal shape.
        var s1 = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k1");
        var s2 = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k2");
        var inner = await EnvelopeWriter.PackSignedAsync(new PackSignedParameters(EmptyMessage(), new[] { s1.PrivateJwk.ToJwsSigner() }));
        var outer = await SignWrapAsync(inner, s2.PrivateJwk);

        Action act = () => EnvelopeReaderTestRunner.Unpack(outer,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: kid => kid == s1.PublicJwk.Kid ? s1.PublicJwk : kid == s2.PublicJwk.Kid ? s2.PublicJwk : null,
            _crypto);

        act.Should().Throw<MalformedMessageException>().WithMessage("*Illegal*composition*");
    }

    [Fact]
    public async Task Authcrypt_of_sign_is_accepted_on_receive_FrEnv03()
    {
        // [AuthEncrypt, Sign, Plaintext] — we never EMIT authcrypt(sign), but the spec lets us ACCEPT
        // it on receive (FR-ENV-03). With the signer and skid both under did:example:alice, the
        // consistency checks pass and the message unpacks; the gate must NOT reject the shape.
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var aliceSign = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: aliceKa.PrivateJwk.ToEcdhKey(_crypto),
                Skid: aliceKa.PublicJwk.Kid,
                Signers: new[] { aliceSign.PrivateJwk.ToJwsSigner() }), _crypto);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { aliceKa.PublicJwk }),
            signerLookup: kid => kid == aliceSign.PublicJwk.Kid ? aliceSign.PublicJwk : null,
            _crypto);

        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Signed, EnvelopeKind.Plaintext);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.NonRepudiation.Should().BeTrue();
    }

    [Fact]
    public async Task Anoncrypt_authcrypt_sign_is_accepted_on_receive()
    {
        // [AnonEncrypt, AuthEncrypt, Sign, Plaintext] — protect-sender + sign. The library emits this
        // via ProtectSender + signers, and the spec's Appendix C.3 vector requires accepting it. The
        // legal receive grammar (anoncrypt? authcrypt? sign? plaintext) admits it.
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var aliceSign = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: aliceKa.PrivateJwk.ToEcdhKey(_crypto),
                Skid: aliceKa.PublicJwk.Kid,
                Signers: new[] { aliceSign.PrivateJwk.ToJwsSigner() },
                ProtectSender: true), _crypto);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { aliceKa.PublicJwk }),
            signerLookup: kid => kid == aliceSign.PublicJwk.Kid ? aliceSign.PublicJwk : null,
            _crypto);

        unpacked.Stack.Should().Equal(EnvelopeKind.Encrypted, EnvelopeKind.Encrypted, EnvelopeKind.Signed, EnvelopeKind.Plaintext);
        unpacked.Authenticated.Should().BeTrue();
        unpacked.AnonymousSender.Should().BeTrue();
        unpacked.NonRepudiation.Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_length_iv_is_a_uniform_decrypt_failure_under_constant_work()
    {
        // Constant-work decrypt (dataproofs #12, consumed via the async ParseAsync path): a non-canonical
        // iv length is folded into the SAME uniform "JWE could not be decrypted." failure
        // (JoseCryptoException -> CryptoException) as a wrong-key / tampered-ciphertext failure, so a
        // malformed iv cannot be distinguished from a decrypt failure by exception type or message —
        // closing that oracle. (Pre-1.1.0 the synchronous parser surfaced the iv length as an
        // ArgumentException -> MalformedMessageException; the constant-work path deliberately does not.)
        // The ArgumentException -> MalformedMessageException boundary guard (#22, FR-API-07) remains
        // covered by the signed-path test below.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);

        var jwe = System.Text.Json.Nodes.JsonNode.Parse(packed)!.AsObject();
        jwe["iv"] = DidComm.Jose.Base64Url.Encode(new byte[5]); // valid base64url, wrong length
        var tampered = jwe.ToJsonString();

        Action act = () => EnvelopeReaderTestRunner.Unpack(tampered,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }), senderLookup: null, signerLookup: null, _crypto);

        act.Should().Throw<CryptoException>();
    }

    [Fact]
    public async Task ArgumentException_from_the_delegated_parse_maps_to_MalformedMessageException()
    {
        // Issue #22 boundary guard — directly exercised. The wrong-length-iv path is wrapped upstream,
        // so this drives the new catch via a signer lookup that throws ArgumentException (the lookup is
        // invoked inside the delegated parse). Contract: ANY ArgumentException from the delegated parse
        // becomes MalformedMessageException, never escapes raw; InnerException is preserved for diagnosis.
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, "did:example:alice#k");
        var packed = await EnvelopeWriter.PackSignedAsync(new PackSignedParameters(EmptyMessage(), new[] { signer.PrivateJwk.ToJwsSigner() }));

        Action act = () => EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(Array.Empty<Jwk>()),
            senderLookup: null,
            signerLookup: _ => throw new ArgumentException("boom from a buggy lookup"),
            _crypto);

        act.Should().Throw<MalformedMessageException>().WithInnerException<ArgumentException>();
    }

    [Fact]
    public async Task AnonymousSender_reflects_the_outermost_encrypt_layer_not_accumulation()
    {
        // Issue #23 — characterization / defense-in-depth (NOT a regression guard: the #17 gate already
        // rejects the only shapes where the old OR-accumulation would have differed, so this passes on
        // main too). It pins the documented contract: for anoncrypt(authcrypt(...)) the OUTERMOST encrypt
        // layer is anoncrypt, so AnonymousSender is true even though the inner authcrypt authenticates
        // the sender — the flag now reads from the outermost layer rather than OR-accumulating.
        var alice = TestKeyMaterial.Generate(KeyType.X25519, "did:example:alice#x");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");

        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256CBC-HS512",
                SenderKey: alice.PrivateJwk.ToEcdhKey(_crypto), Skid: alice.PublicJwk.Kid, ProtectSender: true), _crypto);

        var unpacked = EnvelopeReaderTestRunner.Unpack(packed,
            new DictionarySecretsLookup(new[] { bob.PrivateJwk }),
            senderLookup: new DictionarySenderKeyLookup(new[] { alice.PublicJwk }),
            signerLookup: null, _crypto);

        unpacked.AnonymousSender.Should().BeTrue();             // outermost layer is anoncrypt
        unpacked.Authenticated.Should().BeTrue();               // inner authcrypt bound the sender
        unpacked.SenderKid.Should().Be(alice.PublicJwk.Kid);
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(System.Security.Cryptography.CryptographicException))]
    public async Task Opaque_recipient_handle_fault_maps_to_uniform_CryptoException(Type faultType)
    {
        // Adversarial regression (issue #45 review, Finding 1): an opaque (keystore/HSM) recipient handle
        // can fault in ways the in-process decoy path never can — a KeyNotFoundException when the kid is
        // rotated out between selection and derive, or an enclave CryptographicException. UnpackAsync MUST
        // fold these into the SAME uniform CryptoException as any decrypt failure, so no raw exception
        // escapes the FR-API-07 contract and the held path leaks no possession oracle by exception type.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#x");
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(EmptyMessage(), new[] { bob.PublicJwk }, "A256GCM"), _crypto);

        var fault = (Exception)Activator.CreateInstance(faultType)!;
        var faulting = new FaultingOpaqueResolver(bob.PublicJwk, fault);

        Action act = () => EnvelopeReaderTestRunner.Unpack(packed, faulting, senderLookup: null, signerLookup: null, _crypto);

        act.Should().Throw<CryptoException>();
    }

    // Reports the recipient kid as held and returns a key-agreement handle whose derive throws — modelling
    // a keystore key that vanishes (or an enclave that errors) between selection and the ECDH operation.
    private sealed class FaultingOpaqueResolver : ISecretsResolver, IOpaqueKeyResolver
    {
        private readonly Jwk _publicJwk;
        private readonly Exception _fault;
        public FaultingOpaqueResolver(Jwk publicJwk, Exception fault) { _publicJwk = publicJwk; _fault = fault; }

        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => Task.FromResult<Jwk?>(kid == _publicJwk.Kid ? _publicJwk : null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(kids.Where(k => k == _publicJwk.Kid).ToArray());
        public Task<ISigner?> ResolveSignerAsync(string kid, CancellationToken ct = default)
            => Task.FromResult<ISigner?>(null);
        public Task<DpEnc.IEcdhKey?> ResolveKeyAgreementAsync(string kid, CancellationToken ct = default)
            => Task.FromResult<DpEnc.IEcdhKey?>(new ThrowingEcdhKey(_publicJwk.Crv!, _fault));
    }

    private sealed class ThrowingEcdhKey : DpEnc.IEcdhKey
    {
        private readonly Exception _fault;
        public ThrowingEcdhKey(string crv, Exception fault) { Crv = crv; _fault = fault; }
        public string Crv { get; }
        public ValueTask<byte[]> DeriveAsync(ReadOnlyMemory<byte> peerPublicKey, CancellationToken ct = default)
            => throw _fault;
    }
}
