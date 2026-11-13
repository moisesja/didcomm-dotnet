using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// The in-process "network" of the #95 end-to-end tests: every TestServer of a test registers under
/// its host name, and an HTTP transport whose primary handler is <see cref="CreateHandler"/> reaches
/// the server whose host the request names (the pattern of <c>ReturnRouteClientHarness</c>, extended
/// to several servers so two mediators, or a mediator and a recipient's inbox, reach each other).
/// </summary>
internal sealed class MediatorNetwork
{
    /// <summary>The host names the tests use. Every container allow-lists them in its egress guard (they are not public addresses).</summary>
    public static readonly IReadOnlyList<string> KnownHosts = new[] { "mediator.test", "m1.test", "m2.test", "inbox.test" };

    private readonly ConcurrentDictionary<string, Lazy<HttpMessageInvoker>> _servers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register <paramref name="server"/> under its host; its handler is created on first use (a TestServer serves only once started).</summary>
    public void Add(TestServer server)
        => _servers[server.BaseAddress.Host] = new Lazy<HttpMessageInvoker>(() => new HttpMessageInvoker(server.CreateHandler(), disposeHandler: false));

    /// <summary>A primary handler for an <c>HttpClient</c>; a fresh wrapper each time (the client factory disposes its handlers).</summary>
    public HttpMessageHandler CreateHandler() => new Router(this);

    private sealed class Router(MediatorNetwork network) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => request.RequestUri is { } uri && network._servers.TryGetValue(uri.Host, out var server)
                ? server.Value.SendAsync(request, cancellationToken)
                : throw new HttpRequestException("No test server answers for this host.");
    }
}

/// <summary>
/// An <see cref="IMeterFactory"/> whose meters carry it as their scope, so a test listens to its own
/// container's instruments only (tests run in parallel and share the meter name). A copy of engineer
/// B's helper in <c>DidComm.Mediator.Tests</c>, which has no InternalsVisibleTo towards this project.
/// </summary>
internal sealed class MediatorTestMeterFactory : IMeterFactory
{
    private readonly ConcurrentDictionary<string, Meter> _meters = new(StringComparer.Ordinal);

    public Meter Create(MeterOptions options)
        => _meters.GetOrAdd(options.Name, name => new Meter(new MeterOptions(name) { Version = options.Version, Tags = options.Tags, Scope = this }));

    public void Dispose()
    {
        foreach (var meter in _meters.Values)
            meter.Dispose();
    }
}

/// <summary>Records every long/int measurement of the instruments whose meter belongs to one <see cref="MediatorTestMeterFactory"/>.</summary>
internal sealed class MediatorMeasurements : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags)> _measurements = new();

    public MediatorMeasurements(MediatorTestMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>The sum recorded on <paramref name="instrument"/> with <paramref name="tag"/> = <paramref name="value"/>.</summary>
    public long Sum(string instrument, string tag, string value)
        => _measurements.Where(m => m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault(tag), value)).Sum(m => m.Value);

    /// <summary>The sum recorded on <paramref name="instrument"/>, whatever its tags.</summary>
    public long Sum(string instrument) => _measurements.Where(m => m.Instrument == instrument).Sum(m => m.Value);

    /// <summary>Every tag value recorded on <paramref name="instrument"/> under <paramref name="tag"/>, in order.</summary>
    public IReadOnlyList<string?> TagValues(string instrument, string tag)
        => _measurements.Where(m => m.Instrument == instrument).Select(m => m.Tags.GetValueOrDefault(tag) as string).ToArray();

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
            copy[tag.Key] = tag.Value;
        _measurements.Enqueue((instrument.Name, value, copy));
    }
}

/// <summary>Mints the did:peer:2 identities of the #95 tests.</summary>
internal static class MediatorCast
{
    /// <summary>Mint an identity, with a <c>DIDCommMessaging</c> service built by the library's <c>DidCommServiceBuilder</c> when <paramref name="service"/> is given.</summary>
    public static async Task<PeerIdentity> MintAsync(DidCommServiceSpec? service = null)
    {
        await using var provider = MintingProvider();
        return await PeerIdentityFactory.CreateAsync(
            provider.GetRequiredService<IDidManager>(),
            provider.GetRequiredService<IKeyGenerator>(),
            provider.GetRequiredService<ICryptoProvider>(),
            service);
    }

