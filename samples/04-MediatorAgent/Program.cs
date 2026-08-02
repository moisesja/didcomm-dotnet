using System.IO;
using System.Text.Json.Nodes;
using DidComm.AspNetCore;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.MediatorAgent;

/// <summary>
/// Mediated routing over HTTP, end to end (PRD §14.3 sample 04, tasks O/P/Q): an ASP.NET Core
/// mediator receives forwards on <c>MapDidCommEndpoint</c> and relays them via
/// <c>ForwardProcessor</c> (Routing 2.0), while a console flow routes Alice → Mediator → Bob.
/// Bob's <c>did:peer:2</c> carries the mediator in its <c>routingKeys</c>, so Alice's
/// <c>SendAsync</c> discovers the route, forward-wraps, and posts to the mediator without any
/// routing code of her own. Also demonstrates the outbound-endpoint SSRF guard
/// (<c>DidCommOptions.OutboundEndpointPolicy</c>) refusing the loopback mediator by default,
/// and the explicit opt-in a local demo needs.
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02 — dynamic ports, loopback only, no fixed timing).
/// </summary>
public static class Program
{
    /// <summary>CLI entry point — writes to <see cref="Console.Out"/> and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MediatorAgent failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole flow, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // Each party owns its own keys — three separate secrets resolvers, exactly like three
        // separate processes would have. did:peer:2 resolves offline, so no party ever needs
        // another's private material, only its DID.
        var mediatorSecrets = new InMemorySecretsResolver();
        var bobSecrets = new InMemorySecretsResolver();
        var aliceSecrets = new InMemorySecretsResolver();

        // ── 1. The mediator: an ASP.NET Core app on a dynamic loopback port ──────────────
        narrator.Section("1", "Start the mediator (ASP.NET Core, dynamic port)");

