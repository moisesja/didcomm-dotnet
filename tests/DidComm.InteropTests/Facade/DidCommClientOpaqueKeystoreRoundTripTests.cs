using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Resolution;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.Facade;

/// <summary>
/// End-to-end facade round-trips driven entirely through <strong>non-extractable</strong> custody
/// (FR-SEC-06 / issue #45): the consumer's keys live in a NetCrypto <c>IKeyStore</c> behind
/// <see cref="DidComm.Adapters.NetDid.NetDidKeyStoreSecretsResolver"/>, which surfaces only public
/// JWKs and performs signing / ECDH inside the keystore. These mirror the extractable
/// <see cref="DidCommClientRoundTripTests"/> cases against the very same Appendix-A key material —
/// proving a wallet can authcrypt / anoncrypt / sign on send and unpack on receive with no private
/// key bytes ever leaving the store.
/// </summary>
public sealed class DidCommClientOpaqueKeystoreRoundTripTests
{
    private static readonly Lazy<SpecActorRegistry> Actors = new(SpecActorRegistry.LoadDefault);

    private static readonly Lazy<FixtureDidResolver> DocResolver = new(() =>
        FixtureDidResolver.LoadFromDirectory(Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec")));

    // A fresh keystore-backed resolver per client: the private scalars are imported into the IKeyStore
    // once here and are unreachable thereafter (IKeyStore exposes no extraction API).
    private static DidCommClient NewOpaqueClient()
    {
        var keyService = new NetDidKeyService(DocResolver.Value);
        return new DidCommClient(Actors.Value.AsKeyStoreResolver(), keyService, new DidCommOptions());
    }

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();

    [Fact]
    public async Task Keystore_resolver_surfaces_public_only_jwks_never_d()
    {
        // The custody invariant, asserted directly: the resolver the facade drives never exposes 'd'.
        var resolver = Actors.Value.AsKeyStoreResolver();
        var jwk = await resolver.FindAsync("did:example:alice#key-x25519-1");
        jwk.Should().NotBeNull();
        jwk!.X.Should().NotBeNullOrEmpty();
        jwk.D.Should().BeNull("a non-extractable keystore never releases the private scalar (FR-SEC-06)");
    }

    [Fact]
    public async Task Signed_round_trips_through_the_keystore()
    {
        var client = NewOpaqueClient();
        var packed = await client.PackSignedAsync(NewProposal(), "did:example:alice");

        var result = await client.UnpackAsync(packed);

        result.NonRepudiation.Should().BeTrue();
        result.SignerKid.Should().StartWith("did:example:alice#");
        result.Message.From.Should().Be("did:example:alice");
    }

    [Fact]
    public async Task Anoncrypt_round_trips_through_the_keystore()
    {
        var client = NewOpaqueClient();
        var packed = (await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }))).Message;

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.AnonymousSender.Should().BeTrue();
        result.Authenticated.Should().BeFalse();
        result.RecipientKid.Should().StartWith("did:example:bob#");
        result.Message.Body.Should().NotBeNull();
    }

    [Fact]
    public async Task Authcrypt_round_trips_through_the_keystore()
    {
        var client = NewOpaqueClient();
        var packed = (await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"))).Message;

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.Authenticated.Should().BeTrue();
        result.AnonymousSender.Should().BeFalse();
        result.SenderKid.Should().StartWith("did:example:alice#");
        result.RecipientKid.Should().StartWith("did:example:bob#");
    }

    [Fact]
    public async Task SignThenEncrypt_round_trips_through_the_keystore()
    {
        var client = NewOpaqueClient();
        var packed = (await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(
                Recipients: new[] { "did:example:bob" },
                From: "did:example:alice",
                SignFrom: "did:example:alice"))).Message;

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.Authenticated.Should().BeTrue();
        result.NonRepudiation.Should().BeTrue();
        result.SignerKid.Should().StartWith("did:example:alice#");
    }

    [Fact]
    public async Task AnoncryptOfAuthcrypt_round_trips_through_the_keystore()
    {
        var client = NewOpaqueClient();
        var packed = (await client.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(
                Recipients: new[] { "did:example:bob" },
                From: "did:example:alice",
                ProtectSender: true))).Message;

        var result = await client.UnpackAsync(packed);

        result.Encrypted.Should().BeTrue();
        result.Authenticated.Should().BeTrue();
        result.Stack.Where(k => k == DidComm.Jose.EnvelopeKind.Encrypted).Should().HaveCount(2);
    }

    [Fact]
    public async Task Opaque_send_is_unpackable_by_an_extractable_receiver_and_vice_versa()
    {
        // Interop both directions: the opaque (keystore) and extractable (in-memory) paths produce and
        // consume byte-compatible envelopes — the seam only changes WHERE the secret op runs.
        var opaque = NewOpaqueClient();
        var extractable = new DidCommClient(Actors.Value.AsSecretsResolver(), new NetDidKeyService(DocResolver.Value), new DidCommOptions());

        var fromOpaque = (await opaque.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"))).Message;
        (await extractable.UnpackAsync(fromOpaque)).Authenticated.Should().BeTrue();

        var fromExtractable = (await extractable.PackEncryptedAsync(
            NewProposal(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:example:alice"))).Message;
        (await opaque.UnpackAsync(fromExtractable)).Authenticated.Should().BeTrue();
    }
}
