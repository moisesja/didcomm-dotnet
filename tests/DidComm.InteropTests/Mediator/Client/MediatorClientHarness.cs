using DataProofsDotnet.Jose;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>Knobs of <see cref="MediatorClientHarness"/>.</summary>
internal sealed class MediatorClientHarnessOptions
{
    /// <summary>Register the HTTP transport over the mediator's test network (http allowed, no retries). Default on.</summary>
    public bool UseHttp { get; set; } = true;

    /// <summary>
    /// Register the WebSocket transport, connected to the mediator's TestServer, with
    /// <c>ReceiveInbound = true</c> (answers and live pushes on the session socket reach the
    /// dispatcher, and so the clients). Default off.
    /// </summary>
    public bool UseWebSocket { get; set; }

    /// <summary>With <see cref="UseWebSocket"/>: also register <c>UseInboundDispatch()</c> (the documented set-up). Turn off only to test the refusal without it.</summary>
    public bool UseInboundDispatch { get; set; } = true;

    /// <summary>Register <c>AddMediatorClient(<see cref="Client"/>)</c>. Default on.</summary>
    public bool AddMediatorClient { get; set; } = true;

    /// <summary>The clients' settings (<c>AddMediatorClient(configure)</c>).</summary>
    public Action<MediatorClientOptions>? Client { get; set; }

    /// <summary>Extra WebSocket transport settings, applied after the harness's own.</summary>
    public Action<WebSocketTransportOptions>? WebSocket { get; set; }

    /// <summary>Extra <c>AddDidComm</c> configuration, after the transports and <c>AddMediatorClient()</c> (for example <c>AddMediator()</c> for a host that is both, or <c>AddBuiltInProtocols()</c>).</summary>
    public Action<DidCommBuilder>? DidComm { get; set; }

    /// <summary>Runs before <c>AddDidComm</c> (what a host registers first).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>How far the recipient's clock is from the mediator's (a second machine whose clock runs ahead or behind); zero shares it.</summary>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>A <see cref="TimeProvider"/> to register (the clients' deadlines and poll intervals); <see cref="TimeProvider.System"/> when <c>null</c>.</summary>
    public TimeProvider? Time { get; set; }
}

