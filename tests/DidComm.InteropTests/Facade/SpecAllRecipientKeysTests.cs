using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Resolution;
using AwesomeAssertions;
using Xunit;
using DpBase64Url = DataProofsDotnet.Jose.Base64Url;

namespace DidComm.InteropTests.Facade;

/// <summary>
/// FR-ENC-19 against the spec's own material: the Appendix B DID documents and the Appendix C.3
/// encrypted vectors. Each C.3 vector is encrypted to every one of Bob's keys on its curve, and its
/// protected <c>apv</c> is the SHA-256 of all of those kids, sorted and joined by '.'. So when
/// <see cref="DidCommClient.PackEncryptedAsync"/> picks the same keys, it produces the same
/// <c>apv</c> as the vector, byte for byte. Packing to Bob's first key only would give a different
/// hash.
/// </summary>
public sealed class SpecAllRecipientKeysTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";

    // The 'apv' of each Appendix C.3 vector, read from its protected header.
    private const string X25519Apv = "NcsuAnrRfPK69A-rkZ0L9XWUG4jMvNC3Zg74BPz53PA";
    private const string P256Apv = "z-LqpvVXDb_sGYn3mjQLpuu2CQLewYuZoTWOIXPH3FM";
    private const string P384Apv = "LJA9Eoks5tamUFVBalMwBhJ6DkDcJ8HK4SlXZWqDqno";
    private const string P521Apv = "GOeo76ym6NCg9WWMEYfW0eVDT5668zEhl2uAIW-E-HE";

    private static readonly Lazy<SpecActorRegistry> Actors = new(SpecActorRegistry.LoadDefault);

    private static readonly Lazy<FixtureDidResolver> DocResolver = new(() =>
        FixtureDidResolver.LoadFromDirectory(Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec")));

    [Fact]
    public async Task Anoncrypt_to_Bob_addresses_all_three_X25519_keys_and_matches_the_C3_apv()
    {
        var client = Client(keyAgreementCrv: null);

        var packed = (await client.PackEncryptedAsync(Proposal(), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(
            "did:example:bob#key-x25519-1", "did:example:bob#key-x25519-2", "did:example:bob#key-x25519-3");
        ProtectedHeader(packed)["apv"]!.GetValue<string>().Should().Be(X25519Apv);
        (await client.UnpackAsync(packed)).AllRecipientKids.Should().HaveCount(3);
    }

    [Fact]
    public async Task Authcrypt_from_Alice_to_Bob_matches_the_C3_X25519_vector_headers()
    {
        var client = Client(keyAgreementCrv: null);

        var packed = (await client.PackEncryptedAsync(Proposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        RecipientKids(packed).Should().Equal(
            "did:example:bob#key-x25519-1", "did:example:bob#key-x25519-2", "did:example:bob#key-x25519-3");
        var header = ProtectedHeader(packed);
        header["skid"]!.GetValue<string>().Should().Be("did:example:alice#key-x25519-1");
        header["apv"]!.GetValue<string>().Should().Be(X25519Apv);
        var result = await client.UnpackAsync(packed);
        result.Authenticated.Should().BeTrue();
        result.SenderKid.Should().Be("did:example:alice#key-x25519-1");
    }

    [Theory]
    [InlineData("P-384", P384Apv, "did:example:bob#key-p384-1", "did:example:bob#key-p384-2")]
    [InlineData("P-521", P521Apv, "did:example:bob#key-p521-1", "did:example:bob#key-p521-2")]
    public async Task Anoncrypt_on_a_NIST_curve_addresses_both_of_Bobs_keys_and_matches_the_C3_apv(
        string crv, string expectedApv, string firstKid, string secondKid)
    {
        var client = Client(keyAgreementCrv: crv);

        var packed = (await client.PackEncryptedAsync(Proposal(), new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        RecipientKids(packed).Should().Equal(firstKid, secondKid);
        var header = ProtectedHeader(packed);
        header["epk"]!["crv"]!.GetValue<string>().Should().Be(crv);
        header["apv"]!.GetValue<string>().Should().Be(expectedApv);
        (await client.UnpackAsync(packed)).RecipientKid.Should().Be(firstKid);
    }

    [Fact]
    public async Task Authcrypt_on_P256_matches_the_C3_P256_vector_headers()
    {
        var client = Client(keyAgreementCrv: "P-256");

        var packed = (await client.PackEncryptedAsync(Proposal(), new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        RecipientKids(packed).Should().Equal("did:example:bob#key-p256-1", "did:example:bob#key-p256-2");
        var header = ProtectedHeader(packed);
        header["skid"]!.GetValue<string>().Should().Be("did:example:alice#key-p256-1");
        header["apv"]!.GetValue<string>().Should().Be(P256Apv);
        (await client.UnpackAsync(packed)).SenderKid.Should().Be("did:example:alice#key-p256-1");
    }

    [Fact]
    public async Task A_kid_pinned_DID_URL_addresses_exactly_that_key()
    {
        var client = Client(keyAgreementCrv: null);

        var packed = (await client.PackEncryptedAsync(
            Proposal(), new PackEncryptedOptions(Recipients: new[] { "did:example:bob#key-x25519-2" }, From: Alice))).Message;

        RecipientKids(packed).Should().Equal("did:example:bob#key-x25519-2");
        var result = await client.UnpackAsync(packed);
        result.RecipientKid.Should().Be("did:example:bob#key-x25519-2");
        result.Message.To.Should().Equal(Bob);
    }

    private static DidCommClient Client(string? keyAgreementCrv)
    {
        IDidKeyService keyService = new NetDidKeyService(DocResolver.Value);
        if (keyAgreementCrv is not null)
            keyService = new CurveFilteredKeyService(keyService, keyAgreementCrv);
        return new DidCommClient(Actors.Value.AsSecretsResolver(), keyService, new DidCommOptions());
    }

    private static Message Proposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom(Alice)
        .WithTo(Bob)
        .WithBody(JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();

    private static string[] RecipientKids(string packed)
        => JsonNode.Parse(packed)!["recipients"]!.AsArray()
            .Select(r => r!["header"]!["kid"]!.GetValue<string>())
            .ToArray();

    private static JsonNode ProtectedHeader(string packed)
    {
        var encoded = JsonNode.Parse(packed)!["protected"]!.GetValue<string>();
        return JsonNode.Parse(DpBase64Url.Decode(encoded))!;
    }
}