    /// <summary>Mint an identity with a service entry the caller built (one the library's builder would refuse).</summary>
    public static async Task<PeerIdentity> MintWithServiceEntryAsync(NetDid.Core.Model.Service entry)
    {
        await using var provider = MintingProvider();
        return await PeerIdentityFactory.CreateWithServiceEntryAsync(
            provider.GetRequiredService<IDidManager>(),
            provider.GetRequiredService<IKeyGenerator>(),
            provider.GetRequiredService<ICryptoProvider>(),
            entry);
    }

    /// <summary>A peer with its own secrets and facade (no transport).</summary>
    public static async Task<TestPeer> MintPeerAsync(DidCommServiceSpec? service = null)
        => TestPeer.Create(await MintAsync(service));

    /// <summary>A recipient DID reached through <paramref name="mediatorDid"/> (FR-MED-07: <c>uri</c> = the routing DID, <c>accept: ["didcomm/v2"]</c>), with optional further routing keys.</summary>
    public static Task<TestPeer> MintRecipientAsync(string mediatorDid, params string[] routingKeys)
        => MintPeerAsync(new DidCommServiceSpec(mediatorDid, routingKeys.Length == 0 ? null : routingKeys, new[] { "didcomm/v2" }));

    private static ServiceProvider MintingProvider()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// A sender's own container, built the way an application would: did:peer resolution, the HTTP
/// transport (http allowed, no retries) over the test network, the test hosts allow-listed in the
/// egress guard, and the test clock.
/// </summary>
internal sealed class MediatorSender : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private MediatorSender(TestPeer peer, ServiceProvider services)
    {
        Peer = peer;
        _services = services;
        Client = services.GetRequiredService<DidCommClient>();
    }

    public TestPeer Peer { get; }

    public string Did => Peer.Did;

    public DidCommClient Client { get; }

    public static async Task<MediatorSender> CreateAsync(MediatorNetwork network, TestClock clock, CapturingLoggerProvider? logs = null)
    {
        var peer = await MediatorCast.MintPeerAsync();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
                b.AddProvider(logs);
        });
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(peer.Secrets);
            b.UseHttpTransport(MediatorTestServer.ConfigureHttp);
            b.Configure(o =>
            {
                o.Clock = () => clock.Now;
                foreach (var host in MediatorNetwork.KnownHosts)
                    o.OutboundEndpointPolicy.AllowedHosts.Add(host);
            });
        });
        // Registered after UseHttpTransport, so this primary handler replaces its socket handler.
        services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(network.CreateHandler);
        return new MediatorSender(peer, services.BuildServiceProvider());
    }

    /// <summary>A basic message from this sender to <paramref name="to"/>.</summary>
    public Message Note(string to, string content = "hello")
        => new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(Did)
            .WithTo(to)
            .WithBody(new JsonObject { ["content"] = content })
            .Build();

    /// <summary>Authcrypt <paramref name="message"/> to <paramref name="to"/> with no forward wrapping (an onward payload).</summary>
    public async Task<string> AuthcryptAsync(Message message, string to)
        => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: Did, Forward: false))).Message;

    /// <summary>
    /// A forward built by hand: <paramref name="inner"/> (already packed) wrapped for
    /// <paramref name="mediatorDid"/> with <c>next</c> = <paramref name="next"/>, optional headers set on
    /// the forward, anoncrypted to the mediator.
    /// </summary>
    public async Task<(string Packed, Message Forward)> ForwardAsync(string mediatorDid, string next, string inner, Action<Message>? shape = null)
    {
        var forward = ForwardMessage.Create(mediatorDid, next, new[] { inner });
        shape?.Invoke(forward);
        var packed = await Client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediatorDid }, From: null, Forward: false));
        return (packed.Message, forward);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Peer.DisposeAsync();
    }
}

/// <summary>A recipient's own HTTP inbox on <c>inbox.test</c>: records each POST body, answers with <see cref="Status"/>, can hold every request at a gate.</summary>
internal sealed class InboxServer : IAsyncDisposable
{
    public const string Host = "inbox.test";

    private readonly ConcurrentQueue<(string Path, byte[] Body)> _received = new();
    private int _entered;

    private InboxServer(WebApplication app) => App = app;

    public WebApplication App { get; }

    /// <summary>The status every request is answered with (202 by default).</summary>
    public int Status { get; set; } = StatusCodes.Status202Accepted;

