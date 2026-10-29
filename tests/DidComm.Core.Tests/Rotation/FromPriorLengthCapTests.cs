using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Tests.Facade;
using AwesomeAssertions;
using NetCrypto;
using NetDid.Core.Model;
using Xunit;
using VerificationRelationship = DidComm.Resolution.VerificationRelationship;

namespace DidComm.Tests.Rotation;

/// <summary>
/// Pins the length bounds on the three identifiers a <c>from_prior</c> JWT carries (FR-API-09, #80):
/// the header <c>kid</c> (key-id bound) and the <c>iss</c> and <c>sub</c> claims (DID bound). The
/// validator DID-parses <c>kid</c> and <c>iss</c> before it checks the signature and quotes
/// <c>sub</c> in its errors, and the termination form rides on plain anoncrypt, so all of this is
/// reachable without an authenticated sender. Over-long values are refused as
/// <see cref="ProtocolException"/>, the validator's documented type for a malformed JWT.
/// <para>
/// Each field is covered through <see cref="FromPriorValidator.ValidateAsync(string, string?, IDidKeyService, CancellationToken)"/>
/// with both the binding-capable key service and a legacy one (they take different resolution
/// paths), and through <see cref="DidCommClient.UnpackAsync"/>. Hostile JWTs are properly signed, and
/// where the shape allows it the served document publishes the long kid, so without the bound the
/// JWT would validate.
/// </para>
/// </summary>
public sealed class FromPriorLengthCapTests
{
    private const string PriorDid = "did:example:alice";
    private const string PriorKid = PriorDid + "#auth-1";
    private const string NewDid = "did:example:newalice";
    private const string NewKid = NewDid + "#ka-1";
    private const string Bob = "did:example:bob";
    private const string BobKid = Bob + "#ka-1";
    private const long Iat = 1700000000;

