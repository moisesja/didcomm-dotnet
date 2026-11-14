using System.Collections.Concurrent;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports;

/// <summary>
/// #92 G3 — <see cref="DidCommClient.SendPackedAsync"/> (JC-20): already-packed bytes go to the
/// router as given, with the SSRF guard always applied, the queue URI and non-DIDComm media types
/// refused, and no return-route fields on the request (so no transport reads a response body).
/// </summary>
public sealed class DidCommClientSendPackedTests
{
    private static readonly byte[] Packed = Encoding.UTF8.GetBytes("{\"protected\":\"x\",\"recipients\":[],\"iv\":\"x\",\"ciphertext\":\"x\",\"tag\":\"x\"}");

    [Theory]
    [InlineData("https://10.0.0.7/relay")]
    [InlineData("https://127.0.0.1/relay")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("wss://[::1]/ws")]
    public async Task Guard_refuses_a_private_ip_literal_before_the_router(string endpoint)
    {
        var router = new CapturingRouter();

        var act = () => Client(router).SendPackedAsync(Packed, new Uri(endpoint), MediaTypes.Encrypted);

        await act.Should().ThrowAsync<TransportException>();
        router.Requests.Should().BeEmpty("the guard always runs: a relay target is attacker-influenced");
    }

    [Fact]
    public async Task Guard_honours_the_configured_policy()
    {
        var router = new CapturingRouter();
        var options = new DidCommOptions { OutboundEndpointPolicy = new OutboundEndpointPolicy { BlockPrivateNetworks = false } };

        await Client(router, options).SendPackedAsync(Packed, new Uri("https://10.0.0.7/relay"), MediaTypes.Encrypted);

        router.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Queue_uri_is_refused()
    {
        var router = new CapturingRouter();

        var act = () => Client(router).SendPackedAsync(Packed, new Uri(QueueTransport.Uri), MediaTypes.Encrypted);

        var thrown = (await act.Should().ThrowAsync<TransportException>()).Which;
        thrown.Message.Should().Be(QueueTransport.RefusalMessage);
        thrown.Scheme.Should().Be("didcomm");
        router.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MediaTypes.Encrypted, MediaTypes.Encrypted)]
    [InlineData(MediaTypes.Signed, MediaTypes.Signed)]
    [InlineData(MediaTypes.Plaintext, MediaTypes.Plaintext)]
    [InlineData("didcomm-encrypted+json", MediaTypes.Encrypted)]
    [InlineData("APPLICATION/DIDCOMM-SIGNED+JSON", MediaTypes.Signed)]
    public async Task Router_receives_exact_bytes_media_type_and_no_return_route_fields(string mediaType, string canonical)
    {
        var router = new CapturingRouter();
        var endpoint = new Uri("https://93.184.216.34/relay");

        var result = await Client(router).SendPackedAsync(Packed, endpoint, mediaType);

        result.Should().Be(router.Result);
        var request = router.Requests.Should().ContainSingle().Which;
        request.Endpoint.Should().Be(endpoint);
        request.Payload.ToArray().Should().Equal(Packed);
        request.MediaType.Should().Be(canonical, "the canonical constant goes on the wire (FR-TRN-02)");
        request.ReturnRoute.Should().BeNull();
        request.LocalDid.Should().BeNull("no connection affinity");
        request.PeerDid.Should().BeNull();
        request.MaxResponseBytes.Should().BeNull("no response body is ever read for a relay");
    }

    [Theory]
    [InlineData("application/json", "https://93.184.216.34/relay", false)]
    [InlineData("text/plain", "https://93.184.216.34/relay", false)]
    [InlineData("application/didcomm-encrypted+json; charset=utf-8", "https://93.184.216.34/relay", false)]
    [InlineData("", "https://93.184.216.34/relay", false)]
    [InlineData(MediaTypes.Encrypted, "/relative/inbox", false)]
    [InlineData(MediaTypes.Encrypted, "https://93.184.216.34/relay", true)]
    public async Task Unsupported_media_type_and_relative_uri_are_ArgumentException(string mediaType, string endpoint, bool emptyPayload)
    {
        var router = new CapturingRouter();
        var uri = new Uri(endpoint, UriKind.RelativeOrAbsolute);

        var act = () => Client(router).SendPackedAsync(emptyPayload ? ReadOnlyMemory<byte>.Empty : Packed, uri, mediaType);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        thrown.Should().NotBeOfType<ArgumentNullException>();
        router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_endpoint_or_media_type_is_ArgumentNullException()
    {
        var client = Client(new CapturingRouter());

        await ((Func<Task>)(() => client.SendPackedAsync(Packed, null!, MediaTypes.Encrypted))).Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => client.SendPackedAsync(Packed, new Uri("https://93.184.216.34/relay"), null!))).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Without_router_throws_InvalidOperationException()
    {
        var client = new DidCommClient(new NoSecrets(), new NoKeys(), new DidCommOptions());

        var act = () => client.SendPackedAsync(Packed, new Uri("https://93.184.216.34/relay"), MediaTypes.Encrypted);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ITransportRouter");
    }

    private static DidCommClient Client(ITransportRouter router, DidCommOptions? options = null)
        => new(new NoSecrets(), new NoKeys(), new NoServices(), router, options ?? new DidCommOptions());

    private sealed class CapturingRouter : ITransportRouter
    {
        public ConcurrentQueue<TransportRequest> Requests { get; } = new();

        public TransportResult Result { get; } = new(Accepted: true, HttpStatusCode: 202);

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class NoServices : IServiceEndpointResolver
    {
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not resolve anything.");
    }

    private sealed class NoSecrets : ISecretsResolver
    {
        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default) => throw new InvalidOperationException("SendPackedAsync must not look up a secret.");

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not look up a secret.");
    }

    private sealed class NoKeys : IDidKeyService
    {
        public Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not resolve a DID.");

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => throw new InvalidOperationException("SendPackedAsync must not resolve a DID.");

        public void RejectUnsupportedMethod(string did) { }
    }
}
