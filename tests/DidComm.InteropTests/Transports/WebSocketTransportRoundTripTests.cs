using System.Net.WebSockets;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.WebSocket;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// Phase 5 Checkpoints D + E — end-to-end FR-TRN-09/10 over WebSocket: Alice packs authcrypt
/// for Bob and sends as one binary WebSocket message via
/// <see cref="WebSocketDidCommTransport"/>; the in-process <see cref="TestServer"/> accepts
/// the WS, the <c>MapDidCommWebSocket</c> loop reassembles fragments + unpacks + dispatches
/// to the receiver delegate. Includes the FR-TRN-09 reassembly assertion via the
/// <see cref="System.Net.WebSockets.WebSocket.SendAsync(System.ArraySegment{byte}, WebSocketMessageType, bool, CancellationToken)"/>
/// chunked-send path.
/// </summary>
public sealed class WebSocketTransportRoundTripTests
{
    [Fact]
    public async Task Registry_reply_uses_real_decrypting_identity_when_another_tenant_is_first_in_to()
    {
        var aliceSecrets = new InMemorySecretsResolver();
        var sharedServerSecrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid, carolDid) = await CreateThreePeersAsync(aliceSecrets, sharedServerSecrets);
        await using var serverApp = await BuildReplyingServerAsync(sharedServerSecrets);
        var server = serverApp.GetTestServer();
        await using var aliceServices = BuildPeerServices(aliceSecrets);
        var client = aliceServices.GetRequiredService<DidCommClient>();

