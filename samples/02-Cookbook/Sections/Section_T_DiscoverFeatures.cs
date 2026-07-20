using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Asks Bob "what do you support?" via a Discover Features 2.0 <c>queries</c> message and
/// inspects the <c>disclose</c> reply the dispatcher produces. Two queries in one round trip:
/// (1) the protocol wildcard <c>https://didcomm.org/*</c> — every spec protocol Bob has
/// registered; (2) the <c>max_receive_bytes</c> constraint — Bob's currently-configured
/// payload cap so Alice can negotiate sizes before tripping the FR-API-06 413 path.
/// </summary>
/// <remarks>
/// <para>
/// The handler that ships with <c>AddBuiltInProtocols()</c> consults two <see cref="IFeatureProvider"/>s:
/// <c>ProtocolFeatureProvider</c> (reflects the registry) and
/// <c>MaxReceiveBytesConstraintProvider</c> (advertises <c>DidCommOptions.MaxReceiveBytes</c>).
/// Consumers add more providers via <c>b.AddFeatureProvider&lt;T&gt;()</c> when they want to
/// expose goal-codes / custom headers / app-specific constraints.
/// </para>
/// <para>Maps to PRD §14.2 task <strong>T</strong> (FR-PROTO-05).</para>
/// </remarks>
public static class Section_T_DiscoverFeatures
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("T", "Discover Features (initiator: ask, then await the answer)");

        // The initiator client is the requester side of Discover Features: you call QueryFeaturesAsync
        // and it returns the peer's disclosures once they arrive. It is registered by
        // AddBuiltInProtocols(), so resolve it from DI. In a real app its send goes over your HTTP
        // transport and the peer's `disclose` arrives out-of-band at your own receive endpoint —
        // here the cookbook's in-process loopback transport plays the peer so the sample needs no
        // network. The endpoint override just points the send at that loopback.
        var initiator = ctx.ServiceProvider.GetRequiredService<DiscoverFeaturesClient>();

        // Alice asks two questions in one shot: "list every PIURI under didcomm.org" and
        // "what's your max_receive_bytes?" — then awaits Bob's answer.
        ctx.Narrator.Step("Alice calls QueryFeaturesAsync and awaits Bob's disclose.");
        var disclosures = await initiator.QueryFeaturesAsync(
            from: ctx.Alice.Did,
            to: ctx.Bob.Did,
            queries: new[]
            {
                new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/*" },
                new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeConstraint, Match = DiscoverFeaturesApi.ConstraintMaxReceiveBytes },
            },
            timeout: TimeSpan.FromSeconds(10),
            serviceEndpointOverride: new Uri("loopback://cookbook/didcomm"));

        ctx.Narrator.Value("Disclosure count", disclosures.Count);
        foreach (var d in disclosures)
        {
            var value = d.Value is long v ? $" (value={v})" : string.Empty;
            ctx.Narrator.Value($"- {d.FeatureType}", $"{d.Id}{value}");
        }
        ctx.Narrator.Note("Only an authenticated disclose from the queried peer completes the call; a timeout throws. Empty disclosures ≠ \"unsupported\".");
    }
}