        // The mediator's registry of who it delivers for: recipient DID → inbox URL. In a real
        // deployment the coordinate-mediation protocol populates this when a recipient enrolls;
        // that protocol is out of the messaging spec's scope, so this sample registers Bob
        // directly below.
        var deliveryRoutes = new Dictionary<string, Uri>(StringComparer.Ordinal);
        var relayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        WebApplication mediatorApp = null!;
        var mediatorBuilder = WebApplication.CreateBuilder();
        mediatorBuilder.Logging.ClearProviders(); // keep the sample's console output to the narration
        mediatorBuilder.WebHost.UseUrls("http://127.0.0.1:0"); // port 0 = let the OS pick a free one
        mediatorBuilder.Services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(mediatorSecrets)
            // The mediator relays onward over HTTP. Loopback plaintext http is a demo-only
            // choice — production endpoints are https (the transport's default AllowedSchemes).
            .UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
            })
            // The SSRF guard (see section 4) also pins the HTTP transport's connections at TCP
            // connect time. This process deliberately talks to loopback, so allow it here too.
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));

        // The mediator's inbox: MapDidCommEndpoint validates the content type (415), caps the
        // body (413), unpacks the envelope with the mediator's keys, and hands the result to
        // this callback (FR-TRN-07). For a mediator every inbound should be a Routing 2.0
        // forward — ForwardProcessor validates exactly that and extracts the onward payload.
        mediatorApp = mediatorBuilder.Build();
        mediatorApp.MapDidCommEndpoint("/didcomm", async (unpacked, ct) =>
        {
            narrator.Step($"[mediator] received an envelope (type = {unpacked.Message.Type}).");

            var mediatorClient = mediatorApp.Services.GetRequiredService<DidCommClient>();
            var keyService = mediatorApp.Services.GetRequiredService<IDidKeyService>();

            // MapDidCommEndpoint already peeled the encryption layer that was addressed to the
            // mediator; hand the decrypted forward back to ForwardProcessor (in packed
            // plaintext form) so it applies the Routing 2.0 rules: reject non-forwards, drop
            // please_ack (FR-ROUTE-07), and surface the next hop + the onward payload.
            var processor = new ForwardProcessor(mediatorClient, keyService, new ForwardProcessorOptions());
            var forwardPlain = await mediatorClient.PackPlaintextAsync(unpacked.Message, ct);
            var processed = await processor.ProcessAsync(forwardPlain, ct);

            var nextHop = StripFragment(processed.NextHop);
            narrator.Step($"[mediator] forward unwrapped — next hop {Trunc(nextHop, 56)}, onward payload {processed.OnwardPacked.Length} bytes.");

            if (!deliveryRoutes.TryGetValue(nextHop, out var inbox))
            {
                narrator.Note($"[mediator] no delivery route registered for {Trunc(nextHop, 56)} — dropping.");
                return;
            }

            // Relay the still-encrypted inner envelope to Bob's inbox. The mediator never saw
            // the plaintext content — only Bob's keys can open what it just relayed.
            var router = mediatorApp.Services.GetRequiredService<ITransportRouter>();
            var result = await router.SendAsync(
                new TransportRequest(inbox, processed.OnwardPacked, ForwardConstants.PayloadMediaType), ct);
            narrator.Step($"[mediator] relayed to {inbox} (HTTP {result.HttpStatusCode}).");
            relayed.TrySetResult(nextHop);
        });
        await mediatorApp.StartAsync();
        var mediatorUrl = mediatorApp.Urls.Single();
        var mediatorInbox = new Uri(mediatorUrl + "/didcomm");
        narrator.Value("Mediator inbox", mediatorInbox);

        try
        {
            // ── 2. Identities: Bob's DID carries the mediator as its route ───────────────
            narrator.Section("2", "Mint identities — Bob's did:peer advertises the mediator via routingKeys");

            var manager = mediatorApp.Services.GetRequiredService<IDidManager>();
            var keyGen = mediatorApp.Services.GetRequiredService<IKeyGenerator>();
            var crypto = mediatorApp.Services.GetRequiredService<ICryptoProvider>();

            var mediator = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
            foreach (var key in mediator.Privates) mediatorSecrets.Add(key);
            narrator.Step($"Minted mediator = {Trunc(mediator.Did, 64)}");

            // The mediator's X25519 key-agreement kid is the routing key: senders will add one
            // forward layer encrypted to this key (FR-ROUTE-02).
            var mediatorRoutingKid = mediator.Privates
                .First(k => string.Equals(k.Crv, "X25519", StringComparison.Ordinal)).Kid
                ?? throw new InvalidOperationException("Mediator X25519 key has no kid.");

            // Bob's DID embeds a DIDCommMessaging service: "reach me at the mediator's inbox,
            // and wrap for this routing key first". Everything a sender needs to route to Bob
            // travels inside Bob's DID string — no directory lookup, no side channel.
            var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto,
                new DidCommServiceSpec(
                    EndpointUriOrDid: mediatorInbox.ToString(),
                    RoutingKeys: new[] { mediatorRoutingKid },
                    Accept: new[] { "didcomm/v2" }));
            foreach (var key in bob.Privates) bobSecrets.Add(key);
            narrator.Step($"Minted bob = {Trunc(bob.Did, 64)}");
            narrator.Value("Bob's routingKeys[0]", Trunc(mediatorRoutingKid, 64));

            // ── 3. Bob: his own ASP.NET Core inbox on a second dynamic port ──────────────
            narrator.Section("3", "Start Bob's agent (his own receive endpoint)");

            var bobReceived = new TaskCompletionSource<UnpackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bobBuilder = WebApplication.CreateBuilder();
            bobBuilder.Logging.ClearProviders();
            bobBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            bobBuilder.Services.AddDidComm(b => b
                .UseNetDidResolver()
                .UseSecretsResolver(bobSecrets)
                // Telling the client which DIDs are "us" lets unpack report whether the message
                // was actually addressed to this agent (FR-CONSIST-04, advisory).
                .Configure(o => o.OwnIdentifiers = new[] { bob.Did }));

            var bobApp = bobBuilder.Build();
            bobApp.MapDidCommEndpoint("/didcomm", (unpacked, _) =>
            {
                bobReceived.TrySetResult(unpacked);
                return Task.CompletedTask;
            });
            await bobApp.StartAsync();
            try
            {
                var bobInbox = new Uri(bobApp.Urls.Single() + "/didcomm");
                deliveryRoutes[bob.Did] = bobInbox; // the "coordinate-mediation" stand-in
                narrator.Value("Bob inbox (known only to the mediator)", bobInbox);

                // ── 4. Alice: the SSRF guard, then the routed send ───────────────────────
                narrator.Section("4", "The outbound-endpoint guard — why the default refuses this demo");

                // Alice discovers the mediator's inbox URL from BOB'S DID document — data a
                // counterparty chose. A malicious DID could point that endpoint at a cloud
                // metadata service or an internal host, turning Alice's agent into an SSRF
                // proxy. DidCommOptions.OutboundEndpointPolicy therefore blocks private,
                // loopback, link-local, and metadata destinations by default; this sample runs
                // entirely on 127.0.0.1, so the default policy MUST refuse it.
                var alice = await MintAliceAsync(aliceSecrets);

                var strictAlice = BuildAliceClient(aliceSecrets, allowLoopback: false);
                await using (strictAlice.Provider)
                {
                    try
                    {
                        await strictAlice.Client.SendAsync(
                            Hello(alice.Did, bob.Did, "This must not leave the guard."),
                            new SendOptions(Recipients: new[] { bob.Did }, From: alice.Did));
                        narrator.Note("UNEXPECTED: the default policy did not refuse the loopback endpoint.");
                    }
                    catch (TransportException ex)
                    {
                        narrator.Step("Default policy refused, as designed:");
                        narrator.Note(ex.Message);
                    }
                }

                // The opt-in is explicit and narrow: allow exactly this loopback host, keep the
                // rest of the policy intact. A private-network deployment would allowlist its
                // known mediator hosts the same way rather than disabling the guard wholesale.
                narrator.Section("5", "Alice sends — route discovery, forward wrapping, HTTP, relay, delivery");
                var routedAlice = BuildAliceClient(aliceSecrets, allowLoopback: true);
                await using (routedAlice.Provider)
                {
                    // One call does all of it: resolve Bob's DIDCommMessaging service, wrap a
                    // Routing 2.0 forward for the mediator's routing key (FR-ROUTE-02), pick
                    // the HTTP transport by URI scheme (FR-TRN-01), and POST to the mediator.
                    var sent = await routedAlice.Client.SendAsync(
                        Hello(alice.Did, bob.Did, "Routed through the mediator."),
                        new SendOptions(Recipients: new[] { bob.Did }, From: alice.Did));

                    narrator.Value("Endpoint used (the mediator, from Bob's DID)", sent.EndpointUsed);
                    narrator.Value("Transport HTTP status", sent.Transport.HttpStatusCode);

                    await AwaitOrFailAsync(relayed.Task, "mediator relay");
                    var delivered = await AwaitOrFailAsync(bobReceived.Task, "delivery to Bob");

                    narrator.Step("[bob] unpacked the relayed envelope as if no mediator had been involved:");
                    narrator.Value("[bob] Content", delivered.Message.Body?["content"]?.GetValue<string>());
                    narrator.Value("[bob] Authenticated", delivered.Authenticated);
                    narrator.Value("[bob] From == alice", delivered.Message.From == alice.Did);
                    narrator.Value("[bob] RecipientAddressing", delivered.RecipientAddressing);
                    narrator.Note("The mediator saw only an anoncrypt forward addressed to its routing key — never the content, never Alice's authenticated identity.");
                }
            }
            finally
            {
                await bobApp.StopAsync();
                await bobApp.DisposeAsync();
            }
        }
        finally
        {
            await mediatorApp.StopAsync();
            await mediatorApp.DisposeAsync();
        }
    }

    /// <summary>Alice's client container: her secrets + the HTTP transport, with or without the loopback allowlist entry.</summary>
    private static (ServiceProvider Provider, DidCommClient Client) BuildAliceClient(
        InMemorySecretsResolver aliceSecrets, bool allowLoopback)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver()
                .UseSecretsResolver(aliceSecrets)
                .UseHttpTransport(o =>
                {
                    o.AllowedSchemes = new[] { "http", "https" }; // loopback demo; production stays https-only
                    o.MaxRetryAttempts = 0;
                });
            if (allowLoopback)
            {
                // The narrow opt-in: this exact host, nothing else. The rest of the policy
                // (private ranges, link-local, metadata IPs) keeps blocking.
                b.Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1"));
            }
        });
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<DidCommClient>());
    }

    private static async Task<PeerIdentity> MintAliceAsync(InMemorySecretsResolver aliceSecrets)
    {
        // Alice mints from her own container — parties don't share infrastructure, only DIDs.
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(aliceSecrets));
        await using var sp = services.BuildServiceProvider();
        var alice = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(),
            sp.GetRequiredService<IKeyGenerator>(),
            sp.GetRequiredService<ICryptoProvider>());
        foreach (var key in alice.Privates) aliceSecrets.Add(key);
        return alice;
    }

    private static Message Hello(string from, string to, string content) => new MessageBuilder()
        .WithType("https://didcomm.org/basicmessage/2.0/message")
        .WithFrom(from)
        .WithTo(to)
        .WithBody(new JsonObject { ["content"] = content })
        .Build();

    /// <summary>Await a signal with a hard upper bound — a hang fails loudly instead of forever (FR-DX-02).</summary>
    private static async Task<T> AwaitOrFailAsync<T>(Task<T> task, string what)
    {
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (winner != task)
            throw new TimeoutException($"Timed out waiting for {what}.");
        return await task;
    }

    private static string StripFragment(string didUrl)
    {
        var hash = didUrl.IndexOf('#');
        return hash < 0 ? didUrl : didUrl[..hash];
    }

    private static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";
}
