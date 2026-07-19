using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Transports;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetDid.Core;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// PR #51 review findings 1 &amp; 2: the opt-in <see cref="DidCommReceiveOptions.AutoSendReplies"/>
/// must NOT auto-reply to an unauthenticated inbound (which would make the server an authenticated
/// outbound reflector, since a plaintext/anoncrypt `from` is attacker-controlled), and a downstream
/// send failure — including a cancellation-shaped one while the inbound request is not aborted — must
/// still answer a bare 202, never a 400.
/// </summary>
public sealed class AutoSendRepliesSecurityTests
{
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";

    /// <summary>Records outbound sends; can be told to fail like a downstream timeout.</summary>
    private sealed class SpyTransport : IDidCommTransport
    {
        public readonly List<TransportRequest> Sent = new();
        public bool ThrowCanceled;
        public string Scheme => "http";
        public bool CanHandle(Uri endpoint) => true;
        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            lock (Sent) Sent.Add(request);
            if (ThrowCanceled)
                throw new TaskCanceledException("simulated downstream HttpClient.Timeout (inbound token NOT cancelled)");
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }

    [Fact]
    public async Task Plaintext_inbound_does_not_trigger_an_outbound_auto_reply()
    {
        var (server, spy) = await BuildBobAsync();
        var plaintext = await PackerClient().PackPlaintextAsync(NewQuery(from: Alice)); // attacker-settable from

        var status = await PostAsync(server, plaintext, "application/didcomm-plain+json");

        status.Should().Be(HttpStatusCode.Accepted);
        lock (spy.Sent) spy.Sent.Should().BeEmpty("a plaintext inbound's `from` is unauthenticated — no outbound reflection");
    }

    [Fact]
    public async Task Anoncrypt_inbound_does_not_trigger_an_outbound_auto_reply()
    {
        var (server, spy) = await BuildBobAsync();
        // Anoncrypt: encrypted to Bob but sender NOT authenticated; inner `from` is attacker-set.
        var anoncrypt = (await PackerClient().PackEncryptedAsync(NewQuery(from: Alice),
            new PackEncryptedOptions(Recipients: new[] { Bob }))).Message;

        var status = await PostAsync(server, anoncrypt, "application/didcomm-encrypted+json");

        status.Should().Be(HttpStatusCode.Accepted);
        lock (spy.Sent) spy.Sent.Should().BeEmpty("an anoncrypt inbound is unauthenticated — no outbound reflection");
    }

    [Fact]
    public async Task Authcrypt_inbound_triggers_exactly_one_auto_reply_to_the_authenticated_sender()
    {
        var (server, spy) = await BuildBobAsync();
        var authcrypt = (await PackerClient().PackEncryptedAsync(NewQuery(from: Alice),
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        var status = await PostAsync(server, authcrypt, "application/didcomm-encrypted+json");

        status.Should().Be(HttpStatusCode.Accepted);
        lock (spy.Sent) spy.Sent.Should().HaveCount(1, "an authenticated inbound gets a single out-of-band reply to its sender");
    }

    [Fact]
    public async Task A_downstream_cancellation_while_the_inbound_is_not_aborted_still_answers_202()
    {
        var (server, spy) = await BuildBobAsync();
        spy.ThrowCanceled = true; // resolver/transport timeout fires, but RequestAborted is NOT cancelled
        var authcrypt = (await PackerClient().PackEncryptedAsync(NewQuery(from: Alice),
            new PackEncryptedOptions(Recipients: new[] { Bob }, From: Alice))).Message;

        var status = await PostAsync(server, authcrypt, "application/didcomm-encrypted+json");

        status.Should().Be(HttpStatusCode.Accepted, "a cancellation-shaped downstream send failure must not turn a dispatched message into 400");
        lock (spy.Sent) spy.Sent.Should().HaveCount(1, "the send was attempted (and failed) — the failure is swallowed");
    }

    private static Message NewQuery(string from) => DiscoverFeaturesApi.CreateQuery(
        from, Bob, new DidComm.Protocols.DiscoverFeatures.FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" });

    private static DidCommClient PackerClient()
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        return new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
    }

    private static async Task<(TestServer Server, SpyTransport Spy)> BuildBobAsync()
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var spy = new SpyTransport();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDidKeyService>(keyService);
        builder.Services.AddSingleton<IServiceEndpointResolver>(new FixedServiceResolver(Alice, "http://localhost/didcomm"));
        builder.Services.AddSingleton<IDidCommTransport>(spy);
        builder.Services.AddDidComm(b =>
        {
            b.UseSecretsResolver(actors.AsSecretsResolver());
            b.AddBuiltInProtocols();
            b.Configure(o => o.OutboundEndpointPolicy.BlockPrivateNetworks = false);
        });
        builder.Services.Configure<DidCommReceiveOptions>(o => o.AutoSendReplies = true);

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm");
        await app.StartAsync();
        return (app.GetTestServer(), spy);
    }

    private static async Task<HttpStatusCode> PostAsync(TestServer server, string body, string mediaType)
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class FixedServiceResolver(string did, string endpoint) : IServiceEndpointResolver
    {
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string d, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(
                string.Equals(d, did, StringComparison.Ordinal)
                    ? new[] { new DidCommServiceInfo(endpoint, Array.Empty<string>(), Array.Empty<string>()) }
                    : Array.Empty<DidCommServiceInfo>());
    }

    private static FixtureDidResolver LoadResolver()
    {
        var docs = new Dictionary<string, NetDid.Core.Model.DidDocument>(StringComparer.Ordinal);
        foreach (var file in new[] { "alice.json", "bob.json" })
        {
            var path = Path.Combine(FixtureCatalog.FixturesRoot, "diddocs", "spec", file);
            var doc = NetDid.Core.Serialization.DidDocumentSerializer.Deserialize(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"failed to deserialize {file}");
            docs[doc.Id.Value!] = doc;
        }
        return new FixtureDidResolver(docs);
    }
}
