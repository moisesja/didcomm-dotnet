using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DidComm.Composition;
using DidComm.Diagnostics;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using DpBase64Url = DataProofsDotnet.Jose.Base64Url;
using DpEnc = DataProofsDotnet.Jose.Encryption;
using DpSig = DataProofsDotnet.Jose.Signing;
using JoseAlgorithms = DataProofsDotnet.Jose.JoseAlgorithms;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Facade;

/// <summary>
/// The permutations the per-position tests in the other half of this class leave open (FR-API-09,
/// #80): every JWE recipient-entry position, held or not; inner recipient lists; the flattened JWS
/// form; multi-signature JWS; which error wins when an over-long kid meets another defect, for a
/// holder and a stranger; the length unit; a legacy key service with no binding support; the
/// DI-built client with its logs and spans; concurrent hostile and benign unpacks on one client;
/// and the from/to bound at the unpack boundary.
/// </summary>
public sealed partial class IdentifierLengthCapTests
{
    private const string AliceAuthKid = Alice + "#auth-1";
    private const string AliceKaKid = Alice + "#ka-1";
    private const string SignerField = "JWS signer 'kid'";
    private const string RecipientField = "JWE recipient 'kid'";
    private const string SenderField = "JWE sender 'skid'";
    private const string LengthRefusal = "exceeds the maximum length";

    private static readonly string Astral = char.ConvertFromUtf32(0x1F600);

