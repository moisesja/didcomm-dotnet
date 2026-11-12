using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Transports;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// Shared harness for the #91 return-route integration tests: a TestServer agent ("Bob") built
/// with <c>AddDidComm</c> (did:peer resolution, built-in protocols, a <see cref="RecordingHandler"/>
/// for <see cref="RecordingHandler.ProtocolUri"/>, an injected clock, a capturing logger), mapped at
/// <c>/didcomm</c> (registry-aware HTTP), <c>/ws</c> (registry-aware WebSocket) and
/// <c>/didcomm-callback</c> (the session-aware callback overload), plus did:peer peers (Alice,
/// Carol, Mallory) that each hold their own secrets and facade.
/// </summary>
internal sealed class ReturnRouteTestHost : IAsyncDisposable
{
    private ReturnRouteTestHost(
        WebApplication app,
        TestClock clock,
        CapturingLoggerProvider logs,
        RecordingHandlerState recorder,
        string serverDid,
        IReadOnlyList<string> serverKids,
        TestPeer alice,
        TestPeer carol,
        TestPeer mallory)
    {
        App = app;
        Clock = clock;
        Logs = logs;
        Recorder = recorder;
        ServerDid = serverDid;
        ServerKids = serverKids;
        Alice = alice;
        Carol = carol;
        Mallory = mallory;
    }

    public WebApplication App { get; }

    public TestServer Server => App.GetTestServer();

    public TestClock Clock { get; }

    public CapturingLoggerProvider Logs { get; }

    public RecordingHandlerState Recorder { get; }

    /// <summary>The server agent's DID ("Bob").</summary>
    public string ServerDid { get; }

    /// <summary>Every key id of the server DID (for log scans).</summary>
    public IReadOnlyList<string> ServerKids { get; }

    public TestPeer Alice { get; }

    public TestPeer Carol { get; }

    public TestPeer Mallory { get; }

    public IReturnRouteSessionRegistry Registry => App.Services.GetRequiredService<IReturnRouteSessionRegistry>();

    /// <summary>The replay guard the endpoints use (the host's own registration when the test made one).</summary>
    public IInboundReplayGuard ReplayGuard => App.Services.GetRequiredService<IInboundReplayGuard>();

    /// <summary>WebSocket requests on <c>/ws*</c> currently inside the endpoint (the receive loop has not returned).</summary>
    public int ActiveWebSocketConnections => Volatile.Read(ref _connections.Active);

    /// <summary>WebSocket requests on <c>/ws*</c> whose endpoint delegate has returned (the receive loop and its finally ran).</summary>
    public int EndedWebSocketConnections => Volatile.Read(ref _connections.Ended);

    private ConnectionCounters _connections = new();

    /// <summary>Every DID and key id the host knows about — none may ever appear in a log line.</summary>
    public IEnumerable<string> SensitiveValues
        => new[] { ServerDid, Alice.Did, Carol.Did, Mallory.Did }
            .Concat(ServerKids).Concat(Alice.Kids).Concat(Carol.Kids).Concat(Mallory.Kids);

