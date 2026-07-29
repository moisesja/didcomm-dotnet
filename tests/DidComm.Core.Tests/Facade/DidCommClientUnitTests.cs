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

    [Fact]
    public async Task Unpack_WarnsWhenOwnIdentifierAbsentFromTo_FrConsist04()
    {
        // The agent declared who it is; the message's 'to' names someone else. The advisory
        // FR-CONSIST-04 outcome must warn — and still deliver the message (#59).
        var options = new DidCommOptions { OwnIdentifiers = new[] { "did:example:frank" } };
        var client = NewClient(options);
        var packed = await client.PackPlaintextAsync(NewMessage());

        var result = await client.UnpackAsync(packed);

        result.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);
        result.Message.Type.Should().Be("http://example.com/p/1.0/m");
    }

    [Fact]
    public async Task Unpack_ReportsAddressedWhenOwnIdentifierInTo_FrConsist04()
    {
        var options = new DidCommOptions { OwnIdentifiers = new[] { "did:example:bob" } };
        var client = NewClient(options);
        var packed = await client.PackPlaintextAsync(NewMessage());

        var result = await client.UnpackAsync(packed);

        result.RecipientAddressing.Should().Be(RecipientAddressing.Addressed);
    }

    [Fact]
    public async Task Unpack_ReportsNotEvaluatedWhenUnconfigured_FrConsist04()
    {
        // No declared identity and no decrypting key on a plaintext message — nothing to check.
        var client = NewClient();
        var packed = await client.PackPlaintextAsync(NewMessage());

        var result = await client.UnpackAsync(packed);

        result.RecipientAddressing.Should().Be(RecipientAddressing.NotEvaluated);
    }

    [Fact]
    public void Construction_RejectsUnparseableOwnIdentifier_FrConsist04()
    {
        // A typo'd entry would otherwise be skipped silently on every message, leaving the
        // recipient-addressing warning permanently dead with no indication to the operator.
        var options = new DidCommOptions { OwnIdentifiers = new[] { "did:example:bob", "bob@example.com" } };

        Action act = () => NewClient(options);

        act.Should().Throw<ArgumentException>().WithMessage("*bob@example.com*FR-CONSIST-04*");
    }

    [Fact]
    public async Task Unpack_IsUnaffectedByOwnIdentifiersMutatedAfterConstruction_FrConsist04()
    {
        // The options object is shared and mutable; enumerating it live mid-unpack would throw
        // (and drop the message) the moment the app appended to its own list. The advisory check
        // must never affect delivery, so the client works from a construction-time snapshot.
        var live = new List<string> { "did:example:frank" };
        var client = NewClient(new DidCommOptions { OwnIdentifiers = live });
        var packed = await client.PackPlaintextAsync(NewMessage());

        live.Add("did:example:bob"); // would make it "Addressed" if read live

        var result = await client.UnpackAsync(packed);

        result.RecipientAddressing.Should().Be(RecipientAddressing.NotAddressed);
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
