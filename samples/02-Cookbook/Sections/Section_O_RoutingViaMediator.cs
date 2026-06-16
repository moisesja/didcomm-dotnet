using System.Text;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Shows how the facade automatically wraps a forward message when the recipient's DID
/// publishes a <c>DIDCommMessaging</c> service with <c>routingKeys</c> (PRD §14.2 task O,
/// FR-ROUTE-02 / FR-ROUTE-05). Alice pulls a single switch — <c>Forward = true</c> — and
/// the library handles route resolution, reverse-order anoncrypt wrapping, and surfacing
/// the transport endpoint URI on the result. A mediator then unwraps the outer forward
/// and the original payload arrives at Bob unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The cookbook keeps every other section using the three shared <c>did:peer</c> identities
/// from <see cref="CookbookContext"/>. <c>did:peer:2</c> does not advertise routing services
/// in net-did's current builder, so this section creates one additional identity — a
/// mediator — and patches Bob's "perceived" routing service via a tiny inline
/// <see cref="IServiceEndpointResolver"/>. The pack pipeline is otherwise the same one
/// every other section uses; only the section-local <see cref="DidCommClient"/> is bound to
/// the patched routing.
/// </para>
/// </remarks>
public static class Section_O_RoutingViaMediator
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("O", "Routing via a mediator (automatic forward wrapping)");

        // Mint a mediator identity inside the section. This is the party Alice's outermost
        // forward layer will be anoncrypted to; the mediator decrypts it and emits the
        // payload onward to Bob.
        var sp = ctx.ServiceProvider;
        var mediator = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(),
            sp.GetRequiredService<IKeyGenerator>(),
            sp.GetRequiredService<ICryptoProvider>());

        // The shared secrets resolver already holds Alice / Bob / Alice2 keys. Add the
        // mediator's privates so it can decrypt the forward layer addressed to it. The
        // CookbookContext always registers an InMemorySecretsResolver, so the cast is safe.
        var secrets = sp.GetRequiredService<ISecretsResolver>();
        var inMemorySecrets = (InMemorySecretsResolver)secrets;
        foreach (var jwk in mediator.Privates)
            inMemorySecrets.Add(jwk);

        ctx.Narrator.Step($"Minted mediator = {Truncate(mediator.Did)}");

        // Pick the mediator's X25519 verification key as the routing key.
        var mediatorKx = mediator.Privates.First(j => string.Equals(j.Crv, "X25519", StringComparison.Ordinal));
        var mediatorKxKid = mediatorKx.Kid ?? throw new InvalidOperationException("Mediator X25519 key has no kid.");
        const string mediatorEndpoint = "https://mediator.example/inbox";

        // Build a section-local IServiceEndpointResolver that claims Bob has the mediator
        // configured. A real deployment publishes this in Bob's DID Document; for the
        // cookbook the in-process adapter keeps the dependency surface tiny.
        var routingResolver = new PerSectionRoutingResolver(
            recipient: ctx.Bob.Did,
            endpoint: mediatorEndpoint,
            routingKeys: new[] { mediatorKxKid });

        var keyService = sp.GetRequiredService<IDidKeyService>();
        var sectionClient = new DidCommClient(secrets, keyService, routingResolver, new DidCommOptions());

        ctx.Narrator.Step("Alice packs an authcrypt message for Bob with Forward = true.");
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .WithBody(JsonNode.Parse("""{"content":"Routed through the mediator."}""")!.AsObject())
            .Build();

        var packed = await sectionClient.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice.Did,
            Forward: true));

        ctx.Narrator.Value("ServiceEndpoint", packed.ServiceEndpoint);
        ctx.Narrator.Value("FallbackServiceEndpoints", packed.FallbackServiceEndpoints.Count);
        ctx.Narrator.Value("OutermostEnvelopeBytes", packed.Message.Length);

        ctx.Narrator.Step("Mediator receives, unwraps via ForwardProcessor, and reads body.next.");
        var processor = new ForwardProcessor(sectionClient, keyService, new ForwardProcessorOptions());
        var processed = await processor.ProcessAsync(packed.Message);

        ctx.Narrator.Value("NextHop", processed.NextHop);
        ctx.Narrator.Value("OnwardPayloadBytes", processed.OnwardPacked.Length);

        ctx.Narrator.Step("Bob unpacks the onward payload as if no mediator had been involved.");
        var bobUnpack = await sectionClient.UnpackAsync(Encoding.UTF8.GetString(processed.OnwardPacked));
        ctx.Narrator.Value("Authenticated",  bobUnpack.Authenticated);
        ctx.Narrator.Value("From",           bobUnpack.Message.From);
        ctx.Narrator.Value("ContentMatched", bobUnpack.Message.Body?["content"]?.GetValue<string>());
    }

    private static string Truncate(string did) => did.Length <= 64 ? did : did[..61] + "…";

    /// <summary>
    /// Inline <see cref="IServiceEndpointResolver"/> that recognises one recipient DID and
    /// returns a single <see cref="DidCommServiceInfo"/> pointing at a fixed endpoint plus
    /// one routing key. Everyone else is reported as having no service entry. Production
    /// hosts would publish this through their DID Document; the cookbook uses an inline
    /// adapter to keep the runnable example free of external fixtures.
    /// </summary>
    private sealed class PerSectionRoutingResolver : IServiceEndpointResolver
    {
        private readonly string _recipient;
        private readonly DidCommServiceInfo _info;

        public PerSectionRoutingResolver(string recipient, string endpoint, IReadOnlyList<string> routingKeys)
        {
            _recipient = recipient;
            _info = new DidCommServiceInfo(endpoint, routingKeys, Accept: new[] { "didcomm/v2" });
        }

        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(
                string.Equals(did, _recipient, StringComparison.Ordinal) ? new[] { _info } : Array.Empty<DidCommServiceInfo>());
    }

}
