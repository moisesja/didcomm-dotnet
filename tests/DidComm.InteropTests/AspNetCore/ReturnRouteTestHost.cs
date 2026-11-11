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
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(serverSecrets);
            b.AddBuiltInProtocols();
            b.AddProtocol<RecordingHandler>();
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

        var app = builder.Build();
        app.UseWebSockets();
        app.UseRouting();
        app.MapDidCommEndpoint("/didcomm");
        app.MapDidCommWebSocket("/ws");
        var callback = options.Callback;
        app.MapDidCommEndpoint("/didcomm-callback", (unpacked, session, ct) =>
            callback is null ? Task.FromResult<Message?>(null) : callback(unpacked, session, ct));
        await app.StartAsync();

        return new ReturnRouteTestHost(app, clock, logs, recorder, server.Did, KidsOf(server), alice, carol, mallory);
    }

    /// <summary>Stamp <c>created_time</c> from the server's injected clock (FR-RR-09 client stamping is #92).</summary>
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
        var sensitive = SensitiveValues.Concat(extraSensitive).Where(v => !string.IsNullOrEmpty(v)).ToArray();
        return Logs.Entries.Where(line => sensitive.Any(v => line.Contains(v, StringComparison.Ordinal))).ToList();
    }

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

    /// <summary>The <c>/didcomm-callback</c> endpoint's behaviour (the session-aware overload).</summary>
    public Func<UnpackResult, IReturnRouteSession?, CancellationToken, Task<Message?>>? Callback { get; set; }
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

    public IReadOnlyCollection<RecordedInbound> Seen => _seen;

    public int Count => _seen.Count;

    internal void Record(RecordedInbound inbound) => _seen.Enqueue(inbound);
}

/// <summary>
/// A protocol handler for <see cref="ProtocolUri"/> that records <c>context.Session</c> and
/// <c>context.ReturnRoute</c> and does whatever <see cref="RecordingHandlerState.Behavior"/> says:
/// push messages through the session and / or return a reply.
/// </summary>
internal sealed class RecordingHandler : IProtocolHandler
{
    public const string ProtocolUri = "https://didcomm.org/test-rr/1.0";

    private readonly RecordingHandlerState _state;

    public RecordingHandler(RecordingHandlerState state) => _state = state;

    string IProtocolHandler.ProtocolUri => ProtocolUri;

    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        var (pushes, reply) = _state.Behavior(message, context);
        var results = new List<bool>();
        foreach (var push in pushes)
            results.Add(context.Session is not null && await context.Session.TrySendAsync(push, ct));
        _state.Record(new RecordedInbound(message, context.Session, context.ReturnRoute, results));
        return reply;
    }
}
