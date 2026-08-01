using System.Net.WebSockets;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Resolution;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.Stomp;
using DidComm.Transports.WebSocket;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// FR-TRN-12 — minimal opt-in STOMP 1.2 framing over WebSocket, end to end against the
/// TestServer harness: the transport's CONNECT→CONNECTED handshake plus SEND with
/// <c>content-type: application/didcomm-encrypted+json</c> on the sending side, and the
/// <c>DidCommReceiveOptions.UseStomp</c> receive side (both <c>MapDidCommWebSocket</c>
/// overloads). Default-off is proven too: with STOMP disabled the plain framing is unchanged
/// and a STOMP frame is just one more rejected envelope.
/// </summary>
public sealed class StompWebSocketTransportTests
{
    [Fact]
    public void Stomp_is_off_by_default_on_both_sides()
    {
        new WebSocketTransportOptions().UseStomp.Should().BeFalse("FR-TRN-12 is opt-in (MAY)");
        new DidCommReceiveOptions().UseStomp.Should().BeFalse("FR-TRN-12 is opt-in (MAY)");
    }

    [Fact]
    public async Task RoundTrip_StompTransport_to_StompServer_DeliversEnvelope()
    {
        var (server, received) = await BuildServerAsync(useStomp: true);

        var actors = SpecActorRegistry.LoadDefault();
        var keyService = new NetDidKeyService(LoadResolver());
        var serviceResolver = new NetDidServiceEndpointResolver(LoadResolver(), keyService, new DidCommOptions());

        var wsClient = server.CreateWebSocketClient();
        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
            UseStomp = true, // FR-TRN-12 opt-in; destination defaults to the endpoint path
            WebSocketFactory = () => wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default).GetAwaiter().GetResult(),
            Connect = (_, _, _) => Task.CompletedTask,
        });
        await using var transport = new WebSocketDidCommTransport(options);
        var router = new TransportRouter(new IDidCommTransport[] { transport });
        var client = new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, router, new DidCommOptions());

        await client.SendAsync(NewProposal(), new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            ServiceEndpointOverride: BuildWebSocketUri(server, "/ws/didcomm")));

        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5));
        received.Should().HaveCount(1);
        received[0].Message.From.Should().Be("did:example:alice");
        received[0].Authenticated.Should().BeTrue();
    }

    [Fact]
    public async Task RawStompClient_HandshakeSendDisconnect_FullSession()
    {
        var (server, received) = await BuildServerAsync(useStomp: true);
        var packed = await PackProposalAsync();

        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);

        // CONNECT → CONNECTED (version 1.2, heart-beat 0,0 — no heart-beating).
        await SendFrameAsync(socket, new StompFrame("CONNECT", new[]
        {
            KeyValuePair.Create("accept-version", "1.1,1.2"),
            KeyValuePair.Create("host", "localhost"),
        }, ReadOnlyMemory<byte>.Empty));
        var connected = StompFrameCodec.Decode(await ReceiveOneAsync(socket));
        connected.Command.Should().Be("CONNECTED");
        connected.TryGetHeader("version", out var version).Should().BeTrue();
        version.Should().Be("1.2");
        connected.TryGetHeader("heart-beat", out var heartBeat).Should().BeTrue();
        heartBeat.Should().Be("0,0");

        // SEND carrying exactly one packed DIDComm message with the FR-TRN-12 content type.
        await SendFrameAsync(socket, new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/ws/didcomm"),
            KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
        }, System.Text.Encoding.UTF8.GetBytes(packed)));
        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5));
        received.Should().HaveCount(1);
        received[0].Message.From.Should().Be("did:example:alice");

        // DISCONNECT → normal close.
        await SendFrameAsync(socket, new StompFrame("DISCONNECT", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty));
        var closeResult = await socket.ReceiveAsync(new byte[16], default);
        closeResult.MessageType.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task Registry_dispatch_overload_also_speaks_stomp()
    {
        // L-043: both receive loops (delegate + dispatcher) must speak the same framing.
        await using var serverApp = await BuildDispatchServerAsync(useStomp: true);
        var server = serverApp.GetTestServer();
        var packed = await PackProposalAsync();

        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);
        await SendFrameAsync(socket, new StompFrame("CONNECT", new[]
        {
            KeyValuePair.Create("accept-version", "1.2"),
            KeyValuePair.Create("host", "localhost"),
        }, ReadOnlyMemory<byte>.Empty));
        StompFrameCodec.Decode(await ReceiveOneAsync(socket)).Command.Should().Be("CONNECTED");

        await SendFrameAsync(socket, new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/ws/didcomm"),
            KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
        }, System.Text.Encoding.UTF8.GetBytes(packed)));

        // The proposal has no registered handler (NoHandler outcome) — the point is the STOMP
        // frame was decoded and dispatched instead of tearing the socket down.
        await Task.Delay(200);
        socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task Send_before_connect_closes_with_protocol_error()
    {
        var (server, received) = await BuildServerAsync(useStomp: true);
        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);

        await SendFrameAsync(socket, new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/d"),
            KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
        }, System.Text.Encoding.UTF8.GetBytes("{}")));

        var closeResult = await socket.ReceiveAsync(new byte[16], default);
        closeResult.MessageType.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.ProtocolError);
        received.Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_stomp_frame_closes_with_protocol_error()
    {
        var (server, received) = await BuildServerAsync(useStomp: true);
        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);

        // Not a STOMP frame at all (no NUL terminator either).
        var garbage = System.Text.Encoding.UTF8.GetBytes("GARBAGE\nwithout:terminator\n\n");
        await socket.SendAsync(garbage, WebSocketMessageType.Binary, endOfMessage: true, default);

        var closeResult = await socket.ReceiveAsync(new byte[16], default);
        closeResult.MessageType.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.ProtocolError);
        received.Should().BeEmpty();
    }

    [Fact]
    public async Task Unaccepted_content_type_discards_message_but_keeps_session()
    {
        var (server, received) = await BuildServerAsync(useStomp: true);
        var packed = await PackProposalAsync();
        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);

        await SendFrameAsync(socket, new StompFrame("CONNECT", new[]
        {
            KeyValuePair.Create("accept-version", "1.2"),
            KeyValuePair.Create("host", "localhost"),
        }, ReadOnlyMemory<byte>.Empty));
        StompFrameCodec.Decode(await ReceiveOneAsync(socket)).Command.Should().Be("CONNECTED");

        // Wrong content-type: the WebSocket analog of the HTTP 415 — message discarded, session lives.
        await SendFrameAsync(socket, new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/d"),
            KeyValuePair.Create("content-type", "text/plain"),
        }, System.Text.Encoding.UTF8.GetBytes(packed)));

        // A correct SEND on the SAME session is still delivered.
        await SendFrameAsync(socket, new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/d"),
            KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
        }, System.Text.Encoding.UTF8.GetBytes(packed)));

        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5));
        socket.State.Should().Be(WebSocketState.Open);
        received.Should().HaveCount(1, "the text/plain SEND is discarded; the didcomm one is delivered");
    }

    [Fact]
    public async Task Oversize_stomp_message_still_closes_1009_per_FR_API_06()
    {
        // The MaxReceiveBytes cap applies to the whole WebSocket message BEFORE STOMP decoding,
        // so the 413-equivalent policy (RFC 6455 close 1009) is unchanged by the framing.
        var (server, received) = await BuildServerAsync(useStomp: true, configureOptions: o => o.MaxReceiveBytes = 64);
        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);

        await socket.SendAsync(new byte[256], WebSocketMessageType.Binary, endOfMessage: true, default);

        var closeResult = await socket.ReceiveAsync(new byte[16], default);
        closeResult.MessageType.Should().Be(WebSocketMessageType.Close);
        ((int)socket.CloseStatus!.Value).Should().Be(1009);
        received.Should().BeEmpty();
    }

    [Fact]
    public async Task DefaultOff_server_treats_stomp_frame_as_rejected_envelope_and_plain_framing_still_works()
    {
        // Default-off proof: without UseStomp the server never interprets STOMP — a CONNECT frame
        // is just one more malformed envelope (dropped, socket kept), and a plain bare envelope on
        // the same socket is delivered exactly as before.
        var (server, received) = await BuildServerAsync(useStomp: false);
        var packed = await PackProposalAsync();
        var wsClient = server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(BuildWebSocketUri(server, "/ws/didcomm"), default);

        await SendFrameAsync(socket, new StompFrame("CONNECT", new[]
        {
            KeyValuePair.Create("accept-version", "1.2"),
            KeyValuePair.Create("host", "localhost"),
        }, ReadOnlyMemory<byte>.Empty));
        await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(packed), WebSocketMessageType.Binary, endOfMessage: true, default);

        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5));
        socket.State.Should().Be(WebSocketState.Open, "a rejected frame is a per-message drop, not a disconnect");
        received.Should().HaveCount(1, "only the plain envelope unpacks; no CONNECTED was ever sent");
    }

    // ---- helpers ----

    private static async Task SendFrameAsync(WebSocket socket, StompFrame frame)
        => await socket.SendAsync(StompFrameCodec.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, default);

    private static async Task<byte[]> ReceiveOneAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), default);
            result.MessageType.Should().Be(WebSocketMessageType.Binary);
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return ms.ToArray();
        }
    }

    private static async Task<string> PackProposalAsync()
    {
        var actors = SpecActorRegistry.LoadDefault();
        var keyService = new NetDidKeyService(LoadResolver());
        var client = new DidCommClient(actors.AsSecretsResolver(), keyService, new DidCommOptions());
        var packed = await client.PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice"));
        return packed.Message;
    }

    private static async Task<(TestServer Server, List<UnpackResult> Received)> BuildServerAsync(
        bool useStomp, Action<DidCommOptions>? configureOptions = null)
    {
        var received = new List<UnpackResult>();
        var actors = SpecActorRegistry.LoadDefault();
        var keyService = new NetDidKeyService(LoadResolver());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISecretsResolver>(actors.AsSecretsResolver());
        builder.Services.AddSingleton<IDidKeyService>(keyService);
        builder.Services.AddSingleton<DidCommClient>(sp => new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value));
        builder.Services.AddOptions<DidCommOptions>().Configure(o =>
        {
            o.MaxReceiveBytes = 64 * 1024;
            configureOptions?.Invoke(o);
        });
        builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o => o.UseStomp = useStomp);

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

    private static async Task<WebApplication> BuildDispatchServerAsync(bool useStomp)
    {
        var actors = SpecActorRegistry.LoadDefault();
        var keyService = new NetDidKeyService(LoadResolver());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDidComm(b =>
        {
            b.UseSecretsResolver(actors.AsSecretsResolver());
            // Fixture-backed key service (AddDidComm fail-fast requires it registered in-scope).
            b.Services.AddSingleton<IDidKeyService>(keyService);
            b.AddBuiltInProtocols();
        });
        builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o => o.UseStomp = useStomp);

        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommWebSocket("/ws/didcomm");
        await app.StartAsync();
        return app;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private static Uri BuildWebSocketUri(TestServer server, string path)
    {
        var builder = new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = path };
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
