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
        ctx.Narrator.Section("T", "Discover Features (queries → disclose)");

        var dispatcher = ctx.ServiceProvider.GetRequiredService<ProtocolDispatcher>();
        var options = ctx.ServiceProvider.GetRequiredService<IOptions<DidCommOptions>>().Value;

        // Alice asks two questions in one shot: "list every PIURI under didcomm.org" and
        // "what's your max_receive_bytes?". Both pack into one queries message.
        var query = DiscoverFeaturesApi.CreateQuery(ctx.Alice.Did, ctx.Bob.Did,
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeProtocol, Match = "https://didcomm.org/*" },
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeConstraint, Match = DiscoverFeaturesApi.ConstraintMaxReceiveBytes });

        ctx.Narrator.Step("Alice packs a Discover Features 'queries' message with 2 queries.");
        var packed = (await ctx.Client.PackEncryptedAsync(query, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did))).Message;

        var unpacked = await ctx.Client.UnpackAsync(packed);
        var outcome = await dispatcher.DispatchAsync(unpacked, ctx.Client, options);
        ctx.Narrator.Value("DispatchResult", outcome.Result);
        ctx.Narrator.Value("Reply.Type", outcome.Reply?.Type);
        ctx.Narrator.Value("Reply.Thid == query.Id", outcome.Reply?.Thid == query.Id);

        var disclosures = DiscoverFeaturesApi.ReadDisclosures(outcome.Reply!);
        ctx.Narrator.Value("Disclosure count", disclosures.Count);
        foreach (var d in disclosures)
        {
            var value = d.Value is long v ? $" (value={v})" : string.Empty;
            ctx.Narrator.Value($"- {d.FeatureType}", $"{d.Id}{value}");
        }
        ctx.Narrator.Note("Unrecognized feature-types are silently ignored per FR-PROTO-05; empty disclosures ≠ \"unsupported\".");
    }
}
