using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Transports;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NetCrypto;
using Xunit;
using DpEnc = DataProofsDotnet.Jose.Encryption;

namespace DidComm.Tests.Facade;

/// <summary>
/// FR-ENC-19 / DD-07 at the public pack boundary: by default an envelope is encrypted to every
/// keyAgreement key of each recipient on the negotiated curve, in document order, so the recipient
/// can decrypt on any device that holds any one of those keys. A DID URL in
/// <see cref="PackEncryptedOptions.Recipients"/> pins exactly that key. Each test packs through
/// <see cref="DidCommClient.PackEncryptedAsync"/> and unpacks with a receiver that holds only some
/// of the keys, so it proves the facade's own selection, not the envelope writer's.
/// </summary>
public sealed class AllRecipientKeysTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string Carol = "did:example:carol";

    // ---- T2: every key of the recipient, any one device decrypts ----

    [Fact]
    public async Task Anoncrypt_addresses_every_keyAgreement_key_and_a_device_holding_only_the_third_decrypts()
    {
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2", "x3");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));

        var packed = (await Sender(resolver).PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x1"), Kid(Bob, "x2"), Kid(Bob, "x3"));
        var result = await Receiver(resolver, bob[2]).UnpackAsync(packed);
        result.RecipientKid.Should().Be(Kid(Bob, "x3"));
        result.RecipientKeyBinding.Should().NotBeNull();
        result.RecipientKeyBinding!.Kid.Should().Be(Kid(Bob, "x3"));
        result.AllRecipientKids.Should().HaveCount(3);
    }

    // ---- T3: mixed curves ----

    [Fact]
    public async Task Mixed_curve_recipient_gets_its_X25519_key_only_and_the_skip_is_logged_as_counts()
    {
        var p256 = Keys(KeyType.P256, Bob, "p1", "p2");
        var x = Keys(KeyType.X25519, Bob, "x1");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(p256).Concat(Publics(x)).ToArray()));
        var log = new CapturingLogger();

        var packed = (await SenderWithLog(resolver, log).PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x1"));
        var skipped = log.Entries.Should().ContainSingle(e => e.EventId == SkipEventId).Which;
        skipped.Level.Should().Be(LogLevel.Debug);
        skipped.Message.Should().Contain("2 keyAgreement keys on other curves");
        skipped.Message.Should().NotContain("did:", "the skip log carries counts only, never DIDs or kids (NFR-04)");
    }

    [Fact]
    public async Task Curve_common_to_all_recipients_wins_and_takes_every_key_on_it()
    {
        var bobX = Keys(KeyType.X25519, Bob, "x1", "x2", "x3");
        var bobP = Keys(KeyType.P256, Bob, "p1", "p2");
        var carol = Keys(KeyType.P256, Carol, "p1");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bobX).Concat(Publics(bobP)).ToArray()),
            LengthCap.DocWithKeys(Carol, Array.Empty<Jwk>(), Publics(carol)));

        var packed = (await Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob, Carol), new PackEncryptedOptions(Recipients: new[] { Bob, Carol }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "p1"), Kid(Bob, "p2"), Kid(Carol, "p1"));
        (await Receiver(resolver, bobP[1]).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Bob, "p2"));
        (await Receiver(resolver, carol[0]).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Carol, "p1"));
    }

    [Fact]
    public async Task No_skip_log_when_every_key_is_used()
    {
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));
        var log = new CapturingLogger();

        await SenderWithLog(resolver, log).PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }));

        log.Entries.Should().NotContain(e => e.EventId == SkipEventId);
    }

    // ---- T4: the authcrypt sender constrains the curve ----

    [Fact]
    public async Task Authcrypt_sender_holding_only_P256_gets_all_of_the_recipients_P256_keys()
    {
        var aliceX = TestKeyMaterial.Generate(KeyType.X25519, Kid(Alice, "x1"));
        var aliceP = TestKeyMaterial.Generate(KeyType.P256, Kid(Alice, "p1"));
        var bobX = Keys(KeyType.X25519, Bob, "x1", "x2");
        var bobP = Keys(KeyType.P256, Bob, "p1", "p2");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, Array.Empty<Jwk>(), new[] { aliceX.PublicJwk, aliceP.PublicJwk }),
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bobX).Concat(Publics(bobP)).ToArray()));

        // Alice publishes an X25519 key but holds only the P-256 one.
        var packed = (await Sender(resolver, aliceP.PrivateJwk).PackEncryptedAsync(
            Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "p1"), Kid(Bob, "p2"));
        DpEnc.JweParser.PeekRecipients(packed).Skid.Should().Be(Kid(Alice, "p1"));
        var result = await Receiver(resolver, bobP[1]).UnpackAsync(packed);
        result.Authenticated.Should().BeTrue();
        result.RecipientKid.Should().Be(Kid(Bob, "p2"));
    }

    [Fact]
    public async Task Authcrypt_sender_with_no_curve_in_common_is_refused()
    {
        var aliceP384 = TestKeyMaterial.Generate(KeyType.P384, Kid(Alice, "p384"));
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, Array.Empty<Jwk>(), new[] { aliceP384.PublicJwk }),
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));

        var act = () => Sender(resolver, aliceP384.PrivateJwk).PackEncryptedAsync(
            Ping(Alice, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("No common keyAgreement curve");
    }

    // ---- T5: SignFrom and ProtectSender inherit the full list ----

    [Fact]
    public async Task Every_layer_of_a_signed_protect_sender_envelope_carries_every_key()
    {
        var aliceKa = TestKeyMaterial.Generate(KeyType.X25519, Kid(Alice, "ka"));
        var aliceAuth = TestKeyMaterial.Generate(KeyType.Ed25519, Kid(Alice, "auth"));
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2", "x3");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, new[] { aliceAuth.PublicJwk }, new[] { aliceKa.PublicJwk }),
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));

        var packed = (await Sender(resolver, aliceKa.PrivateJwk, aliceAuth.PrivateJwk).PackEncryptedAsync(
            Ping(Alice, Bob),
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice, SignFrom: Alice, ProtectSender: true))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x1"), Kid(Bob, "x2"), Kid(Bob, "x3"));
        // Each device holds one key. Unpacking runs through the outer anoncrypt and the inner
        // authcrypt, so each success proves both layers name that device's key.
        foreach (var device in bob)
        {
            var result = await Receiver(resolver, device).UnpackAsync(packed);
            result.RecipientKid.Should().Be(device.PublicJwk.Kid);
            result.Authenticated.Should().BeTrue();
            result.NonRepudiation.Should().BeTrue();
            result.AnonymousSender.Should().BeTrue();
            result.AllRecipientKids.Should().HaveCount(3);
        }
    }

    // ---- T7: #80, an over-limit first kid no longer hides the usable one ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Over_limit_first_kid_is_packed_alongside_the_usable_kid_and_the_recipient_decrypts(bool authcrypt)
    {
        var longKid = LengthCap.KidOfLength(Bob, LengthCap.KeyIdCap + 1);
        var bobKid = Kid(Bob, "ka-1");
        var longKey = TestKeyMaterial.Generate(KeyType.X25519, longKid);
        var bobKey = TestKeyMaterial.Generate(KeyType.X25519, bobKid);
        var alice = TestKeyMaterial.Generate(KeyType.X25519, Kid(Alice, "ka"));
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Alice, Array.Empty<Jwk>(), new[] { alice.PublicJwk }),
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), new[] { longKey.PublicJwk, bobKey.PublicJwk }));

        var packed = (await Sender(resolver, alice.PrivateJwk).PackEncryptedAsync(
            Ping(authcrypt ? Alice : null, Bob),
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: authcrypt ? Alice : null))).Message;

        // Compared by position and length so a failure message does not print the 4097-character kid.
        var kids = RecipientKids(packed);
        kids.Should().HaveCount(2);
        (kids[0] == longKid).Should().BeTrue("the over-limit kid is first in the document, so it is first in the envelope");
        kids[1].Should().Be(bobKid);

        // Bob holds both keys: the over-limit entry is passed over, the usable one decrypts.
        var bobSecrets = new SpySecretsResolver(longKey.PrivateJwk, bobKey.PrivateJwk);
        var result = await new DidCommClient(bobSecrets, new NetDidKeyService(resolver), new DidCommOptions()).UnpackAsync(packed);
        result.RecipientKid.Should().Be(bobKid);
        result.Authenticated.Should().Be(authcrypt);
        bobSecrets.Requested.Should().NotContain(longKid, "an over-long recipient kid is never offered to the keystore");

        // A device holding only the long key fails exactly like a stranger.
        var onlyLong = await FailureOf(Receiver(resolver, longKey), packed);
        var stranger = await FailureOf(Receiver(resolver, TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, "other"))), packed);
        onlyLong.Should().BeOfType<CryptoException>();
        stranger.Should().BeOfType<CryptoException>();
        onlyLong.Message.Should().Be(stranger.Message);
    }

    // ---- T8: DID URL pins ----

    [Fact]
    public async Task A_DID_URL_pins_exactly_that_key()
    {
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2", "x3");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));

        var packed = (await Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "x2") }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x2"));
        var result = await Receiver(resolver, bob[1]).UnpackAsync(packed);
        result.RecipientKid.Should().Be(Kid(Bob, "x2"));
        result.Message.To.Should().Equal(new[] { Bob }, "the message 'to' is the caller's, untouched by a pin");
        resolver.Requested.Should().NotContain(Kid(Bob, "x2"), "only the bare DID is resolved");
        (await FailureOf(Receiver(resolver, bob[0]), packed)).Should().BeOfType<CryptoException>(
            "a device holding only an unpinned key is not addressed");
    }

    [Fact]
    public async Task A_P256_pin_forces_P256_for_every_recipient()
    {
        var bobX = Keys(KeyType.X25519, Bob, "x1");
        var bobP = Keys(KeyType.P256, Bob, "p1", "p2");
        var carolX = Keys(KeyType.X25519, Carol, "x1");
        var carolP = Keys(KeyType.P256, Carol, "p1");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bobX).Concat(Publics(bobP)).ToArray()),
            LengthCap.DocWithKeys(Carol, Array.Empty<Jwk>(), Publics(carolX).Concat(Publics(carolP)).ToArray()));

        var packed = (await Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob, Carol), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "p2"), Carol }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "p2"), Kid(Carol, "p1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bare_DID_and_a_pin_for_the_same_DID_give_every_key_on_the_pins_curve(bool pinFirst)
    {
        var bobX = Keys(KeyType.X25519, Bob, "x1", "x2");
        var bobP = Keys(KeyType.P256, Bob, "p1", "p2");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bobX).Concat(Publics(bobP)).ToArray()));
        var recipients = pinFirst ? new[] { Kid(Bob, "p2"), Bob } : new[] { Bob, Kid(Bob, "p2") };

        var packed = (await Sender(resolver).PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: recipients))).Message;

        RecipientKids(packed).Should().Equal(pinFirst
            ? new[] { Kid(Bob, "p2"), Kid(Bob, "p1") }
            : new[] { Kid(Bob, "p1"), Kid(Bob, "p2") });
        resolver.Requested.Count(d => d == Bob).Should().BeLessThanOrEqualTo(1, "one DID is resolved once per pack");
    }

    [Fact]
    public async Task A_pin_naming_a_key_the_DID_does_not_publish_is_refused()
    {
        var bob = Keys(KeyType.X25519, Bob, "x1");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));

        var act = () => Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "missing") }));

        var failure = (await act.Should().ThrowAsync<DidResolutionException>()).Which;
        failure.Did.Should().Be(Bob);
        failure.Message.Should().Contain(Kid(Bob, "missing"));
    }

    [Fact]
    public async Task A_pin_naming_another_relationships_key_is_refused()
    {
        // The DID's authentication key is not a keyAgreement key, so it cannot be pinned.
        var auth = TestKeyMaterial.Generate(KeyType.Ed25519, Kid(Bob, "auth"));
        var bob = Keys(KeyType.X25519, Bob, "x1");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, new[] { auth.PublicJwk }, Publics(bob)));

        var act = () => Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "auth") }));

        await act.Should().ThrowAsync<DidResolutionException>();
    }

    [Theory]
    [InlineData("did:example:bob/path#x1")]
    [InlineData("did:example:bob?service=inbox")]
    [InlineData("did:example:bob?versionId=1#x1")]
    [InlineData("did:example:bob;param=1")]
    [InlineData("did:example:bob#")]
    [InlineData("#x1")]
    public async Task An_entry_with_a_path_query_params_or_no_fragment_text_is_refused_before_resolution(string entry)
    {
        var bob = Keys(KeyType.X25519, Bob, "x1");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));

        var act = () => Sender(resolver).PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { entry }));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("options");
        resolver.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pin_is_method_checked_on_its_bare_DID()
    {
        // The method check sees the DID part, so a did:web pin is refused as did:web (DD-08), not as
        // "not a syntactically valid DID".
        var resolver = new SpyDidResolver();

        var act = () => Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { "did:web:example.com#key-1" }));

        await act.Should().ThrowAsync<UnsupportedDidMethodException>();
        resolver.Requested.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_over_long_entry_is_refused_without_quoting_it(bool didUrl)
    {
        var entry = didUrl
            ? LengthCap.KidOfLength(Bob, LengthCap.KeyIdCap + 1)
            : LengthCap.DidOfLength(LengthCap.KeyIdCap + 1);
        var resolver = new SpyDidResolver();

        var act = () => Sender(resolver).PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { entry }));

        var failure = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        failure.ShouldNotQuoteTheValue();
        failure.Message.Should().Contain("4096");
        resolver.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pin_with_no_curve_in_common_with_the_other_recipients_is_refused()
    {
        var bobP = Keys(KeyType.P256, Bob, "p1");
        var bobX = Keys(KeyType.X25519, Bob, "x1");
        var carol = Keys(KeyType.X25519, Carol, "x1");
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bobX).Concat(Publics(bobP)).ToArray()),
            LengthCap.DocWithKeys(Carol, Array.Empty<Jwk>(), Publics(carol)));

        var act = () => Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob, Carol), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "p1"), Carol }));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("No common keyAgreement curve");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Forward_accepts_entries_that_all_name_one_DID(bool withBareEntry)
    {
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2", "x3");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));
        var services = new StubServiceResolver((Bob, new[] { new DidCommServiceInfo("https://bob.example/inbox", Array.Empty<string>(), Array.Empty<string>()) }));
        var client = new DidCommClient(new SpySecretsResolver(), new NetDidKeyService(resolver), services, new DidCommOptions());
        var recipients = withBareEntry ? new[] { Kid(Bob, "x2"), Bob } : new[] { Kid(Bob, "x2") };

        var result = await client.PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: recipients, Forward: true));

        result.ServiceEndpoint.Should().Be("https://bob.example/inbox");
        services.Resolved.Should().Equal(Bob);
        RecipientKids(result.Message).Should().Equal(withBareEntry
            ? new[] { Kid(Bob, "x2"), Kid(Bob, "x1"), Kid(Bob, "x3") }
            : new[] { Kid(Bob, "x2") });
    }

    [Fact]
    public async Task Forward_refuses_entries_naming_two_DIDs_even_when_one_is_pinned()
    {
        var client = new DidCommClient(new SpySecretsResolver(), new NetDidKeyService(new SpyDidResolver()), new StubServiceResolver(), new DidCommOptions());

        var act = () => client.PackEncryptedAsync(
            Ping(null, Bob, Carol), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "x1"), Carol }, Forward: true));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("single-recipient");
    }

    [Fact]
    public async Task SendAsync_passes_a_pin_through_to_the_pack()
    {
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));
        var router = new CapturingRouter();
        var client = new DidCommClient(new SpySecretsResolver(), new NetDidKeyService(resolver), new StubServiceResolver(), router, new DidCommOptions());

        await client.SendAsync(Ping(null, Bob), new SendOptions(Recipients: new[] { Kid(Bob, "x2") })
        {
            ServiceEndpointOverride = new Uri("https://bob.example/inbox"),
        });

        var sent = router.Requests.Should().ContainSingle().Which;
        RecipientKids(Encoding.UTF8.GetString(sent.Payload.Span)).Should().Equal(Kid(Bob, "x2"));
    }

    // ---- T9: dedupe and key-id hygiene ----

    [Fact]
    public async Task The_same_DID_twice_addresses_each_key_once()
    {
        var bob = Keys(KeyType.X25519, Bob, "x1", "x2", "x3");
        var resolver = new SpyDidResolver(LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), Publics(bob)));

        var packed = (await Sender(resolver).PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob, Bob }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x1"), Kid(Bob, "x2"), Kid(Bob, "x3"));
        (await Receiver(resolver, bob[2]).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Bob, "x3"));
    }

    [Fact]
    public async Task A_document_listing_the_same_key_twice_addresses_it_once()
    {
        var x1 = TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, "x1"));
        var keys = new StubKeyService((Bob, new[] { x1.PublicJwk, x1.PublicJwk }));

        var packed = (await new DidCommClient(new SpySecretsResolver(), keys, new DidCommOptions())
            .PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x1"));
    }

    [Fact]
    public async Task A_key_listed_by_two_recipient_DIDs_is_addressed_once()
    {
        var shared = TestKeyMaterial.Generate(KeyType.X25519, Kid(Carol, "shared"));
        var bobOwn = TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, "x1"));
        var keys = new StubKeyService(
            (Bob, new[] { bobOwn.PublicJwk, shared.PublicJwk }),
            (Carol, new[] { shared.PublicJwk }));

        var packed = (await new DidCommClient(new SpySecretsResolver(), keys, new DidCommOptions())
            .PackEncryptedAsync(Ping(null, Bob, Carol), new PackEncryptedOptions(Recipients: new[] { Bob, Carol }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x1"), Kid(Carol, "shared"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_key_without_a_kid_is_skipped(string? missingKid)
    {
        var kidless = TestKeyMaterial.Generate(KeyType.X25519, "unused").PublicJwk;
        kidless.Kid = missingKid;
        var x1 = TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, "x1"));
        var keys = new StubKeyService((Bob, new[] { kidless, x1.PublicJwk }));

        var packed = (await new DidCommClient(new SpySecretsResolver(), keys, new DidCommOptions())
            .PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x1"));
    }

    [Fact]
    public async Task A_DID_whose_keys_all_lack_a_kid_is_a_resolution_failure()
    {
        var kidless = TestKeyMaterial.Generate(KeyType.X25519, "unused").PublicJwk;
        kidless.Kid = null;
        var keys = new StubKeyService((Bob, new[] { kidless }));

        var act = () => new DidCommClient(new SpySecretsResolver(), keys, new DidCommOptions())
            .PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }));

        (await act.Should().ThrowAsync<DidResolutionException>()).Which.Did.Should().Be(Bob);
    }

    // ---- T9b: a document cannot address keys under another DID's kid ----

    [Fact]
    public async Task A_co_recipient_publishing_another_recipients_kid_cannot_lock_that_recipient_out()
    {
        // Bob's document lists Alice's kid alice#a1 with key material of his own. The envelope holds
        // each kid once, first wins, so if Bob's entry were taken, Alice's real a1 would be dropped
        // and her a1 device could not decrypt.
        var (resolver, alice, bob) = BobPublishesAlicesKid(fakeMaterial: true);

        var packed = (await Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob, Alice), new PackEncryptedOptions(Recipients: new[] { Bob, Alice }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "b1"), Kid(Alice, "a1"), Kid(Alice, "a2"));
        (await Receiver(resolver, alice[0]).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Alice, "a1"));
        (await Receiver(resolver, alice[1]).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Alice, "a2"));
        (await Receiver(resolver, bob).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Bob, "b1"));
    }

    [Fact]
    public async Task A_pin_of_a_co_recipients_kid_is_that_recipients_real_key()
    {
        var (resolver, alice, _) = BobPublishesAlicesKid(fakeMaterial: true);

        var packed = (await Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob, Alice), new PackEncryptedOptions(Recipients: new[] { Bob, Kid(Alice, "a1") }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "b1"), Kid(Alice, "a1"));
        (await Receiver(resolver, alice[0]).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Alice, "a1"));
    }

    [Fact]
    public async Task A_document_listing_another_DIDs_real_key_does_not_encrypt_to_that_DID()
    {
        // Bob's document lists Alice's real a1. A message for Bob alone must not be readable by her.
        var (resolver, alice, bob) = BobPublishesAlicesKid(fakeMaterial: false);

        var packed = (await Sender(resolver).PackEncryptedAsync(
            Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "b1"));
        (await FailureOf(Receiver(resolver, alice[0]), packed)).Should().BeOfType<CryptoException>();
        (await Receiver(resolver, bob).UnpackAsync(packed)).RecipientKid.Should().Be(Kid(Bob, "b1"));
    }

    [Fact]
    public async Task A_DID_whose_keys_all_name_another_DID_is_a_resolution_failure()
    {
        var alien = TestKeyMaterial.Generate(KeyType.X25519, Kid(Alice, "a1"));
        var keys = new StubKeyService((Bob, new[] { alien.PublicJwk }));

        var act = () => new DidCommClient(new SpySecretsResolver(), keys, new DidCommOptions())
            .PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }));

        var thrown = (await act.Should().ThrowAsync<DidResolutionException>()).Which;
        thrown.Did.Should().Be(Bob);
        thrown.Message.Should().Contain("none of its 1 keyAgreement keys has a key id of this DID");
    }

    // Alice with a1 and a2; Bob with b1, whose document also lists alice#a1: with key material of
    // his own (fakeMaterial), or Alice's real a1.
    private static (SpyDidResolver Resolver, TestKeyMaterial[] Alice, TestKeyMaterial Bob) BobPublishesAlicesKid(bool fakeMaterial)
    {
        var alice = Keys(KeyType.X25519, Alice, "a1", "a2");
        var bob = TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, "b1"));
        var listed = fakeMaterial ? TestKeyMaterial.Generate(KeyType.X25519, Kid(Alice, "a1")).PublicJwk : alice[0].PublicJwk;
        var resolver = new SpyDidResolver(
            LengthCap.DocWithKeys(Bob, Array.Empty<Jwk>(), new[] { listed, bob.PublicJwk }),
            LengthCap.DocWithKeys(Alice, Array.Empty<Jwk>(), Publics(alice)));
        return (resolver, alice, bob);
    }

    // ---- T10: the per-DID cap ----

    [Fact]
    public async Task At_most_64_keys_per_recipient_DID_are_addressed_in_document_order_and_the_rest_are_logged()
    {
        var bob = Enumerable.Range(1, 65)
            .Select(i => TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, $"x{i}")))
            .ToArray();
        var keys = new StubKeyService((Bob, Publics(bob)));
        var log = new CapturingLogger();

        var packed = (await new DidCommClient(new SpySecretsResolver(), keys, new DidCommOptions(), log)
            .PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(bob.Take(64).Select(k => k.PublicJwk.Kid));
        var skipped = log.Entries.Should().ContainSingle(e => e.EventId == SkipEventId).Which;
        skipped.Message.Should().Contain("1 over the limit of 64 keys per recipient DID");
        skipped.Message.Should().NotContain("did:");
    }

    [Fact]
    public async Task The_cap_does_not_hide_a_pinned_key()
    {
        var bob = Enumerable.Range(1, 65)
            .Select(i => TestKeyMaterial.Generate(KeyType.X25519, Kid(Bob, $"x{i}")))
            .ToArray();
        var keys = new StubKeyService((Bob, Publics(bob)));

        var packed = (await new DidCommClient(new SpySecretsResolver(), keys, new DidCommOptions())
            .PackEncryptedAsync(Ping(null, Bob), new PackEncryptedOptions(Recipients: new[] { Kid(Bob, "x65") }))).Message;

        RecipientKids(packed).Should().Equal(Kid(Bob, "x65"));
    }

    // ---- helpers ----

    // DidCommClient's skip log (FR-ENC-19 D9).
    private const int SkipEventId = 5;

    private static string Kid(string did, string fragment) => did + "#" + fragment;

    private static TestKeyMaterial[] Keys(KeyType type, string did, params string[] fragments)
        => fragments.Select(f => TestKeyMaterial.Generate(type, Kid(did, f))).ToArray();

    private static Jwk[] Publics(IEnumerable<TestKeyMaterial> keys) => keys.Select(k => k.PublicJwk).ToArray();

    private static DidCommClient Sender(SpyDidResolver resolver, params Jwk[] privates)
        => new(new SpySecretsResolver(privates), new NetDidKeyService(resolver), new DidCommOptions());

    private static DidCommClient SenderWithLog(SpyDidResolver resolver, CapturingLogger log)
        => new(new SpySecretsResolver(), new NetDidKeyService(resolver), new DidCommOptions(), log);

    private static DidCommClient Receiver(SpyDidResolver resolver, params TestKeyMaterial[] held)
        => new(new SpySecretsResolver(held.Select(k => k.PrivateJwk).ToArray()), new NetDidKeyService(resolver), new DidCommOptions());

    private static async Task<Exception> FailureOf(DidCommClient receiver, string packed)
    {
        try
        {
            await receiver.UnpackAsync(packed);
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new Xunit.Sdk.XunitException("expected the unpack to fail");
    }

    private static Message Ping(string? from, params string[] to)
    {
        var builder = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/ping")
            .WithTo(to)
            .WithBody(new JsonObject());
        if (from is not null)
            builder = builder.WithFrom(from);
        return builder.Build();
    }

    private static string[] RecipientKids(string packed)
        => JsonNode.Parse(packed)!["recipients"]!.AsArray()
            .Select(r => r!["header"]!["kid"]!.GetValue<string>())
            .ToArray();

    internal sealed class CapturingLogger : ILogger<DidCommClient>
    {
        public ConcurrentQueue<(LogLevel Level, int EventId, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, eventId.Id, formatter(state, exception)));
    }

    internal sealed class StubKeyService : IDidKeyService
    {
        private readonly Dictionary<string, IReadOnlyList<Jwk>> _map;

        public StubKeyService(params (string Did, Jwk[] Keys)[] entries)
        {
            _map = new Dictionary<string, IReadOnlyList<Jwk>>(StringComparer.Ordinal);
            foreach (var (did, keys) in entries) _map[did] = keys;
        }

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(relationship == VerificationRelationship.KeyAgreement && _map.TryGetValue(did, out var k) ? k : Array.Empty<Jwk>());

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(_map.TryGetValue(did, out var k) && k.Any(j => j.Kid == kid));

        public void RejectUnsupportedMethod(string did) { }
    }

    internal sealed class StubServiceResolver : IServiceEndpointResolver
    {
        private readonly Dictionary<string, IReadOnlyList<DidCommServiceInfo>> _map;
        private readonly ConcurrentQueue<string> _resolved = new();

        public StubServiceResolver(params (string Did, DidCommServiceInfo[] Services)[] entries)
        {
            _map = new Dictionary<string, IReadOnlyList<DidCommServiceInfo>>(StringComparer.Ordinal);
            foreach (var (did, services) in entries) _map[did] = services;
        }

        public IReadOnlyCollection<string> Resolved => _resolved;

        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
        {
            _resolved.Enqueue(did);
            return Task.FromResult(_map.TryGetValue(did, out var s) ? s : Array.Empty<DidCommServiceInfo>());
        }
    }

    private sealed class CapturingRouter : ITransportRouter
    {
        public ConcurrentQueue<TransportRequest> Requests { get; } = new();

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }
}
