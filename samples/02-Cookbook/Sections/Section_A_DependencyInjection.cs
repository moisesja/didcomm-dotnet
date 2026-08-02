using System.Text.Json.Nodes;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.TrustPing;
using DidComm.TestSupport;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// The one-time wiring every DIDComm application does at startup: register the library on an
/// <c>IServiceCollection</c>, tell it how to resolve DIDs, where private keys live, which
/// transports to speak, which protocols to answer, and tweak the process-wide options — then
/// resolve one ready-to-use <c>DidCommClient</c> from the container.
/// </summary>
/// <remarks>
/// <para>
/// Every other cookbook section leans on the shared container built in
/// <see cref="CookbookContext"/>. This section builds a container of its own, from scratch,
/// so the full builder chain is visible in one place: <c>UseNetDidResolver</c> (DID
/// resolution — did:key and did:peer by default, both offline-resolvable),
/// <c>UseSecretsResolver</c> (your private keys; the library never stores keys itself),
/// <c>UseHttpTransport</c> / <c>UseWebSocketTransport</c> (outbound delivery, picked by
/// endpoint scheme), <c>AddProtocol&lt;T&gt;</c> (which inbound protocols this agent
/// answers), and <c>Configure</c> (process-wide knobs like the receive size ceiling).
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>A</strong> (FR-API-08 — DI composition; FR-SEC-02 fail-fast).
/// </para>
/// </remarks>
public static class Section_A_DependencyInjection
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("A", "Dependency-injection setup");

        // The secrets resolver is YOUR side of the contract: the library asks it for private
        // keys by kid and never persists them (FR-SEC-01). Here we reuse the cookbook's two
        // test identities so the fresh container can act as both Alice and Bob.
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in ctx.Alice.Privates) secrets.Add(jwk);
        foreach (var jwk in ctx.Bob.Privates) secrets.Add(jwk);

        ctx.Narrator.Step("Register DIDComm on a fresh IServiceCollection via AddDidComm(b => ...).");
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();                 // net-did resolution: did:key + did:peer, fully offline
            b.UseSecretsResolver(secrets);         // consumer-supplied private keys (FR-SEC-01)
            b.UseHttpTransport(o => o.RequestTimeout = TimeSpan.FromSeconds(15));
            b.UseWebSocketTransport();
            b.AddProtocol<TrustPingHandler>();     // answer inbound trust pings (FR-PROTO-03/04)
            b.AddProtocol<DiscoverFeaturesHandler>();
            b.Configure(o => o.MaxReceiveBytes = 256 * 1024);  // process-wide knobs (FR-API-05/06)
        });

        // AddDidComm fails fast if you forget the secrets resolver or the DID resolver — the
        // two pieces the facade cannot invent for you (FR-SEC-02).
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DidCommClient>();
        ctx.Narrator.Value("Resolved DidCommClient", client.GetType().Name);
        ctx.Narrator.Value("Configured MaxReceiveBytes", provider.GetRequiredService<IOptions<DidCommOptions>>().Value.MaxReceiveBytes);

        // Use the freshly wired client once, end to end: Alice authcrypts to Bob and Bob
        // unpacks — proof the graph above is complete.
        ctx.Narrator.Step("Use the container-resolved client once: Alice → Bob authcrypt round-trip.");
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Wired up via AddDidComm."}""")!.AsObject())
            .Build();
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did));
        var unpacked = await client.UnpackAsync(packed.Message);
        ctx.Narrator.Value("Round-trip Authenticated", unpacked.Authenticated);
        ctx.Narrator.Value("Round-trip Body[content]", unpacked.Message.Body?["content"]?.GetValue<string>());

        ctx.Narrator.Note("Register DidCommClient once at startup and inject it everywhere — it is thread-safe (NFR-03).");
    }
}
