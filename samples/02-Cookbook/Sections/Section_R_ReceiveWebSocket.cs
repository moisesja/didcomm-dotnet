using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.TrustPing;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Threading;
using DidComm.Transports;
using DidComm.Transports.WebSocket;

// L-014: alias the static TrustPing API class so the namespace import doesn't shadow it.
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Section R (PRD §14.2 / FR-TRN-09..11): one packed envelope per WebSocket message; the
/// receiver reassembles fragments before unpacking, and the connection is one-way. The
/// example also subscribes to the transport's lifecycle event so the reader sees the
/// Connected/Disconnected hooks fire.
/// </summary>
public static class Section_R_ReceiveWebSocket
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("R", "Receive over WebSocket (MapDidCommWebSocket + binary frames)");

        var received = new List<UnpackResult>();
        var bobServer = await BuildBobInboxAsync(ctx, received);
        var wsClient = bobServer.CreateWebSocketClient();
        var endpoint = new UriBuilder(bobServer.BaseAddress) { Scheme = "ws", Path = "/ws/didcomm" }.Uri;

        var options = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
            ConnectTimeout = TimeSpan.FromSeconds(5),   // cap the dial...
            SendTimeout = TimeSpan.FromSeconds(5),      // ...and each frame write
            OutboundEndpointPolicy = new OutboundEndpointPolicy(), // the default SSRF stance, made visible
            // The TestServer's ClientWebSocket is built by its WebSocketClient.ConnectAsync;
            // wire that through the options seam so the transport doesn't try to create a
            // real ClientWebSocket against a non-existent network endpoint.
            WebSocketFactory = () => wsClient.ConnectAsync(endpoint, default).GetAwaiter().GetResult(),
            Connect = (_, _, _) => Task.CompletedTask,
        });
        // The same SSRF policy the HTTP transport enforces guards ws:// endpoints (section P).
        ctx.Narrator.Value("WS OutboundEndpointPolicy.BlockPrivateNetworks",
            options.Value.OutboundEndpointPolicy!.BlockPrivateNetworks);
        await using var transport = new WebSocketDidCommTransport(options);

        // Lifecycle events carry the endpoint and, on failure kinds, the exception that ended the
        // connection — null on a clean connect/close.
        transport.Lifecycle += (_, args) => ctx.Narrator.Note(
            $"Lifecycle: {args.Kind} → {args.Endpoint}{(args.Exception is null ? string.Empty : $" ({args.Exception.GetType().Name})")}");

        var secrets = ctx.ServiceProvider.GetRequiredService<ISecretsResolver>();
        var keyService = ctx.ServiceProvider.GetRequiredService<IDidKeyService>();
        var serviceResolver = ctx.ServiceProvider.GetRequiredService<IServiceEndpointResolver>();
        var router = new TransportRouter(new IDidCommTransport[] { transport });
        var aliceSender = new DidCommClient(secrets, keyService, serviceResolver, router, new DidCommOptions());

        ctx.Narrator.Step("Alice sends one envelope as a single binary WebSocket message.");
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Section R: bytes over WS."}""")!.AsObject())
            .Build();

        var sendResult = await aliceSender.SendAsync(message, new SendOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            ServiceEndpointOverride: endpoint));

        ctx.Narrator.Value("Accepted", sendResult.Transport.Accepted);
        ctx.Narrator.Value("TransportEndpoint", sendResult.EndpointUsed);

        // Wait for the receive loop to drain before reading the captured message (rather than
        // guessing a fixed sleep).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (received.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        var bobMessage = received.Single();
        ctx.Narrator.Value("ContentReceivedByBob", bobMessage.Message.Body?["content"]?.GetValue<string>());

        // The pattern-only overload dispatches to registered protocol handlers instead of an
        // inline callback (the inbox registered Trust Ping). Ride a ping through it.
        ctx.Narrator.Step("Registry-aware endpoint: MapDidCommWebSocket(pattern) dispatches to handlers.");
        var dispatchEndpoint = new UriBuilder(bobServer.BaseAddress) { Scheme = "ws", Path = "/ws/dispatch" }.Uri;
        var dispatchOptions = Options.Create(new WebSocketTransportOptions
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = 0,
            WebSocketFactory = () => wsClient.ConnectAsync(dispatchEndpoint, default).GetAwaiter().GetResult(),
            Connect = (_, _, _) => Task.CompletedTask,
        });
        await using var dispatchTransport = new WebSocketDidCommTransport(dispatchOptions);
        var dispatchSender = new DidCommClient(secrets, keyService, serviceResolver,
            new TransportRouter(new IDidCommTransport[] { dispatchTransport }), new DidCommOptions());
        var pingSend = await dispatchSender.SendAsync(
            TrustPingApi.CreatePing(from: ctx.Alice.Did, to: ctx.Bob.Did),
            new SendOptions(
                Recipients: new[] { ctx.Bob.Did },
                From: ctx.Alice.Did,
                ServiceEndpointOverride: dispatchEndpoint));
        ctx.Narrator.Value("Ping accepted by dispatching endpoint", pingSend.Transport.Accepted);
    }

    private static async Task<TestServer> BuildBobInboxAsync(CookbookContext ctx, List<UnpackResult> received)
    {
        var secrets = ctx.ServiceProvider.GetRequiredService<ISecretsResolver>();
        var keyService = ctx.ServiceProvider.GetRequiredService<IDidKeyService>();
        var serviceResolver = ctx.ServiceProvider.GetRequiredService<IServiceEndpointResolver>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(secrets);
        builder.Services.AddSingleton(keyService);
        builder.Services.AddSingleton(serviceResolver);
        builder.Services.AddOptions<DidCommOptions>();
        builder.Services.AddSingleton(sp => new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IServiceEndpointResolver>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value));

        // Receive-side policy for the endpoint: which media types are accepted, the minimum time
        // any rejection takes (a timing-oracle defense), whether a handler's reply may ride back
        // on the same socket (off by default per FR-TRN-10's one-way reading), and whether the
        // socket speaks raw binary frames or STOMP (section 05-WebSocketChat shows STOMP).
        var receiveOptions = new DidCommReceiveOptions
        {
            AllowSameSocketReplies = false,
            UseStomp = false,
            ReceiveRejectionFloor = TimeSpan.FromMilliseconds(5),
        };
        // The default media-type acceptance list covers the three DIDComm forms; narrow it here
        // if an endpoint should take encrypted envelopes only.
        _ = receiveOptions.AcceptedMediaTypes.Count;
        builder.Services.AddSingleton(Options.Create(receiveOptions));

        // Handlers for the pattern-only endpoint mapped below.
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new TrustPingHandler());
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IThreadStateStore>(new InMemoryThreadStateStore());
        builder.Services.AddSingleton<ProtocolDispatcher>();

        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommWebSocket("/ws/didcomm", async (unpacked, ct) =>
        {
            received.Add(unpacked);
            await Task.CompletedTask;
        });
        // The registry-aware sibling: no callback — unpack, then dispatch to registered handlers.
        app.MapDidCommWebSocket("/ws/dispatch");
        await app.StartAsync();
        return app.GetTestServer();
    }
}
