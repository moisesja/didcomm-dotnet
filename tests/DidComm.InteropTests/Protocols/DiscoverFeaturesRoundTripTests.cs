using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Protocols;
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
/// FR-PROTO-05a end-to-end: a real two-agent Discover Features round-trip over the shipped HTTP
/// transport. Alice calls the DI-resolved <see cref="DiscoverFeaturesClient.QueryFeaturesAsync"/>;
/// her query is POSTed to Bob's <c>MapDidCommEndpoint</c>; Bob's built-in handler produces the
/// <c>disclose</c> and his endpoint's <c>onReceive</c> callback (APP code — the library ships no
/// automatic egress) sends it out of band to Alice's endpoint, bound to the authenticated inbound
/// sender; Alice's dispatcher hands it to her inline <c>IInboundCorrelator</c>, which completes the
/// awaiting call — <strong>with no manual disclosure injection anywhere</strong>.
/// </summary>
public sealed class DiscoverFeaturesRoundTripTests
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Logs = new();

    [Fact]
    public async Task Alice_QueryFeaturesAsync_completes_from_Bobs_endpoint_over_HTTP_with_no_manual_injection()
    {
        Logs.Clear();

        // SEPARATE per-agent secret stores: Alice holds only her private keys, Bob only his. Each
        // still resolves the other's public keys via did:peer (self-resolving). This is faithful — a
        // single shared store could mask wrong-recipient routing since either host could decrypt for
        // either identity. Two peer identities whose DID docs derive from the very keys held (no
        // fixture decoy keys).
        var aliceSecrets = new InMemorySecretsResolver();
        var bobSecrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid) = await CreateTwoPeersAsync(aliceSecrets, bobSecrets);

        // Late-bound handlers: each agent's outbound HTTP transport targets the OTHER agent's
        // in-process TestServer, whose handler only exists after it starts.
        var aliceInbox = new LateBoundHandler();
        var bobInbox = new LateBoundHandler();

        // Bob: responder — his endpoint (app code) sends the disclose back to the authenticated sender;
        // a fake service resolver maps Alice's DID to her endpoint; loopback is permitted so the SSRF
        // guard allows the in-process TestServer address.
        var bob = await BuildAgentAsync("bob", bobSecrets, outboundTo: aliceInbox, isResponder: true,
            serviceResolver: new FixedServiceResolver(aliceDid, "http://localhost/didcomm"));

        // Alice: initiator — her endpoint dispatches the inbound disclose to her inline correlator.
        var alice = await BuildAgentAsync("alice", aliceSecrets, outboundTo: bobInbox, isResponder: false,
            serviceResolver: new FixedServiceResolver(bobDid, "http://localhost/didcomm"));

        aliceInbox.Target = alice.Server.CreateHandler();
        bobInbox.Target = bob.Server.CreateHandler();

        try
        {
            var discoverClient = alice.Services.GetRequiredService<DiscoverFeaturesClient>();

            IReadOnlyList<FeatureDisclosure> disclosures;
            try
            {
                disclosures = await discoverClient.QueryFeaturesAsync(
                    from: aliceDid,
                    to: bobDid,
                    queries: new[] { new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" } },
                    timeout: TimeSpan.FromSeconds(30),
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
        }
        finally
        {
            await alice.App.DisposeAsync();
            await bob.App.DisposeAsync();
        }
    }

    private static async Task<(string AliceDid, string BobDid)> CreateTwoPeersAsync(
        InMemorySecretsResolver aliceSecrets, InMemorySecretsResolver bobSecrets)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        await using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();

        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var jwk in alice.Privates) aliceSecrets.Add(jwk); // Alice holds only her own keys
        foreach (var jwk in bob.Privates) bobSecrets.Add(jwk);     // Bob holds only his own keys
        return (alice.Did, bob.Did);
    }

    private sealed record Agent(WebApplication App, TestServer Server, IServiceProvider Services);

    private static async Task<Agent> BuildAgentAsync(
        string tag, InMemorySecretsResolver secrets, LateBoundHandler outboundTo, bool isResponder,
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

        var app = builder.Build();
        app.UseRouting();
        if (isResponder)
        {
            // The responder (Bob) delivers its disclose out of band with APP code — the library does
            // not auto-send. This models the correct, safe pattern: dispatch, then (only for an
            // AUTHENTICATED inbound) send the reply back to the authenticated sender, as the identity
            // that decrypted it. There is no library egress that trusts an attacker-controlled from.
            app.MapDidCommEndpoint("/didcomm", async (unpacked, ct) =>
            {
                var client = app.Services.GetRequiredService<DidCommClient>();
                var dispatcher = app.Services.GetRequiredService<ProtocolDispatcher>();
                var options = app.Services.GetRequiredService<IOptions<DidCommOptions>>().Value;

                var outcome = await dispatcher.DispatchAsync(unpacked, client, options, ct);
                if (outcome is { Result: DispatchResult.ReplyProduced, Reply: { From: { Length: > 0 } replyFrom } reply }
                    && unpacked.Authenticated
                    && unpacked.Message.From is { Length: > 0 } authenticatedSender)
                {
                    await client.SendAsync(reply, new SendOptions(Recipients: new[] { authenticatedSender }, From: replyFrom), ct);
                }
            });
        }
        else
        {
            // The initiator (Alice) just dispatches inbound messages; the inline Discover Features
            // correlator completes her awaiting QueryFeaturesAsync when the disclose arrives.
            app.MapDidCommEndpoint("/didcomm");
        }
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