    public static async Task<ReturnRouteTestHost> StartAsync(Action<ReturnRouteTestHostOptions>? configure = null)
    {
        var options = new ReturnRouteTestHostOptions();
        configure?.Invoke(options);

        var serverSecrets = new InMemorySecretsResolver();
        var (server, alice, carol, mallory) = await CreateIdentitiesAsync(serverSecrets);

        var clock = new TestClock(options.Start);
        var logs = new CapturingLoggerProvider();
        var recorder = new RecordingHandlerState();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(recorder);
        // Before AddDidComm, so a test's own registry / guard (decorators) wins over the TryAdd defaults.
        options.ConfigureServices?.Invoke(builder.Services);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(serverSecrets);
            b.AddBuiltInProtocols();
            b.AddProtocol<RecordingHandler>();
            options.ConfigureDidComm?.Invoke(b);
            b.Configure(o =>
            {
                o.Clock = () => clock.Now;
                o.MaxReceiveBytes = options.MaxReceiveBytes;
            });
        });
        builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o =>
        {
            o.AllowSameSocketReplies = options.AllowSameSocketReplies;
            o.UseStomp = options.UseStomp;
        });
        if (options.ConfigureSessions is not null)
            builder.Services.AddOptions<ReturnRouteSessionOptions>().Configure(options.ConfigureSessions);
        if (options.ConfigureReplay is not null)
            builder.Services.AddOptions<ReplayProtectionOptions>().Configure(options.ConfigureReplay);
        options.PostConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseWebSockets();
        var connections = new ConnectionCounters();
        app.Use(async (context, next) =>
        {
            // Observe when a WebSocket request's endpoint delegate returns: that is after the receive
            // loop's finally (unregister, writer stopped, final close or abort) ran.
            if (!context.Request.Path.StartsWithSegments("/ws"))
            {
                await next();
                return;
            }
            Interlocked.Increment(ref connections.Active);
            try
            {
                await next();
            }
            finally
            {
                Interlocked.Decrement(ref connections.Active);
                Interlocked.Increment(ref connections.Ended);
            }
        });
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm");
        app.MapDidCommWebSocket("/ws");
        var callback = options.Callback;
        app.MapDidCommEndpoint("/didcomm-callback", (unpacked, session, ct) =>
            callback is null ? Task.FromResult<Message?>(null) : callback(unpacked, session, ct));
        await app.StartAsync();

        var host = new ReturnRouteTestHost(app, clock, logs, recorder, server.Did, KidsOf(server), alice, carol, mallory);
        host._connections = connections;
        return host;
    }

    /// <summary>Mint <paramref name="count"/> more did:peer peers (their DIDs and kids join <see cref="SensitiveValues"/>).</summary>
    public async Task<IReadOnlyList<TestPeer>> CreatePeersAsync(int count)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IDidManager>();
        var keyGenerator = provider.GetRequiredService<IKeyGenerator>();
        var crypto = provider.GetRequiredService<ICryptoProvider>();
        var peers = new List<TestPeer>(count);
        for (var i = 0; i < count; i++)
            peers.Add(TestPeer.Create(await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto)));
        lock (_extraPeers)
            _extraPeers.AddRange(peers);
        return peers;
    }

    private readonly List<TestPeer> _extraPeers = new();

    /// <summary>Anoncrypt <paramref name="message"/> to the server (no sender authentication), optionally around an authcrypt layer.</summary>
    public async Task<string> AnoncryptAsync(Message message)
    {
        var packed = await Alice.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ServerDid },
            From: null,
            Forward: false));
        return packed.Message;
    }

    /// <summary>
    /// Pack <paramref name="message"/> from <paramref name="from"/> in the given envelope and return it
    /// with the media type an HTTP POST carries for it.
    /// </summary>
    public async Task<(string Packed, string ContentType)> PackAsync(TestPeer from, Message message, EnvelopeShape shape)
        => shape switch
        {
            EnvelopeShape.Plaintext => (await from.Client.PackPlaintextAsync(message), "application/didcomm-plain+json"),
            EnvelopeShape.SignedOnly => (await from.Client.PackSignedAsync(message, from.Did), "application/didcomm-signed+json"),
            EnvelopeShape.Anoncrypt => (await AnoncryptAsync(message), "application/didcomm-encrypted+json"),
            EnvelopeShape.AnoncryptOfAuthcrypt => (await AuthcryptAsync(from, message, protectSender: true), "application/didcomm-encrypted+json"),
            EnvelopeShape.Authcrypt => (await AuthcryptAsync(from, message), "application/didcomm-encrypted+json"),
            EnvelopeShape.AnoncryptOfSigned => ((await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ServerDid }, From: null, SignFrom: from.Did, Forward: false))).Message, "application/didcomm-encrypted+json"),
            EnvelopeShape.AuthcryptOfSigned => ((await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ServerDid }, From: from.Did, SignFrom: from.Did, Forward: false))).Message, "application/didcomm-encrypted+json"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    /// <summary>
    /// Stamp <c>created_time</c> from the server's injected clock. These tests pack with
    /// <c>PackEncryptedAsync</c>, which does not stamp; <c>SendAsync</c> does (FR-RR-09).
    /// </summary>
    public MessageBuilder Stamped(MessageBuilder builder) => builder.WithCreatedTime(Clock.Now.ToUnixTimeSeconds());

    /// <summary>A message of the recording protocol from <paramref name="from"/> to the server.</summary>
    public MessageBuilder RecordingMessage(TestPeer from, string name = "probe")
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/" + name)
            .WithFrom(from.Did)
            .WithTo(ServerDid);

    /// <summary>Authcrypt <paramref name="message"/> from <paramref name="from"/> to the server, no forward wrapping.</summary>
    public async Task<string> AuthcryptAsync(TestPeer from, Message message, bool protectSender = false)
    {
        var packed = await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { ServerDid },
            From: from.Did,
            ProtectSender: protectSender,
            Forward: false));
        return packed.Message;
    }

    /// <summary>POST a packed envelope; returns status, the exact Content-Type header value and the body.</summary>
    public async Task<HttpPostResult> PostAsync(string packed, string path = "/didcomm", string contentType = "application/didcomm-encrypted+json")
    {
        using var client = Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        var responseType = response.Content.Headers.TryGetValues("Content-Type", out var values) ? string.Join(",", values) : null;
        return new HttpPostResult(response.StatusCode, responseType, body, response.Content.Headers.ContentLength);
    }

    public Uri WebSocketUri(string path = "/ws") => new UriBuilder(Server.BaseAddress) { Scheme = "ws", Path = path }.Uri;

    public Task<WebSocket> ConnectAsync(string path = "/ws") => Server.CreateWebSocketClient().ConnectAsync(WebSocketUri(path), default);

    /// <summary>Send one packed envelope as one binary WebSocket message.</summary>
    public static Task SendAsync(WebSocket socket, string packed)
        => socket.SendAsync(Encoding.UTF8.GetBytes(packed), WebSocketMessageType.Binary, endOfMessage: true, default);

    /// <summary>Receive one whole WebSocket message (reassembled), or throw <see cref="OperationCanceledException"/> after <paramref name="timeout"/>.</summary>
    public static async Task<(WebSocketMessageType Type, string Text)> ReceiveAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
        return (result.MessageType, Encoding.UTF8.GetString(ms.ToArray()));
    }

    /// <summary>Poll <paramref name="condition"/> until it holds or <paramref name="timeout"/> passes.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>Log lines that carry any DID or key id the host knows about (must be empty, JC-6).</summary>
    public IReadOnlyList<string> LeakingLogLines(params string[] extraSensitive)
    {
        List<TestPeer> extra;
        lock (_extraPeers)
            extra = _extraPeers.ToList();
        var sensitive = SensitiveValues
            .Concat(extra.SelectMany(p => p.Kids.Append(p.Did)))
            .Concat(extraSensitive)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToArray();
        return Logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
    }

    /// <summary>The FR-RR-08 drop lines (Warning and Debug) written for <paramref name="messageId"/>.</summary>
    public IReadOnlyList<string> DropLines(string messageId)
        => Logs.Entries.Where(line => line.Contains("dropped: replay admission", StringComparison.Ordinal)
                                      && line.Contains(messageId, StringComparison.Ordinal)).ToList();

    /// <summary>Every key id of <paramref name="identity"/>.</summary>
    public static IReadOnlyList<string> KidsOf(PeerIdentity identity)
        => identity.Privates.Select(j => j.Kid).OfType<string>().ToList();

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        await Alice.DisposeAsync();
        await Carol.DisposeAsync();
        await Mallory.DisposeAsync();
        List<TestPeer> extra;
        lock (_extraPeers)
            extra = _extraPeers.ToList();
        foreach (var peer in extra)
            await peer.DisposeAsync();
    }

    private sealed class ConnectionCounters
    {
        public int Active;
        public int Ended;
    }

    /// <summary>Mint the server identity (its private keys go into <paramref name="serverSecrets"/>) and three peers.</summary>
    internal static async Task<(PeerIdentity Server, TestPeer Alice, TestPeer Carol, TestPeer Mallory)> CreateIdentitiesAsync(InMemorySecretsResolver serverSecrets)
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
        });
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IDidManager>();
        var keyGenerator = provider.GetRequiredService<IKeyGenerator>();
        var crypto = provider.GetRequiredService<ICryptoProvider>();

        var server = await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto);
        foreach (var jwk in server.Privates)
            serverSecrets.Add(jwk);
        var alice = TestPeer.Create(await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto));
        var carol = TestPeer.Create(await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto));
        var mallory = TestPeer.Create(await PeerIdentityFactory.CreateAsync(manager, keyGenerator, crypto));
        return (server, alice, carol, mallory);
    }
}

