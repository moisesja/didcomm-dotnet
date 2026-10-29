using System.Text.Json.Nodes;
using DidComm.Composition;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using AwesomeAssertions;
using NetCrypto;
using Xunit;
using DpBase64Url = DataProofsDotnet.Jose.Base64Url;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using DpSig = DataProofsDotnet.Jose.Signing;
using JoseAlgorithms = DataProofsDotnet.Jose.JoseAlgorithms;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Facade;

/// <summary>
/// Pins the key-id length bound at the public unpack boundary (FR-API-09, #80). Without it, a key id
/// could be as long as the whole envelope, and NetDid would DID-parse it before any authentication:
/// tens of milliseconds and tens of megabytes per value, for any sender.
/// <para>
/// Most key ids never pass through didcomm as values. DataProofs reads the JWS signer kid and the
/// authcrypt sender kid out of the envelope and hands them straight to didcomm's lookups, so the
/// bound has to sit in those lookups, and a test has to drive a real envelope through
/// <see cref="DidCommClient.UnpackAsync"/> to reach it (L-048). Each position gets a hostile case one
/// character over the bound, one near the maximum size the receive gate admits, and a control at
/// exactly the bound that unpacks.
/// </para>
/// <para>
/// A hostile kid is a short DID plus a long fragment, and the served DID document publishes it. So
/// without the bound the receiver would genuinely parse the kid, resolve the DID and verify or
/// decrypt. Every hostile case asserts the exception type, that its message does not quote the value,
/// and, through spies, what reached the DID resolver and the secrets resolver. Hostile envelopes are
/// built with the envelope writer, which does no DID parsing; the controls go through the public pack
/// path, the way an honest sender would.
/// </para>
/// </summary>
public sealed class IdentifierLengthCapTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";
    private const string BobKid = Bob + "#ka-1";

    private static readonly JoseCryptoProvider Crypto = new();
    private static readonly IKeyGenerator KeyGenerator = new DefaultKeyGenerator();

    // ---- JWS signer kid ----

    [Theory]
    // Auto is the placement PackSignedAsync uses: the per-signature unprotected header.
    [InlineData(LengthCap.KeyIdCap + 1, DpSig.JwsKidPlacement.Auto, false)]
    [InlineData(LengthCap.KeyIdCap + 1, DpSig.JwsKidPlacement.Auto, true)]
    [InlineData(1_000_000, DpSig.JwsKidPlacement.Auto, false)]
    [InlineData(1_000_000, DpSig.JwsKidPlacement.Auto, true)]
    // In the protected header the kid is base64url-encoded, so the largest that fits is smaller.
    [InlineData(LengthCap.KeyIdCap + 1, DpSig.JwsKidPlacement.Protected, false)]
    [InlineData(700_000, DpSig.JwsKidPlacement.Protected, false)]
    public async Task Signer_kid_over_the_cap_is_refused_before_the_signer_lookup(
        int length, DpSig.JwsKidPlacement placement, bool longDid)
    {
        // longDid: the DID itself is long, so without the bound the resolver would be asked for it.
        var kid = longDid
            ? LengthCap.DidOfLength(length - "#key-1".Length) + "#key-1"
            : LengthCap.KidOfLength(Alice, length);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
        var packed = (await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, placement)).ShouldFitTheReceiveLimit();
        KidIsInProtectedHeader(packed).Should().Be(placement == DpSig.JwsKidPlacement.Protected);

        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: signer.PublicJwk));
        var secrets = new SpySecretsResolver();

        var failure = await RefusalOf(Receiver(secrets, resolver), packed);

        failure.Message.Should().Contain("JWS signer 'kid'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the kid is refused before the signer lookup resolves anything");
        secrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    [Theory]
    [InlineData(DpSig.JwsKidPlacement.Auto)]
    [InlineData(DpSig.JwsKidPlacement.Protected)]
    public async Task Signer_kid_at_exactly_the_cap_unpacks(DpSig.JwsKidPlacement placement)
    {
        var kid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: signer.PublicJwk));

        var packed = placement == DpSig.JwsKidPlacement.Auto
            ? await Client(resolver, signer.PrivateJwk).PackSignedAsync(Ping(Alice, Bob), Alice)
            : await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, placement);
        KidIsInProtectedHeader(packed).Should().Be(placement == DpSig.JwsKidPlacement.Protected);

        var result = await Receiver(new SpySecretsResolver(), resolver).UnpackAsync(packed);

        result.SignerKid.Should().Be(kid);
        result.SignerKeyBinding!.Kid.Should().Be(kid);
        result.Message.From.Should().Be(Alice);
    }

    // ---- JWE recipient kid ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(1_000_000)]
    public async Task Recipient_kid_over_the_cap_is_refused_before_key_selection_for_holder_and_stranger_alike(int length)
    {
        // Carol's entry is the long one; Bob holds a key for the other. Packed for real, so apv
        // commits to both kids and nothing but the length is wrong with the envelope.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Carol, length));
        var packed = (await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, Bob, Carol), new[] { bob.PublicJwk, carol.PublicJwk }, JoseAlgorithms.A256CbcHs512),
            Crypto)).ShouldFitTheReceiveLimit();

        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Carol, keyAgreement: carol.PublicJwk));
        var holderSecrets = new SpySecretsResolver(bob.PrivateJwk);
        var strangerSecrets = new SpySecretsResolver();

        var holder = await RefusalOf(Receiver(holderSecrets, resolver), packed);
        var stranger = await RefusalOf(Receiver(strangerSecrets, resolver), packed);

        holder.Message.Should().Contain("JWE recipient 'kid'").And.Contain("4096");
        stranger.Message.Should().Be(holder.Message,
            "the check runs before key selection, so it must not tell a holder from a stranger");
        holder.ShouldNotQuoteTheValue();
        holderSecrets.Requested.Should().BeEmpty(
            "the refusal comes before FindPresentAsync, so the keystore never sees the over-long kid");
        strangerSecrets.Requested.Should().BeEmpty();
        resolver.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task Recipient_kid_at_exactly_the_cap_unpacks_for_every_recipient()
    {
        var carolKid = LengthCap.KidOfLength(Carol, LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, carolKid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Carol, keyAgreement: carol.PublicJwk));
        var packed = (await Client(resolver).PackEncryptedAsync(
            Ping(null, Bob, Carol), new PackEncryptedOptions(Recipients: new[] { Bob, Carol }))).Message;

        var forBob = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);
        forBob.RecipientKid.Should().Be(BobKid);
        forBob.AllRecipientKids.Should().Contain(carolKid);

        var forCarol = await Receiver(new SpySecretsResolver(carol.PrivateJwk), resolver).UnpackAsync(packed);
        forCarol.RecipientKid.Should().Be(carolKid);
        forCarol.RecipientKeyBinding!.Kid.Should().Be(carolKid);
    }

    // ---- Authcrypt sender kid ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    // skid and apu = base64url(skid) both sit in the base64url-encoded protected header.
    [InlineData(280_000)]
    public async Task Sender_skid_over_the_cap_is_refused_before_the_sender_lookup_for_holder_and_stranger_alike(int length)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Alice, length));
        var packed = (await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                Ping(Alice, Bob), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                SenderKey: alice.PrivateJwk.ToEcdhKey(Crypto), Skid: alice.PublicJwk.Kid),
            Crypto)).ShouldFitTheReceiveLimit();

        await SenderKidShouldBeRefusedAlike(packed, bob, alice);
    }

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(500_000)]
    public async Task Sender_kid_carried_only_in_apu_over_the_cap_is_refused_for_holder_and_stranger_alike(int length)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Alice, length));
        var packed = AuthcryptWithApuOnly(Ping(Alice, Bob), bob.PublicJwk, alice.PrivateJwk, alice.PublicJwk.Kid!)
            .ShouldFitTheReceiveLimit();

        // No skid at all, so a check on the peeked skid would let this through. DataProofs decodes the
        // sender kid from apu and hands it to the sender lookup, the only place didcomm sees it.
        DpEnc.JweParser.PeekRecipients(packed).Skid.Should().BeNull();

        await SenderKidShouldBeRefusedAlike(packed, bob, alice);
    }

    [Fact]
    public async Task Sender_skid_at_exactly_the_cap_unpacks()
    {
        var kid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, kid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk));
        var packed = (await Client(resolver, alice.PrivateJwk).PackEncryptedAsync(
            Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.Authenticated.Should().BeTrue();
        result.SenderKid.Should().Be(kid);
        result.SenderKeyBinding!.Kid.Should().Be(kid);
    }

    [Fact]
    public async Task Sender_kid_carried_only_in_apu_at_exactly_the_cap_unpacks()
    {
        var kid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, kid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk));
        var packed = AuthcryptWithApuOnly(Ping(Alice, Bob), bob.PublicJwk, alice.PrivateJwk, kid);
        DpEnc.JweParser.PeekRecipients(packed).Skid.Should().BeNull();

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.Authenticated.Should().BeTrue();
        result.SenderKid.Should().Be(kid);
        result.SenderKeyBinding!.Kid.Should().Be(kid);
    }

    // ---- Inner layers ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(650_000)]
    public async Task Inner_signer_kid_over_the_cap_under_anoncrypt_is_refused(int length)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(Alice, length));
        var packed = (await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                Ping(Alice, Bob), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                Signers: new[] { signer.PrivateJwk.ToJwsSigner() }),
            Crypto)).ShouldFitTheReceiveLimit();

        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, authentication: signer.PublicJwk));
        var secrets = new SpySecretsResolver(bob.PrivateJwk);

        // Only a holder gets this far: the outer layer has to decrypt before the signature is read.
        var failure = await RefusalOf(Receiver(secrets, resolver), packed);

        failure.Message.Should().Contain("JWS signer 'kid'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the inner kid is refused before the signer lookup resolves anything");
        secrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    // The inner protected header is base64url-encoded twice over: once in the inner JWE, once as outer ciphertext.
    [InlineData(200_000)]
    public async Task Inner_sender_skid_over_the_cap_under_anoncrypt_is_refused(int length)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Alice, length));
        var packed = (await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                Ping(Alice, Bob), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                SenderKey: alice.PrivateJwk.ToEcdhKey(Crypto), Skid: alice.PublicJwk.Kid, ProtectSender: true),
            Crypto)).ShouldFitTheReceiveLimit();

        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk));
        var secrets = new SpySecretsResolver(bob.PrivateJwk);

        var failure = await RefusalOf(Receiver(secrets, resolver), packed);

        failure.Message.Should().Contain("JWE sender 'skid'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the inner skid is refused before the sender lookup resolves anything");
        secrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    [Fact]
    public async Task Inner_signer_kid_at_exactly_the_cap_under_anoncrypt_unpacks()
    {
        var kid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, authentication: signer.PublicJwk));
        var packed = (await Client(resolver, signer.PrivateJwk).PackEncryptedAsync(
            Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, SignFrom: Alice))).Message;

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.NonRepudiation.Should().BeTrue();
        result.SignerKid.Should().Be(kid);
        result.SignerKeyBinding!.Kid.Should().Be(kid);
    }

    [Fact]
    public async Task Inner_sender_skid_at_exactly_the_cap_under_anoncrypt_unpacks()
    {
        var kid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, kid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk));
        var packed = (await Client(resolver, alice.PrivateJwk).PackEncryptedAsync(
            Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, ProtectSender: true))).Message;

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.AnonymousSender.Should().BeTrue();
        result.Authenticated.Should().BeTrue();
        result.SenderKid.Should().Be(kid);
    }

    // ---- The margin between the two bounds (D2) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sender_whose_from_is_exactly_the_DID_cap_can_present_its_own_kid(bool authcrypt)
    {
        // FR-CONSIST-01/03 require the sender's kid to be under 'from'. A 'from' at the DID bound plus
        // a fragment is longer than the DID bound, which is why key ids get a larger one.
        var from = LengthCap.DidOfLength(LengthCap.DidCap);
        var kid = from + "#key-1";
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var senderKey = TestKeyMaterial.Generate(authcrypt ? KeyType.X25519 : KeyType.Ed25519, kid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            authcrypt
                ? LengthCap.Doc(from, keyAgreement: senderKey.PublicJwk)
                : LengthCap.Doc(from, authentication: senderKey.PublicJwk));
        var sender = Client(resolver, senderKey.PrivateJwk);

        var packed = authcrypt
            ? (await sender.PackEncryptedAsync(Ping(from, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: from))).Message
            : await sender.PackSignedAsync(Ping(from, Bob), from);

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.Authenticated.Should().BeTrue();
        result.Message.From.Should().Be(from);
        (authcrypt ? result.SenderKid : result.SignerKid).Should().Be(kid);
    }

    // ---- DidCommOptions.OwnIdentifiers ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(1_000_000)]
    public void OwnIdentifiers_entry_over_the_cap_is_refused_at_construction_without_quoting_it(int length)
    {
        var options = new DidCommOptions { OwnIdentifiers = new[] { Bob, LengthCap.KidOfLength(Bob, length) } };

        Action act = () => _ = new DidCommClient(
            new SpySecretsResolver(), new NetDidKeyService(new SpyDidResolver()), options);

        var failure = act.Should().Throw<ArgumentException>().Which;
        failure.Message.Should().Contain("OwnIdentifiers").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
    }

    [Fact]
    public async Task OwnIdentifiers_entry_at_exactly_the_cap_is_honored()
    {
        var options = new DidCommOptions { OwnIdentifiers = new[] { LengthCap.KidOfLength(Bob, LengthCap.KeyIdCap) } };
        var receiver = new DidCommClient(
            new SpySecretsResolver(), new NetDidKeyService(new SpyDidResolver()), options);

        var result = await receiver.UnpackAsync(EnvelopeWriter.PackPlaintext(Ping(null, Bob)));

        result.RecipientAddressing.Should().Be(RecipientAddressing.Addressed);
    }

    // ---- Helpers ----

    private static Message Ping(string? from, params string[] to)
    {
        var builder = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithTo(to)
            .WithBody(new JsonObject { ["v"] = 1 });
        if (from is not null)
            builder = builder.WithFrom(from);
        return builder.Build();
    }

    private static DidCommClient Receiver(SpySecretsResolver secrets, SpyDidResolver resolver)
        => new(secrets, new NetDidKeyService(resolver), new DidCommOptions());

    private static DidCommClient Client(SpyDidResolver resolver, params Jwk[] privateJwks)
        => Receiver(new SpySecretsResolver(privateJwks), resolver);

    private static async Task<MalformedMessageException> RefusalOf(DidCommClient receiver, string packed)
        => (await receiver.Invoking(c => c.UnpackAsync(packed)).Should().ThrowAsync<MalformedMessageException>()).Which;

    /// <summary>
    /// The authcrypt-sender assertions, shared by the skid and apu-only forms. Alice's served document
    /// publishes the long kid, so without the bound the sender lookup would parse it, resolve Alice
    /// and decrypt. Holder and stranger must fail the same way: the refusal runs inside DataProofs'
    /// ResolveSender, which both the held and the decoy path reach.
    /// </summary>
    private static async Task SenderKidShouldBeRefusedAlike(string packed, TestKeyMaterial bob, TestKeyMaterial alice)
    {
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk));
        var holderSecrets = new SpySecretsResolver(bob.PrivateJwk);
        var strangerSecrets = new SpySecretsResolver();

        var holder = await RefusalOf(Receiver(holderSecrets, resolver), packed);
        var stranger = await RefusalOf(Receiver(strangerSecrets, resolver), packed);

        holder.Message.Should().Contain("JWE sender 'skid'").And.Contain("4096");
        stranger.Message.Should().Be(holder.Message, "a holder and a stranger must not be told apart");
        holder.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the sender kid is refused before the sender lookup resolves anything");
        holderSecrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
        strangerSecrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    /// <summary>Sign with the kid in the header <paramref name="placement"/> selects; Auto is what the facade emits.</summary>
    private static Task<string> PackSigned(Message message, Jwk signerPrivateJwk, DpSig.JwsKidPlacement placement)
    {
        var handle = signerPrivateJwk.ToJwsSigner();
        return EnvelopeWriter.PackSignedAsync(new PackSignedParameters(
            message, new[] { new DpSig.JwsSigner(handle.Signer, handle.Kid, placement) }));
    }

    private static bool KidIsInProtectedHeader(string packedJws)
    {
        var signature = JsonNode.Parse(packedJws)!["signatures"]![0]!;
        var protectedHeader = JsonNode.Parse(DpBase64Url.DecodeUtf8(signature["protected"]!.GetValue<string>()))!.AsObject();
        return protectedHeader.ContainsKey("kid");
    }

    /// <summary>
    /// Build an ECDH-1PU+A256KW / A256CBC-HS512 JWE that names its sender only in <c>apu</c>, with no
    /// <c>skid</c>. DataProofs' builder always writes <c>skid</c>, and the protected header is the
    /// content-encryption AAD, so deleting <c>skid</c> from a built envelope would break decryption.
    /// This assembles the same construction from public primitives (draft-madden-jose-ecdh-1pu-04
    /// §2.3: Z = Ze ‖ Zs, and SuppPubInfo binds the key length and the length-prefixed tag), so the
    /// at-cap control decrypts for real.
    /// </summary>
    private static string AuthcryptWithApuOnly(Message message, Jwk recipientPublic, Jwk senderPrivate, string senderKid)
    {
        var plaintext = Encoding.UTF8.GetBytes(EnvelopeWriter.PackPlaintext(message));
        var ephemeral = KeyGenerator.Generate(KeyType.X25519);
        var recipientPublicKey = DpBase64Url.Decode(recipientPublic.X!);
        var senderPrivateKey = DpBase64Url.Decode(senderPrivate.D!);
        var apu = Encoding.UTF8.GetBytes(senderKid);
        var apv = DpEnc.ApvComputer.ComputeBytes(new[] { recipientPublic.Kid! });

        var header = new JsonObject
        {
            ["typ"] = MediaTypes.Encrypted,
            ["alg"] = JoseAlgorithms.Ecdh1PuA256Kw,
            ["enc"] = JoseAlgorithms.A256CbcHs512,
            ["epk"] = new JsonObject
            {
                ["kty"] = "OKP",
                ["crv"] = JoseAlgorithms.CrvX25519,
                ["x"] = DpBase64Url.Encode(ephemeral.PublicKey),
            },
            ["apu"] = DpBase64Url.Encode(apu),
            ["apv"] = DpBase64Url.Encode(apv),
        };
        var protectedB64u = DpBase64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));

        var cek = new byte[64];
        var iv = new byte[16];
        Crypto.Fill(cek);
        Crypto.Fill(iv);
        var (ciphertext, tag) = Crypto.AeadEncrypt(
            JoseAlgorithms.A256CbcHs512, cek, iv, Encoding.ASCII.GetBytes(protectedB64u), plaintext);

        var ze = Crypto.DeriveSharedSecret(JoseAlgorithms.CrvX25519, ephemeral.PrivateKey, recipientPublicKey);
        var zs = Crypto.DeriveSharedSecret(JoseAlgorithms.CrvX25519, senderPrivateKey, recipientPublicKey);
        var suppPubInfo = new byte[8 + tag.Length];
        BinaryPrimitives.WriteUInt32BigEndian(suppPubInfo, 256);
        BinaryPrimitives.WriteUInt32BigEndian(suppPubInfo.AsSpan(4), (uint)tag.Length);
        tag.CopyTo(suppPubInfo, 8);
        var kek = ConcatKdf.DeriveKey(
            [.. ze, .. zs], Encoding.ASCII.GetBytes(JoseAlgorithms.Ecdh1PuA256Kw), apu, apv, suppPubInfo,
            ReadOnlySpan<byte>.Empty, 32);
        var encryptedKey = Crypto.KeyWrap(JoseAlgorithms.A256Kw, kek, cek);

        return new JsonObject
        {
            ["protected"] = protectedB64u,
            ["recipients"] = new JsonArray(new JsonObject
            {
                ["header"] = new JsonObject { ["kid"] = recipientPublic.Kid },
                ["encrypted_key"] = DpBase64Url.Encode(encryptedKey),
            }),
            ["iv"] = DpBase64Url.Encode(iv),
            ["ciphertext"] = DpBase64Url.Encode(ciphertext),
            ["tag"] = DpBase64Url.Encode(tag),
        }.ToJsonString();
    }
}
