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
public sealed partial class IdentifierLengthCapTests
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

    // ---- JWE recipient kid: over the limits means not held ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(1_000_000)]
    public async Task Group_message_naming_a_member_kid_over_the_cap_still_reaches_the_other_members(int length)
    {
        // The real scenario: Carol's own DID document publishes an over-long kid (DID Core sets no
        // limit), and an honest sender packs one message to Bob and Carol through the public API.
        // Refusing the whole envelope would make every group message undeliverable to Bob because of
        // Carol's document, so Carol's entry is treated as not held instead.
        var carolKid = LengthCap.KidOfLength(Carol, length);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, carolKid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Carol, keyAgreement: carol.PublicJwk));
        var packed = (await Client(resolver).PackEncryptedAsync(
            Ping(null, Bob, Carol), new PackEncryptedOptions(Recipients: new[] { Bob, Carol }))).Message.ShouldFitTheReceiveLimit();
        var bobSecrets = new SpySecretsResolver(bob.PrivateJwk);

        var result = await Receiver(bobSecrets, resolver).UnpackAsync(packed);

        result.RecipientKid.Should().Be(BobKid);
        result.AllRecipientKids.Should().Equal(DpEnc.JweParser.PeekRecipients(packed).RecipientKids,
            "AllRecipientKids reports the envelope's kids verbatim");
        result.AllRecipientKids.Should().Contain(carolKid);
        bobSecrets.Requested.Should().NotContain(carolKid, "an over-long recipient kid is never offered to the keystore");
        resolver.Requested.Should().NotContain(d => d.Length > LengthCap.DidCap);

        await PassedOverKidShouldChangeNothingForOthers(packed, carol);
    }

    [Fact]
    public async Task Recipient_kid_whose_DID_part_is_over_the_DID_cap_is_passed_over()
    {
        // Under the 4096 total, but 2049 characters of DID before the fragment. That DID cannot appear
        // in 'to' (the DID bound applies there), so the plaintext names Bob alone.
        var longKid = LengthCap.KidWithDidPart(LengthCap.DidCap + 1, "#ka-1");
        longKid.Length.Should().BeLessThan(LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var other = TestKeyMaterial.Generate(KeyType.X25519, longKid);
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, Bob), new[] { bob.PublicJwk, other.PublicJwk }, JoseAlgorithms.A256CbcHs512),
            Crypto);
        var resolver = new SpyDidResolver(LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));
        var bobSecrets = new SpySecretsResolver(bob.PrivateJwk);

        var result = await Receiver(bobSecrets, resolver).UnpackAsync(packed);

        result.RecipientKid.Should().Be(BobKid);
        result.AllRecipientKids.Should().Equal(BobKid, longKid);
        bobSecrets.Requested.Should().NotContain(longKid);
        resolver.Requested.Should().NotContain(d => d.Length > LengthCap.DidCap);

        await PassedOverKidShouldChangeNothingForOthers(packed, other);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("did-part")]
    public async Task Envelope_whose_only_recipient_kid_is_over_the_limits_fails_like_any_envelope_not_addressed_to_us(string shape)
    {
        var longKid = shape == "total"
            ? LengthCap.KidOfLength(Carol, LengthCap.KeyIdCap + 1)
            : LengthCap.KidWithDidPart(LengthCap.DidCap + 1, "#ka-1");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, longKid);
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, Bob), new[] { carol.PublicJwk }, JoseAlgorithms.A256CbcHs512), Crypto);
        var baseline = await NotAddressedToUsFailure();

        // The holder of the over-long kid's key, a holder of an unrelated key, and a stranger.
        var spies = new[]
        {
            new SpySecretsResolver(carol.PrivateJwk),
            new SpySecretsResolver(bob.PrivateJwk),
            new SpySecretsResolver(),
        };
        foreach (var secrets in spies)
        {
            var failure = (await Receiver(secrets, new SpyDidResolver()).Invoking(c => c.UnpackAsync(packed))
                .Should().ThrowAsync<CryptoException>()).Which;
            failure.Message.Should().Be(baseline.Message, "the outcome must not depend on which keys are held");
            failure.ShouldNotQuoteTheValue();
        }

        spies.Should().HaveCount(3);
        spies.Should().AllSatisfy(s =>
        {
            s.Requested.Should().BeEmpty("no kid in this envelope is within the limits");
            s.FindPresentCalls.Should().Be(0, "with no candidate left, the decoy runs without the keystore being called at all");
        });
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

    // ---- The DID part of a signer or sender kid ----

    [Theory]
    [InlineData("jws", "#key-1")]
    [InlineData("jws", "")]
    [InlineData("skid", "#key-1")]
    [InlineData("skid", "")]
    [InlineData("apu", "#key-1")]
    [InlineData("apu", "")]
    public async Task Signer_or_sender_kid_whose_DID_part_is_over_the_DID_cap_is_refused(string position, string suffix)
    {
        // Within the 4096 total, but 2049 characters before the first delimiter (an empty suffix
        // leaves no delimiter at all). Without the rule, the lookup would resolve that DID before
        // FR-CONSIST-01/03 compare it with 'from'. Its document publishes the kid, so it would succeed.
        // A DID part of exactly 2048 is accepted: see Sender_whose_from_is_exactly_the_DID_cap_can_present_its_own_kid.
        var kid = LengthCap.KidWithDidPart(LengthCap.DidCap + 1, suffix);
        var longDid = kid[..(LengthCap.DidCap + 1)];
        kid.Length.Should().BeLessThan(LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);

        if (position == "jws")
        {
            var signer = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
            var signed = await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, DpSig.JwsKidPlacement.Auto);
            var resolver = new SpyDidResolver(LengthCap.Doc(longDid, authentication: signer.PublicJwk));

            var failure = await RefusalOf(Receiver(new SpySecretsResolver(), resolver), signed);

            failure.Message.Should().Contain("JWS signer 'kid' has a DID part longer than the maximum of 2048");
            failure.ShouldNotQuoteTheValue();
            resolver.Requested.Should().BeEmpty("the kid is refused before the signer lookup resolves its DID");
            return;
        }

        var alice = TestKeyMaterial.Generate(KeyType.X25519, kid);
        var packed = position == "skid"
            ? await EnvelopeWriter.PackEncryptedAsync(
                new PackEncryptedParameters(
                    Ping(Alice, Bob), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                    SenderKey: alice.PrivateJwk.ToEcdhKey(Crypto), Skid: kid),
                Crypto)
            : AuthcryptWithApuOnly(Ping(Alice, Bob), bob.PublicJwk, alice.PrivateJwk, kid);

        await SenderKidShouldBeRefusedAlike(packed, bob, alice,
            expected: "JWE sender 'skid' has a DID part longer than the maximum of 2048", senderDid: longDid);
    }

    // ---- Our own signer and sender selection ----

    [Theory]
    [InlineData("total")]
    [InlineData("did-part")]
    public async Task Signer_whose_first_key_is_over_the_limits_signs_with_the_next_one(string shape)
    {
        // Every didcomm-dotnet receiver refuses a signer kid over the limits, so picking it would
        // produce an undeliverable message. It is left out before the keystore is asked.
        var longKid = OwnKidOverTheLimits(shape);
        var normalKid = Alice + "#auth-2";
        var longKey = TestKeyMaterial.Generate(KeyType.Ed25519, longKid);
        var normal = TestKeyMaterial.Generate(KeyType.Ed25519, normalKid);
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, new[] { longKey.PublicJwk, normal.PublicJwk }, Array.Empty<Jwk>()));
        var aliceSecrets = new SpySecretsResolver(longKey.PrivateJwk, normal.PrivateJwk);
        var sender = new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions());

        var packed = await sender.PackSignedAsync(Ping(Alice, Bob), Alice);

        aliceSecrets.Requested.Should().NotContain(longKid, "the over-long kid is never offered to the keystore");
        var result = await Receiver(new SpySecretsResolver(), resolver).UnpackAsync(packed);
        result.SignerKid.Should().Be(normalKid);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("did-part")]
    public async Task Signer_whose_only_key_is_over_the_limits_is_told_why_there_is_no_usable_key(string shape)
    {
        var longKid = OwnKidOverTheLimits(shape);
        var longKey = TestKeyMaterial.Generate(KeyType.Ed25519, longKid);
        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: longKey.PublicJwk));
        var aliceSecrets = new SpySecretsResolver(longKey.PrivateJwk);
        var sender = new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions());

        var failure = (await sender.Invoking(c => c.PackSignedAsync(Ping(Alice, Bob), Alice))
            .Should().ThrowAsync<SecretNotFoundException>()).Which;

        failure.Message.Should().Contain("FR-API-09 identifier limits").And.Contain("1 of its 1 authentication keys")
            .And.NotContain("returned null", "the keystore was never asked about the over-limit key");
        failure.Kid.Should().Be(Alice);
        failure.ShouldNotQuoteTheValue();
        aliceSecrets.Requested.Should().NotContain(longKid);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("did-part")]
    public async Task Authcrypt_sender_whose_first_key_is_over_the_limits_sends_with_the_next_one(string shape)
    {
        var longKid = OwnKidOverTheLimits(shape);
        var normalKid = Alice + "#ka-2";
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var longKey = TestKeyMaterial.Generate(KeyType.X25519, longKid);
        var normal = TestKeyMaterial.Generate(KeyType.X25519, normalKid);
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, Array.Empty<Jwk>(), new[] { longKey.PublicJwk, normal.PublicJwk }),
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));
        var aliceSecrets = new SpySecretsResolver(longKey.PrivateJwk, normal.PrivateJwk);
        var sender = new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions());

        var packed = (await sender.PackEncryptedAsync(
            Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        aliceSecrets.Requested.Should().NotContain(longKid, "the over-long kid is never offered to the keystore");
        DpEnc.JweParser.PeekRecipients(packed).Skid.Should().Be(normalKid);
        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);
        result.SenderKid.Should().Be(normalKid);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("did-part")]
    public async Task Authcrypt_sender_whose_only_key_is_over_the_limits_is_told_why_there_is_no_usable_key(string shape)
    {
        var longKid = OwnKidOverTheLimits(shape);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var longKey = TestKeyMaterial.Generate(KeyType.X25519, longKid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Alice, keyAgreement: longKey.PublicJwk),
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));
        var aliceSecrets = new SpySecretsResolver(longKey.PrivateJwk);
        var sender = new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions());

        var failure = (await sender.Invoking(c => c.PackEncryptedAsync(
                Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice)))
            .Should().ThrowAsync<SecretNotFoundException>()).Which;

        failure.Message.Should().Contain("FR-API-09 identifier limits").And.Contain("1 of its 1 keyAgreement keys")
            .And.NotContain("returned null", "the keystore was never asked about the over-limit key");
        failure.Kid.Should().Be(Alice);
        failure.ShouldNotQuoteTheValue();
        aliceSecrets.Requested.Should().NotContain(longKid);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("did-part")]
    public async Task Signer_skips_an_over_limit_key_even_when_the_secrets_resolver_lists_more_than_it_was_asked(string shape)
    {
        // A resolver that answers FindPresentAsync with everything it holds breaks the subset
        // contract. Its first answer is the over-limit key; the signer must still not pick it.
        var longKid = OwnKidOverTheLimits(shape);
        var normalKid = Alice + "#auth-2";
        var longKey = TestKeyMaterial.Generate(KeyType.Ed25519, longKid);
        var normal = TestKeyMaterial.Generate(KeyType.Ed25519, normalKid);
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, new[] { longKey.PublicJwk, normal.PublicJwk }, Array.Empty<Jwk>()));
        var aliceSecrets = new SpySecretsResolver(longKey.PrivateJwk, normal.PrivateJwk) { ListsEverythingHeld = true };
        var sender = new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions());

        var packed = await sender.PackSignedAsync(Ping(Alice, Bob), Alice);

        packed.Should().NotContain(longKid);
        var result = await Receiver(new SpySecretsResolver(), resolver).UnpackAsync(packed);
        result.SignerKid.Should().Be(normalKid);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("did-part")]
    public async Task Authcrypt_sender_skips_an_over_limit_key_even_when_the_secrets_resolver_lists_more_than_it_was_asked(string shape)
    {
        var longKid = OwnKidOverTheLimits(shape);
        var normalKid = Alice + "#ka-2";
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var longKey = TestKeyMaterial.Generate(KeyType.X25519, longKid);
        var normal = TestKeyMaterial.Generate(KeyType.X25519, normalKid);
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, Array.Empty<Jwk>(), new[] { longKey.PublicJwk, normal.PublicJwk }),
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));
        var aliceSecrets = new SpySecretsResolver(longKey.PrivateJwk, normal.PrivateJwk) { ListsEverythingHeld = true };
        var sender = new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions());

        var packed = (await sender.PackEncryptedAsync(
            Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        DpEnc.JweParser.PeekRecipients(packed).Skid.Should().Be(normalKid);
        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);
        result.SenderKid.Should().Be(normalKid);
    }

    [Fact]
    public async Task No_common_curve_error_says_a_sender_key_was_skipped_for_its_length()
    {
        // Alice's only X25519 key is over the limits and Bob has only X25519, so the remaining P-256
        // key shares no curve with him. The error should say why, not just that no curve matched.
        var longKid = OwnKidOverTheLimits("total");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var longKey = TestKeyMaterial.Generate(KeyType.X25519, longKid);
        var p256 = TestKeyMaterial.Generate(KeyType.P256, Alice + "#p256");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, Array.Empty<Jwk>(), new[] { longKey.PublicJwk, p256.PublicJwk }),
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));
        var aliceSecrets = new SpySecretsResolver(longKey.PrivateJwk, p256.PrivateJwk);
        var sender = new DidCommClient(aliceSecrets, new NetDidKeyService(resolver), new DidCommOptions());

        var failure = (await sender.Invoking(c => c.PackEncryptedAsync(
                Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice)))
            .Should().ThrowAsync<InvalidOperationException>()).Which;

        failure.Message.Should().StartWith("No common keyAgreement curve")
            .And.Contain("1 of the sender's keyAgreement keys were not considered")
            .And.Contain("FR-API-09");
        failure.Message.Should().NotContain(longKid);
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
    /// The authcrypt-sender assertions, shared by the skid and apu-only forms. The sender DID's served
    /// document (Alice's unless <paramref name="senderDid"/> says otherwise) publishes the long kid, so
    /// without the bound the sender lookup would parse it, resolve that DID and decrypt. Holder and stranger must fail the same way: the refusal runs inside DataProofs'
    /// ResolveSender, which both the held and the decoy path reach.
    /// </summary>
    private static async Task SenderKidShouldBeRefusedAlike(
        string packed, TestKeyMaterial bob, TestKeyMaterial alice,
        string expected = "JWE sender 'skid' exceeds the maximum length of 4096", string senderDid = Alice)
    {
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(senderDid, keyAgreement: alice.PublicJwk));
        var holderSecrets = new SpySecretsResolver(bob.PrivateJwk);
        var strangerSecrets = new SpySecretsResolver();

        var holder = await RefusalOf(Receiver(holderSecrets, resolver), packed);
        var stranger = await RefusalOf(Receiver(strangerSecrets, resolver), packed);

        holder.Message.Should().Contain(expected);
        stranger.Message.Should().Be(holder.Message, "a holder and a stranger must not be told apart");
        holder.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the sender kid is refused before the sender lookup resolves anything");
        holderSecrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
        strangerSecrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    /// <summary>
    /// A receiver holding only the key of a passed-over recipient kid, and a receiver holding nothing,
    /// must both get exactly the failure of an envelope not addressed to them: holding that key changes
    /// nothing, and neither keystore is asked about the kid.
    /// </summary>
    private static async Task PassedOverKidShouldChangeNothingForOthers(string packed, TestKeyMaterial passedOverKey)
    {
        var baseline = await NotAddressedToUsFailure();
        var holderSecrets = new SpySecretsResolver(passedOverKey.PrivateJwk);
        var strangerSecrets = new SpySecretsResolver();

        foreach (var secrets in new[] { holderSecrets, strangerSecrets })
        {
            var failure = (await Receiver(secrets, new SpyDidResolver()).Invoking(c => c.UnpackAsync(packed))
                .Should().ThrowAsync<CryptoException>()).Which;
            failure.Message.Should().Be(baseline.Message, "holding the passed-over kid's key must not be observable");
            failure.ShouldNotQuoteTheValue();
        }
        holderSecrets.Requested.Should().NotContain(passedOverKey.PrivateJwk.Kid);
        strangerSecrets.Requested.Should().NotContain(passedOverKey.PrivateJwk.Kid);
    }

    /// <summary>What a stranger gets for an ordinary envelope addressed to someone else.</summary>
    private static async Task<CryptoException> NotAddressedToUsFailure()
    {
        var dave = TestKeyMaterial.Generate(KeyType.X25519, "did:example:dave#ka-1");
        var notForUs = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, "did:example:dave"), new[] { dave.PublicJwk }, JoseAlgorithms.A256CbcHs512),
            Crypto);
        return (await Receiver(new SpySecretsResolver(), new SpyDidResolver()).Invoking(c => c.UnpackAsync(notForUs))
            .Should().ThrowAsync<CryptoException>()).Which;
    }

    /// <summary>
    /// An own key id over the limits, as Alice's document might publish it: over 4096 in all
    /// (<c>total</c>), or within 4096 with a 2049-character DID part (<c>did-part</c>).
    /// </summary>
    private static string OwnKidOverTheLimits(string shape) => shape switch
    {
        "total" => LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap + 1),
        "did-part" => LengthCap.KidWithDidPart(LengthCap.DidCap + 1, "#key-1"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

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