/// <summary>Knobs for <see cref="ReturnRouteTestHost.StartAsync"/>.</summary>
internal sealed class ReturnRouteTestHostOptions
{
    public bool AllowSameSocketReplies { get; set; }

    public bool UseStomp { get; set; }

    public int MaxReceiveBytes { get; set; } = 256 * 1024;

    public DateTimeOffset Start { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public Action<ReturnRouteSessionOptions>? ConfigureSessions { get; set; }

    public Action<ReplayProtectionOptions>? ConfigureReplay { get; set; }

    /// <summary>Runs before <c>AddDidComm</c>: a registry or replay guard registered here replaces the defaults.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>Runs after <c>AddDidComm</c>: lets a test remove a default registration (a host that never registered the registry / guard).</summary>
    public Action<IServiceCollection>? PostConfigureServices { get; set; }

    /// <summary>Extra <c>AddDidComm</c> configuration (more protocol handlers).</summary>
    public Action<DidCommBuilder>? ConfigureDidComm { get; set; }

    /// <summary>The <c>/didcomm-callback</c> endpoint's behaviour (the session-aware overload).</summary>
    public Func<UnpackResult, IReturnRouteSession?, CancellationToken, Task<Message?>>? Callback { get; set; }
}

/// <summary>The envelope an inbound arrives in (FR-RR-03 eligibility matrix).</summary>
public enum EnvelopeShape
{
    Plaintext,
    SignedOnly,
    Anoncrypt,
    /// <summary>anoncrypt(sign(plaintext)): encrypted, and <c>Authenticated</c> by the JWS, but no authcrypt layer.</summary>
    AnoncryptOfSigned,
    AnoncryptOfAuthcrypt,
    Authcrypt,
    /// <summary>authcrypt(sign(plaintext)).</summary>
    AuthcryptOfSigned,
}

internal sealed record HttpPostResult(HttpStatusCode Status, string? ContentType, byte[] Body, long? ContentLength)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>A did:peer identity with its own secrets and facade.</summary>
internal sealed class TestPeer : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private TestPeer(PeerIdentity identity, InMemorySecretsResolver secrets, ServiceProvider services)
    {
        Did = identity.Did;
        Kids = ReturnRouteTestHost.KidsOf(identity);
        Secrets = secrets;
        _services = services;
        Client = services.GetRequiredService<DidCommClient>();
    }