    // ---- JWE recipient kid: every entry position, held or not ----

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Recipient_kid_over_the_cap_is_refused_in_any_entry_and_whether_or_not_it_is_held(bool longFirst, bool longIsHeld)
    {
        // longIsHeld: the receiver holds the private key of the over-long entry itself, so key
        // selection would pick it. It must still be refused before the keystore is asked.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Carol, LengthCap.KeyIdCap + 1));
        var recipients = longFirst ? new[] { carol.PublicJwk, bob.PublicJwk } : new[] { bob.PublicJwk, carol.PublicJwk };
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, Bob, Carol), recipients, JoseAlgorithms.A256CbcHs512), Crypto);
        var peeked = DpEnc.JweParser.PeekRecipients(packed).RecipientKids;
        (longFirst ? peeked[0] : peeked[^1]).Length.Should().Be(LengthCap.KeyIdCap + 1);

        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Carol, keyAgreement: carol.PublicJwk));
        var holderSecrets = new SpySecretsResolver(longIsHeld ? carol.PrivateJwk : bob.PrivateJwk);
        var strangerSecrets = new SpySecretsResolver();

        var holder = await RefusalOf(Receiver(holderSecrets, resolver), packed);
        var stranger = await RefusalOf(Receiver(strangerSecrets, resolver), packed);

        holder.Message.Should().Contain(RecipientField).And.Contain("4096");
        stranger.Message.Should().Be(holder.Message, "the refusal must not tell a holder from a stranger");
        holder.ShouldNotQuoteTheValue();
        holderSecrets.Requested.Should().BeEmpty("the refusal comes before the keystore is asked about any kid");
        strangerSecrets.Requested.Should().BeEmpty();
        resolver.Requested.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    // The inner JWE travels as base64url ciphertext of the outer one, so it grows by a third.
    [InlineData(600_000)]
    public async Task Inner_recipient_kid_over_the_cap_under_anoncrypt_is_refused(int length)
    {
        // The outer anoncrypt names only Bob, so the outer check passes; only the inner list is long.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Carol, length));
        var packed = (await AnoncryptOfAuthcrypt(bob, alice, carol)).ShouldFitTheReceiveLimit();
        DpEnc.JweParser.PeekRecipients(packed).RecipientKids.Should().Equal(BobKid);

        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk),
            LengthCap.Doc(Carol, keyAgreement: carol.PublicJwk));
        var secrets = new SpySecretsResolver(bob.PrivateJwk);

        var failure = await RefusalOf(Receiver(secrets, resolver), packed);

        failure.Message.Should().Contain(RecipientField).And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the inner list is refused before the inner sender lookup resolves Alice");
        secrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    [Fact]
    public async Task Inner_recipient_kid_at_exactly_the_cap_under_anoncrypt_unpacks()
    {
        var carolKid = LengthCap.KidOfLength(Carol, LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, carolKid);
        var packed = await AnoncryptOfAuthcrypt(bob, alice, carol);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk));

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.AnonymousSender.Should().BeTrue();
        result.SenderKid.Should().Be(AliceKaKid);
        result.AllRecipientKids.Should().Contain(carolKid);
    }

    // ---- JWS: the flattened serialization ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(1_000_000)]
    public async Task Flattened_JWS_signer_kid_over_the_cap_is_refused(int length)
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(Alice, length));
        var packed = ToFlattened(await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, DpSig.JwsKidPlacement.Auto))
            .ShouldFitTheReceiveLimit();
        JsonNode.Parse(packed)!.AsObject().ContainsKey("signatures").Should().BeFalse("this must be the flattened form");
        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: signer.PublicJwk));

        var failure = await RefusalOf(Receiver(new SpySecretsResolver(), resolver), packed);

        failure.Message.Should().Contain(SignerField).And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task Flattened_JWS_signer_kid_at_exactly_the_cap_unpacks()
    {
        var kid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
        var packed = ToFlattened(await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, DpSig.JwsKidPlacement.Auto));
        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: signer.PublicJwk));

        var result = await Receiver(new SpySecretsResolver(), resolver).UnpackAsync(packed);

        result.SignerKid.Should().Be(kid);
    }

    // ---- JWS: more than one signature ----

    [Theory]
    // A signature that fails to verify, then one with an over-long kid: the parser moves on to the
    // second signature and asks for its kid, so the bound refuses it.
    [InlineData("failing-then-long")]
    // An over-long kid first: refused before the valid second signature is tried. A valid signature
    // later in the list does not rescue the envelope.
    [InlineData("long-then-valid")]
    public async Task Multi_signature_JWS_is_refused_once_an_over_long_kid_is_looked_up(string order)
    {
        var (packed, resolver, longKid) = await MultiSignature(order);

        var failure = await RefusalOf(Receiver(new SpySecretsResolver(), resolver), packed);

        failure.Message.Should().Contain(SignerField).And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().NotContain(d => d.Length > LengthCap.DidCap, "the over-long kid's DID is never resolved");
        if (order == "failing-then-long")
            resolver.Requested.Should().Contain(Alice, "the first signature was really tried before the second kid was refused");
        else
            resolver.Requested.Should().BeEmpty("the first kid looked up is the over-long one");
        longKid.Length.Should().BeGreaterThan(LengthCap.KeyIdCap);
    }

    [Fact]
    public async Task Multi_signature_JWS_whose_first_signature_verifies_unpacks_without_looking_up_the_over_long_kid()
    {
        // The documented limit of the claim (plan, "The claim we can defend"): a kid that is never
        // looked up is never checked. It is also never parsed, resolved or surfaced.
        var (packed, resolver, _) = await MultiSignature("valid-then-long");

        var result = await Receiver(new SpySecretsResolver(), resolver).UnpackAsync(packed);

        result.SignerKid.Should().Be(AliceAuthKid);
        resolver.Requested.Should().Equal(Alice);
    }

    // ---- Which error wins: JWS ----

    [Theory]
    [InlineData("alg-none")]
    [InlineData("alg-mismatch")]
    [InlineData("crit")]
    [InlineData("bad-signature")]
    public async Task Signer_kid_over_the_cap_is_refused_ahead_of_any_other_JWS_defect(string defect)
    {
        // Control: the same defect under a short, published kid fails for its own reason, so each
        // defect is real and the length refusal below is not a coincidence.
        var shortSigner = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var shortPacked = WithJwsDefect(await PackSigned(Ping(Alice, Bob), shortSigner.PrivateJwk, DpSig.JwsKidPlacement.Auto), defect);
        var shortResolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: shortSigner.PublicJwk));
        var control = await Record.ExceptionAsync(() => Receiver(new SpySecretsResolver(), shortResolver).UnpackAsync(shortPacked));
        control.Should().BeAssignableTo<DidCommException>();
        control!.Message.Should().NotContain(LengthRefusal);

        var longSigner = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap + 1));
        var packed = WithJwsDefect(await PackSigned(Ping(Alice, Bob), longSigner.PrivateJwk, DpSig.JwsKidPlacement.Auto), defect);
        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: longSigner.PublicJwk));

        var failure = await RefusalOf(Receiver(new SpySecretsResolver(), resolver), packed);

        failure.Message.Should().Contain(SignerField, "the signer lookup runs before the parser checks alg, crit or the signature");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty();
    }

    // ---- Which error wins: JWE, for a holder and a stranger ----

    [Theory]
    [InlineData("none", typeof(MalformedMessageException), SenderField)]
    [InlineData("crit", typeof(MalformedMessageException), "('crit')")]
    [InlineData("apv", typeof(CryptoException), "'apv' mismatch")]
    [InlineData("enc", typeof(CryptoException), "requires enc=A256CBC-HS512")]
    [InlineData("apu", typeof(CryptoException), "FR-ENC-14")]
    // A P-256 epk while the holder's key is X25519: the holder's key is swapped for a P-256 decoy,
    // the path whose shape depends on which key is held. Both still reach the sender lookup.
    [InlineData("epk-p256", typeof(MalformedMessageException), SenderField)]
    public async Task Sender_skid_over_the_cap_with_another_defect_fails_identically_for_holder_and_stranger(
        string defect, Type expectedType, string expectedFragment)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap + 1));
        var packed = WithJweDefect(await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                Ping(Alice, Bob), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                SenderKey: alice.PrivateJwk.ToEcdhKey(Crypto), Skid: alice.PublicJwk.Kid),
            Crypto), defect);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, keyAgreement: alice.PublicJwk));

        var outcomes = await HolderAndStrangerOutcomes(packed, bob, resolver);

        outcomes[0].Should().BeOfType(expectedType);
        outcomes[0].Message.Should().Contain(expectedFragment);
        foreach (var outcome in outcomes)
        {
            outcome.GetType().Should().Be(outcomes[0].GetType(), "holder and stranger, run twice each, must fail the same way");
            outcome.Message.Should().Be(outcomes[0].Message);
            outcome.ShouldNotQuoteTheValue();
        }
        resolver.Requested.Should().BeEmpty("no variant resolves the over-long sender kid");
    }

    [Theory]
    [InlineData("none", RecipientField)]
    [InlineData("crit", RecipientField)]
    [InlineData("apv", RecipientField)]
    [InlineData("enc", RecipientField)]
    [InlineData("alg", RecipientField)]
    // The receive-size gate runs before anything is parsed, so it wins over the recipient check.
    [InlineData("oversize", "MaxReceiveBytes")]
    public async Task Recipient_kid_over_the_cap_with_another_defect_fails_identically_for_holder_and_stranger(
        string defect, string expectedFragment)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Carol, LengthCap.KeyIdCap + 1));
        var packed = WithJweDefect(await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, Bob, Carol), new[] { bob.PublicJwk, carol.PublicJwk }, JoseAlgorithms.A256CbcHs512),
            Crypto), defect);
        var resolver = new SpyDidResolver(LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));

        var outcomes = await HolderAndStrangerOutcomes(packed, bob, resolver);

        outcomes[0].Should().BeOfType<MalformedMessageException>();
        outcomes[0].Message.Should().Contain(expectedFragment);
        foreach (var outcome in outcomes)
        {
            outcome.GetType().Should().Be(outcomes[0].GetType());
            outcome.Message.Should().Be(outcomes[0].Message, "holder and stranger, run twice each, must fail the same way");
            outcome.ShouldNotQuoteTheValue();
        }
        resolver.Requested.Should().BeEmpty();
    }

    // ---- The length unit: UTF-16 code units, counted before any decoding ----

    [Fact]
    public async Task Recipient_kid_of_astral_characters_at_the_cap_is_accepted_although_its_UTF8_form_is_longer()
    {
        // 4096 UTF-16 code units, about 8 KB of UTF-8: a bound that counted bytes would refuse it.
        var carolKid = AstralKid(Carol, LengthCap.KeyIdCap);
        carolKid.Length.Should().Be(LengthCap.KeyIdCap);
        Encoding.UTF8.GetByteCount(carolKid).Should().BeGreaterThan(LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var carol = TestKeyMaterial.Generate(KeyType.X25519, carolKid);
        var packed = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, Bob, Carol), new[] { bob.PublicJwk, carol.PublicJwk }, JoseAlgorithms.A256CbcHs512),
            Crypto);
        var resolver = new SpyDidResolver(LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.RecipientKid.Should().Be(BobKid);
        result.AllRecipientKids.Should().Contain(carolKid, "the astral kid survives the round trip unchanged");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Kid_of_astral_characters_one_unit_over_the_cap_is_refused_although_it_has_far_fewer_code_points(bool recipient)
    {
        // 4097 UTF-16 code units but only about 2060 code points: a bound that counted code points
        // would accept it.
        var kid = AstralKid(recipient ? Carol : Alice, LengthCap.KeyIdCap + 1);
        kid.Length.Should().Be(LengthCap.KeyIdCap + 1);
        kid.EnumerateRunes().Count().Should().BeLessThan(LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var other = TestKeyMaterial.Generate(recipient ? KeyType.X25519 : KeyType.Ed25519, kid);
        var packed = recipient
            ? await EnvelopeWriter.PackEncryptedAsync(
                new PackEncryptedParameters(Ping(null, Bob, Carol), new[] { bob.PublicJwk, other.PublicJwk }, JoseAlgorithms.A256CbcHs512),
                Crypto)
            : await PackSigned(Ping(Alice, Bob), other.PrivateJwk, DpSig.JwsKidPlacement.Auto);
        var resolver = new SpyDidResolver(LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk));

        var failure = await RefusalOf(Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver), packed);

        failure.Message.Should().Contain(recipient ? RecipientField : SignerField);
        failure.Message.Should().NotContain(Astral, "the refusal does not quote the kid");
        resolver.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task Signer_kid_of_astral_characters_at_the_cap_passes_the_bound_and_fails_as_not_a_DID()
    {
        // Raw non-ASCII is not legal in a DID URL, so nothing resolves this kid, and the failure is
        // the ordinary missing-key one: the length bound did not fire on an 8 KB UTF-8 value.
        var kid = AstralKid(Alice, LengthCap.KeyIdCap);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
        var packed = await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, DpSig.JwsKidPlacement.Auto);
        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: signer.PublicJwk));

        var failure = (await Receiver(new SpySecretsResolver(), resolver)
            .Invoking(c => c.UnpackAsync(packed)).Should().ThrowAsync<CryptoException>()).Which;

        failure.Message.Should().NotContain(LengthRefusal);
        resolver.Requested.Should().BeEmpty("DidSubjectOf finds no DID in it, so nothing is resolved");
    }

    [Theory]
    [InlineData(LengthCap.KeyIdCap, true)]
    // About 1360 characters once percent-decoded: the bound counts the value as it is on the wire.
    [InlineData(LengthCap.KeyIdCap + 1, false)]
    public async Task Percent_encoded_signer_kid_is_bounded_by_its_encoded_length(int length, bool accepted)
    {
        var kid = PercentEncodedKid(Alice, length);
        kid.Length.Should().Be(length);
        Uri.UnescapeDataString(kid).Length.Should().BeLessThan(LengthCap.DidCap);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, kid);
        var packed = await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, DpSig.JwsKidPlacement.Auto);
        var resolver = new SpyDidResolver(LengthCap.Doc(Alice, authentication: signer.PublicJwk));
        var receiver = Receiver(new SpySecretsResolver(), resolver);

        if (accepted)
        {
            var result = await receiver.UnpackAsync(packed);
            result.SignerKid.Should().Be(kid);
            result.SignerKeyBinding!.Kid.Should().Be(kid);
        }
        else
        {
            var failure = await RefusalOf(receiver, packed);
            failure.Message.Should().Contain(SignerField);
            failure.Message.Should().NotContain("%41%41%41");
            resolver.Requested.Should().BeEmpty();
        }
    }

    // ---- A custom key service without binding support ----

    [Theory]
    [InlineData("jws-unprotected")]
    [InlineData("jws-protected")]
    [InlineData("recipient")]
    [InlineData("skid")]
    [InlineData("apu")]
    [InlineData("inner-signer")]
    [InlineData("inner-skid")]
    [InlineData("inner-recipient")]
    public async Task Over_long_kid_is_refused_before_a_legacy_key_service_sees_anything(string position)
    {
        // A consumer's own IDidKeyService that is not an IDidKeyBindingService takes the legacy
        // lookups (DidKeyServiceLookups), not the capturing ones. The bound has to hold there too.
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var hostile = await HostileEnvelope(position, bob);
        var legacy = new RecordingLegacyKeyService(new SpyDidResolver(LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk)));
        var receiver = new DidCommClient(new SpySecretsResolver(bob.PrivateJwk), legacy, new DidCommOptions());

        var failure = await RefusalOf(receiver, hostile.Packed);

        failure.Message.Should().Contain(hostile.Field).And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        legacy.Seen.Should().BeEmpty("the refusal comes before any legacy key-service call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kid_at_exactly_the_cap_unpacks_through_a_legacy_key_service(bool authcrypt)
    {
        var kid = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var sender = TestKeyMaterial.Generate(authcrypt ? KeyType.X25519 : KeyType.Ed25519, kid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            authcrypt ? LengthCap.Doc(Alice, keyAgreement: sender.PublicJwk) : LengthCap.Doc(Alice, authentication: sender.PublicJwk));
        var packer = Client(resolver, sender.PrivateJwk);
        var packed = authcrypt
            ? (await packer.PackEncryptedAsync(Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message
            : await packer.PackSignedAsync(Ping(Alice, Bob), Alice);
        var legacy = new RecordingLegacyKeyService(resolver);

        var result = await new DidCommClient(new SpySecretsResolver(bob.PrivateJwk), legacy, new DidCommOptions()).UnpackAsync(packed);

        result.Authenticated.Should().BeTrue();
        (authcrypt ? result.SenderKid : result.SignerKid).Should().Be(kid);
        (authcrypt ? result.SenderKeyBinding : result.SignerKeyBinding).Should().BeNull("the legacy path attaches no provenance");
        legacy.Seen.Should().Contain(kid, "the legacy authorization check really ran with the at-cap kid");
    }

    // ---- The DI-built client, its logs and its spans ----

    [Fact]
    public async Task Client_built_by_AddDidComm_refuses_over_long_kids_and_keeps_them_out_of_logs_and_spans()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var log = new CapturingClientLogger();
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DidCommDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddSingleton<IDidKeyService>(new NetDidKeyService(new SpyDidResolver(LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk))));
        services.AddSingleton<ILogger<DidCommClient>>(log);
        services.AddDidComm(b => b.UseSecretsResolver(new SpySecretsResolver(bob.PrivateJwk)));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DidCommClient>();

        var positions = new[] { "jws-unprotected", "recipient", "skid", "inner-signer" };
        foreach (var position in positions)
        {
            var hostile = await HostileEnvelope(position, bob);
            var failure = await RefusalOf(client, hostile.Packed);
            failure.Message.Should().Contain(hostile.Field);
            failure.ShouldNotQuoteTheValue();
        }

        // Positive: the failure breadcrumb fired for each refusal, so the scan below is not vacuous.
        log.Entries.Count(e => e.Contains("Unpack failed with MalformedMessageException")).Should().Be(positions.Length);
        log.Entries.Should().NotContain(e => e.Contains(LengthCap.Run), "logs must never carry the over-long value");

        var failedUnpacks = spans.Where(s =>
            s.OperationName == DidCommDiagnostics.UnpackActivity
            && s.Status == ActivityStatusCode.Error
            && Equals(s.GetTagItem(DidCommDiagnostics.ErrorTypeTag), nameof(MalformedMessageException))).ToArray();
        failedUnpacks.Length.Should().BeGreaterThanOrEqualTo(positions.Length);
        foreach (var span in spans)
        {
            span.DisplayName.Should().NotContain(LengthCap.Run);
            (span.StatusDescription ?? string.Empty).Should().NotContain(LengthCap.Run);
            foreach (var (key, value) in span.TagObjects)
                (value?.ToString() ?? string.Empty).Should().NotContain(LengthCap.Run, $"span tag '{key}' must not carry the value");
        }
    }

    // ---- Concurrency on one shared client ----

    [Fact]
    public async Task Concurrent_hostile_unpacks_are_refused_while_benign_unpacks_on_the_same_client_are_in_flight()
    {
        // L-044: the interleaving is forced, not hoped for. Four benign unpacks are parked inside DID
        // resolution, all at once; the hostile ones run to completion while they are parked; then
        // the benign ones are released. Every result is checked on its own.
        var atCapAuth = LengthCap.KidOfLength(Alice, LengthCap.KeyIdCap);
        var atCapKa = Alice + "#j" + new string('k', LengthCap.KeyIdCap - Alice.Length - 2);
        atCapKa.Length.Should().Be(LengthCap.KeyIdCap);
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, atCapAuth);
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, atCapKa);
        var docs = new[]
        {
            LengthCap.Doc(Alice, authentication: aliceAuth.PublicJwk, keyAgreement: aliceKa.PublicJwk),
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
        };
        var packer = Client(new SpyDidResolver(docs), aliceAuth.PrivateJwk, aliceKa.PrivateJwk);
        var benign = new List<(string Packed, bool Signed)>();
        for (var i = 0; i < 2; i++)
        {
            benign.Add((await packer.PackSignedAsync(Ping(Alice, Bob), Alice), true));
            benign.Add(((await packer.PackEncryptedAsync(Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message, false));
        }
        var hostile = new List<Hostile>();
        foreach (var position in new[] { "jws-unprotected", "jws-protected", "recipient", "skid", "apu", "inner-skid" })
        {
            for (var i = 0; i < 3; i++)
                hostile.Add(await HostileEnvelope(position, bob));
        }

        var resolver = new ParkingDidResolver(parkCount: benign.Count, docs);
        var receiver = new DidCommClient(new SpySecretsResolver(bob.PrivateJwk), new NetDidKeyService(resolver), new DidCommOptions());

        var benignTasks = benign.Select(b => Task.Run(() => receiver.UnpackAsync(b.Packed))).ToArray();
        await resolver.AllParked.WaitAsync(TimeSpan.FromSeconds(30));

        var hostileOutcomes = await Task.WhenAll(hostile.Select(h =>
            Task.Run(() => Record.ExceptionAsync(() => receiver.UnpackAsync(h.Packed))))).WaitAsync(TimeSpan.FromSeconds(30));

        benignTasks.Should().HaveCount(4);
        benignTasks.Should().OnlyContain(t => !t.IsCompleted, "the hostile unpacks finished while every benign one was still in flight");

        resolver.Release();
        var benignResults = await Task.WhenAll(benignTasks).WaitAsync(TimeSpan.FromSeconds(30));

        hostileOutcomes.Should().HaveCount(hostile.Count);
        for (var i = 0; i < hostile.Count; i++)
        {
            hostileOutcomes[i].Should().BeOfType<MalformedMessageException>($"hostile #{i} ({hostile[i].Position})");
            hostileOutcomes[i]!.Message.Should().Contain(hostile[i].Field);
            hostileOutcomes[i]!.ShouldNotQuoteTheValue();
        }
        for (var i = 0; i < benign.Count; i++)
        {
            benignResults[i].Authenticated.Should().BeTrue($"benign #{i}");
            if (benign[i].Signed)
                benignResults[i].SignerKid.Should().Be(atCapAuth);
            else
                benignResults[i].SenderKid.Should().Be(atCapKa);
            benignResults[i].Message.From.Should().Be(Alice);
        }
        resolver.Requested.Should().NotContain(d => d.Length > LengthCap.DidCap);
    }

    // ---- from and to at the unpack boundary (regression) ----

    [Theory]
    [InlineData("from", LengthCap.DidCap, true)]
    [InlineData("from", LengthCap.DidCap + 1, false)]
    [InlineData("to", LengthCap.DidCap, true)]
    [InlineData("to", LengthCap.DidCap + 1, false)]
    public async Task Plaintext_from_and_to_keep_their_2048_bound_at_unpack(string field, int length, bool accepted)
    {
        var did = LengthCap.DidOfLength(length);
        var message = new JsonObject
        {
            ["id"] = "len-1",
            ["typ"] = MediaTypes.Plaintext,
            ["type"] = "https://example.com/protocols/test/1.0/ping",
            ["from"] = field == "from" ? did : Alice,
            ["to"] = new JsonArray(field == "to" ? did : Bob),
            ["body"] = new JsonObject(),
        }.ToJsonString();
        var resolver = new SpyDidResolver();
        var receiver = Receiver(new SpySecretsResolver(), resolver);

        if (accepted)
        {
            var result = await receiver.UnpackAsync(message);
            (field == "from" ? result.Message.From : result.Message.To![0]).Should().Be(did);
        }
        else
        {
            var failure = await RefusalOf(receiver, message);
            failure.Message.Should().Contain($"'{field}'").And.Contain("maximum length of 2048");
            failure.ShouldNotQuoteTheValue();
            resolver.Requested.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Failures_unrelated_to_length_keep_their_exception_types_and_messages()
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var published = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(Alice, authentication: published.PublicJwk));
        var receiver = Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver);

        // A short 'from' with a fragment: still malformed, and still quoted as before.
        var fragment = new JsonObject
        {
            ["id"] = "len-2",
            ["typ"] = MediaTypes.Plaintext,
            ["type"] = "https://example.com/protocols/test/1.0/ping",
            ["from"] = AliceAuthKid,
            ["body"] = new JsonObject(),
        }.ToJsonString();
        (await RefusalOf(receiver, fragment)).Message.Should().Contain("MUST NOT contain a fragment").And.Contain(AliceAuthKid);

        // A short signer kid Alice's document does not publish: still the crypto failure.
        var unknown = TestKeyMaterial.Generate(KeyType.Ed25519, Alice + "#unpublished");
        var signed = await PackSigned(Ping(Alice, Bob), unknown.PrivateJwk, DpSig.JwsKidPlacement.Auto);
        await receiver.Invoking(c => c.UnpackAsync(signed)).Should().ThrowAsync<CryptoException>();

        // An envelope for a key we do not hold: still the uniform decryption failure.
        var carol = TestKeyMaterial.Generate(KeyType.X25519, Carol + "#ka-1");
        var notForUs = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(Ping(null, Carol), new[] { carol.PublicJwk }, JoseAlgorithms.A256CbcHs512), Crypto);
        (await receiver.Invoking(c => c.UnpackAsync(notForUs)).Should().ThrowAsync<CryptoException>())
            .Which.Message.Should().Be("JWE could not be decrypted.");
    }

    // ---- Helpers ----

    private sealed record Hostile(string Position, string Packed, string Field);

    /// <summary>
    /// An envelope carrying one key id one character over the bound, at <paramref name="position"/>,
    /// addressed to <paramref name="bob"/> where it is encrypted. Only Bob's document is needed to
    /// reach the bound: the over-long kid is refused before anything else would be resolved.
    /// </summary>
    private static async Task<Hostile> HostileEnvelope(string position, TestKeyMaterial bob)
    {
        var length = LengthCap.KeyIdCap + 1;
        switch (position)
        {
            case "jws-unprotected":
            case "jws-protected":
            {
                var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(Alice, length));
                var placement = position == "jws-protected" ? DpSig.JwsKidPlacement.Protected : DpSig.JwsKidPlacement.Auto;
                return new(position, await PackSigned(Ping(Alice, Bob), signer.PrivateJwk, placement), SignerField);
            }
            case "recipient":
            {
                var carol = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Carol, length));
                return new(position, await EnvelopeWriter.PackEncryptedAsync(
                    new PackEncryptedParameters(Ping(null, Bob, Carol), new[] { bob.PublicJwk, carol.PublicJwk }, JoseAlgorithms.A256CbcHs512),
                    Crypto), RecipientField);
            }
            case "skid":
            case "inner-skid":
            {
                var alice = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Alice, length));
                return new(position, await EnvelopeWriter.PackEncryptedAsync(
                    new PackEncryptedParameters(
                        Ping(Alice, Bob), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                        SenderKey: alice.PrivateJwk.ToEcdhKey(Crypto), Skid: alice.PublicJwk.Kid,
                        ProtectSender: position == "inner-skid"),
                    Crypto), SenderField);
            }
            case "apu":
            {
                var alice = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Alice, length));
                return new(position, AuthcryptWithApuOnly(Ping(Alice, Bob), bob.PublicJwk, alice.PrivateJwk, alice.PublicJwk.Kid!), SenderField);
            }
            case "inner-signer":
            {
                var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(Alice, length));
                return new(position, await EnvelopeWriter.PackEncryptedAsync(
                    new PackEncryptedParameters(
                        Ping(Alice, Bob), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                        Signers: new[] { signer.PrivateJwk.ToJwsSigner() }),
                    Crypto), SignerField);
            }
            case "inner-recipient":
            {
                var alice = TestKeyMaterial.Generate(KeyType.X25519, AliceKaKid);
                var carol = TestKeyMaterial.Generate(KeyType.X25519, LengthCap.KidOfLength(Carol, length));
                return new(position, await AnoncryptOfAuthcrypt(bob, alice, carol), RecipientField);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(position), position, null);
        }
    }

    /// <summary>
    /// anoncrypt to Bob alone, around an authcrypt from Alice to Bob and Carol. Unlike
    /// <c>ProtectSender</c>, which gives both layers the same recipients, this lets the inner
    /// recipient list differ from the outer one.
    /// </summary>
    private static async Task<string> AnoncryptOfAuthcrypt(TestKeyMaterial bob, TestKeyMaterial alice, TestKeyMaterial carol)
    {
        var inner = await EnvelopeWriter.PackEncryptedAsync(
            new PackEncryptedParameters(
                Ping(Alice, Bob, Carol), new[] { bob.PublicJwk, carol.PublicJwk }, JoseAlgorithms.A256CbcHs512,
                SenderKey: alice.PrivateJwk.ToEcdhKey(Crypto), Skid: alice.PublicJwk.Kid),
            Crypto);
        return DpEnc.JweBuilder.BuildEcdhEsA256Kw(
            Encoding.UTF8.GetBytes(inner), new[] { bob.PublicJwk }, JoseAlgorithms.A256CbcHs512, Crypto, MediaTypes.Encrypted);
    }

    /// <summary>
    /// A two-signature JWS from Alice. <c>failing-then-long</c>: an impostor key under Alice's
    /// published kid, then an over-long kid. <c>long-then-valid</c> and <c>valid-then-long</c>: the
    /// over-long kid and Alice's real signature in that order. The over-long kid's DID is itself
    /// long, so resolving it would show in the spy.
    /// </summary>
    private static async Task<(string Packed, SpyDidResolver Resolver, string LongKid)> MultiSignature(string order)
    {
        var longKid = LengthCap.DidOfLength(LengthCap.KeyIdCap + 1 - "#key-1".Length) + "#key-1";
        var valid = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var impostor = TestKeyMaterial.Generate(KeyType.Ed25519, AliceAuthKid);
        var overLong = TestKeyMaterial.Generate(KeyType.Ed25519, longKid);
        var signers = order switch
        {
            "failing-then-long" => new[] { impostor, overLong },
            "long-then-valid" => new[] { overLong, valid },
            "valid-then-long" => new[] { valid, overLong },
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, null),
        };
        var packed = await EnvelopeWriter.PackSignedAsync(new PackSignedParameters(
            Ping(Alice, Bob), signers.Select(s => s.PrivateJwk.ToJwsSigner()).ToArray()));
        JsonNode.Parse(packed)!["signatures"]!.AsArray().Should().HaveCount(2);
        return (packed, new SpyDidResolver(LengthCap.Doc(Alice, authentication: valid.PublicJwk)), longKid);
    }

    /// <summary>Run the envelope through a holder of Bob's key and a stranger, twice each.</summary>
    private static async Task<Exception[]> HolderAndStrangerOutcomes(string packed, TestKeyMaterial bob, SpyDidResolver resolver)
    {
        var holder = Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver);
        var stranger = Receiver(new SpySecretsResolver(), resolver);
        var outcomes = new List<Exception>();
        foreach (var receiver in new[] { holder, stranger, holder, stranger })
        {
            var outcome = await Record.ExceptionAsync(() => receiver.UnpackAsync(packed));
            outcome.Should().NotBeNull("every variant must be refused");
            outcomes.Add(outcome!);
        }
        return outcomes.ToArray();
    }

    private static string WithJwsDefect(string packedJws, string defect)
    {
        var jws = JsonNode.Parse(packedJws)!.AsObject();
        var signature = jws["signatures"]![0]!.AsObject();
        var protectedB64u = signature["protected"]!.GetValue<string>();
        switch (defect)
        {
            case "alg-none":
                signature["protected"] = MutateHeader(protectedB64u, h => h["alg"] = "none");
                break;
            case "alg-mismatch":
                signature["protected"] = MutateHeader(protectedB64u, h => h["alg"] = "ES256");
                break;
            case "crit":
                signature["protected"] = MutateHeader(protectedB64u, h => h["crit"] = new JsonArray("exp"));
                break;
            case "bad-signature":
                var sig = signature["signature"]!.GetValue<string>();
                signature["signature"] = (sig[0] == 'A' ? "B" : "A") + sig[1..];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect), defect, null);
        }
        return jws.ToJsonString();
    }

    private static string WithJweDefect(string packedJwe, string defect)
    {
        var jwe = JsonNode.Parse(packedJwe)!.AsObject();
        var protectedB64u = jwe["protected"]!.GetValue<string>();
        switch (defect)
        {
            case "none":
                break;
            case "crit":
                jwe["protected"] = MutateHeader(protectedB64u, h => h["crit"] = new JsonArray("exp"));
                break;
            case "apv":
                jwe["protected"] = MutateHeader(protectedB64u, h => h["apv"] = DpBase64Url.Encode(new byte[32]));
                break;
            case "enc":
                jwe["protected"] = MutateHeader(protectedB64u, h => h["enc"] = JoseAlgorithms.A256Gcm);
                break;
            case "alg":
                jwe["protected"] = MutateHeader(protectedB64u, h => h["alg"] = "ECDH-ES+A128KW");
                break;
            case "apu":
                jwe["protected"] = MutateHeader(protectedB64u, h => h["apu"] = DpBase64Url.Encode(Encoding.UTF8.GetBytes(AliceKaKid)));
                break;
            case "epk-p256":
                var p256 = TestKeyMaterial.Generate(KeyType.P256, "epk").PublicJwk;
                jwe["protected"] = MutateHeader(protectedB64u, h => h["epk"] = new JsonObject
                {
                    ["kty"] = "EC",
                    ["crv"] = JoseAlgorithms.CrvP256,
                    ["x"] = p256.X,
                    ["y"] = p256.Y,
                });
                break;
            case "oversize":
                jwe["pad"] = new string('p', new DidCommOptions().MaxReceiveBytes);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect), defect, null);
        }
        return jwe.ToJsonString();
    }

    private static string MutateHeader(string protectedB64u, Action<JsonObject> mutate)
    {
        var header = JsonNode.Parse(DpBase64Url.DecodeUtf8(protectedB64u))!.AsObject();
        mutate(header);
        return DpBase64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));
    }

    private static string ToFlattened(string generalJws)
    {
        var jws = JsonNode.Parse(generalJws)!.AsObject();
        var signature = jws["signatures"]!.AsArray().Single()!.AsObject();
        var flattened = new JsonObject
        {
            ["payload"] = jws["payload"]!.GetValue<string>(),
            ["protected"] = signature["protected"]!.GetValue<string>(),
            ["signature"] = signature["signature"]!.GetValue<string>(),
        };
        if (signature["header"] is JsonObject header)
            flattened["header"] = header.DeepClone();
        return flattened.ToJsonString();
    }

    /// <summary>A key id of exactly <paramref name="utf16Length"/> code units: the DID, '#', then U+1F600 pairs, padded with 'k'.</summary>
    private static string AstralKid(string did, int utf16Length)
    {
        var kid = new StringBuilder(did).Append('#');
        while (kid.Length + 2 <= utf16Length)
            kid.Append(Astral);
        while (kid.Length < utf16Length)
            kid.Append('k');
        return kid.ToString();
    }

    /// <summary>A key id of exactly <paramref name="length"/> characters whose fragment is mostly <c>%41</c>.</summary>
    private static string PercentEncodedKid(string did, int length)
    {
        var kid = new StringBuilder(did).Append('#');
        while (kid.Length < length && (length - kid.Length) % 3 != 0)
            kid.Append('k');
        while (kid.Length < length)
            kid.Append("%41");
        return kid.ToString();
    }

    /// <summary>
    /// A consumer key service that is not an <see cref="IDidKeyBindingService"/>, so the facade takes
    /// the legacy lookups. Records every DID and kid it is handed.
    /// </summary>
    private sealed class RecordingLegacyKeyService : IDidKeyService
    {
        private readonly NetDidKeyService _inner;
        private readonly ConcurrentQueue<string> _seen = new();

        public RecordingLegacyKeyService(SpyDidResolver resolver) => _inner = new NetDidKeyService(resolver);

        public IReadOnlyCollection<string> Seen => _seen;

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            _seen.Enqueue(did);
            return _inner.GetVerificationMethodsAsync(did, relationship, ct);
        }

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
        {
            _seen.Enqueue(did);
            _seen.Enqueue(kid);
            return _inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);
        }

        public void RejectUnsupportedMethod(string did)
        {
            _seen.Enqueue(did);
            _inner.RejectUnsupportedMethod(did);
        }
    }

    /// <summary>Records each log line with its exception text, the way a real sink would write it.</summary>
    private sealed class CapturingClientLogger : ILogger<DidCommClient>
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyCollection<string> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : Environment.NewLine + exception));
    }

    /// <summary>
    /// Serves fixed documents, but parks the first <c>parkCount</c> resolutions until
    /// <see cref="Release"/>, so a test can hold that many unpacks in flight at once.
    /// </summary>
    private sealed class ParkingDidResolver : IDidResolver
    {
        private readonly SpyDidResolver _inner;
        private readonly int _parkCount;
        private readonly TaskCompletionSource _allParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public ParkingDidResolver(int parkCount, DidDocument[] docs)
        {
            _inner = new SpyDidResolver(docs);
            _parkCount = parkCount;
        }

        public Task AllParked => _allParked.Task;

        public IReadOnlyCollection<string> Requested => _inner.Requested;

        public void Release() => _release.TrySetResult();

        public bool CanResolve(string did) => _inner.CanResolve(did);

        public async Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            var arrival = Interlocked.Increment(ref _arrivals);
            if (arrival <= _parkCount)
            {
                if (arrival == _parkCount)
                    _allParked.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            return await _inner.ResolveAsync(did, options, ct).ConfigureAwait(false);
        }
    }
}