    /// <summary>When set, each request waits for it (bounded) before it is recorded and answered.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Requests that reached the endpoint (counted before the gate).</summary>
    public int Entered => Volatile.Read(ref _entered);

    public IReadOnlyCollection<(string Path, byte[] Body)> Received => _received;

    public Uri Endpoint(string path) => new($"http://{Host}{path}");

    public static async Task<InboxServer> StartAsync(MediatorNetwork network)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{Host}/"));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        InboxServer? inbox = null;
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            Interlocked.Increment(ref inbox!._entered);
            if (inbox.Gate is { } gate)
                await Task.WhenAny(gate.Task, Task.Delay(MediatorTestServer.Timeout));
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            inbox._received.Enqueue((context.Request.Path.Value ?? string.Empty, ms.ToArray()));
            return Results.StatusCode(inbox.Status);
        });
        inbox = new InboxServer(app);
        await app.StartAsync();
        network.Add(app.GetTestServer());
        return inbox;
    }

    public async ValueTask DisposeAsync()
    {
        Gate?.TrySetResult();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>Knobs of <see cref="MediatorTestServer"/>.</summary>
internal sealed class MediatorTestServerOptions
{
    /// <summary>The server's host name (one of <see cref="MediatorNetwork.KnownHosts"/>).</summary>
    public string Host { get; set; } = "mediator.test";

    /// <summary>The <c>MapDidCommMediator</c> pattern; <c>"/"</c> as in the issue's acceptance line.</summary>
    public string Pattern { get; set; } = "/";

    /// <summary>The path of the mediator DID's service <c>uri</c>; the pattern when <c>null</c>.</summary>
    public string? ServicePath { get; set; }

    /// <summary>The mediator's identity; minted with a service at <c>http://{Host}{ServicePath}</c> when <c>null</c>.</summary>
    public PeerIdentity? Identity { get; set; }

    /// <summary>Whether the mediator's secrets resolver holds the identity's private keys.</summary>
    public bool HoldSecrets { get; set; } = true;

    public MediatorNetwork? Network { get; set; }

    public TestClock? Clock { get; set; }

    public int MaxReceiveBytes { get; set; } = 1024 * 1024;

    /// <summary>The host's global STOMP switch (same-socket replies stay off globally).</summary>
    public bool GlobalStomp { get; set; }

    /// <summary>Settings on top of the routing DID (<c>AddMediator(configure)</c>).</summary>
    public Action<MediatorOptions>? Mediator { get; set; }

    /// <summary>Runs before <c>AddDidComm</c>: what a host registers before <c>AddMediator()</c> (directory, routing-key provider, stores).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>Runs after <c>AddDidComm</c> (a registration made after <c>AddMediator()</c>).</summary>
    public Action<IServiceCollection>? PostConfigureServices { get; set; }

    public Action<WebApplicationBuilder>? ConfigureBuilder { get; set; }

    /// <summary>Runs after <c>UseWebSockets()</c>, before the mediator is mapped.</summary>
    public Action<WebApplication>? ConfigurePipeline { get; set; }

    /// <summary>Conventions applied to the builder <c>MapDidCommMediator</c> returns.</summary>
    public Action<IEndpointConventionBuilder>? Conventions { get; set; }
}

/// <summary>Test switches the request pipeline reads on every request.</summary>
internal sealed class MediatorRequestControls
{
    private TaskCompletionSource? _responseGate;
    private int _abortNextPost;

    /// <summary>When set, every POST's response is held at <c>OnStarting</c> until it completes.</summary>
    public TaskCompletionSource? ResponseGate
    {
        get => Volatile.Read(ref _responseGate);
        set => Volatile.Write(ref _responseGate, value);
    }

    /// <summary>When set, the next POST is aborted after the endpoint returned and before its response is committed.</summary>
    public bool AbortNextPost
    {
        set => Volatile.Write(ref _abortNextPost, value ? 1 : 0);
    }

    public bool TakeAbort() => Interlocked.Exchange(ref _abortNextPost, 0) == 1;
}

/// <summary>
/// The #95 mediator under test (design §12): a TestServer <c>WebApplication</c> with
/// <c>AddDidComm(UseNetDidResolver, UseSecretsResolver(mediator), UseHttpTransport, AddMediator(…))</c>,
/// <c>UseWebSockets()</c> and <c>MapDidCommMediator(pattern)</c>. The mediator's DID is a did:peer:2
/// minted with <c>DidCommServiceBuilder.ForEndpoint</c> at the server's own address, so the FR-MED-07
/// start-up checks pass; its HTTP egress (direct push, a relay to a second mediator) goes over the
/// <see cref="MediatorNetwork"/>. The clock is injected (<c>DidCommOptions.Clock</c>), metrics go to a
/// per-test meter factory, logs to a capturing provider. The recipient side is hand-rolled
/// (stamped, <c>return_route: all</c>, authcrypted) Coordinate Mediation and pickup messages.
/// </summary>
internal sealed class MediatorTestServer : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly List<IAsyncDisposable> _owned = new();
    private readonly List<string> _sensitive = new();

    private MediatorTestServer(WebApplication app, MediatorTestServerOptions options, PeerIdentity identity, MediatorNetwork network, TestClock clock,
        CapturingLoggerProvider logs, MediatorTestMeterFactory meters, MediatorMeasurements measurements, MediatorRequestControls controls)
    {
        App = app;
        Options = options;
        Did = identity.Did;
        Kids = ReturnRouteTestHost.KidsOf(identity);
        Network = network;
        Clock = clock;
        Logs = logs;
        Meters = meters;
        Measurements = measurements;
        Controls = controls;
        _sensitive.Add(Did);
        _sensitive.AddRange(Kids);
    }

    public WebApplication App { get; }

    public MediatorTestServerOptions Options { get; }

    /// <summary>The mediator's routing DID.</summary>
    public string Did { get; }

    public IReadOnlyList<string> Kids { get; }

    public MediatorNetwork Network { get; }

    public TestClock Clock { get; }

    public CapturingLoggerProvider Logs { get; }

    public MediatorTestMeterFactory Meters { get; }

    public MediatorMeasurements Measurements { get; }

    public MediatorRequestControls Controls { get; }

    public TestServer Server => App.GetTestServer();

    /// <summary>The base address without a trailing <c>/</c> (the <c>publicBase</c> of the XML docs).</summary>
    public string PublicBase => Server.BaseAddress.GetLeftPart(UriPartial.Authority);

    /// <summary>The mediator's HTTP endpoint (<c>POST {pattern}</c>).</summary>
    public Uri Endpoint => new(Server.BaseAddress, Options.Pattern);

    public MessagePickupQueue Queue => App.Services.GetRequiredService<MessagePickupQueue>();

    public InMemoryMessageStore Store => (InMemoryMessageStore)App.Services.GetRequiredService<IMessageStore>();

    public IMediationStore Mediation => App.Services.GetRequiredService<IMediationStore>();

    public ForwardHandler Handler => App.Services.GetRequiredService<ForwardHandler>();

    internal DirectPushDispatcher Pushes => App.Services.GetRequiredService<DirectPushDispatcher>();

    internal MediatorHostedService Hosted => App.Services.GetServices<IHostedService>().OfType<MediatorHostedService>().Single();

    internal MediatorInvitationHolder Invitation => App.Services.GetRequiredService<MediatorInvitationHolder>();

    public DidCommReceiveOptions GlobalReceiveOptions => App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DidCommReceiveOptions>>().Value;

    /// <summary>The HTTP transport settings of every container here: http allowed (TestServer), no retries, a breaker that never opens.</summary>
    public static void ConfigureHttp(HttpTransportOptions o)
    {
        o.AllowedSchemes = new[] { "http", "https" };
        o.MaxRetryAttempts = 0;
        o.CircuitBreakerFailureThreshold = 1000;
    }

    /// <summary>Build and start a mediator.</summary>
    public static async Task<MediatorTestServer> StartAsync(Action<MediatorTestServerOptions>? configure = null)
    {
        var server = await CreateAsync(configure);
        try
        {
            await server.App.StartAsync();
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        return server;
    }

    /// <summary>Build a mediator without starting it (the start-up failure tests start it themselves).</summary>
    public static async Task<MediatorTestServer> CreateAsync(Action<MediatorTestServerOptions>? configure = null)
    {
        var options = new MediatorTestServerOptions();
        configure?.Invoke(options);
        var network = options.Network ??= new MediatorNetwork();
        var clock = options.Clock ??= new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var servicePath = options.ServicePath ?? options.Pattern;
        var identity = options.Identity ?? await MediatorCast.MintAsync(new DidCommServiceSpec($"http://{options.Host}{servicePath}", Accept: new[] { "didcomm/v2" }));

        var secrets = new InMemorySecretsResolver();
        if (options.HoldSecrets)
        {
            foreach (var jwk in identity.Privates)
                secrets.Add(jwk);
        }

        var logs = new CapturingLoggerProvider();
        var meters = new MediatorTestMeterFactory();
        var measurements = new MediatorMeasurements(meters);
        var controls = new MediatorRequestControls();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{options.Host}/"));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<IMeterFactory>(meters);
        options.ConfigureServices?.Invoke(builder.Services);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.UseHttpTransport(ConfigureHttp);
            b.AddMediator(m =>
            {
                m.CoordinateMediation.RoutingDids.Add(identity.Did);
                options.Mediator?.Invoke(m);
            });
            b.Configure(o =>
            {
                o.Clock = () => clock.Now;
                o.MaxReceiveBytes = options.MaxReceiveBytes;
                foreach (var host in MediatorNetwork.KnownHosts)
                    o.OutboundEndpointPolicy.AllowedHosts.Add(host);
            });
        });
        builder.Services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(network.CreateHandler);
        builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o =>
        {
            o.AllowSameSocketReplies = false;
            o.UseStomp = options.GlobalStomp;
        });
        options.PostConfigureServices?.Invoke(builder.Services);
        options.ConfigureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            var post = HttpMethods.IsPost(context.Request.Method);
            if (post && controls.ResponseGate is { } gate)
                context.Response.OnStarting(static state => ((TaskCompletionSource)state!).Task, gate);
            await next(context);
            if (post && controls.TakeAbort())
                context.Abort();
        });
        app.UseWebSockets();
        options.ConfigurePipeline?.Invoke(app);
        var mapped = app.MapDidCommMediator(options.Pattern);
        options.Conventions?.Invoke(mapped);
        network.Add(app.GetTestServer());

        return new MediatorTestServer(app, options, identity, network, clock, logs, meters, measurements, controls);
    }

    // ---- the cast ---------------------------------------------------------------------------

    /// <summary>A pickup peer (no service): it enrols and picks up. Its DID and kids join the log scan.</summary>
    public async Task<TestPeer> NewPeerAsync() => Own(await MediatorCast.MintPeerAsync());

    /// <summary>A recipient DID reached through this mediator (optionally through further routing keys).</summary>
    public async Task<TestPeer> NewRecipientAsync(params string[] routingKeys) => Own(await MediatorCast.MintRecipientAsync(Did, routingKeys));

    /// <summary>A sender with an HTTP transport over the test network.</summary>
    public async Task<MediatorSender> NewSenderAsync()
    {
        var sender = await MediatorSender.CreateAsync(Network, Clock, Logs);
        _owned.Add(sender);
        _sensitive.Add(sender.Did);
        _sensitive.AddRange(sender.Peer.Kids);
        return sender;
    }

    /// <summary>Count <paramref name="peer"/>'s DID and kids as values no log line may carry, and dispose it with the server.</summary>
    public TestPeer Own(TestPeer peer)
    {
        _owned.Add(peer);
        AddSensitive(peer);
        return peer;
    }

    public void AddSensitive(TestPeer peer)
    {
        _sensitive.Add(peer.Did);
        _sensitive.AddRange(peer.Kids);
    }

    public void AddSensitive(string value) => _sensitive.Add(value);

    // ---- metrics ----------------------------------------------------------------------------

    /// <summary>How many forwards ended with <paramref name="outcome"/> (<c>didcomm.forward.outcomes</c>).</summary>
    public long Outcome(string outcome) => Measurements.Sum(ForwardOutcomes.ForwardOutcomesInstrument, ForwardOutcomes.OutcomeTag, outcome);

    /// <summary>Every forward outcome recorded, in order.</summary>
    public IReadOnlyList<string?> Outcomes => Measurements.TagValues(ForwardOutcomes.ForwardOutcomesInstrument, ForwardOutcomes.OutcomeTag);

    /// <summary>How many direct pushes ended with <paramref name="outcome"/>.</summary>
    public long PushOutcome(string outcome) => Measurements.Sum(MediatorMetrics.DirectPushOutcomesInstrument, MediatorMetrics.PushOutcomeTag, outcome);

    public IReadOnlyList<string?> PushOutcomes => Measurements.TagValues(MediatorMetrics.DirectPushOutcomesInstrument, MediatorMetrics.PushOutcomeTag);

    // ---- requests ---------------------------------------------------------------------------

    /// <summary>What <c>SendAsync</c> does for a mediator request (FR-RR-09): <c>created_time</c> from the server's clock and <c>return_route: all</c>.</summary>
    public Message Prepare(Message message)
    {
        message.CreatedTime = Clock.Now.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(message, ReturnRouteMode.All, thread: null);
        return message;
    }

    public async Task<string> AuthcryptAsync(TestPeer from, Message message)
        => (await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { Did }, From: from.Did, Forward: false))).Message;

    /// <summary>POST a packed envelope to <paramref name="path"/> (the pattern by default, or an absolute URL).</summary>
    public async Task<(HttpStatusCode Status, string? ContentType, string Body)> PostAsync(string packed, string? path = null)
    {
        using var client = Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path ?? Options.Pattern)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode Status, string? ContentType, string Body)> GetAsync(string pathOrUrl)
    {
        using var client = Server.CreateClient();
        using var response = await client.GetAsync(pathOrUrl);
        return (response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync());
    }

    /// <summary>Authcrypt <paramref name="request"/> (prepared here) from <paramref name="from"/>, POST it and read the mediator's answer from the 200 body.</summary>
    public async Task<PickupAnswer> AskAsync(TestPeer from, Message request, string? path = null)
    {
        var response = await PostAsync(await AuthcryptAsync(from, Prepare(request)), path);
        response.Status.Should().Be(HttpStatusCode.OK);
        response.ContentType.Should().Be(MediaTypes.Encrypted);
        return await UnpackAnswerAsync(from, Encoding.UTF8.GetBytes(response.Body));
    }

    /// <summary><c>mediate-request</c> → <c>mediate-grant</c>; returns the routing DIDs of the grant.</summary>
    public async Task<IReadOnlyList<string>> RequestMediationAsync(TestPeer peer, string? path = null)
    {
        var granted = await AskAsync(peer, CoordinateMediation.CreateMediateRequest(peer.Did, Did), path);
        CoordinateMediation.TryParseGrant(granted.Message, out var routingDids).Should().BeTrue();
        return routingDids!;
    }

    /// <summary><c>recipient-update</c> adding <paramref name="recipientDids"/> under <paramref name="peer"/>'s grant.</summary>
    public async Task AddRecipientsAsync(TestPeer peer, IReadOnlyList<string> recipientDids, string? path = null)
    {
        var updated = await AskAsync(peer, CoordinateMediation.CreateRecipientUpdate(peer.Did, Did,
            recipientDids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray()), path);
        CoordinateMediation.TryParseRecipientUpdateResponse(updated.Message, out var results).Should().BeTrue();
        results!.Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
    }

    /// <summary>Enrol <paramref name="peer"/> and register <paramref name="recipientDids"/> (its own DID when none is given).</summary>
    public async Task EnrolAsync(TestPeer peer, params string[] recipientDids)
    {
        (await RequestMediationAsync(peer)).Should().Equal(Did);
        await AddRecipientsAsync(peer, recipientDids.Length == 0 ? new[] { peer.Did } : recipientDids);
    }

    /// <summary>A <c>status-request</c> over HTTP.</summary>
    public async Task<PickupStatus> StatusAsync(TestPeer peer, string? recipientDid = null)
    {
        var request = MessagePickup.CreateStatusRequest(peer.Did, Did, recipientDid);
        return (await AskAsync(peer, request)).Status(request.Id);
    }

    /// <summary>A <c>delivery-request</c> over HTTP: the delivered messages, or an empty list when the answer is a <c>status</c>.</summary>
    public async Task<IReadOnlyList<DeliveredMessage>> FetchAsync(TestPeer peer, int limit = 10)
    {
        var request = MessagePickup.CreateDeliveryRequest(peer.Did, Did, limit);
        var answer = await AskAsync(peer, request);
        if (answer.Message.Type == MessagePickup.StatusType)
        {
            answer.Status(request.Id);
            return Array.Empty<DeliveredMessage>();
        }
        return answer.Delivery(request.Id);
    }

    /// <summary><c>messages-received</c> over HTTP; returns the <c>status</c> that answers it.</summary>
    public async Task<PickupStatus> AcknowledgeAsync(TestPeer peer, params string[] ids)
    {
        var request = MessagePickup.CreateMessagesReceived(peer.Did, Did, ids);
        return (await AskAsync(peer, request)).Status(request.Id);
    }

    public Task<WebSocket> ConnectAsync(string? path = null)
        => Server.CreateWebSocketClient().ConnectAsync(new UriBuilder(Server.BaseAddress) { Scheme = "ws", Path = path ?? Options.Pattern }.Uri, default);

    /// <summary>Authcrypt <paramref name="request"/> (prepared here) on <paramref name="socket"/> and return the next frame, the mediator's answer.</summary>
    public async Task<PickupAnswer> AskOverSocketAsync(WebSocket socket, TestPeer from, Message request)
    {
        await ReturnRouteTestHost.SendAsync(socket, await AuthcryptAsync(from, Prepare(request)));
        return await ReceiveOverSocketAsync(socket, from);
    }

    public async Task<PickupAnswer> ReceiveOverSocketAsync(WebSocket socket, TestPeer peer)
    {
        var (type, text) = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        type.Should().Be(WebSocketMessageType.Binary, "each message of the mediator is one binary WebSocket message, never a STOMP frame");
        return await UnpackAnswerAsync(peer, Encoding.UTF8.GetBytes(text));
    }

    private async Task<PickupAnswer> UnpackAnswerAsync(TestPeer peer, byte[] packed)
    {
        var unpacked = await peer.Client.UnpackAsync(Encoding.UTF8.GetString(packed));
        unpacked.Encrypted.Should().BeTrue();
        unpacked.Authenticated.Should().BeTrue();
        unpacked.SenderKid.Should().StartWith(Did + "#", "the answer is authcrypted by the mediator DID the request was addressed to");
        unpacked.Message.From.Should().Be(Did);
        unpacked.Message.To.Should().Equal(peer.Did);
        return new PickupAnswer(unpacked.Message, packed);
    }

    // ---- waiting and checks -----------------------------------------------------------------

    /// <summary>Every forward admitted so far has been relayed, dropped or discarded (the handler's test seam).</summary>
    public Task FlushAsync() => Handler.FlushAsync(Timeout);

    /// <summary>The direct-push workers are idle.</summary>
    public Task PushesIdleAsync() => Pushes.WaitForIdleAsync(Timeout);

    public static async Task WaitUntilAsync(Func<bool> condition, string because)
        => (await ReturnRouteTestHost.WaitUntilAsync(condition, Timeout)).Should().BeTrue(because);

    /// <summary>Log lines (of this server and the senders it made) that carry a DID or key id of the cast (must be empty).</summary>
    public IReadOnlyList<string> LeakingLogLines(params string[] extraSensitive)
    {
        var sensitive = _sensitive.Concat(extraSensitive).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToArray();
        return Logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await App.StopAsync();
        }
        catch (Exception)
        {
            // A host that never started (a start-up failure test) has nothing to stop.
        }
        await App.DisposeAsync();
        foreach (var owned in _owned)
            await owned.DisposeAsync();
        Measurements.Dispose();
        Meters.Dispose();
    }
}

/// <summary>Reading what a recipient received.</summary>
internal static class MediatorRecipient
{
    /// <summary>Unpack an onward payload as <paramref name="recipient"/>: Alice's authcrypted message, exactly one layer.</summary>
    public static async Task<UnpackResult> UnpackFromSenderAsync(TestPeer recipient, ReadOnlyMemory<byte> payload, MediatorSender sender)
    {
        var unpacked = await recipient.Client.UnpackAsync(Encoding.UTF8.GetString(payload.Span));
        unpacked.Encrypted.Should().BeTrue();
        unpacked.Authenticated.Should().BeTrue("the sender's authcrypt survives the relay");
        unpacked.SenderKid.Should().StartWith(sender.Did + "#");
        unpacked.Message.From.Should().Be(sender.Did);
        unpacked.Message.To.Should().Equal(recipient.Did);
        return unpacked;
    }

    /// <summary>A queue id: 16 random bytes as 32 lowercase hex characters (FR-PICK-04, JC-3).</summary>
    public static bool IsRandomQueueId(string id)
        => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
