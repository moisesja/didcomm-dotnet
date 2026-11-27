using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="SenderPartition"/> (#111, I-14, D-1): only an authcrypt (<c>ECDH-1PU+A256KW</c>) outer
/// envelope that names a <c>skid</c> within the key-id limits counts against a named sender, its DID
/// subject; everything else is anonymous, and nothing it is given makes it throw.
/// </summary>
public sealed class SenderPartitionTests
{
    private const string AliceKid = "did:example:alice#key-x25519-1";

    [Theory]
    [InlineData(AliceKid, "did:example:alice")]
    [InlineData("did:example:alice", "did:example:alice")]
    [InlineData("did:example:alice/path?versionId=2#key-1", "did:example:alice")]
    public void An_authcrypt_envelope_with_a_skid_counts_against_the_skids_DID_subject(string skid, string expected)
        => SenderPartition.Of(TestJwe.Authcrypt(skid, "m")).Should().Be(expected);

    [Fact]
    public void T9_an_ECDH_ES_envelope_that_carries_a_skid_is_anonymous()
        => SenderPartition.Of(TestJwe.EcdhEsWithSkid(AliceKid, "m")).Should().BeNull();

    [Fact]
    public void T10_an_authcrypt_envelope_that_names_its_sender_only_in_apu_is_anonymous()
        => SenderPartition.Of(TestJwe.AuthcryptApuOnly(AliceKid, "m")).Should().BeNull();

    [Fact]
    public void An_anoncrypt_envelope_is_anonymous()
        => SenderPartition.Of(TestJwe.Anoncrypt("m")).Should().BeNull();

    public static TheoryData<string, string> NotANamedSender => new()
    {
        { "a skid that is not a DID URL", Header(new JsonObject { ["alg"] = TestJwe.Authcrypt1Pu, ["enc"] = "A256CBC-HS512", ["skid"] = "alice#key-1" }) },
        { "an empty skid", Header(new JsonObject { ["alg"] = TestJwe.Authcrypt1Pu, ["enc"] = "A256CBC-HS512", ["skid"] = "" }) },
        { "a skid longer than the key-id limit", Header(new JsonObject { ["alg"] = TestJwe.Authcrypt1Pu, ["enc"] = "A256CBC-HS512", ["skid"] = "did:example:alice#" + new string('k', 4096) }) },
        { "a skid whose DID is longer than the DID limit", Header(new JsonObject { ["alg"] = TestJwe.Authcrypt1Pu, ["enc"] = "A256CBC-HS512", ["skid"] = "did:example:" + new string('a', 2048) + "#k" }) },
        { "another ECDH-1PU key wrap", Header(new JsonObject { ["alg"] = "ECDH-1PU+A128KW", ["enc"] = "A256CBC-HS512", ["skid"] = AliceKid }) },
        { "ECDH-1PU without a key wrap", Header(new JsonObject { ["alg"] = "ECDH-1PU", ["enc"] = "A256CBC-HS512", ["skid"] = AliceKid }) },
        { "the algorithm in another case", Header(new JsonObject { ["alg"] = "ecdh-1pu+a256kw", ["enc"] = "A256CBC-HS512", ["skid"] = AliceKid }) },
        { "no alg", Header(new JsonObject { ["enc"] = "A256CBC-HS512", ["skid"] = AliceKid }) },
        { "a protected header that is not JSON", Envelope(Base64("not json")) },
        { "a protected header that is not base64url", Envelope("@@@") },
        { "no recipients", new JsonObject { ["protected"] = Base64(new JsonObject { ["alg"] = TestJwe.Authcrypt1Pu, ["skid"] = AliceKid }.ToJsonString()), ["iv"] = "AAAA", ["ciphertext"] = "AAAA", ["tag"] = "AAAA" }.ToJsonString() },
        { "a JWS", new JsonObject { ["payload"] = "e30", ["signatures"] = new JsonArray(new JsonObject { ["protected"] = "e30", ["signature"] = "AAAA" }) }.ToJsonString() },
        { "a plaintext message", "{\"id\":\"1\",\"type\":\"https://example.org/x\",\"body\":{},\"from\":\"did:example:alice\"}" },
        { "the pickup tests' stand-in", FakePickupExchange.Jwe("packed") },
        { "not JSON", "garbage" },
        { "an empty text", "" },
        { "JSON that is not an object", "[1,2,3]" },
    };

    [Theory]
    [MemberData(nameof(NotANamedSender))]
    public void Anything_else_is_anonymous_and_never_throws(string name, string packed)
    {
        var sender = SenderPartition.Of(packed);

        sender.Should().BeNull(name);
    }

    private static string Header(JsonObject header) => Envelope(Base64(header.ToJsonString()));

    private static string Envelope(string protectedHeader)
        => new JsonObject
        {
            ["protected"] = protectedHeader,
            ["recipients"] = new JsonArray(new JsonObject { ["header"] = new JsonObject { ["kid"] = TestJwe.RecipientKid }, ["encrypted_key"] = "AAAA" }),
            ["iv"] = "AAAA",
            ["ciphertext"] = "AAAA",
            ["tag"] = "AAAA",
        }.ToJsonString();

    private static string Base64(string text) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));
}