/// <summary>
/// The recipient side of the #96 end-to-end tests: a container built the way an application builds
/// one — <c>AddDidComm</c> with did:peer resolution, the secrets of the recipient's own DIDs, the
/// HTTP transport over <see cref="MediatorTestServer"/>'s network and/or the WebSocket transport
/// connected to its TestServer (<c>ReceiveInbound</c> + <c>UseInboundDispatch()</c>), and
/// <c>AddMediatorClient()</c> — sharing the mediator's network, clock (<c>DidCommOptions.Clock</c>)
/// and log capture, so <see cref="MediatorTestServer.LeakingLogLines"/> scans the clients' lines
/// too. Engineers B and C use it as it is; changes go through engineer C.
/// </summary>
/// <remarks>
/// The mediator's DID document names <c>http://{host}{pattern}</c> unless the test mints the
/// mediator with another identity (<see cref="MintMediatorIdentityAsync"/>), so a WebSocket test
/// either passes <see cref="WebSocketEndpoint"/> as <c>serviceEndpointOverride</c> or starts the
/// mediator with a <c>ws://</c> identity.
/// </remarks>
internal sealed class MediatorClientHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly HarnessSecrets _secrets;
    private readonly ConnectCounter _connects;

    private MediatorClientHarness(MediatorTestServer mediator, MediatorClientHarnessOptions options, ServiceProvider services, HarnessSecrets secrets, ConnectCounter connects)
    {
        Mediator = mediator;
        Options = options;
        _services = services;
        _secrets = secrets;
        _connects = connects;
        Client = services.GetRequiredService<DidCommClient>();
    }

    /// <summary>The mediator under test.</summary>
    public MediatorTestServer Mediator { get; }

    /// <summary>The options the harness was built with.</summary>
    public MediatorClientHarnessOptions Options { get; }

    /// <summary>The recipient's container.</summary>
    public IServiceProvider Services => _services;

    /// <summary>The recipient's facade.</summary>
    public DidCommClient Client { get; }

    /// <summary>The recipient's <see cref="MediationClient"/> (requires <see cref="MediatorClientHarnessOptions.AddMediatorClient"/>).</summary>
    public MediationClient Mediation => _services.GetRequiredService<MediationClient>();

    /// <summary>The recipient's <see cref="PickupClient"/> (requires <see cref="MediatorClientHarnessOptions.AddMediatorClient"/>).</summary>
    public PickupClient Pickup => _services.GetRequiredService<PickupClient>();

    /// <summary>The container's shared correlation channel (requires <see cref="MediatorClientHarnessOptions.AddMediatorClient"/>).</summary>
    public MediatorClientChannel Channel => _services.GetRequiredService<MediatorClientChannel>();

    /// <summary>The recipient's dispatcher (the WebSocket inbound path ends there).</summary>
    public ProtocolDispatcher Dispatcher => _services.GetRequiredService<ProtocolDispatcher>();

    /// <summary>The secrets resolver of the recipient's container: every DID minted through this harness, plus any <see cref="Hold"/> added.</summary>
    public ISecretsResolver Secrets => _secrets;

    /// <summary>The log capture shared with the mediator.</summary>
    public CapturingLoggerProvider Logs => Mediator.Logs;

    /// <summary>The mediator's clock (the recipient's is it plus <see cref="MediatorClientHarnessOptions.ClockOffset"/>).</summary>
    public TestClock Clock => Mediator.Clock;

    /// <summary>The mediator's HTTP endpoint (<c>POST {pattern}</c>).</summary>
    public Uri HttpEndpoint => Mediator.Endpoint;

    /// <summary>The mediator's WebSocket endpoint (<c>ws://{host}{pattern}</c>).</summary>
    public Uri WebSocketEndpoint => new UriBuilder(Mediator.Server.BaseAddress) { Scheme = "ws", Path = Mediator.Options.Pattern }.Uri;

    /// <summary>WebSocket connections the recipient's transport opened so far.</summary>
    public int Connects => Volatile.Read(ref _connects.Count);

    /// <summary>Build the recipient's container against <paramref name="mediator"/>.</summary>
    /// <param name="mediator">The running mediator.</param>
    /// <param name="configure">Harness knobs.</param>
    public static Task<MediatorClientHarness> CreateAsync(MediatorTestServer mediator, Action<MediatorClientHarnessOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        var options = new MediatorClientHarnessOptions();
        configure?.Invoke(options);

        var secrets = new HarnessSecrets();
        var connects = new ConnectCounter();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(mediator.Logs);
        });
        if (options.Time is { } time)
            services.AddSingleton(time);
        options.ConfigureServices?.Invoke(services);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            if (options.UseHttp)
                b.UseHttpTransport(MediatorTestServer.ConfigureHttp);
            if (options.UseWebSocket)
            {
                b.UseWebSocketTransport(o =>
                {
                    o.AllowedSchemes = new[] { "ws" };
                    o.MaxReconnectAttempts = 0;
                    o.ReceiveInbound = true;
                    o.WebSocketFactory = () => new TestServerClientWebSocket();
                    o.Connect = async (socket, uri, ct) =>
                    {
                        Interlocked.Increment(ref connects.Count);
                        await ((TestServerClientWebSocket)socket).ConnectAsync(mediator.Server, uri, ct).ConfigureAwait(false);
                    };
                    options.WebSocket?.Invoke(o);
                });
                if (options.UseInboundDispatch)
                    b.UseInboundDispatch();
            }
            if (options.AddMediatorClient)
                b.AddMediatorClient(options.Client);
            options.DidComm?.Invoke(b);
            b.Configure(o =>
            {
                o.Clock = () => mediator.Clock.Now + options.ClockOffset;
                foreach (var host in MediatorNetwork.KnownHosts)
                    o.OutboundEndpointPolicy.AllowedHosts.Add(host);
            });
        });
        if (options.UseHttp)
        {
            // Registered after UseHttpTransport, so this primary handler replaces its socket handler.
            services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(mediator.Network.CreateHandler);
        }
        return Task.FromResult(new MediatorClientHarness(mediator, options, services.BuildServiceProvider(), secrets, connects));
    }

    /// <summary>
    /// Mint an identity for a mediator whose DID document names <c>{scheme}://{host}{path}</c> (for
    /// <see cref="MediatorTestServerOptions.Identity"/>): <c>ws</c> makes the clients reach it over a
    /// WebSocket without an endpoint override.
    /// </summary>
    /// <param name="scheme"><c>http</c> or <c>ws</c>.</param>
    /// <param name="host">The mediator's host (one of <see cref="MediatorNetwork.KnownHosts"/>).</param>
    /// <param name="path">The service path (the mediator's pattern).</param>
    public static Task<PeerIdentity> MintMediatorIdentityAsync(string scheme, string host = "mediator.test", string path = "/")
        => MediatorCast.MintAsync(new DidCommServiceSpec($"{scheme}://{host}{path}", Accept: new[] { "didcomm/v2" }));

    /// <summary>
    /// Mint a DID of the recipient's own (no service, or <paramref name="service"/>) whose keys this
    /// container holds: typically the DID it enrols as (<c>from</c>). Its DID and key ids join the
    /// mediator's log scan; the mediator disposes it.
    /// </summary>
    /// <param name="service">An optional <c>DIDCommMessaging</c> service.</param>
    public async Task<TestPeer> NewPeerAsync(DidCommServiceSpec? service = null)
    {
        var peer = Mediator.Own(await MediatorCast.MintPeerAsync(service));
        Hold(peer);
        return peer;
    }

    /// <summary>
    /// Mint a DID reached through <paramref name="mediatorDid"/> (the mediator under test by
    /// default; optionally through further routing keys) whose keys this container holds: the DID
    /// others write to, whose messages the recipient picks up.
    /// </summary>
    /// <param name="mediatorDid">The mediator DID of its service (<c>uri</c>); <see cref="MediatorTestServer.Did"/> when <c>null</c>.</param>
    /// <param name="routingKeys">Further routing keys after the mediator.</param>
    public async Task<TestPeer> NewRecipientAsync(string? mediatorDid = null, params string[] routingKeys)
    {
        var peer = Mediator.Own(await MediatorCast.MintRecipientAsync(mediatorDid ?? Mediator.Did, routingKeys));
        Hold(peer);
        return peer;
    }

    /// <summary>Hold <paramref name="peer"/>'s keys in this container (a DID minted elsewhere).</summary>
    /// <param name="peer">The peer.</param>
    public void Hold(TestPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        _secrets.Add(peer.Secrets);
    }

    /// <summary>A sender ("Alice") with her own HTTP container over the test network; the mediator disposes it.</summary>
    public Task<MediatorSender> NewAliceAsync() => Mediator.NewSenderAsync();

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private sealed class ConnectCounter
    {
        public int Count;
    }

    /// <summary>A secrets resolver that grows as the test mints DIDs; safe to read while one is added.</summary>
    private sealed class HarnessSecrets : ISecretsResolver
    {
        private readonly object _gate = new();
        private ISecretsResolver[] _inner = Array.Empty<ISecretsResolver>();

        public void Add(ISecretsResolver resolver)
        {
            lock (_gate)
                Volatile.Write(ref _inner, [.. _inner, resolver]);
        }

        public async Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
        {
            foreach (var resolver in Volatile.Read(ref _inner))
            {
                if (await resolver.FindAsync(kid, ct).ConfigureAwait(false) is { } found)
                    return found;
            }
            return null;
        }

        public async Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        {
            var asked = kids.ToList();
            var present = new List<string>();
            foreach (var resolver in Volatile.Read(ref _inner))
                present.AddRange(await resolver.FindPresentAsync(asked, ct).ConfigureAwait(false));
            return present.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
