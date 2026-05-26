using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Transports;
using FluentAssertions;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// SSRF-defense integration tests for <see cref="DidCommClient.SendAsync"/>. An endpoint resolved
/// from a recipient's DID document that points at a private / loopback / metadata host must be
/// rejected before any transport dispatch; a caller-supplied <c>ServiceEndpointOverride</c> is
/// trusted and bypasses the gate; and the policy can opt out or allowlist a host. The spec key
/// fixtures back the inner authcrypt pack so we reach the egress check on the real send path.
/// </summary>
public sealed class OutboundEndpointGuardSendTests
{
    [Theory]
    [InlineData("http://169.254.169.254/inbox")] // cloud metadata
    [InlineData("http://127.0.0.1/inbox")]        // loopback
    [InlineData("http://10.0.0.5/inbox")]         // RFC 1918
    public async Task SendAsync_rejects_resolved_private_endpoint_before_dispatch(string endpoint)
    {
        var router = new RecordingRouter();
        var client = BuildClient(new StaticServiceResolver(endpoint), router, new DidCommOptions());

        var act = async () => await client.SendAsync(NewProposal(), new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice"));

        (await act.Should().ThrowAsync<TransportException>())
            .Which.Message.Should().Contain("private or reserved");
        router.Called.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_allows_resolved_private_endpoint_when_block_disabled()
    {
        var router = new RecordingRouter();
        var options = new DidCommOptions();
        options.OutboundEndpointPolicy.BlockPrivateNetworks = false;
        var client = BuildClient(new StaticServiceResolver("http://10.0.0.5/inbox"), router, options);

        await client.SendAsync(NewProposal(), new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice"));

        router.LastEndpoint.Should().Be("http://10.0.0.5/inbox");
    }

    [Fact]
    public async Task SendAsync_allows_resolved_private_endpoint_when_host_allowlisted()
    {
        var router = new RecordingRouter();
        var options = new DidCommOptions();
        options.OutboundEndpointPolicy.AllowedHosts.Add("10.0.0.5");
        var client = BuildClient(new StaticServiceResolver("http://10.0.0.5/inbox"), router, options);

        await client.SendAsync(NewProposal(), new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice"));

        router.LastEndpoint.Should().Be("http://10.0.0.5/inbox");
    }

    [Fact]
    public async Task SendAsync_trusts_ServiceEndpointOverride_even_when_private()
    {
        var router = new RecordingRouter();
        var client = BuildClient(new StaticServiceResolver("https://example.com/inbox"), router, new DidCommOptions());

        await client.SendAsync(NewProposal(), new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            ServiceEndpointOverride: new Uri("http://127.0.0.1:9999/inbox")));

        router.LastEndpoint.Should().Be("http://127.0.0.1:9999/inbox");
    }

    private static DidCommClient BuildClient(IServiceEndpointResolver serviceResolver, ITransportRouter router, DidCommOptions options)
    {
        var actors = SpecActorRegistry.LoadDefault();
        var keyService = new NetDidKeyService(LoadResolver());
        return new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, router, options);
    }

    private static FixtureDidResolver LoadResolver()
    {
        var docs = new Dictionary<string, DidDocument>(StringComparer.Ordinal);
        foreach (var file in new[] { "alice.json", "bob.json" })
        {
            var path = Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec", file);
            var doc = NetDid.Core.Serialization.DidDocumentSerializer.Deserialize(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"failed to deserialize {file}");
            docs[doc.Id.Value!] = doc;
        }
        return new FixtureDidResolver(docs);
    }

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();

    private sealed class StaticServiceResolver : IServiceEndpointResolver
    {
        private readonly string _uri;
        public StaticServiceResolver(string uri) => _uri = uri;

        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(
                new[] { new DidCommServiceInfo(_uri, Array.Empty<string>(), Array.Empty<string>()) });
    }

    private sealed class RecordingRouter : ITransportRouter
    {
        public bool Called { get; private set; }
        public string? LastEndpoint { get; private set; }

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Called = true;
            LastEndpoint = request.Endpoint.ToString();
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }
}