        // The shared server holds both Bob and Carol. The plaintext puts Carol first, but the real
        // authcrypt recipient is Bob. The old path would successfully authenticate the reply as
        // Carol, so this is a real cross-tenant regression—not merely a missing-key failure.
        var ping = new MessageBuilder()
            .WithType(TrustPing.PingType)
            .WithFrom(aliceDid)
            .WithTo(carolDid, bobDid)
            .Build();
        var packed = await client.PackEncryptedAsync(ping, new PackEncryptedOptions(
            Recipients: new[] { bobDid },
            From: aliceDid,
            Forward: false));

        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);
        var bytes = System.Text.Encoding.UTF8.GetBytes(packed.Message);
        await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, default);

        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, default);
        result.MessageType.Should().Be(WebSocketMessageType.Binary);
        result.EndOfMessage.Should().BeTrue();

        var replyEnvelope = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        var reply = await client.UnpackAsync(replyEnvelope);
        reply.Encrypted.Should().BeTrue();
        reply.Authenticated.Should().BeTrue();
        reply.Message.Type.Should().Be(TrustPing.ResponseType);
        reply.Message.From.Should().Be(bobDid);
        reply.Message.To.Should().Equal(aliceDid);
        reply.SenderKid.Should().StartWith($"{bobDid}#");
        reply.RecipientKid.Should().StartWith($"{aliceDid}#");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);
    }

    [Fact]
    public async Task Registry_does_not_emit_a_same_socket_reply_for_signed_only_input()
    {
        var aliceSecrets = new InMemorySecretsResolver();
        var serverSecrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid, _) = await CreateThreePeersAsync(aliceSecrets, serverSecrets);
        await using var serverApp = await BuildReplyingServerAsync(serverSecrets);
        var server = serverApp.GetTestServer();
        await using var aliceServices = BuildPeerServices(aliceSecrets);
        var client = aliceServices.GetRequiredService<DidCommClient>();

        var ping = TrustPing.CreatePing(aliceDid, bobDid);
        var signed = await client.PackSignedAsync(ping, aliceDid);
        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);
        await socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(signed),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            default);

        using var noReply = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var receive = async () => await socket.ReceiveAsync(new byte[4096], noReply.Token);
        await receive.Should().ThrowAsync<OperationCanceledException>(
            "signed-only input has no cryptographically selected local recipient for automatic egress");

        socket.Abort();
    }

    [Fact]
    public async Task RoundTrip_Alice_WebSocket_to_Bob_unpacks_original_plaintext()
    {
        var (server, received) = await BuildServerAsync();

        await SendFromAliceAsync(server, NewProposal(), received);

        var unpacked = received.Single();
        unpacked.Message.From.Should().Be("did:example:alice");
        unpacked.Message.To.Should().Contain("did:example:bob");
        unpacked.Message.Type.Should().Be("http://example.com/protocols/lets_do_lunch/1.0/proposal");
        unpacked.Authenticated.Should().BeTrue();
    }

    [Fact]
    public async Task RoundTrip_fragmented_send_is_reassembled_into_one_logical_envelope()
    {
        var (server, received) = await BuildServerAsync();

        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());

        // Pack outside the transport so we can drive the chunked SendAsync ourselves and
        // exercise FR-TRN-09's reassembly invariant explicitly.
        var aliceClient = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions());
        var packed = await aliceClient.PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            Forward: false));
        var bytes = System.Text.Encoding.UTF8.GetBytes(packed.Message);

        var wsClient = server.CreateWebSocketClient();
        var endpoint = BuildWebSocketUri(server, "/ws/didcomm");
        using var socket = await wsClient.ConnectAsync(endpoint, default);

        var chunkSize = bytes.Length / 3;
        var sent = 0;
        var chunks = 0;
        while (sent < bytes.Length)
        {
            var remaining = bytes.Length - sent;
            var take = Math.Min(chunkSize, remaining);
            var endOfMessage = sent + take == bytes.Length;
            await socket.SendAsync(new ArraySegment<byte>(bytes, sent, take), WebSocketMessageType.Binary, endOfMessage, default);
            sent += take;
            chunks++;
        }
        chunks.Should().BeGreaterOrEqualTo(3);

        // Wait for the server to reassemble + dispatch before closing, rather than guessing a sleep.
        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);

        received.Should().HaveCount(1);
        received[0].Message.From.Should().Be("did:example:alice");
    }

    [Fact]
    public async Task Oversize_message_triggers_1009_close_per_FR_API_06()
    {
        var (server, received) = await BuildServerAsync(opts => opts.MaxReceiveBytes = 64);

        var wsClient = server.CreateWebSocketClient();
        var endpoint = BuildWebSocketUri(server, "/ws/didcomm");
        using var socket = await wsClient.ConnectAsync(endpoint, default);

        var payload = new byte[256];
        await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, endOfMessage: true, default);

        // The server should send a Close frame back; receive it on this side.
        var buffer = new byte[16];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), default);
        result.MessageType.Should().Be(WebSocketMessageType.Close);
        ((int)socket.CloseStatus!.Value).Should().Be(1009);
        received.Should().BeEmpty();
    }

    [Fact]
    public void WebSocketDidCommTransport_CanHandle_respects_AllowedSchemes()
    {
        var transport = new WebSocketDidCommTransport(
            Options.Create(new WebSocketTransportOptions { AllowedSchemes = new[] { "wss" } }));

        transport.CanHandle(new Uri("wss://agents.r.us/ws")).Should().BeTrue();
        transport.CanHandle(new Uri("ws://agents.r.us/ws")).Should().BeFalse();
        transport.CanHandle(new Uri("https://agents.r.us/inbox")).Should().BeFalse();
    }

    private static async Task SendFromAliceAsync(TestServer server, Message message, List<UnpackResult> received)
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());

        var wsClient = server.CreateWebSocketClient();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
            WebSocketFactory = () => wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default).GetAwaiter().GetResult(),
            // We pre-connected the socket inside the factory above, so Connect is a no-op.
            Connect = (_, _, _) => Task.CompletedTask,
        });
        await using var transport = new WebSocketDidCommTransport(options);
        var router = new TransportRouter(new IDidCommTransport[] { transport });
        var client = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, router, new DidCommOptions());

        var endpoint = BuildWebSocketUri(server, "/ws/didcomm");
        await client.SendAsync(message, new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            ServiceEndpointOverride: endpoint));

        // Wait for the server to drain the message before disposing the transport (dispose closes
        // the socket and would otherwise race the server's ReceiveAsync loop).
        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private static async Task<(TestServer Server, List<UnpackResult> Received)> BuildServerAsync(Action<DidCommOptions>? configureOptions = null)
    {
        var received = new List<UnpackResult>();
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISecretsResolver>(actors.AsSecretsResolver());
        builder.Services.AddSingleton<IDidKeyService>(keyService);
        builder.Services.AddSingleton<IServiceEndpointResolver>(serviceResolver);
        builder.Services.AddSingleton<DidCommClient>(sp => new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IServiceEndpointResolver>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value));
        builder.Services.AddOptions<DidCommOptions>().Configure(o =>
        {
            o.MaxReceiveBytes = 64 * 1024;
            configureOptions?.Invoke(o);
        });

        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommWebSocket("/ws/didcomm", async (result, ct) =>
        {
            received.Add(result);
            await Task.CompletedTask;
        });

        await app.StartAsync();
        return (app.GetTestServer(), received);
    }

    private static async Task<WebApplication> BuildReplyingServerAsync(InMemorySecretsResolver bobSecrets)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(bobSecrets);
            b.AddBuiltInProtocols();
        });
        builder.Services.AddOptions<DidCommReceiveOptions>()
            .Configure(o => o.AllowSameSocketReplies = true);

        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommWebSocket("/ws/didcomm");
        await app.StartAsync();
        return app;
    }

    private static ServiceProvider BuildPeerServices(InMemorySecretsResolver secrets)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
        });
        return services.BuildServiceProvider();
    }

    private static async Task<(string AliceDid, string BobDid, string CarolDid)> CreateThreePeersAsync(
        InMemorySecretsResolver aliceSecrets,
        InMemorySecretsResolver sharedServerSecrets)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IDidManager>();
        var keyGenerator = provider.GetRequiredService<IKeyGenerator>();
        var crypto = provider.GetRequiredService<ICryptoProvider>();

        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto);
        var carol = await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto);
        foreach (var jwk in alice.Privates) aliceSecrets.Add(jwk);
        foreach (var jwk in bob.Privates) sharedServerSecrets.Add(jwk);
        foreach (var jwk in carol.Privates) sharedServerSecrets.Add(jwk);
        return (alice.Did, bob.Did, carol.Did);
    }

    private static Uri BuildWebSocketUri(TestServer server, string path)
    {
        var http = server.BaseAddress;
        var builder = new UriBuilder(http) { Scheme = "ws", Path = path };
        return builder.Uri;
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

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(System.Text.Json.Nodes.JsonNode.Parse("""{"messagespecificattribute":"and its value"}""")!.AsObject())
        .Build();
}