    public string Did { get; }

    public IReadOnlyList<string> Kids { get; }

    public InMemorySecretsResolver Secrets { get; }

    public DidCommClient Client { get; }

    /// <summary>The peer's own container (did:peer resolution, its secrets).</summary>
    public IServiceProvider Services => _services;

    public static TestPeer Create(PeerIdentity identity)
    {
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in identity.Privates)
            secrets.Add(jwk);
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
        });
        return new TestPeer(identity, secrets, services.BuildServiceProvider());
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

/// <summary>A clock the test moves by hand (injected as <c>DidCommOptions.Clock</c>).</summary>
internal sealed class TestClock
{
    private long _ticks;

    public TestClock(DateTimeOffset start) => _ticks = start.UtcTicks;

    public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>What the <see cref="RecordingHandler"/> saw for one inbound.</summary>
internal sealed record RecordedInbound(Message Message, IReturnRouteSession? Session, ReturnRouteHeader ReturnRoute, IReadOnlyList<bool> PushResults);

/// <summary>Shared, test-controlled state of the <see cref="RecordingHandler"/>.</summary>
internal sealed class RecordingHandlerState
{
    private readonly ConcurrentQueue<RecordedInbound> _seen = new();

    /// <summary>
    /// What the handler does with each inbound: returns the messages to push through
    /// <c>context.Session</c> (in order) and the reply to return. Default: push nothing, return nothing.
    /// </summary>
    public Func<Message, ProtocolContext, (IReadOnlyList<Message> Pushes, Message? Reply)> Behavior { get; set; }
        = (_, _) => (Array.Empty<Message>(), null);

