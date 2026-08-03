using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        // The responder side, by hand. What AddBuiltInProtocols wires for you decomposes into
        // four calls you can make yourself when you own the pipeline: read the queries off the
        // incoming message, construct the handler over the providers you choose (a provider is
        // one small class — the same seam AddFeatureProvider<T>() registers), let it answer, or
        // shape the disclose yourself with CreateDisclose.
        ctx.Narrator.Step("Responder by hand: ReadQueries → your own IFeatureProvider → CreateDisclose.");
        var query = DiscoverFeaturesApi.CreateQuery(ctx.Alice.Did, ctx.Bob.Did,
            new FeatureQuery { FeatureType = DiscoverFeaturesApi.FeatureTypeGoalCode, Match = "org.example.*" });
        var packedQuery = (await ctx.Client.PackEncryptedAsync(query, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did))).Message;
        var bobReceives = await ctx.Client.UnpackAsync(packedQuery);

        var queries = DiscoverFeaturesApi.ReadQueries(bobReceives.Message);
        ctx.Narrator.Value("ReadQueries", $"{queries.Count} query ({queries[0].FeatureType}: {queries[0].Match})");

        // Everything a handler learns arrives on ProtocolContext: the verified receive, the
        // thread's state, and the client/options/store to answer with. The dispatcher assembles
        // it per message; assembling it yourself is one constructor call.
        var options2 = ctx.ServiceProvider.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var threads = new InMemoryThreadStateStore();
        var context = new ProtocolContext(
            bobReceives, threads.GetOrCreate(bobReceives.Message.Id), ctx.Client, options2, threads);
        ctx.Narrator.Value("Context.Received.Message.Type", context.Received.Message.Type);
        ctx.Narrator.Value("Context.Thread.Thid", context.Thread.Thid[..8] + "…");
        ctx.Narrator.Value("Context.Client/Options/Threads wired",
            context.Client is not null && context.Options is not null && context.Threads is not null);

        var handler = new DiscoverFeaturesHandler(new IFeatureProvider[] { new CookbookGoalCodeProvider() });
        var disclose = await handler.HandleAsync(bobReceives.Message, context, CancellationToken.None);
        var answered = DiscoverFeaturesApi.ReadDisclosures(disclose!);
        ctx.Narrator.Value("Handler disclosed", $"{answered.Count} feature ({answered[0].Id})");

        // Or skip the handler and shape the disclose directly — same wire message.
        var manual = DiscoverFeaturesApi.CreateDisclose(ctx.Bob.Did, ctx.Alice.Did, query.Id,
            new FeatureDisclosure { FeatureType = DiscoverFeaturesApi.FeatureTypeGoalCode, Id = "org.example.lunch" });
        ctx.Narrator.Value("CreateDisclose.Thid == query.Id", manual.Thid == query.Id);

        // The initiator client is likewise just a class: DI does exactly this construction. (It
        // answers QueryFeaturesAsync by watching inbound disclosures, so a hand-built one must be
        // registered where your receive pipeline can feed it.)
        var manualInitiator = new DiscoverFeaturesClient(ctx.Client,
            NullLogger<DiscoverFeaturesClient>.Instance);
        ctx.Narrator.Value("Hand-built initiator ready", manualInitiator is not null);
    }

    /// <summary>
    /// A complete <see cref="IFeatureProvider"/>: one feature type, one answer. Register yours
    /// with <c>b.AddFeatureProvider&lt;T&gt;()</c> to advertise goal-codes, headers, or any
    /// app-specific capability through the same handler.
    /// </summary>
    private sealed class CookbookGoalCodeProvider : IFeatureProvider
    {
        /// <summary>Answers queries whose <c>feature-type</c> is <c>goal-code</c>.</summary>
        public string FeatureType => DiscoverFeaturesApi.FeatureTypeGoalCode;

        /// <summary>Disclose the goal codes this agent pursues, filtered by the query's match pattern.</summary>
        public IEnumerable<FeatureDisclosure> Disclose(string match, ProtocolContext context)
        {
            if (FeatureMatch.Matches(match, "org.example.lunch"))
                yield return new FeatureDisclosure { FeatureType = FeatureType, Id = "org.example.lunch" };
        }
    }
}
