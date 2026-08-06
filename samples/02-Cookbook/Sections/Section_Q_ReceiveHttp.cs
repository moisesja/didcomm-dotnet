using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Section Q (PRD §14.2 / FR-TRN-07 / FR-API-06): zoom in on the receive side. Bob registers
/// <c>MapDidCommEndpoint</c>, Alice POSTs a packed envelope to it, and Bob's receiver records
/// the unpacked message and surfaces its metadata. Two negative cases run after: a
/// <c>415</c> when the content type is wrong, and a <c>413</c> when the body exceeds
/// <c>MaxReceiveBytes</c>.
/// </summary>
public static class Section_Q_ReceiveHttp
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("Q", "Receive over HTTP (ASP.NET Core MapDidCommEndpoint)");

        var received = new List<UnpackResult>();
        // Tight MaxReceiveBytes makes the 413 branch easy to demonstrate without a giant body.
        var bobServer = await BuildBobInboxAsync(ctx, received, maxReceiveBytes: 16 * 1024);
        using var http = bobServer.CreateClient();

        // Pack a real authcrypt envelope from Alice to Bob using the SHARED facade (no
        // transport involved). The cookbook then ships the bytes through the HTTP path.
        var aliceClient = ctx.Client;
        var packed = await aliceClient.PackEncryptedAsync(
            new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(ctx.Alice.Did)
                .WithTo(ctx.Bob.Did)
                .WithBody(JsonNode.Parse("""{"content":"Section Q: receive side spotlight."}""")!.AsObject())
                .Build(),
            new PackEncryptedOptions(Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did));

        ctx.Narrator.Step("Alice POSTs the packed envelope with application/didcomm-encrypted+json.");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
               {
                   Content = new StringContent(packed.Message),
               })
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/didcomm-encrypted+json");
            using var response = await http.SendAsync(request);
            ctx.Narrator.Value("Status", (int)response.StatusCode);
        }

        var bobMessage = received.Single();
        ctx.Narrator.Value("From", bobMessage.Message.From);
        ctx.Narrator.Value("Authenticated", bobMessage.Authenticated);
        ctx.Narrator.Value("Content", bobMessage.Message.Body?["content"]?.GetValue<string>());

        ctx.Narrator.Step("Wrong Content-Type → 415 Unsupported Media Type.");
        using (var bad = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
               {
                   Content = new StringContent("{}", Encoding.UTF8, "application/json"),
               })
        {
            using var response = await http.SendAsync(bad);
            ctx.Narrator.Value("Status", (int)response.StatusCode);
        }

        ctx.Narrator.Step("Body > MaxReceiveBytes → 413 Payload Too Large.");
        using (var oversize = new HttpRequestMessage(HttpMethod.Post, "/didcomm")
               {
                   Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', 32 * 1024))),
               })
        {
            oversize.Content.Headers.ContentType = new MediaTypeHeaderValue("application/didcomm-encrypted+json");
            using var response = await http.SendAsync(oversize);
            ctx.Narrator.Value("Status", (int)response.StatusCode);
        }

        // The pattern-only overload skips the inline callback entirely: each received message is
        // dispatched straight to the registered protocol handlers (the inbox below registered
        // Trust Ping). Post a ping at it and the dispatcher answers server-side.
        ctx.Narrator.Step("Registry-aware endpoint: MapDidCommEndpoint(pattern) dispatches to handlers.");
        var ping = TrustPingApi.CreatePing(from: ctx.Alice.Did, to: ctx.Bob.Did);
        var packedPing = await aliceClient.PackEncryptedAsync(ping,
            new PackEncryptedOptions(Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did));
        using (var pingRequest = new HttpRequestMessage(HttpMethod.Post, "/didcomm-dispatch")
               {
                   Content = new StringContent(packedPing.Message),
               })
        {
            pingRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/didcomm-encrypted+json");
            using var response = await http.SendAsync(pingRequest);
            ctx.Narrator.Value("Dispatched ping status", (int)response.StatusCode);
        }
    }

    private static async Task<TestServer> BuildBobInboxAsync(
        CookbookContext ctx,
        List<UnpackResult> received,
        int maxReceiveBytes)
    {
        var secrets = ctx.ServiceProvider.GetRequiredService<ISecretsResolver>();
        var keyService = ctx.ServiceProvider.GetRequiredService<IDidKeyService>();
        var serviceResolver = ctx.ServiceProvider.GetRequiredService<IServiceEndpointResolver>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(secrets);
        builder.Services.AddSingleton(keyService);
        builder.Services.AddSingleton(serviceResolver);
        builder.Services.AddOptions<DidCommOptions>().Configure(opts => opts.MaxReceiveBytes = maxReceiveBytes);
        builder.Services.AddSingleton(sp => new DidCommClient(
            sp.GetRequiredService<ISecretsResolver>(),
            sp.GetRequiredService<IDidKeyService>(),
            sp.GetRequiredService<IServiceEndpointResolver>(),
            sp.GetRequiredService<IOptions<DidCommOptions>>().Value));

        // For the pattern-only endpoint: the handlers the dispatcher will consult. This inbox
        // answers Trust Ping and nothing else.
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new TrustPingHandler());
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IThreadStateStore>(new InMemoryThreadStateStore());
        builder.Services.AddSingleton<ProtocolDispatcher>();

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm", async (unpacked, ct) =>
        {
            received.Add(unpacked);
            await Task.CompletedTask;
        });
        // The registry-aware sibling: no callback — unpack, then dispatch to registered handlers.
        app.MapDidCommEndpoint("/didcomm-dispatch");
        await app.StartAsync();
        return app.GetTestServer();
    }
}
