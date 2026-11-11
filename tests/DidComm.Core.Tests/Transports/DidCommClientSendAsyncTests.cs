using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Tests.Envelopes;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using NetCrypto;
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

    [Fact]
    public async Task SendAsync_refuses_a_queue_endpoint_override_before_any_router_call()
    {
        // FR-RR-06: the override is judged on its shape before packing, so an empty key graph suffices.
        var router = new CountingRouter();
        var client = new DidCommClient(new EmptySecretsResolver(), new EmptyKeyService(), new EmptyServiceResolver(), router, new DidCommOptions());

        var act = async () => await client.SendAsync(NewMessage(),
            new SendOptions(Recipients: new[] { "did:example:bob" }, ServiceEndpointOverride: new Uri("didcomm:transport/queue")));

        var ex = (await act.Should().ThrowAsync<TransportException>()).Which;
        ex.Message.Should().Be(QueueTransport.RefusalMessage);
        ex.Message.Should().NotContain("did:");
        ex.Scheme.Should().Be("didcomm");
        router.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_refuses_a_resolved_queue_endpoint_after_packing_and_before_any_router_call()
    {
        // FR-RR-06, stated from the caller's viewpoint: the recipient's keys and service entry are
        // resolved and the message is packed first (the endpoint lives in the service entry), and
        // SendAsync throws before any transport activity.
        var bobKey = TestKeyMaterial.Generate(KeyType.X25519, "did:example:bob#ka-1");
        var keys = new OneKeyService("did:example:bob", bobKey.PublicJwk);
        var services = new QueueServiceResolver("did:example:bob");
        var router = new CountingRouter();
        var client = new DidCommClient(new InMemorySecretsResolver(), keys, services, router, new DidCommOptions());

        var act = async () => await client.SendAsync(NewMessage(), new SendOptions(Recipients: new[] { "did:example:bob" }));

        var ex = (await act.Should().ThrowAsync<TransportException>()).Which;
        ex.Message.Should().Be(QueueTransport.RefusalMessage);
        ex.Message.Should().NotContain("did:example:bob").And.NotContain("did:example:alice");
        ex.Scheme.Should().Be("didcomm");
        services.Resolved.Should().Be(1, "the service entry is resolved before the refusal");
        router.Calls.Should().Be(0);
    }

    private static Message NewMessage() => new MessageBuilder()
        .WithType("http://example.com/p/1.0/m")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .Build();

    private sealed class CountingRouter : ITransportRouter
    {
        public int Calls { get; private set; }
        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }

    private sealed class EmptyServiceResolver : IServiceEndpointResolver
    {
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(Array.Empty<DidCommServiceInfo>());
    }

    private sealed class QueueServiceResolver : IServiceEndpointResolver
    {
        private readonly string _did;
        public QueueServiceResolver(string did) => _did = did;
        public int Resolved { get; private set; }
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
        {
            Resolved++;
            IReadOnlyList<DidCommServiceInfo> result = did == _did
                ? new[] { new DidCommServiceInfo(QueueTransport.Uri, Array.Empty<string>(), Array.Empty<string>()) }
                : Array.Empty<DidCommServiceInfo>();
            return Task.FromResult(result);
        }
    }

    private sealed class OneKeyService : IDidKeyService
    {
        private readonly string _did;
        private readonly Jwk _key;
        public OneKeyService(string did, Jwk key) { _did = did; _key = key; }
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Jwk>>(did == _did && relationship == VerificationRelationship.KeyAgreement ? new[] { _key } : Array.Empty<Jwk>());
        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => Task.FromResult(did == _did && kid == _key.Kid);
        public void RejectUnsupportedMethod(string did) { }
    }

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