    // ---- kid, validator called directly ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1, true)]
    [InlineData(LengthCap.KeyIdCap + 1, false)]
    [InlineData(500_000, true)]
    [InlineData(500_000, false)]
    public async Task Header_kid_over_the_cap_is_refused_before_any_parse_or_resolution(int length, bool bindingService)
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(PriorDid, length));
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, Iat, signer.PrivateJwk);
        var resolver = new SpyDidResolver(LengthCap.Doc(PriorDid, authentication: signer.PublicJwk));

        var act = () => FromPriorValidator.ValidateAsync(jwt, currentSenderDid: null, KeyService(resolver, bindingService));

        var failure = (await act.Should().ThrowAsync<ProtocolException>()).Which;
        failure.Message.Should().Contain("'kid'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the kid is refused before the prior DID is resolved");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Header_kid_at_exactly_the_cap_validates(bool bindingService)
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(PriorDid, LengthCap.KeyIdCap));
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, Iat, signer.PrivateJwk);
        var resolver = new SpyDidResolver(LengthCap.Doc(PriorDid, authentication: signer.PublicJwk));

        var validated = await FromPriorValidator.ValidateAsync(jwt, currentSenderDid: null, KeyService(resolver, bindingService));

        validated.IsTermination.Should().BeTrue();
        validated.Iss.Should().Be(PriorDid);
    }

    // ---- iss, validator called directly ----

    [Theory]
    [InlineData(LengthCap.DidCap + 1, true)]
    [InlineData(LengthCap.DidCap + 1, false)]
    [InlineData(500_000, true)]
    [InlineData(500_000, false)]
    public async Task Iss_over_the_cap_is_refused_before_any_parse_or_resolution(int length, bool bindingService)
    {
        var (jwt, priorDoc) = await TerminationFromLongIss(length);
        var resolver = new SpyDidResolver(priorDoc);

        var act = () => FromPriorValidator.ValidateAsync(jwt, currentSenderDid: null, KeyService(resolver, bindingService));

        var failure = (await act.Should().ThrowAsync<ProtocolException>()).Which;
        failure.Message.Should().Contain("'iss'").And.Contain("2048");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the iss is refused before the prior DID is resolved");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Iss_at_exactly_the_cap_validates(bool bindingService)
    {
        var iss = LengthCap.DidOfLength(LengthCap.DidCap);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, iss + "#auth-1");
        var jwt = await FromPriorBuilder.BuildTerminationAsync(iss, Iat, signer.PrivateJwk);
        var resolver = new SpyDidResolver(LengthCap.Doc(iss, authentication: signer.PublicJwk));

        var validated = await FromPriorValidator.ValidateAsync(jwt, currentSenderDid: null, KeyService(resolver, bindingService));

        validated.Iss.Should().Be(iss);
    }

    // ---- sub, validator called directly ----

    [Theory]
    // With a 'from' the mismatch error quotes sub; without one, the presence error does.
    [InlineData(LengthCap.DidCap + 1, NewDid, true)]
    [InlineData(LengthCap.DidCap + 1, NewDid, false)]
    [InlineData(LengthCap.DidCap + 1, null, true)]
    [InlineData(LengthCap.DidCap + 1, null, false)]
    [InlineData(500_000, NewDid, true)]
    [InlineData(500_000, NewDid, false)]
    [InlineData(500_000, null, true)]
    [InlineData(500_000, null, false)]
    public async Task Sub_over_the_cap_is_refused_before_it_is_quoted(int length, string? currentSenderDid, bool bindingService)
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(
            new FromPriorClaims(Sub: LengthCap.DidOfLength(length), Iss: PriorDid, Iat: Iat), signer.PrivateJwk);
        var resolver = new SpyDidResolver(LengthCap.Doc(PriorDid, authentication: signer.PublicJwk));

        var act = () => FromPriorValidator.ValidateAsync(jwt, currentSenderDid, KeyService(resolver, bindingService));

        var failure = (await act.Should().ThrowAsync<ProtocolException>()).Which;
        failure.Message.Should().Contain("'sub'").And.Contain("2048");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().BeEmpty("the sub is refused before the prior DID is resolved");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sub_at_exactly_the_cap_validates(bool bindingService)
    {
        var sub = LengthCap.DidOfLength(LengthCap.DidCap);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(new FromPriorClaims(Sub: sub, Iss: PriorDid, Iat: Iat), signer.PrivateJwk);
        var resolver = new SpyDidResolver(LengthCap.Doc(PriorDid, authentication: signer.PublicJwk));

        var validated = await FromPriorValidator.ValidateAsync(jwt, sub, KeyService(resolver, bindingService));

        validated.Sub.Should().Be(sub);
    }

    // ---- Through UnpackAsync ----

    [Theory]
    [InlineData(LengthCap.KeyIdCap + 1)]
    [InlineData(500_000)]
    public async Task Termination_with_an_over_long_kid_over_anoncrypt_is_refused_at_unpack(int length)
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(PriorDid, length));
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, Iat, signer.PrivateJwk);
        var (packed, bob, resolver) = await TerminationOverAnoncrypt(jwt, LengthCap.Doc(PriorDid, authentication: signer.PublicJwk));
        var secrets = new SpySecretsResolver(bob.PrivateJwk);

        var failure = await RefusalAtUnpack(Receiver(secrets, resolver), packed);

        failure.Message.Should().Contain("'kid'").And.Contain("4096");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().NotContain(PriorDid, "the validator refuses the kid before it resolves the prior DID");
        secrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    [Theory]
    [InlineData(LengthCap.DidCap + 1)]
    [InlineData(500_000)]
    public async Task Termination_with_an_over_long_iss_over_anoncrypt_is_refused_at_unpack(int length)
    {
        // The termination form carries no 'from', so this path needs no sender authentication at all.
        var (jwt, priorDoc) = await TerminationFromLongIss(length);
        var (packed, bob, resolver) = await TerminationOverAnoncrypt(jwt, priorDoc);
        var secrets = new SpySecretsResolver(bob.PrivateJwk);

        var failure = await RefusalAtUnpack(Receiver(secrets, resolver), packed);

        failure.Message.Should().Contain("'iss'").And.Contain("2048");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().NotContain(d => d.Length > LengthCap.DidCap,
            "the over-long iss must never be resolved");
        resolver.Requested.Should().NotContain(PriorDid);
        secrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    [Theory]
    [InlineData(LengthCap.DidCap + 1)]
    [InlineData(500_000)]
    public async Task Rotation_with_an_over_long_sub_over_authcrypt_is_refused_at_unpack(int length)
    {
        // A rotation (sub present) needs an authenticated 'from', so this one rides on authcrypt.
        var prior = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(
            new FromPriorClaims(Sub: LengthCap.DidOfLength(length), Iss: PriorDid, Iat: Iat), prior.PrivateJwk);
        var (packed, bob, resolver) = await RotationOverAuthcrypt(jwt, NewDid, NewKid, prior);
        var secrets = new SpySecretsResolver(bob.PrivateJwk);

        var failure = await RefusalAtUnpack(Receiver(secrets, resolver), packed);

        failure.Message.Should().Contain("'sub'").And.Contain("2048");
        failure.ShouldNotQuoteTheValue();
        resolver.Requested.Should().NotContain(PriorDid, "the validator refuses the sub before it resolves the prior DID");
        secrets.Requested.Should().NotContain(k => k.Length > LengthCap.KeyIdCap);
    }

    [Fact]
    public async Task Termination_with_a_kid_at_exactly_the_cap_unpacks()
    {
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, LengthCap.KidOfLength(PriorDid, LengthCap.KeyIdCap));
        var jwt = await FromPriorBuilder.BuildTerminationAsync(PriorDid, Iat, signer.PrivateJwk);
        var (packed, bob, resolver) = await TerminationOverAnoncrypt(jwt, LengthCap.Doc(PriorDid, authentication: signer.PublicJwk));

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.FromPrior!.IsTermination.Should().BeTrue();
        result.FromPrior.Iss.Should().Be(PriorDid);
    }

    [Fact]
    public async Task Termination_with_an_iss_at_exactly_the_cap_unpacks()
    {
        var iss = LengthCap.DidOfLength(LengthCap.DidCap);
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, iss + "#auth-1");
        var jwt = await FromPriorBuilder.BuildTerminationAsync(iss, Iat, signer.PrivateJwk);
        var (packed, bob, resolver) = await TerminationOverAnoncrypt(jwt, LengthCap.Doc(iss, authentication: signer.PublicJwk));

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.FromPrior!.Iss.Should().Be(iss);
    }

    [Fact]
    public async Task Rotation_to_a_sub_at_exactly_the_cap_unpacks()
    {
        // The new DID is at the DID bound and authenticates with its own kid, one fragment longer.
        var newDid = LengthCap.DidOfLength(LengthCap.DidCap);
        var prior = TestKeyMaterial.Generate(KeyType.Ed25519, PriorKid);
        var jwt = await FromPriorBuilder.BuildAsync(new FromPriorClaims(Sub: newDid, Iss: PriorDid, Iat: Iat), prior.PrivateJwk);
        var (packed, bob, resolver) = await RotationOverAuthcrypt(jwt, newDid, newDid + "#ka-1", prior);

        var result = await Receiver(new SpySecretsResolver(bob.PrivateJwk), resolver).UnpackAsync(packed);

        result.FromPrior!.Sub.Should().Be(newDid);
        result.Message.From.Should().Be(newDid);
    }

    // ---- Helpers ----

    /// <summary>
    /// A properly signed termination JWT whose <c>iss</c> is <paramref name="length"/> characters. When
    /// a kid under that iss still fits the key-id bound, the kid sits under it and the long DID's
    /// document publishes it, so without the iss bound the JWT would resolve that DID and validate.
    /// At the near-maximum size such a kid would trip the kid bound first, so the kid stays short
    /// there and the test still isolates the iss check.
    /// </summary>
    private static async Task<(string Jwt, DidDocument PriorDoc)> TerminationFromLongIss(int length)
    {
        var iss = LengthCap.DidOfLength(length);
        var kidUnderIss = iss + "#auth-1";
        var fits = kidUnderIss.Length <= LengthCap.KeyIdCap;
        var signer = TestKeyMaterial.Generate(KeyType.Ed25519, fits ? kidUnderIss : PriorKid);
        var jwt = await FromPriorBuilder.BuildTerminationAsync(iss, Iat, signer.PrivateJwk);
        return (jwt, LengthCap.Doc(fits ? iss : PriorDid, authentication: signer.PublicJwk));
    }

    /// <summary>A termination message (no 'from') carrying <paramref name="jwt"/>, anoncrypted to Bob.</summary>
    private static async Task<(string Packed, TestKeyMaterial Bob, SpyDidResolver Resolver)> TerminationOverAnoncrypt(
        string jwt, params DidDocument[] priorDocs)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var resolver = new SpyDidResolver([LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk), .. priorDocs]);
        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/goodbye")
            .WithTo(Bob)
            .WithFromPrior(jwt)
            .WithBody(new JsonObject())
            .Build();
        var packed = (await Client(resolver).PackEncryptedAsync(
            message, new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;
        return (packed.ShouldFitTheReceiveLimit(), bob, resolver);
    }

    /// <summary>A rotation message from <paramref name="newDid"/> carrying <paramref name="jwt"/>, authcrypted to Bob.</summary>
    private static async Task<(string Packed, TestKeyMaterial Bob, SpyDidResolver Resolver)> RotationOverAuthcrypt(
        string jwt, string newDid, string newKid, TestKeyMaterial prior)
    {
        var bob = TestKeyMaterial.Generate(KeyType.X25519, BobKid);
        var sender = TestKeyMaterial.Generate(KeyType.X25519, newKid);
        var resolver = new SpyDidResolver(
            LengthCap.Doc(Bob, keyAgreement: bob.PublicJwk),
            LengthCap.Doc(newDid, keyAgreement: sender.PublicJwk),
            LengthCap.Doc(PriorDid, authentication: prior.PublicJwk));
        var message = new MessageBuilder()
            .WithType("https://example.com/protocols/test/1.0/hello")
            .WithFrom(newDid)
            .WithTo(Bob)
            .WithFromPrior(jwt)
            .WithBody(new JsonObject())
            .Build();
        var packed = (await Client(resolver, sender.PrivateJwk).PackEncryptedAsync(
            message, new PackEncryptedOptions(Recipients: new[] { Bob }, From: newDid))).Message;
        return (packed.ShouldFitTheReceiveLimit(), bob, resolver);
    }

    private static DidCommClient Receiver(SpySecretsResolver secrets, SpyDidResolver resolver)
        => new(secrets, new NetDidKeyService(resolver), new DidCommOptions());

    private static DidCommClient Client(SpyDidResolver resolver, params Jwk[] privateJwks)
        => Receiver(new SpySecretsResolver(privateJwks), resolver);

    private static async Task<ProtocolException> RefusalAtUnpack(DidCommClient receiver, string packed)
        => (await receiver.Invoking(c => c.UnpackAsync(packed)).Should().ThrowAsync<ProtocolException>()).Which;

    private static IDidKeyService KeyService(SpyDidResolver resolver, bool bindingService)
        => bindingService
            ? new NetDidKeyService(resolver)
            : new LegacyKeyService(new NetDidKeyService(resolver));

    /// <summary>Hides the binding capability, so the validator takes the legacy two-resolution path.</summary>
    private sealed class LegacyKeyService : IDidKeyService
    {
        private readonly NetDidKeyService _inner;
        public LegacyKeyService(NetDidKeyService inner) => _inner = inner;

        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => _inner.GetVerificationMethodsAsync(did, relationship, ct);

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => _inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => _inner.RejectUnsupportedMethod(did);
    }
}
