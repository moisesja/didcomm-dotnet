using DidComm.AspNetCore;
using DidComm.Consistency;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using AwesomeAssertions;
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
            serviceResolver: new FixedServiceResolver(aliceDid, "http://localhost/didcomm"),
            localDid: bobDid, expectedPeerDid: aliceDid);

        // Alice: initiator — her endpoint dispatches the inbound disclose to her inline correlator.
        var alice = await BuildAgentAsync("alice", aliceSecrets, outboundTo: bobInbox, isResponder: false,
            serviceResolver: new FixedServiceResolver(bobDid, "http://localhost/didcomm"),
            localDid: aliceDid, expectedPeerDid: bobDid);

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

    [Fact]
    public async Task HTTP_responder_uses_the_real_decrypting_tenant_when_plaintext_to_puts_another_tenant_first()
    {
        var aliceSecrets = new InMemorySecretsResolver();
        var sharedResponderSecrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid, carolDid) = await CreateThreePeersAsync(aliceSecrets, sharedResponderSecrets);
        var aliceInbox = new LateBoundHandler();
        var bobInbox = new LateBoundHandler();
        var receivedReply = new TaskCompletionSource<UnpackResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var bob = await BuildAgentAsync("bob", sharedResponderSecrets, outboundTo: aliceInbox, isResponder: true,
            serviceResolver: new FixedServiceResolver(aliceDid, "http://localhost/didcomm"),
            localDid: bobDid, expectedPeerDid: aliceDid);
        var alice = await BuildAgentAsync("alice", aliceSecrets, outboundTo: bobInbox, isResponder: false,
            serviceResolver: new FixedServiceResolver(bobDid, "http://localhost/didcomm"),
            localDid: aliceDid, expectedPeerDid: bobDid, captureInbound: receivedReply);
        aliceInbox.Target = alice.Server.CreateHandler();
        bobInbox.Target = bob.Server.CreateHandler();

        try
        {
            // Bob's process hosts Bob and Carol. The attacker-controlled plaintext puts Carol first,
            // while the real JWE recipient is Bob. Neither the handler nor the app egress policy may
            // turn that ordering into a Carol-authenticated response.
            var query = DiscoverFeaturesApi.CreateQuery(
                aliceDid,
                bobDid,
                new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" });
            query.To = new[] { carolDid, bobDid };

            var client = alice.Services.GetRequiredService<DidCommClient>();
            await client.SendAsync(query, new SendOptions(
                Recipients: new[] { bobDid },
                From: aliceDid,
                ServiceEndpointOverride: new Uri("http://localhost/didcomm")));

            var reply = await receivedReply.Task.WaitAsync(TimeSpan.FromSeconds(30));
            reply.Encrypted.Should().BeTrue();
            reply.Authenticated.Should().BeTrue();
            reply.Message.From.Should().Be(bobDid);
            reply.Message.To.Should().Equal(aliceDid);
            reply.SenderKid.Should().StartWith($"{bobDid}#");
            reply.RecipientKid.Should().StartWith($"{aliceDid}#");
        }
        finally
        {
            await alice.App.DisposeAsync();
            await bob.App.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Out_of_band_reply_from_Bobs_endpoint_reaches_Alice_on_the_thid_her_request_declared(bool trustPing, bool declaresThid)
    {
        // #138, the plain outbound path (no return route): Bob's app sends the built-in responder's
        // reply back over HTTP. threading.md L15: a request without a thid has its id as its thid;
        // L17: the reply MUST carry the request's thid, so it is never answered on the id instead.
        var aliceSecrets = new InMemorySecretsResolver();
        var bobSecrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid) = await CreateTwoPeersAsync(aliceSecrets, bobSecrets);
        var aliceInbox = new LateBoundHandler();
        var bobInbox = new LateBoundHandler();
        var receivedReply = new TaskCompletionSource<UnpackResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var bob = await BuildAgentAsync("bob", bobSecrets, outboundTo: aliceInbox, isResponder: true,
            serviceResolver: new FixedServiceResolver(aliceDid, "http://localhost/didcomm"),
            localDid: bobDid, expectedPeerDid: aliceDid);
        var alice = await BuildAgentAsync("alice", aliceSecrets, outboundTo: bobInbox, isResponder: false,
            serviceResolver: new FixedServiceResolver(bobDid, "http://localhost/didcomm"),
            localDid: aliceDid, expectedPeerDid: bobDid, captureInbound: receivedReply);
        aliceInbox.Target = alice.Server.CreateHandler();
        bobInbox.Target = bob.Server.CreateHandler();

        try
        {
            var declared = declaresThid ? "thr-" + Guid.NewGuid().ToString("N") : null;
            var request = trustPing
                ? TrustPingApi.CreatePing(aliceDid, bobDid)
                : DiscoverFeaturesApi.CreateQuery(aliceDid, bobDid, new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" });
            request.Thid = declared;

            var client = alice.Services.GetRequiredService<DidCommClient>();
            await client.SendAsync(request, new SendOptions(
                Recipients: new[] { bobDid },
                From: aliceDid,
                ServiceEndpointOverride: new Uri("http://localhost/didcomm")));

            // Hang guard only: Bob's reply is certain with a correct product.
            var reply = await receivedReply.Task.WaitAsync(TimeSpan.FromSeconds(30));
            reply.Authenticated.Should().BeTrue();
            reply.Message.Type.Should().Be(trustPing ? TrustPingApi.ResponseType : DiscoverFeaturesApi.DiscloseType);
            reply.Message.From.Should().Be(bobDid);
            reply.Message.Thid.Should().Be(declared ?? request.Id);
            if (declared is not null)
                reply.Message.Thid.Should().NotBe(request.Id, "a reply on the request's id is not in the thread the request declared (threading.md L17)");
            reply.Message.Pthid.Should().BeNull();
        }
        finally
        {
            await alice.App.DisposeAsync();
            await bob.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueryFeaturesAsync_completes_over_HTTP_when_the_responder_answers_on_the_query_id_in_upper_case()
    {
        // DIDComm v2.1 threading.md L9/L15: ids, and so thids, compare ignoring letter case. Bob is a
        // peer that writes the thread in upper case: his app answers with a disclose it builds by hand
        // whose thid is Alice's query id upper-cased. Her real QueryFeaturesAsync completes with it.
        var aliceSecrets = new InMemorySecretsResolver();
        var bobSecrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid) = await CreateTwoPeersAsync(aliceSecrets, bobSecrets);
        var aliceInbox = new LateBoundHandler();
        var bobInbox = new LateBoundHandler();
        string? queryId = null;
        string? answeredOn = null;

        var bob = await BuildAgentAsync("bob", bobSecrets, outboundTo: aliceInbox, isResponder: true,
            serviceResolver: new FixedServiceResolver(aliceDid, "http://localhost/didcomm"),
            localDid: bobDid, expectedPeerDid: aliceDid,
            replyByHand: builtIn =>
            {
                queryId = builtIn.Thid;
                answeredOn = builtIn.Thid!.ToUpperInvariant();
                return DiscoverFeaturesApi.CreateDisclose(from: builtIn.From!, to: builtIn.To![0], thid: answeredOn,
                    disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "https://example.org/hand-built/1.0" });
            });
        // Alice's query ids hold letters (LetterIds), so the upper-cased answer is certain to differ.
        var alice = await BuildAgentAsync("alice", aliceSecrets, outboundTo: bobInbox, isResponder: false,
            serviceResolver: new FixedServiceResolver(bobDid, "http://localhost/didcomm"),
            localDid: aliceDid, expectedPeerDid: bobDid, queryIds: new LetterIds("query"));
        aliceInbox.Target = alice.Server.CreateHandler();
        bobInbox.Target = bob.Server.CreateHandler();

        try
        {
            var disclosures = await alice.Services.GetRequiredService<DiscoverFeaturesClient>().QueryFeaturesAsync(
                from: aliceDid,
                to: bobDid,
                queries: new[] { new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" } },
                timeout: TimeSpan.FromSeconds(30),
                serviceEndpointOverride: new Uri("http://localhost/didcomm"));

            queryId.Should().Be(LetterIds.Id("query"), "precondition: Alice's query id is the pinned one");
            answeredOn.Should().NotBe(queryId, "precondition: the answer names the query id in other letter case");
            disclosures.Select(d => d.Id).Should().Equal("https://example.org/hand-built/1.0");
        }
        finally
        {
            await alice.App.DisposeAsync();
            await bob.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueryFeaturesAsync_is_not_completed_over_HTTP_by_a_disclose_on_a_thread_that_differs_in_more_than_case()
    {
        // Control for the test above: the upper-cased query id with one more character is another
        // thread. The disclose is delivered (Bob's send to Alice's endpoint returned, so her dispatcher
        // ran it), and the query ends by its own short deadline.
        var aliceSecrets = new InMemorySecretsResolver();
        var bobSecrets = new InMemorySecretsResolver();
        var (aliceDid, bobDid) = await CreateTwoPeersAsync(aliceSecrets, bobSecrets);
        var aliceInbox = new LateBoundHandler();
        var bobInbox = new LateBoundHandler();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var bob = await BuildAgentAsync("bob", bobSecrets, outboundTo: aliceInbox, isResponder: true,
            serviceResolver: new FixedServiceResolver(aliceDid, "http://localhost/didcomm"),
            localDid: bobDid, expectedPeerDid: aliceDid,
            replyByHand: builtIn => DiscoverFeaturesApi.CreateDisclose(from: builtIn.From!, to: builtIn.To![0], thid: builtIn.Thid!.ToUpperInvariant() + "0",
                disclosures: new FeatureDisclosure { FeatureType = "protocol", Id = "https://example.org/other-thread/1.0" }),
            replySent: delivered);
        var alice = await BuildAgentAsync("alice", aliceSecrets, outboundTo: bobInbox, isResponder: false,
            serviceResolver: new FixedServiceResolver(bobDid, "http://localhost/didcomm"),
            localDid: aliceDid, expectedPeerDid: bobDid, queryIds: new LetterIds("query"));
        aliceInbox.Target = alice.Server.CreateHandler();
        bobInbox.Target = bob.Server.CreateHandler();

        try
        {
            var query = alice.Services.GetRequiredService<DiscoverFeaturesClient>().QueryFeaturesAsync(
                from: aliceDid,
                to: bobDid,
                queries: new[] { new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" } },
                timeout: TimeSpan.FromSeconds(5),
                serviceEndpointOverride: new Uri("http://localhost/didcomm"));
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // hang guard only

            var ex = await Record.ExceptionAsync(() => query);

            ex.Should().BeOfType<TimeoutException>("a disclose on another thread answers nothing; the query's own deadline ends it");
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

    private static async Task<(string AliceDid, string BobDid, string CarolDid)> CreateThreePeersAsync(
        InMemorySecretsResolver aliceSecrets,
        InMemorySecretsResolver sharedResponderSecrets)
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
        var carol = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var jwk in alice.Privates) aliceSecrets.Add(jwk);
        foreach (var jwk in bob.Privates) sharedResponderSecrets.Add(jwk);
        foreach (var jwk in carol.Privates) sharedResponderSecrets.Add(jwk);
        return (alice.Did, bob.Did, carol.Did);
    }

    private sealed record Agent(WebApplication App, TestServer Server, IServiceProvider Services);

    private static async Task<Agent> BuildAgentAsync(
        string tag, InMemorySecretsResolver secrets, LateBoundHandler outboundTo, bool isResponder,
        IServiceEndpointResolver serviceResolver, string localDid, string expectedPeerDid,
        TaskCompletionSource<UnpackResult>? captureInbound = null,
        Func<DidComm.Messages.Message, DidComm.Messages.Message>? replyByHand = null,
        TaskCompletionSource? replySent = null,
        DidComm.Messages.IMessageIdGenerator? queryIds = null)
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
        if (queryIds is not null)
        {
            // The same client AddDidComm would register (TryAdd), over the same facade, with its query
            // ids pinned through the internal test seam; AddDidComm's correlator registration resolves it.
            builder.Services.AddSingleton(sp => new DiscoverFeaturesClient(
                (message, options, ct) => sp.GetRequiredService<DidCommClient>().SendAsync(message, options, ct),
                sp.GetService<ILogger<DiscoverFeaturesClient>>(),
                queryIds));
        }

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
                if (outcome is { Result: DispatchResult.ReplyProduced, Reply: not null })
                {
                    // This is application egress policy, not automatic library egress. Bind the peer
                    // and local identities to the verified envelope keys, require the exact expected
                    // relationship, and snapshot the mutable handler reply before validating/sending.
                    var replyJson = System.Text.Json.JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default);
                    var reply = System.Text.Json.JsonSerializer.Deserialize<DidComm.Messages.Message>(replyJson, DidCommJson.Default)!;
                    // A test may answer with a message it builds by hand from the built-in reply, as a
                    // peer that writes the thread its own way would.
                    if (replyByHand is not null)
                        reply = replyByHand(reply);
                    var authenticatedPeerDid = DidSubject.DidSubjectOf(unpacked.SenderKid ?? unpacked.SignerKid);
                    var decryptingDid = DidSubject.DidSubjectOf(unpacked.RecipientKid);

                    if (unpacked.Encrypted
                        && unpacked.Authenticated
                        && string.Equals(authenticatedPeerDid, expectedPeerDid, StringComparison.Ordinal)
                        && string.Equals(decryptingDid, localDid, StringComparison.Ordinal)
                        && DidSubject.SameDidSubject(reply.From, decryptingDid)
                        && reply.To is { Count: 1 }
                        && DidSubject.SameDidSubject(reply.To[0], authenticatedPeerDid))
                    {
                        await client.SendAsync(reply, new SendOptions(
                            Recipients: new[] { authenticatedPeerDid! },
                            From: decryptingDid!), ct);
                        replySent?.TrySetResult();
                    }
                }
            });
        }
        else
        {
            // The initiator (Alice) just dispatches inbound messages; the inline Discover Features
            // correlator completes her awaiting QueryFeaturesAsync when the disclose arrives.
            if (captureInbound is null)
            {
                app.MapDidCommEndpoint("/didcomm");
            }
            else
            {
                app.MapDidCommEndpoint("/didcomm", (unpacked, _) =>
                {
                    captureInbound.TrySetResult(unpacked);
                    return Task.CompletedTask;
                });
            }
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
