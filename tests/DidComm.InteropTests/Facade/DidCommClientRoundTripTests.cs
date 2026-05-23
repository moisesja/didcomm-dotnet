using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Resolution;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.Facade;

/// <summary>
/// End-to-end facade round-trips using vendored Appendix A secrets and Appendix B DID Docs.
/// Verifies <c>DidCommClient</c> drives the Phase 2 envelope layer correctly against real
/// spec key material — every legal FR-ENV-02 composition pack + unpack returns the original
/// plaintext.
/// </summary>
public sealed class DidCommClientRoundTripTests
{
    private static readonly Lazy<SpecActorRegistry> Actors = new(SpecActorRegistry.LoadDefault);

    private static readonly Lazy<FixtureDidResolver> DocResolver = new(() =>
        FixtureDidResolver.LoadFromDirectory(Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec")));

    private static DidCommClient NewClient()
    {
        var keyService = new NetDidKeyService(DocResolver.Value);
        return new DidCommClient(Actors.Value.AsSecretsResolver(), keyService, new DidCommOptions());
    }

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();

    [Fact]
    public async Task PackPlaintext_UnpackPlaintext_RoundTrips()
    {
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(NewProposal());

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeFalse();
        result.Authenticated.Should().BeFalse();
        result.Message.From.Should().Be("did:example:alice");
        result.Message.Type.Should().Contain("lets_do_lunch");
    }

    [Fact]
    public async Task PackSigned_UnpackSigned_PreservesNonRepudiation()
    {
        var client = NewClient();
        var packed = await client.PackSignedAsync(NewProposal(), "did:example:alice");

        var result = await client.UnpackAsync(packed);

        result.NonRepudiation.Should().BeTrue();
        result.SignerKid.Should().StartWith("did:example:alice#");
        result.Message.From.Should().Be("did:example:alice");
    }

    [Fact]
    public async Task PackEncrypted_Anoncrypt_RoundTrips()
    {
        var client = NewClient();
        var packed = await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }));

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.AnonymousSender.Should().BeTrue();
        result.Authenticated.Should().BeFalse();
        result.RecipientKid.Should().StartWith("did:example:bob#");
    }

    [Fact]
    public async Task PackEncrypted_Authcrypt_RoundTrips()
    {
        var client = NewClient();
        var packed = await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"));

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.Authenticated.Should().BeTrue();
        result.AnonymousSender.Should().BeFalse();
        result.SenderKid.Should().StartWith("did:example:alice#");
    }

    [Fact]
    public async Task PackEncrypted_SignThenEncrypt_RoundTrips()
    {
        var client = NewClient();
        var packed = await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(
                Recipients: new[] { "did:example:bob" },
                From: "did:example:alice",
                SignFrom: "did:example:alice"));

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.Authenticated.Should().BeTrue();
        result.NonRepudiation.Should().BeTrue();
        result.SignerKid.Should().StartWith("did:example:alice#");
    }

    [Fact]
    public async Task PackEncrypted_AnoncryptOfAuthcrypt_RoundTrips()
    {
        var client = NewClient();
        var packed = await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(
                Recipients: new[] { "did:example:bob" },
                From: "did:example:alice",
                ProtectSender: true));

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.Authenticated.Should().BeTrue();
        result.Stack.Where(k => k == DidComm.Jose.EnvelopeKind.Encrypted).Should().HaveCount(2);
    }

    [Fact]
    public async Task PackEncrypted_Authcrypt_RefusesGcmPerFrEnc09()
    {
        var client = NewClient();

        var act = async () => await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(
                Recipients: new[] { "did:example:bob" },
                From: "did:example:alice",
                Enc: ContentEncryptionAlgorithm.A256Gcm));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("FR-ENC-09"));
    }
}
