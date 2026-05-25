using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.WebSocket;
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
            // The TestServer's ClientWebSocket is built by its WebSocketClient.ConnectAsync;
            // wire that through the options seam so the transport doesn't try to create a
            // real ClientWebSocket against a non-existent network endpoint.
            WebSocketFactory = () => wsClient.ConnectAsync(endpoint, default).GetAwaiter().GetResult(),
            Connect = (_, _, _) => Task.CompletedTask,
        });
        await using var transport = new WebSocketDidCommTransport(options);

        transport.Lifecycle += (_, args) => ctx.Narrator.Note($"Lifecycle: {args.Kind} → {args.Endpoint}");

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

        // Give the receive loop a tick to drain before reading the captured message.
        await Task.Delay(50);
        var bobMessage = received.Single();
        ctx.Narrator.Value("ContentReceivedByBob", bobMessage.Message.Body?["content"]?.GetValue<string>());
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

        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommWebSocket("/ws/didcomm", async (unpacked, ct) =>
        {
            received.Add(unpacked);
            await Task.CompletedTask;
        });
        await app.StartAsync();
        return app.GetTestServer();
    }
}
