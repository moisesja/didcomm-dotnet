using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Transports;

/// <summary>
/// Phase 5 Checkpoint A — covers the <see cref="DidCommClient.SendAsync"/> guard rails
/// (no-router refusal, missing-endpoint refusal, override path). The full pack-then-send
/// round-trip lives in the interop project against real keys + a TestServer.
/// </summary>
public sealed class DidCommClientSendAsyncTests
{
    [Fact]
    public async Task SendAsync_without_TransportRouter_throws_actionable_InvalidOperationException()
    {
        var client = new DidCommClient(new EmptySecretsResolver(), new EmptyKeyService(), new DidCommOptions());

        var act = async () => await client.SendAsync(NewMessage(),
            new SendOptions(Recipients: new[] { "did:example:bob" }));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("ITransportRouter");
    }

    [Fact]
    public async Task SendAsync_empty_recipients_is_rejected_with_ArgumentException()
    {
        var client = new DidCommClient(new EmptySecretsResolver(), new EmptyKeyService(), new DidCommOptions());

        var act = async () => await client.SendAsync(NewMessage(),
            new SendOptions(Recipients: Array.Empty<string>()));

        await act.Should().ThrowAsync<ArgumentException>();
    }

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

    private sealed class EmptyKeyService : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Jwk>>(Array.Empty<Jwk>());
        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(false);
        public void RejectUnsupportedMethod(string did) { }
    }
}
