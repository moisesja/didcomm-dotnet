using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Resolution;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.Stomp;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #98 (owner decision O-98-1): which WebSocket frame type each DIDComm send uses. Every DIDComm
/// message goes as a text frame, because the Identus mediator drops binary frames and browser
/// wallets expect text (FR-TRN-09). STOMP frames stay binary (FR-TRN-12), and both receive sides
/// keep accepting either frame type, so a peer still on 1.5.x keeps working. The client-side checks
/// read the frames on a real Kestrel socket (<see cref="RawWebSocketPeer"/>), as a deployed peer would.
/// </summary>
public sealed class WebSocketFrameTypeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // ---- client transport (DidComm.Transports.WebSocket) -------------------------------------

    [Fact]
    public async Task Client_transport_sends_a_DIDComm_message_as_a_text_frame()
    {
        await using var peer = await RawWebSocketPeer.StartAsync();
        await using var transport = NewTransport();
        var alice = AliceClient(transport);
        var proposal = NewProposal();

        await alice.SendAsync(proposal, new SendOptions(
            Recipients: new[] { "did:example:bob" },
            From: "did:example:alice",
            ServiceEndpointOverride: peer.Endpoint));

        var frame = await peer.NextAsync(Timeout);
        frame.Type.Should().Be(WebSocketMessageType.Text, "a DIDComm message is UTF-8 JSON and goes as one text frame (FR-TRN-09, #98)");
        transport.ActiveSessionSocketCount.Should().Be(0, "a header-less send uses the pooled socket");
        var unpacked = await AliceClient(null).UnpackAsync(Encoding.UTF8.GetString(frame.Bytes));
        unpacked.Message.Id.Should().Be(proposal.Id);
    }

    [Fact]
    public async Task Client_session_socket_sends_a_DIDComm_message_as_a_text_frame()
    {
        await using var peer = await RawWebSocketPeer.StartAsync();
        await using var transport = NewTransport();
        var packed = await PackAliceToBobAsync(NewProposal());

        // return_route: all with both identities opens the session socket S(E, L, P) (FR-RR-07).
        await transport.SendAsync(new TransportRequest(peer.Endpoint, packed, MediaTypes.Encrypted)
        {
            LocalDid = "did:example:alice",
            PeerDid = "did:example:bob",
            ReturnRoute = ReturnRouteMode.All,
        }, default);

        var frame = await peer.NextAsync(Timeout);
        transport.ActiveSessionSocketCount.Should().Be(1, "the send went over a return-route session socket");
        frame.Type.Should().Be(WebSocketMessageType.Text, "a DIDComm message on a session socket is one text frame (FR-TRN-09, #98)");
        frame.Bytes.Should().Equal(packed);
    }

    [Fact]
    public async Task Client_STOMP_frames_stay_binary()
    {
        await using var peer = await RawWebSocketPeer.StartAsync();
        peer.Answer = frame => StompFrameCodec.Decode(frame.Bytes).Command == "CONNECT"
            ? (WebSocketMessageType.Binary, StompFrameCodec.Encode(new StompFrame("CONNECTED", new[] { KeyValuePair.Create("version", "1.2") }, ReadOnlyMemory<byte>.Empty)))
            : null;
        await using var transport = NewTransport(o => o.UseStomp = true);
        var packed = await PackAliceToBobAsync(NewProposal());

        await transport.SendAsync(new TransportRequest(peer.Endpoint, packed, MediaTypes.Encrypted), default);
        var connect = await peer.NextAsync(Timeout);
        var send = await peer.NextAsync(Timeout);

        connect.Type.Should().Be(WebSocketMessageType.Binary);
        StompFrameCodec.Decode(connect.Bytes).Command.Should().Be("CONNECT");
        send.Type.Should().Be(WebSocketMessageType.Binary, "a STOMP SEND frame is unchanged by #98 (FR-TRN-12)");
        var sendFrame = StompFrameCodec.Decode(send.Bytes);
        sendFrame.Command.Should().Be("SEND");
        sendFrame.Body.ToArray().Should().Equal(packed);
        // The DISCONNECT written on dispose is binary too (unchanged), but it is a best-effort write
        // right before the connection is dropped, so a peer may not read it: not asserted here.
    }

    [Theory]
    [InlineData(WebSocketMessageType.Binary)]
    [InlineData(WebSocketMessageType.Text)]
    public async Task Client_session_socket_accepts_a_DIDComm_message_in_either_frame_type(WebSocketMessageType type)
    {
        // A peer on 1.5.x (or any peer that sends binary) still reaches this client.
        await using var peer = await RawWebSocketPeer.StartAsync();
        var replyPacked = await AliceClient(null).PackEncryptedAsync(NewProposal(), new PackEncryptedOptions(
            Recipients: new[] { "did:example:alice" }, From: "did:example:bob", Forward: false));
        var reply = Encoding.UTF8.GetBytes(replyPacked.Message);
        peer.Answer = _ => (type, reply);
        var consumer = new RecordingFrameConsumer();
        await using var transport = NewTransport(o => o.ReceiveInbound = true, consumer);

        await transport.SendAsync(new TransportRequest(peer.Endpoint, await PackAliceToBobAsync(NewProposal()), MediaTypes.Encrypted)
        {
            LocalDid = "did:example:alice",
            PeerDid = "did:example:bob",
            ReturnRoute = ReturnRouteMode.All,
        }, default);

        (await ReturnRouteTestHost.WaitUntilAsync(() => consumer.Calls >= 1, Timeout)).Should().BeTrue("the {0} frame reaches the inbound consumer", type);
        consumer.Frames.Single().Payload.ToArray().Should().Equal(reply);
    }

    // ---- server (DidComm.AspNetCore) ----------------------------------------------------------

    [Fact]
    public async Task Server_writes_same_socket_pushes_and_replies_as_text_frames()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (new[] { ToAlice(host, "pushed", message.Id) }, ToAlice(host, "replied", message.Id));
        var inbound = host.Stamped(host.RecordingMessage(host.Alice, "frame-type").WithReturnRoute(ReturnRouteMode.All)).Build();
        using var socket = await host.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, inbound));
        var push = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        var reply = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);

        push.Type.Should().Be(WebSocketMessageType.Text, "a push is one text frame (FR-TRN-09, #98)");
        reply.Type.Should().Be(WebSocketMessageType.Text, "a same-socket reply is one text frame (FR-TRN-09, #98)");
        (await host.Alice.Client.UnpackAsync(push.Text)).Message.Type.Should().EndWith("/pushed");
        (await host.Alice.Client.UnpackAsync(reply.Text)).Message.Type.Should().EndWith("/replied");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task Mediator_live_delivery_is_a_text_frame()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, await mediator.AuthcryptAsync(bob, mediator.Prepare(MessagePickup.CreateLiveDeliveryChange(bob.Did, mediator.Did, true))));
        var status = await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout);
        await Qa.SendNoteAsync(mediator, alice, bob);
        var delivery = await ReturnRouteTestHost.ReceiveAsync(socket, MediatorTestServer.Timeout);
        await mediator.PushesIdleAsync();

        delivery.Type.Should().Be(WebSocketMessageType.Text, "the live delivery is one text frame, which browser wallets expect (#98)");
        status.Type.Should().Be(WebSocketMessageType.Text, "the mediator's answer is one text frame");
        mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive);
        (await bob.Client.UnpackAsync(delivery.Text)).Message.Type.Should().EndWith("/delivery");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Theory]
    [InlineData(WebSocketMessageType.Binary)]
    [InlineData(WebSocketMessageType.Text)]
    public async Task Server_endpoint_accepts_a_DIDComm_message_in_either_frame_type(WebSocketMessageType type)
    {
        // A client on 1.5.x (binary) and a browser wallet or Identus (text) both reach the endpoint.
        var received = new List<UnpackResult>();
        await using var app = await StartReceivingServerAsync(received);
        var proposal = NewProposal();
        var packed = await PackAliceToBobAsync(proposal);
        var server = app.GetTestServer();
        using var socket = await server.CreateWebSocketClient().ConnectAsync(new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri, default);

        await socket.SendAsync(packed, type, endOfMessage: true, default);

        (await ReturnRouteTestHost.WaitUntilAsync(() => { lock (received) return received.Count >= 1; }, Timeout)).Should().BeTrue("the {0} frame is unpacked and handed to the host", type);
        UnpackResult only;
        lock (received)
            only = received.Single();
        only.Message.Id.Should().Be(proposal.Id);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static WebSocketDidCommTransport NewTransport(Action<WebSocketTransportOptions>? configure = null, IInboundFrameConsumer? consumer = null)
    {
        var options = new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws" },
            MaxReconnectAttempts = 0,
            // A custom Connect skips the transport's private-address guard (the peer is on loopback).
            Connect = (socket, uri, ct) => ((ClientWebSocket)socket).ConnectAsync(uri, ct),
        };
        configure?.Invoke(options);
        return new WebSocketDidCommTransport(Options.Create(options), logger: null, coreOptions: null, inboundConsumer: consumer);
    }

    // The spec actors' secrets (Alice and Bob both), over the fixture DID documents.
    private static DidCommClient AliceClient(WebSocketDidCommTransport? transport)
    {
        var actors = SpecActorRegistry.LoadDefault();
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var serviceResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        return transport is null
            ? new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new DidCommOptions())
            : new DidCommClient(actors.AsSecretsResolver(), keyService, serviceResolver, new TransportRouter(new IDidCommTransport[] { transport }), new DidCommOptions());
    }

    private static async Task<byte[]> PackAliceToBobAsync(Message message)
    {
        var packed = await AliceClient(null).PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { "did:example:bob" }, From: "did:example:alice", Forward: false));
        return Encoding.UTF8.GetBytes(packed.Message);
    }

    private static Message NewProposal() => new MessageBuilder()
        .WithType("http://example.com/protocols/lets_do_lunch/1.0/proposal")
        .WithFrom("did:example:alice")
        .WithTo("did:example:bob")
        .WithBody(new JsonObject { ["messagespecificattribute"] = "and its value" })
        .Build();

    private static Message ToAlice(ReturnRouteTestHost host, string name, string thid) => new MessageBuilder()
        .WithType(RecordingHandler.ProtocolUri + "/" + name)
        .WithFrom(host.ServerDid)
        .WithTo(host.Alice.Did)
        .WithThid(thid)
        .Build();

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

    private static async Task<WebApplication> StartReceivingServerAsync(List<UnpackResult> received)
    {
        var resolver = LoadResolver();
        var keyService = new NetDidKeyService(resolver);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<DidComm.Secrets.ISecretsResolver>(SpecActorRegistry.LoadDefault().AsSecretsResolver());
        builder.Services.AddSingleton<IDidKeyService>(keyService);
        builder.Services.AddSingleton<IServiceEndpointResolver>(new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions()));
        builder.Services.AddSingleton(sp => new DidCommClient(
            sp.GetRequiredService<DidComm.Secrets.ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IServiceEndpointResolver>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value));
        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommWebSocket("/ws", (result, _) =>
        {
            lock (received)
                received.Add(result);
            return Task.CompletedTask;
        });
        await app.StartAsync();
        return app;
    }
}

