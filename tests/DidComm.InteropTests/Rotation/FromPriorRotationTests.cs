using System.Text;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Resolution;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.Rotation;

/// <summary>
/// End-to-end <c>from_prior</c> rotation tests using Appendix A secrets and Appendix B docs.
/// Per L-005: tampering and wrong-sub / wrong-signer rejection cases are exercised in
/// addition to the round-trip so the validator catches bugs the builder can't surface
/// against itself.
/// </summary>
public sealed class FromPriorRotationTests
{
    private const string PriorDid = "did:example:alice";
    private const string PriorSignerKid = "did:example:alice#key-1"; // Ed25519
    private const string NewSenderDid = "did:example:newAlice";

    private static readonly Lazy<SpecActorRegistry> Actors = new(SpecActorRegistry.LoadDefault);
    private static readonly Lazy<FixtureDidResolver> DocResolver = new(() =>
        FixtureDidResolver.LoadFromDirectory(Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec")));

    private static NetDidKeyService NewKeyService() => new(DocResolver.Value);

    private static Jwk SignerPrivateJwk() => Actors.Value.GetPrivate(PriorSignerKid)
        ?? throw new InvalidOperationException($"Appendix A is missing {PriorSignerKid}");

    private static FromPriorClaims SampleClaims() => new(
        Sub: NewSenderDid,
        Iss: PriorDid,
        Iat: 1700000000);

    [Fact]
    public async Task BuilderValidator_RoundTrip_ReturnsClaims()
    {
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());

        var validated = await FromPriorValidator.ValidateAsync(
            jwt, NewSenderDid, NewKeyService());

        validated.Sub.Should().Be(NewSenderDid);
        validated.Iss.Should().Be(PriorDid);
        validated.Iat.Should().Be(1700000000);
    }

    [Fact]
    public async Task Validator_RejectsTamperedSignature()
    {
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var parts = jwt.Split('.');
        // Flip the last byte of the signature.
        var sig = Base64Url.Decode(parts[2]);
        sig[^1] ^= 0x01;
        var tampered = $"{parts[0]}.{parts[1]}.{Base64Url.Encode(sig)}";

        var act = async () => await FromPriorValidator.ValidateAsync(
            tampered, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ConsistencyException>()
            .Where(e => e.Message.Contains("FR-ROT-01"));
    }

    [Fact]
    public async Task Validator_RejectsMismatchedSub_FrRot02()
    {
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());

        var act = async () => await FromPriorValidator.ValidateAsync(
            jwt, currentSenderDid: "did:example:somebody-else", NewKeyService());

        await act.Should().ThrowAsync<ConsistencyException>()
            .Where(e => e.Message.Contains("FR-ROT-02"));
    }

    [Fact]
    public async Task Validator_RejectsSignerNotInPriorAuthentication()
    {
        // Sign with a key not in alice's authentication list. key-x25519-1 is a keyAgreement key.
        var keyAgreementKid = "did:example:alice#key-x25519-1";
        var keyAgreementJwk = Actors.Value.GetPrivate(keyAgreementKid)
            ?? throw new InvalidOperationException($"Appendix A is missing {keyAgreementKid}");

        // Building with this would fail because X25519 has no signing alg. So instead, build with
        // alice#key-1 (valid) but then surgically swap the header kid to a key not authorized for auth.
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var parts = jwt.Split('.');
        var origHeaderJson = Encoding.UTF8.GetString(Base64Url.Decode(parts[0]));
        var rewrittenHeaderJson = origHeaderJson.Replace(PriorSignerKid, keyAgreementKid, StringComparison.Ordinal);
        var rewrittenHeaderB64u = Base64Url.Encode(Encoding.UTF8.GetBytes(rewrittenHeaderJson));
        var rewrittenJwt = $"{rewrittenHeaderB64u}.{parts[1]}.{parts[2]}";

        var act = async () => await FromPriorValidator.ValidateAsync(
            rewrittenJwt, NewSenderDid, NewKeyService());

        // X25519 is in keyAgreement, not authentication → authorization check fails first.
        await act.Should().ThrowAsync<ConsistencyException>()
            .Where(e => e.Message.Contains("authentication"));

        // Silence the unused warning — the variable exists to document the symmetric path.
        _ = keyAgreementJwk;
    }

    [Fact]
    public async Task Validator_RejectsMalformedJwt()
    {
        var act = async () => await FromPriorValidator.ValidateAsync(
            "not.a.jwt.extra-segment", NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>();
    }

    // Issue #19: malformed from_prior JWT bytes (bad base64url, empty segment, or a claim/header of
    // the wrong JSON value-kind) must surface as the typed ProtocolException, NOT escape UnpackAsync
    // as a raw FormatException / InvalidOperationException / ArgumentException. Each case corrupts a
    // segment of an otherwise-valid JWT and fails at the parse stage, before signature verification.

    [Theory]
    [InlineData("!!!not-base64url!!!", 0)] // header segment is not base64url -> FormatException
    [InlineData("@@@", 2)]                 // signature segment is not base64url -> FormatException
    public async Task Validator_RejectsNonBase64UrlSegment_AsProtocolException(string garbage, int segmentIndex)
    {
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var parts = jwt.Split('.');
        parts[segmentIndex] = garbage;
        var malformed = string.Join('.', parts);

        var act = async () => await FromPriorValidator.ValidateAsync(malformed, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public async Task Validator_RejectsEmptySegment_AsProtocolException()
    {
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var parts = jwt.Split('.');
        var malformed = $"{parts[0]}..{parts[2]}"; // empty claims segment -> ArgumentException in Base64Url.Decode

        var act = async () => await FromPriorValidator.ValidateAsync(malformed, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public async Task Validator_RejectsWrongKindAlgHeader_AsProtocolException()
    {
        // "alg" is a number instead of a string -> GetString() throws InvalidOperationException.
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var malformed = MutateHeader(jwt, h => h["alg"] = 123);

        var act = async () => await FromPriorValidator.ValidateAsync(malformed, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public async Task Validator_RejectsWrongKindSubClaim_AsProtocolException()
    {
        // "sub" is an object instead of a string -> GetString() throws InvalidOperationException.
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var malformed = MutateClaims(jwt, c => c["sub"] = new JsonObject());

        var act = async () => await FromPriorValidator.ValidateAsync(malformed, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public async Task Validator_RejectsWrongKindIatClaim_AsProtocolException()
    {
        // "iat" is a string instead of a number -> GetInt64() throws InvalidOperationException.
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var malformed = MutateClaims(jwt, c => c["iat"] = "not-a-number");

        var act = async () => await FromPriorValidator.ValidateAsync(malformed, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>();
    }

    [Fact]
    public async Task Validator_RejectsOutOfRangeIatClaim_AsProtocolException()
    {
        // "iat" is a JSON number outside Int64 range -> GetInt64() throws FormatException.
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var malformed = MutateClaims(jwt, c => c["iat"] = JsonNode.Parse("99999999999999999999999"));

        var act = async () => await FromPriorValidator.ValidateAsync(malformed, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>();
    }

    private static string MutateHeader(string jwt, Action<JsonObject> mutate) => MutateSegment(jwt, 0, mutate);

    private static string MutateClaims(string jwt, Action<JsonObject> mutate) => MutateSegment(jwt, 1, mutate);

    private static string MutateSegment(string jwt, int segmentIndex, Action<JsonObject> mutate)
    {
        var parts = jwt.Split('.');
        var obj = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[segmentIndex])))!.AsObject();
        mutate(obj);
        parts[segmentIndex] = Base64Url.Encode(Encoding.UTF8.GetBytes(obj.ToJsonString()));
        return string.Join('.', parts);
    }

    [Fact]
    public async Task DidCommClient_PopulatesFromPriorOnAuthcryptedUnpack()
    {
        // from_prior must ride on a sender-authenticated envelope (FR-ROT-03 hardening), so the
        // rotation message is authcrypted as the new sender. alice is the only fixture DID that holds
        // BOTH an authentication signing key (to sign the JWT as the prior DID) AND keyAgreement keys
        // (to authcrypt), so it stands in for both ends here — this test exercises the authenticated-
        // unpack + validation path, not a realistic two-party rotation. The authcrypt direction
        // (alice -> bob) is the round-trip-proven one (see DidCommClientRoundTripTests).
        var claims = new FromPriorClaims(Sub: "did:example:alice", Iss: PriorDid, Iat: 1700000000);
        var jwt = await FromPriorBuilder.BuildAsync(claims, SignerPrivateJwk());

        var message = new MessageBuilder()
            .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .WithFromPrior(jwt)
            .WithBody(JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
            .Build();

        var client = new DidCommClient(Actors.Value.AsSecretsResolver(), NewKeyService(), new DidCommOptions());
        var packed = (await client.PackEncryptedAsync(message,
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"))).Message;

        var unpacked = await client.UnpackAsync(packed);

        unpacked.Authenticated.Should().BeTrue();
        unpacked.FromPrior.Should().NotBeNull();
        unpacked.FromPrior!.Sub.Should().Be("did:example:alice");
        unpacked.FromPrior.Iss.Should().Be(PriorDid);
        unpacked.Message.FromPrior.Should().Be(jwt);
    }

    [Fact]
    public async Task DidCommClient_RejectsFromPriorOnAnoncrypt()
    {
        // Anoncrypt does not authenticate the sender, so `from` (= sub) is attacker-settable. A
        // rotation assertion on such an envelope must be rejected (FR-ROT-03 hardening) — otherwise a
        // captured rotation JWT could be replayed under a spoofed sender.
        var claims = new FromPriorClaims(Sub: "did:example:alice", Iss: PriorDid, Iat: 1700000000);
        var jwt = await FromPriorBuilder.BuildAsync(claims, SignerPrivateJwk());

        var message = new MessageBuilder()
            .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .WithFromPrior(jwt)
            .WithBody(JsonNode.Parse("""{"a":"b"}""")!.AsObject())
            .Build();

        var client = new DidCommClient(Actors.Value.AsSecretsResolver(), NewKeyService(), new DidCommOptions());
        // No From → anoncrypt → not sender-authenticated.
        var packed = (await client.PackEncryptedAsync(message,
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }))).Message;

        var act = async () => await client.UnpackAsync(packed);

        await act.Should().ThrowAsync<ConsistencyException>()
            .Where(e => e.Message.Contains("not authenticated"));
    }
}
