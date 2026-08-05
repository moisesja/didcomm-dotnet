using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Exceptions;
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
        // (TestServer publishes http://localhost as its base address). The options object is the
        // whole delivery policy in one place: schemes, timeout, the retry/circuit-breaker knobs
        // (FR-TRN-08/11), how many redirect hops to follow, and the SSRF policy below.
        var httpOptions = new HttpTransportOptions
        {
            AllowedSchemes = new[] { "http", "https" },
            MaxRetryAttempts = 0,                             // in-process peer: fail fast
            RequestTimeout = TimeSpan.FromSeconds(5),
            RetryBaseDelay = TimeSpan.FromMilliseconds(200),  // exponential backoff base (when retries are on)
            CircuitBreakerFailureThreshold = 5,               // open the breaker after 5 straight failures...
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30), // ...and probe again after 30 s
            MaxRedirectHops = 2,                              // 301/302/307/308 follow-cap (FR-TRN-07)
            OutboundEndpointPolicy = new OutboundEndpointPolicy(), // the default SSRF stance, made visible
        };
        var transportServices = new ServiceCollection();
        transportServices.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => bobServer.CreateHandler());
        await using var transportSp = transportServices.BuildServiceProvider();
        var transport = new HttpDidCommTransport(
            transportSp.GetRequiredService<IHttpClientFactory>(),
            Options.Create(httpOptions));
        // Every transport names the canonical URI scheme it speaks; the router matches a
        // recipient's endpoint against CanHandle, which here also accepts plain http because
        // AllowedSchemes above says so.
        ctx.Narrator.Value("Transport.Scheme (canonical)", transport.Scheme);
        var router = new TransportRouter(new IDidCommTransport[] { transport });

        // Every outbound transport also carries an SSRF policy: a DID document's serviceEndpoint
        // is attacker-controlled text, so before bytes move, private/loopback/metadata addresses
        // are refused (on by default; allowlist trusted internal hosts instead of turning it off).
        ctx.Narrator.Step("The outbound SSRF guard: policy defaults, the address test, a refused endpoint.");
        var policy = httpOptions.OutboundEndpointPolicy!;
        ctx.Narrator.Value("Policy.BlockPrivateNetworks (default)", policy.BlockPrivateNetworks);
        ctx.Narrator.Value("Policy.ResolveDnsNames (default)", policy.ResolveDnsNames);
        var guard = new OutboundEndpointGuard(new OutboundEndpointPolicy());
        ctx.Narrator.Value("IsPrivateOrReserved(10.0.0.1)",
            OutboundEndpointGuard.IsPrivateOrReserved(System.Net.IPAddress.Parse("10.0.0.1")));
        try
        {
            guard.Validate(new Uri("https://169.254.169.254/latest/meta-data")); // the classic cloud-metadata SSRF target
            ctx.Narrator.Note("UNEXPECTED: the metadata endpoint was not refused.");
        }
        catch (TransportException ex)
        {
            ctx.Narrator.Value("Refused endpoint scheme (from the exception)", ex.Scheme);
            ctx.Narrator.Value("HttpStatusCode (null — refused before any request)", ex.HttpStatusCode);
        }
        // ConnectAsync is the same classification applied at socket-connect time — the HTTP
        // transport installs it as its dialer, which is what defeats DNS rebinding.
        Func<System.Net.DnsEndPoint, CancellationToken, ValueTask<System.Net.Sockets.Socket>> guardedDialer = guard.ConnectAsync;
        ctx.Narrator.Value("Guarded dialer wired", guardedDialer is not null);

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
        var sendOptions = new SendOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            ServiceEndpointOverride: endpoint);

        // SendOptions mirrors PackEncryptedOptions (recipients, sender, optional inner signature,
        // cipher, sender protection) plus the delivery-only extras — read it back like any record.
        ctx.Narrator.Value("SendOptions.Recipients", string.Join(", ", sendOptions.Recipients));
        ctx.Narrator.Value("SendOptions.From", sendOptions.From);
        ctx.Narrator.Value("SendOptions.SignFrom (null ⇒ no inner signature)", sendOptions.SignFrom);
        ctx.Narrator.Value("SendOptions.Enc (default cipher)", sendOptions.Enc);
        ctx.Narrator.Value("SendOptions.ProtectSender", sendOptions.ProtectSender);
        ctx.Narrator.Value("SendOptions.ServiceEndpointOverride", sendOptions.ServiceEndpointOverride);

        var sendResult = await aliceSender.SendAsync(message, sendOptions);

        ctx.Narrator.Value("TransportEndpoint", sendResult.EndpointUsed);
        ctx.Narrator.Value("HttpStatusCode", sendResult.Transport.HttpStatusCode);
        ctx.Narrator.Value("Accepted", sendResult.Transport.Accepted);
        // The result also hands back the exact envelope that went over the wire — log or persist
        // it for retry/audit without packing twice.
        ctx.Narrator.Value("Packed envelope bytes", sendResult.Packed.Message.Length);

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
