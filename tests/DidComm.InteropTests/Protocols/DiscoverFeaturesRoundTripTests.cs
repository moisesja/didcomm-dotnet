using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// FR-PROTO-05a end-to-end (PR #51 review finding 1): a real two-agent Discover Features round-trip
/// over the shipped HTTP transport. Alice calls the DI-resolved
/// <see cref="DiscoverFeaturesClient.QueryFeaturesAsync"/>; her query is POSTed to Bob's
/// <c>MapDidCommEndpoint</c>; Bob's built-in handler produces the <c>disclose</c> and his endpoint
/// (with <see cref="DidCommReceiveOptions.AutoSendReplies"/>) forwards it out of band to Alice's own
/// endpoint; Alice's dispatcher hands it to her <see cref="DiscoverFeaturesClient"/> observer, which
/// completes the awaiting call — <strong>with no manual disclosure injection anywhere</strong>.
/// </summary>
public sealed class DiscoverFeaturesRoundTripTests
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Logs = new();

    [Fact]
    public async Task Alice_QueryFeaturesAsync_completes_from_Bobs_endpoint_over_HTTP_with_no_manual_injection()
    {
        Logs.Clear();

        // One shared secrets store + two peer identities whose DID docs are derived from the very
        // keys held here — so both agents can decrypt for each other (no fixture decoy keys).
        var secrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid) = await CreateTwoPeersAsync(secrets);

        // Late-bound handlers: each agent's outbound HTTP transport targets the OTHER agent's
        // in-process TestServer, whose handler only exists after it starts.
        var aliceInbox = new LateBoundHandler();
        var bobInbox = new LateBoundHandler();

        // Bob: responder — his endpoint auto-forwards the disclose to Alice; a fake service resolver
        // maps Alice's DID to her endpoint; loopback is permitted so the SSRF guard allows the
        // in-process TestServer address.
        var bob = await BuildAgentAsync("bob", secrets, outboundTo: aliceInbox, autoSendReplies: true,
            serviceResolver: new FixedServiceResolver(aliceDid, "http://localhost/didcomm"));

        // Alice: initiator — her endpoint dispatches the inbound disclose to her observer.
        var alice = await BuildAgentAsync("alice", secrets, outboundTo: bobInbox, autoSendReplies: false,
            serviceResolver: new FixedServiceResolver(bobDid, "http://localhost/didcomm"));

        aliceInbox.Target = alice.Server.CreateHandler();
        bobInbox.Target = bob.Server.CreateHandler();

        var discoverClient = alice.Services.GetRequiredService<DiscoverFeaturesClient>();

        IReadOnlyList<FeatureDisclosure> disclosures;
        try
        {
            disclosures = await discoverClient.QueryFeaturesAsync(
                from: aliceDid,
                to: bobDid,
                queries: new[] { new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" } },
                timeout: TimeSpan.FromSeconds(10),
                serviceEndpointOverride: new Uri("http://localhost/didcomm"));
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"round-trip failed: {ex.Message}\nLOGS:\n{string.Join("\n", Logs)}");
        }

        disclosures.Select(d => d.Id).Should().Contain(new[]
        {
            TrustPingApi.ProtocolUri,
            DidComm.Protocols.Empty.EmptyProtocol.ProtocolUri,
            DiscoverFeaturesApi.ProtocolUri,
        }, "Alice learns Bob's registered protocols by the real round-trip, not a hand-fed disclose");

        await alice.App.DisposeAsync();
        await bob.App.DisposeAsync();
    }

    private static async Task<(string AliceDid, string BobDid)> CreateTwoPeersAsync(InMemorySecretsResolver secrets)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
        });
        await using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();

        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var jwk in alice.Privates) secrets.Add(jwk);
        foreach (var jwk in bob.Privates) secrets.Add(jwk);
        return (alice.Did, bob.Did);
    }

    private sealed record Agent(WebApplication App, TestServer Server, IServiceProvider Services);

    private static async Task<Agent> BuildAgentAsync(
        string tag, InMemorySecretsResolver secrets, LateBoundHandler outboundTo, bool autoSendReplies,
        IServiceEndpointResolver serviceResolver)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new CollectingLoggerProvider(tag));
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddOptions<HttpTransportOptions>().Configure(o =>
        {
            o.AllowedSchemes = new[] { "http", "https" };
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            o.MaxRetryAttempts = 0;
        });
        builder.Services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => outboundTo);
        builder.Services.AddSingleton<ITransportRouter>(sp => new TransportRouter(new IDidCommTransport[]
        {
            new HttpDidCommTransport(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<HttpTransportOptions>>()),
        }));
        // Override the resolver UseNetDidResolver would register (TryAdd) with our fixed one.
        builder.Services.AddSingleton(serviceResolver);

        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
            b.Configure(o => o.OutboundEndpointPolicy.BlockPrivateNetworks = false); // permit the loopback TestServer
        });

        if (autoSendReplies)
            builder.Services.Configure<DidCommReceiveOptions>(o => o.AutoSendReplies = true);

        var app = builder.Build();
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm");
        await app.StartAsync();
        return new Agent(app, app.GetTestServer(), app.Services);
    }

    /// <summary>An <see cref="IServiceEndpointResolver"/> that maps one DID to one direct endpoint.</summary>
    private sealed class FixedServiceResolver(string did, string endpoint) : IServiceEndpointResolver
    {
        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string d, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(
                string.Equals(d, did, StringComparison.Ordinal)
                    ? new[] { new DidCommServiceInfo(endpoint, Array.Empty<string>(), Array.Empty<string>()) }
                    : Array.Empty<DidCommServiceInfo>());
    }

    /// <summary>Forwards HTTP to a target handler set after the target's server has started.</summary>
    private sealed class LateBoundHandler : HttpMessageHandler
    {
        public HttpMessageHandler? Target { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var invoker = new HttpMessageInvoker(Target ?? throw new InvalidOperationException("Target not set"), disposeHandler: false);
            return await invoker.SendAsync(request, ct);
        }
    }

    private sealed class CollectingLoggerProvider(string tag) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new L(tag, categoryName);
        public void Dispose() { }
        private sealed class L(string tag, string cat) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            {
                if (level >= LogLevel.Warning || cat.Contains("Dispatch"))
                    Logs.Enqueue($"[{tag}/{level}] {fmt(state, ex)}{(ex is null ? "" : " :: " + ex.Message)}");
            }
        }
    }
}
