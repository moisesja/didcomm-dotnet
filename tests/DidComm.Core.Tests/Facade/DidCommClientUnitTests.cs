using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Facade;

/// <summary>
/// Unit-level facade tests that exercise non-crypto behaviour (did:web rejection, expiry,
/// max-bytes, FR-ROT-03 plaintext refusal). The crypto round-trips live in
/// <c>DidComm.InteropTests.Facade.DidCommClientRoundTripTests</c> where the Appendix A/B
/// fixtures are available.
/// </summary>
public sealed class DidCommClientUnitTests
{
    private static DidCommClient NewClient(DidCommOptions? options = null)
    {
        var resolver = new EmptyDidKeyService();
        return new DidCommClient(new EmptySecretsResolver(), resolver, options ?? new DidCommOptions());
    }

    private static Message NewMessage() => new MessageBuilder()
        .WithType("http://example.com/p/1.0/m")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .Build();

    [Fact]
    public async Task PackPlaintext_RejectsFromPrior_FrRot03()
    {
        var client = NewClient();
        var msg = new MessageBuilder()
            .WithType("http://example.com/p/1.0/m")
            .WithFromPrior("eyJhbGciOiJFZERTQSJ9.payload.signature")
            .Build();

        var act = async () => await client.PackPlaintextAsync(msg);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("FR-ROT-03"));
    }

    [Fact]
    public async Task PackSigned_RejectsFromPrior_FrRot03()
    {
        var client = NewClient();
        var msg = new MessageBuilder()
            .WithType("http://example.com/p/1.0/m")
            .WithFromPrior("eyJhbGciOiJFZERTQSJ9.payload.signature")
            .Build();

        var act = async () => await client.PackSignedAsync(msg, "did:example:alice");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("FR-ROT-03"));
    }

    [Fact]
    public async Task PackPlaintext_RejectsDidWebInMessage_FrDid06()
    {
        var client = NewClient();
        var msg = new MessageBuilder()
            .WithType("http://example.com/p/1.0/m")
            .WithFrom("did:web:example.com")
            .Build();

        var act = async () => await client.PackPlaintextAsync(msg);

        await act.Should().ThrowAsync<UnsupportedDidMethodException>()
            .Where(e => e.Method == "web");
    }

    [Fact]
    public async Task PackSigned_RejectsDidWebSigner_FrDid06()
    {
        var client = NewClient();

        var act = async () => await client.PackSignedAsync(NewMessage(), "did:web:example.com");

        await act.Should().ThrowAsync<UnsupportedDidMethodException>();
    }

    [Fact]
    public async Task PackEncrypted_RejectsDidWebRecipient_FrDid06()
    {
        var client = NewClient();

        var act = async () => await client.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: new[] { "did:web:example.com" }));

        await act.Should().ThrowAsync<UnsupportedDidMethodException>();
    }

    [Fact]
    public async Task PackEncrypted_RejectsDidWebSender_FrDid06()
    {
        var client = NewClient();

        var act = async () => await client.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, From: "did:web:example.com"));

        await act.Should().ThrowAsync<UnsupportedDidMethodException>();
    }

    [Fact]
    public async Task PackEncrypted_RejectsDidWebSignFrom_FrDid06()
    {
        var client = NewClient();

        var act = async () => await client.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, SignFrom: "did:web:example.com"));

        await act.Should().ThrowAsync<UnsupportedDidMethodException>();
    }

    [Fact]
    public async Task PackEncrypted_RequiresRecipients()
    {
        var client = NewClient();

        var act = async () => await client.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: Array.Empty<string>()));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Unpack_RejectsOversizedInput_FrApi06()
    {
        var options = new DidCommOptions { MaxReceiveBytes = 16 };
        var client = NewClient(options);

        var act = async () => await client.UnpackAsync("{\"this-is-way-bigger-than-sixteen-bytes\":1}");

        await act.Should().ThrowAsync<MalformedMessageException>()
            .Where(e => e.Message.Contains("MaxReceiveBytes"));
    }

    /// <summary>Empty resolver — every secrets call returns null / empty.</summary>
    private sealed class EmptySecretsResolver : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => Task.FromResult<Jwk?>(null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    /// <summary>Key service that only implements did:web rejection; everything else is unused for these tests.</summary>
    private sealed class EmptyDidKeyService : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(false);

        public void RejectUnsupportedMethod(string did)
        {
            if (did.StartsWith("did:web:", StringComparison.Ordinal))
                throw new UnsupportedDidMethodException("web", did, "did:web is rejected per DD-08");
        }
    }
}
