using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Secrets;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Section P (PRD §14.2 / FR-TRN-01/04/05): Alice picks up the routed-and-packed envelope from
/// section O and hands it to a transport. The transport router (selected by the recipient
/// service-endpoint URI scheme) drives one HTTP POST to Bob's inbox, which we host in-process
/// via <see cref="TestServer"/> so the section stays offline-safe.
/// </summary>
public static class Section_P_SendOverTransport
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("P", "Send over a transport (HTTP chosen by endpoint scheme)");

        // Stand up an in-process ASP.NET Core endpoint that will be Bob's inbox. The Cookbook
        // doesn't open a real port; the TestServer fixture lets the receive side run inside
        // the same process so the example stays self-contained.
        var received = new List<UnpackResult>();
        var bobServer = await BuildBobInboxAsync(ctx, received);

        // The transport router needs an IDidCommTransport. Build the HTTPS transport
        // configured against TestServer's primary message handler and allow the http scheme
        // (TestServer publishes http://localhost as its base address).
        var transportServices = new ServiceCollection();
        transportServices.AddOptions<HttpTransportOptions>().Configure(opts =>
        {
            opts.AllowedSchemes = new[] { "http", "https" };
            opts.MaxRetryAttempts = 0;
            opts.RequestTimeout = TimeSpan.FromSeconds(5);
        });
        transportServices.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => bobServer.CreateHandler());
        await using var transportSp = transportServices.BuildServiceProvider();
        var transport = new HttpDidCommTransport(
            transportSp.GetRequiredService<IHttpClientFactory>(),
            transportSp.GetRequiredService<IOptions<HttpTransportOptions>>());
        var router = new TransportRouter(new IDidCommTransport[] { transport });

        var secrets = ctx.ServiceProvider.GetRequiredService<ISecretsResolver>();
        var keyService = ctx.ServiceProvider.GetRequiredService<IDidKeyService>();
        var serviceResolver = ctx.ServiceProvider.GetRequiredService<IServiceEndpointResolver>();
        var aliceSender = new DidCommClient(secrets, keyService, serviceResolver, router, new DidCommOptions());

        ctx.Narrator.Step("Alice picks SendAsync(...) and overrides the endpoint to point at Bob's in-process inbox.");
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Section P: bytes on the wire."}""")!.AsObject())
            .Build();

        var endpoint = new Uri(new Uri(bobServer.BaseAddress.ToString()), "/didcomm");
        var sendResult = await aliceSender.SendAsync(message, new SendOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            ServiceEndpointOverride: endpoint));

        ctx.Narrator.Value("TransportEndpoint", sendResult.EndpointUsed);
        ctx.Narrator.Value("HttpStatusCode", sendResult.Transport.HttpStatusCode);
        ctx.Narrator.Value("Accepted", sendResult.Transport.Accepted);

        // Bob's receiver collected the unpacked message — confirm the original payload made it.
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
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm", async (unpacked, ct) =>
        {
            received.Add(unpacked);
            await Task.CompletedTask;
        });
        await app.StartAsync();
        return app.GetTestServer();
    }
}