    /// <summary>Runs after the pushes and after the inbound was recorded; may throw (a handler fault after a push).</summary>
    public Action<Message, ProtocolContext, IReadOnlyList<bool>>? AfterPushes { get; set; }

    /// <summary>Runs on handler entry, before <see cref="Behavior"/>; may block (a rendezvous).</summary>
    public Action<Message>? OnEnter { get; set; }

    public IReadOnlyCollection<RecordedInbound> Seen => _seen;

    public int Count => _seen.Count;

    /// <summary>Handler invocations so far (counted on entry, before any push).</summary>
    public int Invocations => Volatile.Read(ref _invocations);

    private int _invocations;

    internal void Entered(Message message)
    {
        Interlocked.Increment(ref _invocations);
        OnEnter?.Invoke(message);
    }

    internal void Record(RecordedInbound inbound) => _seen.Enqueue(inbound);
}

/// <summary>
/// A protocol handler that records <c>context.Session</c> and <c>context.ReturnRoute</c> and does
/// whatever <see cref="RecordingHandlerState.Behavior"/> says: push messages through the session and
/// / or return a reply; then runs <see cref="RecordingHandlerState.AfterPushes"/> (which may throw).
/// </summary>
internal abstract class RecordingHandlerBase : IProtocolHandler
{
    private readonly RecordingHandlerState _state;

    protected RecordingHandlerBase(RecordingHandlerState state) => _state = state;

    protected abstract string Uri { get; }

    string IProtocolHandler.ProtocolUri => Uri;

    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        _state.Entered(message);
        var (pushes, reply) = _state.Behavior(message, context);
        var results = new List<bool>();
        foreach (var push in pushes)
            results.Add(context.Session is not null && await context.Session.TrySendAsync(push, ct));
        _state.Record(new RecordedInbound(message, context.Session, context.ReturnRoute, results));
        _state.AfterPushes?.Invoke(message, context, results);
        return reply;
    }
}

/// <summary>The recording handler for the test protocol <see cref="ProtocolUri"/>.</summary>
internal sealed class RecordingHandler : RecordingHandlerBase
{
    public const string ProtocolUri = "https://didcomm.org/test-rr/1.0";

    public RecordingHandler(RecordingHandlerState state) : base(state) { }

    protected override string Uri => ProtocolUri;
}

/// <summary>A recording handler for Message Pickup 3.0 — a mediator-service PIURI (FR-RR-08 scope b).</summary>
internal sealed class PickupRecordingHandler : RecordingHandlerBase
{
    public const string ProtocolUri = "https://didcomm.org/messagepickup/3.0";

    public PickupRecordingHandler(RecordingHandlerState state) : base(state) { }

    protected override string Uri => ProtocolUri;
}

/// <summary>
/// A recording handler for Message Pickup 4.0 — a mediator-service PIURI (FR-RR-08 scope b) whose
/// <c>messages-received</c> acknowledgement is sent header-less (#92: the FR-RR-09 PIURI clause stamps it).
/// </summary>
internal sealed class Pickup40RecordingHandler : RecordingHandlerBase
{
    public const string ProtocolUri = "https://didcomm.org/message-pickup/4.0";

    public Pickup40RecordingHandler(RecordingHandlerState state) : base(state) { }

    protected override string Uri => ProtocolUri;
}

/// <summary>A recording handler for Routing 2.0 (<c>forward</c>) — replay-exempt and never a session (FR-RR-02).</summary>
internal sealed class RoutingRecordingHandler : RecordingHandlerBase
{
    public const string ProtocolUri = "https://didcomm.org/routing/2.0";