/// <summary>
/// A raw WebSocket peer on a real Kestrel socket (loopback, port chosen by the OS): it records every
/// whole message it receives with its frame type and connection number, and can answer each one. A
/// failure that ends a connection is recorded too, as an <see cref="RawFrame.Error"/> with no type.
/// </summary>
internal sealed class RawWebSocketPeer : IAsyncDisposable
{
    private readonly Channel<RawFrame> _frames = Channel.CreateUnbounded<RawFrame>();
    private readonly WebApplication _app;
    private int _connections;

    private RawWebSocketPeer(WebApplication app) => _app = app;

    /// <summary>The peer's WebSocket URI.</summary>
    public Uri Endpoint { get; private set; } = null!;

    /// <summary>What to send back after a recorded message (<c>null</c>: nothing).</summary>
    public Func<RawFrame, (WebSocketMessageType Type, byte[] Bytes)?>? Answer { get; set; }

    public static async Task<RawWebSocketPeer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var peer = new RawWebSocketPeer(app);
        app.UseWebSockets();
        app.Map("/raw", peer.ServeAsync);
        await app.StartAsync();
        peer.Endpoint = new UriBuilder(app.Urls.Single()) { Scheme = "ws", Path = "/raw" }.Uri;
        return peer;
    }

    /// <summary>The next recorded message, waiting for it (the timeout is a hang guard only).</summary>
    public async Task<RawFrame> NextAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _frames.Reader.ReadAsync(cts.Token);
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = Interlocked.Increment(ref _connections);
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var frame = new RawFrame(connection, result.MessageType, message.ToArray(), Error: null);
                _frames.Writer.TryWrite(frame);
                if (Answer?.Invoke(frame) is { } answer)
                    await socket.SendAsync(answer.Bytes, answer.Type, endOfMessage: true, context.RequestAborted);
            }
        }
        catch (Exception ex)
        {
            // Recorded, never asserted here: the test reads it as the next frame.
            _frames.Writer.TryWrite(new RawFrame(connection, Type: null, Array.Empty<byte>(), $"{ex.GetType().Name} (state {socket.State}, close {socket.CloseStatus})"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>One whole message the <see cref="RawWebSocketPeer"/> received, or (no <see cref="Type"/>, an <see cref="Error"/>) the failure that ended its connection.</summary>
internal sealed record RawFrame(int Connection, WebSocketMessageType? Type, byte[] Bytes, string? Error);
