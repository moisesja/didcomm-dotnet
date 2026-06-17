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
    [InlineData("SGVsbG8=", 1)]            // #24: '=' padded segment -> strict base64url FormatException
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

    // ---- Issue #25: the builder must emit exp/nbf so the FR-ROT-05 freshness control is reachable on
    //      the issuing side; the no-expiry payload stays byte-identical. ----

    [Fact]
    public async Task Builder_emits_exp_and_nbf_in_lexicographic_order()
    {
        var claims = new FromPriorClaims(Sub: NewSenderDid, Iss: PriorDid, Iat: 1700000000, Exp: 1700003600, Nbf: 1699999900);
        var jwt = await FromPriorBuilder.BuildAsync(claims, SignerPrivateJwk());

        var payload = Encoding.UTF8.GetString(Base64Url.Decode(jwt.Split('.')[1]));
        payload.Should().Be(
            """{"exp":1700003600,"iat":1700000000,"iss":"did:example:alice","nbf":1699999900,"sub":"did:example:newAlice"}""");
    }

    [Fact]
    public async Task Builder_without_exp_nbf_emits_only_iat_iss_sub_unchanged()
    {
        // Regression: the no-expiry path is byte-identical to the prior {iat,iss,sub} payload.
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());

        var payload = Encoding.UTF8.GetString(Base64Url.Decode(jwt.Split('.')[1]));
        payload.Should().Be("""{"iat":1700000000,"iss":"did:example:alice","sub":"did:example:newAlice"}""");
    }

    [Fact]
    public async Task Builder_lifetime_overload_sets_exp_from_iat_plus_lifetime()
    {
        var claims = new FromPriorClaims(Sub: NewSenderDid, Iss: PriorDid, Iat: 1700000000);
        var jwt = await FromPriorBuilder.BuildAsync(claims, SignerPrivateJwk(), lifetime: TimeSpan.FromMinutes(5));

        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.Decode(jwt.Split('.')[1])))!.AsObject();
        ((long)payload["exp"]!).Should().Be(1700000000 + 300);
    }

    [Theory]
    [InlineData(0)]    // zero
    [InlineData(-5)]   // negative
    [InlineData(0.5)]  // sub-second: floors to exp == iat (already-expired) → rejected (red-team)
    public async Task Builder_lifetime_overload_rejects_lifetime_below_one_second(double seconds)
    {
        var act = async () => await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk(), TimeSpan.FromSeconds(seconds));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task DidCommClient_RejectsExpiredFromPrior_FrRot05()
    {
        // Issue #25 end-to-end: a self-issued from_prior with exp in the past is now actually rejected
        // on unpack (FR-ROT-05) — previously unreachable because the builder never emitted exp.
        var claims = new FromPriorClaims(Sub: "did:example:alice", Iss: PriorDid, Iat: 1700000000, Exp: 1700000100); // 2023, long past
        var jwt = await FromPriorBuilder.BuildAsync(claims, SignerPrivateJwk());

        var message = new MessageBuilder()
            .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .WithFromPrior(jwt)
            .WithBody(JsonNode.Parse("""{"a":"b"}""")!.AsObject())
            .Build();

        var client = new DidCommClient(Actors.Value.AsSecretsResolver(), NewKeyService(), new DidCommOptions());
        var packed = (await client.PackEncryptedAsync(message,
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"))).Message;

        var act = async () => await client.UnpackAsync(packed);

        await act.Should().ThrowAsync<ConsistencyException>().Where(e => e.Message.Contains("FR-ROT-05"));
    }

    [Fact]
    public async Task DidCommClient_RejectsNotYetValidFromPrior_FrRot05()
    {
        // Issue #25 end-to-end, symmetric to the expired-Exp case: a from_prior whose nbf is in the
        // future must be rejected on unpack (FR-ROT-05). The clock is pinned to iat so nbf = iat + 1 day
        // is deterministically not-yet-valid (well past any skew) regardless of wall-clock time.
        const long iat = 1700000000;
        var pinnedNow = DateTimeOffset.FromUnixTimeSeconds(iat);
        var claims = new FromPriorClaims(Sub: "did:example:alice", Iss: PriorDid, Iat: iat, Nbf: iat + 86400);
        var jwt = await FromPriorBuilder.BuildAsync(claims, SignerPrivateJwk());

        var message = new MessageBuilder()
            .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
            .WithFrom("did:example:alice")
            .WithTo("did:example:bob")
            .WithFromPrior(jwt)
            .WithBody(JsonNode.Parse("""{"a":"b"}""")!.AsObject())
            .Build();

        var options = new DidCommOptions { Clock = () => pinnedNow };
        var client = new DidCommClient(Actors.Value.AsSecretsResolver(), NewKeyService(), options);
        var packed = (await client.PackEncryptedAsync(message,
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"))).Message;

        var act = async () => await client.UnpackAsync(packed);

        await act.Should().ThrowAsync<ConsistencyException>().Where(e => e.Message.Contains("not yet valid"));
    }

    [Fact]
    public async Task Validator_RejectsCritHeader_FrRot26()
    {
        // Issue #26: a from_prior whose protected header marks an extension critical must be rejected
        // (RFC 7515 §4.1.11), mirroring JwsParser/JweParser. The check precedes signature verification,
        // so splicing 'crit' (without re-signing) still triggers it.
        var jwt = await FromPriorBuilder.BuildAsync(SampleClaims(), SignerPrivateJwk());
        var withCrit = MutateHeader(jwt, h => h["crit"] = new JsonArray("urn:example:unsupported"));

        var act = async () => await FromPriorValidator.ValidateAsync(withCrit, NewSenderDid, NewKeyService());

        await act.Should().ThrowAsync<ProtocolException>().Where(e => e.Message.Contains("crit"));
    }
}