    public RoutingRecordingHandler(RecordingHandlerState state) : base(state) { }

    protected override string Uri => ProtocolUri;
}

/// <summary>
/// Counts callers inside a section and parks each one until <c>parties</c> have arrived (bounded),
/// so a concurrency test can force overlap and then prove it happened (L-044).
/// </summary>
internal sealed class Rendezvous
{
    private readonly int _parties;
    private readonly TimeSpan _timeout;
    private readonly ManualResetEventSlim _release = new(false);
    private int _arrived;
    private int _inside;
    private int _maxInside;

    public Rendezvous(int parties, TimeSpan timeout)
    {
        _parties = parties;
        _timeout = timeout;

        // The section is synchronous (IInboundReplayGuard / IReturnRouteSessionRegistry), so each
        // party parks a thread-pool thread until all have arrived. Raise the pool's floor (never
        // lower it) so parking them does not make the pool ramp up slowly and starve the tests that
        // run in parallel with this one; a starved pool delays their Task.Run and timers.
        ThreadPool.GetMinThreads(out var worker, out var io);
        var needed = Environment.ProcessorCount + 2 * parties;
        if (worker < needed)
            ThreadPool.SetMinThreads(needed, io);
    }

    /// <summary>The most callers that were inside the section at the same time.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxInside);

    public int Arrived => Volatile.Read(ref _arrived);

    public T Run<T>(Func<T> body)
    {
        var inside = Interlocked.Increment(ref _inside);
        int seen;
        while ((seen = Volatile.Read(ref _maxInside)) < inside
               && Interlocked.CompareExchange(ref _maxInside, inside, seen) != seen)
        {
        }
        try
        {
            if (Interlocked.Increment(ref _arrived) >= _parties)
                _release.Set();
            _release.Wait(_timeout);
            return body();
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }
}

/// <summary>An <see cref="IInboundReplayGuard"/> that runs every admission through a <see cref="Rendezvous"/>.</summary>
internal sealed class RendezvousReplayGuard : IInboundReplayGuard
{
    private readonly IInboundReplayGuard _inner;

    public RendezvousReplayGuard(IInboundReplayGuard inner, Rendezvous rendezvous)
    {
        _inner = inner;
        Rendezvous = rendezvous;
    }

    public Rendezvous Rendezvous { get; }

    public ReplayAdmission TryAdmit(string peerDidSubject, string messageId, long? createdTimeEpochSeconds, DateTimeOffset now)
        => Rendezvous.Run(() => _inner.TryAdmit(peerDidSubject, messageId, createdTimeEpochSeconds, now));
}

/// <summary>An <see cref="IReturnRouteSessionRegistry"/> whose registrations run through a <see cref="Rendezvous"/> and are recorded.</summary>
internal sealed class RendezvousSessionRegistry : IReturnRouteSessionRegistry
{
    private readonly IReturnRouteSessionRegistry _inner;

    public RendezvousSessionRegistry(IReturnRouteSessionRegistry inner, Rendezvous rendezvous)
    {
        _inner = inner;
        Rendezvous = rendezvous;
    }

    public Rendezvous Rendezvous { get; }

    /// <summary>Every session offered to <see cref="Register"/>, with its outcome.</summary>
    public ConcurrentQueue<(IReturnRouteSession Session, SessionRegistration Result)> Registrations { get; } = new();

    public int Count => _inner.Count;

    public event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened
    {
        add => _inner.SessionOpened += value;
        remove => _inner.SessionOpened -= value;
    }

    public event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed
    {
        add => _inner.SessionClosed += value;
        remove => _inner.SessionClosed -= value;
    }

    public SessionRegistration Register(IReturnRouteSession session)
    {
        var result = Rendezvous.Run(() => _inner.Register(session));
        Registrations.Enqueue((session, result));
        return result;
    }

    public bool Unregister(IReturnRouteSession session) => _inner.Unregister(session);

    public IReadOnlyList<IReturnRouteSession> Find(string did) => _inner.Find(did);
}
