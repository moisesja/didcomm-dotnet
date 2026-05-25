using DidComm.Crypto;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Facade;

/// <summary>
/// Phase 4 Checkpoint D — covers <c>DidCommClient.PackEncryptedAsync(Forward: true)</c>
/// unit-level guard rails. The cryptographic round-trip (Endpoint Examples 1 & 2) lives in
/// the interop project where real Appendix A/B keys are available.
/// </summary>
public sealed class PackEncryptedForwardTests
{
    // The happy-path "Forward = false returns null ServiceEndpoint" coverage lives in the
    // interop project's DidCommClientRoundTripTests + SenderForwardWrappingTests, where real
    // Appendix A/B fixtures let the full pack pipeline complete. This file scopes itself to
    // unit-level Forward guard-rails that don't require real keys.

    [Fact]
    public async Task Forward_true_with_multiple_recipients_is_rejected()
    {
        var client = NewClient();

        var act = async () => await client.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob", "did:example:carol" }, Forward: true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("single-recipient"));
    }

    [Fact]
    public async Task Forward_true_without_IServiceEndpointResolver_throws_with_actionable_message()
    {
        // NewClient builds the facade with the no-service-resolver constructor.
        var client = NewClient();

        var act = async () => await client.PackEncryptedAsync(
            NewMessage(),
            new PackEncryptedOptions(Recipients: new[] { "did:example:bob" }, Forward: true));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("IServiceEndpointResolver");
    }

    private static DidCommClient NewClient() =>
        new(new EmptySecretsResolver(), new EmptyDidKeyService(), new DidCommOptions());

    private static Message NewMessage() => new MessageBuilder()
        .WithType("http://example.com/p/1.0/m")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .Build();

    private sealed class EmptySecretsResolver : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => Task.FromResult<Jwk?>(null);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class EmptyDidKeyService : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(false);

        public void RejectUnsupportedMethod(string did) { }
    }
}
